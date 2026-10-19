using System.Text.Json;
using System.Text.RegularExpressions;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Drafting;
using TicketShine.Support.Core.Email;
using TicketShine.Support.Core.Lookups;

namespace TicketShine.Support.Core.Workflow;

public sealed record WorkflowResult(bool Succeeded, string Message, IReadOnlyList<string>? Errors = null)
{
    public static WorkflowResult Ok(string message) => new(true, message);

    public static WorkflowResult Fail(string message, IReadOnlyList<string>? errors = null) => new(false, message, errors);
}

/// <summary>
/// Orchestrates a case: lookup via MCP → classify → draft → review gate → outbox.
/// Every customer reply must be approved by a staff member; there is no auto-approve.
/// </summary>
public sealed partial class SupportWorkflow(
    ICaseRepository repository,
    ILookupGateway lookups,
    IReplyDraftGenerator generator,
    IDemoClock demoClock,
    TimeProvider time,
    IOutboxSignal outboxSignal)
{
    public const string WorkflowOrigin = "workflow";
    public const int MaxCustomerMessageLength = 4000;

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9-]{1,31}$")]
    private static partial Regex OrderIdFormat();

    public IReplyDraftGenerator Generator => generator;

    public async Task<(Guid? CaseId, WorkflowResult Result)> CreateCaseAsync(NewCaseRequest request, CancellationToken ct = default)
    {
        var orderId = (request.OrderId ?? string.Empty).Trim().ToUpperInvariant();
        if (!OrderIdFormat().IsMatch(orderId))
        {
            return (null, WorkflowResult.Fail("Order ID must be 2–32 letters, digits or dashes."));
        }

        var message = (request.CustomerMessage ?? string.Empty).Trim();
        if (message.Length > MaxCustomerMessageLength)
        {
            return (null, WorkflowResult.Fail($"Customer message must be at most {MaxCustomerMessageLength} characters."));
        }

        var now = time.GetUtcNow();
        var supportCase = new SupportCase(Guid.NewGuid(), orderId, message, CaseState.Created, Classification.Unknown, null, 0, now, now);
        await repository.CreateCaseAsync(supportCase, ActorOrDefault(request.Actor), ct);

        var result = await RunLookupAndDraftAsync(supportCase.Id, [CaseState.Created], ActorOrDefault(request.Actor), ct);
        return (supportCase.Id, result);
    }

    /// <summary>"Retry lookup" button — only available while the case is LookupFailed.</summary>
    public Task<WorkflowResult> RetryLookupAsync(Guid caseId, string actor, CancellationToken ct = default) =>
        RunLookupAndDraftAsync(caseId, [CaseState.LookupFailed], ActorOrDefault(actor), ct);

    /// <summary>
    /// Creates a case from a customer email. Same path as the "Create case" button after the order is resolved,
    /// plus a sender check: the From address must be authenticated and match the order's customer email.
    /// </summary>
    public async Task<(Guid CaseId, WorkflowResult Result)> CreateEmailCaseAsync(EmailCaseRequest request, CancellationToken ct = default)
    {
        var mail = request.Email;
        var sender = EmailAddress.Normalize(mail.FromAddress);
        var body = TruncateMessage(request.Body);
        var now = time.GetUtcNow();
        var caseId = Guid.NewGuid();
        var calls = new List<ToolCallRecord>();
        var evidence = new List<EvidenceSnapshot>();

        string? orderId = null;
        string? unresolved = null;
        if (request.OrderIdsInMessage.Count == 1)
        {
            orderId = request.OrderIdsInMessage[0];
        }
        else if (request.OrderIdsInMessage.Count > 1)
        {
            unresolved = $"The email mentions several orders ({string.Join(", ", request.OrderIdsInMessage)}). Ask the customer which order they mean.";
        }
        else if (!request.SenderAuthenticated)
        {
            unresolved = "No order number in the email and the sender could not be authenticated, so no order search was made.";
        }
        else
        {
            // Only an authenticated sender may trigger a search by email address.
            var found = await lookups.FindOrdersByEmailAsync(sender, EmailIngestActor, ct);
            calls.Add(found.Trace);
            evidence.Add(ToSnapshot(caseId, LookupTools.FindOrdersByEmail, found.Result));
            var ids = found.Result.Data?.OrderIds ?? [];
            if (found.Result.Status == LookupStatus.Error)
            {
                unresolved = $"No order number in the email and the order search failed: {found.Result.Error}";
            }
            else if (ids.Count == 1)
            {
                orderId = ids[0];
            }
            else
            {
                unresolved = ids.Count == 0
                    ? "No order number in the email and no orders were found for the sender."
                    : $"No order number in the email and the sender has several orders ({string.Join(", ", ids)}). Ask which order they mean.";
            }
        }

        var supportCase = new SupportCase(caseId, orderId ?? UnmatchedOrderId, body, CaseState.Created, Classification.Unknown, null, 0, now, now,
            CaseChannel.Email, mail.ThreadId, sender, request.SenderAuthenticated);
        await repository.CreateCaseAsync(supportCase, EmailIngestActor, ct);
        await repository.AddCaseMessageAsync(ToCaseMessage(caseId, request, body), ct);
        await repository.AppendAuditAsync(caseId, "EmailReceived", EmailIngestActor,
            $"From {sender} ({request.AuthenticationSummary}), subject \"{mail.Subject}\"", ct);
        if (calls.Count > 0)
        {
            await repository.SaveLookupDataAsync(caseId, evidence, calls, ct);
        }

        if (unresolved is not null)
        {
            await repository.TransitionAsync(caseId, [CaseState.Created], CaseState.NeedsHuman, Classification.Unknown, unresolved, EmailIngestActor, unresolved, ct);
            return (caseId, WorkflowResult.Ok(unresolved));
        }

        return (caseId, await RunLookupAndDraftAsync(caseId, [CaseState.Created], EmailIngestActor, ct));
    }

    /// <summary>
    /// A new customer message arrived on the thread of an open case. A pending draft no longer answers the
    /// conversation, so the case goes back to a human instead of being approvable.
    /// </summary>
    public async Task<WorkflowResult> HandleCustomerReplyAsync(Guid caseId, EmailCaseRequest request, CancellationToken ct = default)
    {
        var c = await repository.GetCaseAsync(caseId, ct);
        if (c is null) return WorkflowResult.Fail("Case not found.");

        await repository.AddCaseMessageAsync(ToCaseMessage(caseId, request, TruncateMessage(request.Body)), ct);

        var sender = EmailAddress.Normalize(request.Email.FromAddress);
        var trusted = request.SenderAuthenticated && sender == EmailAddress.Normalize(c.SenderEmail);
        var warning = trusted ? string.Empty : " Warning: this message is not from the verified sender of the case.";
        await repository.AppendAuditAsync(caseId, "CustomerReplied", EmailIngestActor,
            $"New message from {sender} ({request.AuthenticationSummary}).{warning}", ct);

        if (c.State == CaseState.AwaitingReview)
        {
            var reason = $"The customer sent a new message after draft v{c.CurrentDraftVersion} was written. " +
                         $"Read the conversation, then generate a new draft.{warning}";
            if (await repository.TransitionAsync(caseId, [CaseState.AwaitingReview], CaseState.NeedsHuman, null, reason, EmailIngestActor, reason, ct))
            {
                return WorkflowResult.Ok(reason);
            }
        }

        return WorkflowResult.Ok("Message added to the case.");
    }

    public const string EmailIngestActor = "email-ingest";

    /// <summary>Order ID placeholder for email cases where no single order could be identified.</summary>
    public const string UnmatchedOrderId = "UNMATCHED";

    private static CaseMessage ToCaseMessage(Guid caseId, EmailCaseRequest r, string body) => new(
        Guid.NewGuid(), caseId, MessageDirection.Inbound, r.Email.Provider, r.Email.ProviderMessageId, r.Email.RfcMessageId,
        EmailAddress.Normalize(r.Email.FromAddress), r.Email.ToAddress, r.Email.Subject, body, r.AuthenticationSummary, r.Email.ReceivedAtUtc);

    public static string ReplySubject(string? original)
    {
        var s = (original ?? string.Empty).Trim();
        if (s.Length == 0) return "Re: your message";
        var reply = s.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? s : $"Re: {s}";
        return reply.Length <= DraftValidator.MaxSubjectLength ? reply : reply[..DraftValidator.MaxSubjectLength];
    }

    private static string TruncateMessage(string? body)
    {
        var text = (body ?? string.Empty).Trim();
        const string marker = "\n[message truncated]";
        return text.Length <= MaxCustomerMessageLength ? text : text[..(MaxCustomerMessageLength - marker.Length)] + marker;
    }

    /// <summary>
    /// A staff member requests a draft for a NeedsHuman case (e.g. an overdue order: a "we're looking into it" reply).
    /// The draft still has to go through review.
    /// </summary>
    public async Task<WorkflowResult> RequestDraftAsync(Guid caseId, string actor, CancellationToken ct = default)
    {
        var details = await repository.GetDetailsAsync(caseId, ct);
        if (details is null) return WorkflowResult.Fail("Case not found.");

        var c = details.Case;
        if (c.State != CaseState.NeedsHuman)
        {
            return WorkflowResult.Fail($"A draft can only be requested while the case needs a human (current state: {c.State}).");
        }

        if (c.Classification is Classification.NotFound or Classification.Unknown)
        {
            return WorkflowResult.Fail("No verified order record — there is no recipient to reply to.");
        }

        var bundle = EvidenceBundleFactory.FromSnapshots(details.Evidence);
        if (bundle is null || !bundle.Order.IsSuccess)
        {
            return WorkflowResult.Fail("Order evidence is missing; retry the lookup first.");
        }

        var kind = c.Classification == Classification.Overdue ? DraftKind.Holding : DraftKind.Standard;
        return await GenerateAndSaveDraftAsync(c, bundle, kind, [CaseState.NeedsHuman], ActorOrDefault(actor), ct);
    }

    public async Task<WorkflowResult> EditDraftAsync(Guid caseId, int expectedVersion, string subject, string body, string reviewer, CancellationToken ct = default)
    {
        var details = await repository.GetDetailsAsync(caseId, ct);
        if (details is null) return WorkflowResult.Fail("Case not found.");

        if (details.Case.State != CaseState.AwaitingReview)
        {
            return WorkflowResult.Fail($"Only drafts awaiting review can be edited (current state: {details.Case.State}).");
        }

        var current = details.CurrentDraft;
        if (current is null || current.Version != expectedVersion)
        {
            return WorkflowResult.Fail("This draft changed in another tab. Reload to see the latest version.");
        }

        var proposal = new DraftProposal(subject.Trim(), body.Trim(), current.EvidenceRefs);
        var bundle = EvidenceBundleFactory.FromSnapshots(details.Evidence);
        var available = details.Evidence.Where(e => e.Status == LookupStatus.Success).Select(e => e.SourceId).ToHashSet();
        var validation = DraftValidator.Validate(details.Case.OrderId, proposal, available, details.Case.Classification, bundle?.Notes?.Data);
        if (!validation.IsValid)
        {
            return WorkflowResult.Fail("Edit rejected by validation.", validation.Errors);
        }

        var draft = current with
        {
            Id = Guid.NewGuid(),
            Version = expectedVersion + 1,
            Subject = proposal.Subject,
            Body = proposal.Body,
            Source = DraftSource.StaffEdit,
            Warnings = validation.Warnings,
            CreatedBy = ActorOrDefault(reviewer),
            CreatedAtUtc = time.GetUtcNow(),
        };

        var saved = await repository.SaveDraftAsync(draft, expectedVersion, [CaseState.AwaitingReview], $"Draft edited by {draft.CreatedBy} (v{expectedVersion} → v{draft.Version})", ct);
        return saved
            ? WorkflowResult.Ok($"Saved draft v{draft.Version}.")
            : WorkflowResult.Fail("This draft changed in another tab. Reload to see the latest version.");
    }

    public async Task<ReviewResult> ApproveAsync(Guid caseId, int draftVersion, string reviewer, CancellationToken ct = default)
    {
        var result = await repository.ApproveAsync(caseId, draftVersion, ActorOrDefault(reviewer), time.GetUtcNow(), ct);
        if (result.Outcome == ApprovalOutcome.Approved)
        {
            outboxSignal.Notify();
        }

        return result;
    }

    public Task<ReviewResult> RejectAsync(Guid caseId, int draftVersion, string reviewer, string? reason, CancellationToken ct = default) =>
        repository.RejectAsync(caseId, draftVersion, ActorOrDefault(reviewer), reason, time.GetUtcNow(), ct);

    private async Task<WorkflowResult> RunLookupAndDraftAsync(Guid caseId, IReadOnlyCollection<CaseState> from, string actor, CancellationToken ct)
    {
        if (!await repository.TransitionAsync(caseId, from, CaseState.LookingUp, null, null, actor, "MCP lookups started", ct))
        {
            return WorkflowResult.Fail("Case is not in a state that allows lookups.");
        }

        var supportCase = (await repository.GetCaseAsync(caseId, ct))!;
        var (bundle, calls, evidence, senderProblem) = await LookupAsync(supportCase, ct);
        await repository.SaveLookupDataAsync(caseId, evidence, calls, ct);

        // An email case whose sender is not verified for this order never gets a draft; staff must handle it.
        var decision = senderProblem is not null
            ? new ClassificationDecision(Classification.Unknown, CaseState.NeedsHuman, senderProblem)
            : CaseClassifier.Classify(bundle, demoClock.Now);
        await repository.TransitionAsync(caseId, [CaseState.LookingUp], CaseState.LookingUp, decision.Classification, decision.Reason, WorkflowOrigin,
            $"Classified as {decision.Classification}: {decision.Reason}", ct);

        if (decision.NextState != CaseState.AwaitingReview)
        {
            await repository.TransitionAsync(caseId, [CaseState.LookingUp], decision.NextState, decision.Classification, decision.Reason, WorkflowOrigin,
                $"Moved to {decision.NextState}", ct);
            return WorkflowResult.Ok($"Case moved to {decision.NextState}: {decision.Reason}");
        }

        supportCase = supportCase with { Classification = decision.Classification };
        var draftResult = await GenerateAndSaveDraftAsync(supportCase, bundle, DraftKind.Standard, [CaseState.LookingUp], WorkflowOrigin, ct);
        if (!draftResult.Succeeded)
        {
            // Show the error clearly; do not silently fall back to the template.
            var reason = $"Draft generation failed: {draftResult.Message}";
            await repository.TransitionAsync(caseId, [CaseState.LookingUp], CaseState.NeedsHuman, decision.Classification, reason, WorkflowOrigin, reason, ct);
        }

        return draftResult;
    }

    private async Task<(EvidenceBundle Bundle, List<ToolCallRecord> Calls, List<EvidenceSnapshot> Evidence, string? SenderProblem)> LookupAsync(
        SupportCase c,
        CancellationToken ct)
    {
        var calls = new List<ToolCallRecord>();
        var evidence = new List<EvidenceSnapshot>();

        void Track<T>(string tool, ToolInvocation<T> inv) where T : class
        {
            calls.Add(inv.Trace);
            evidence.Add(ToSnapshot(c.Id, tool, inv.Result));
        }

        var order = await lookups.GetOrderAsync(c.OrderId, WorkflowOrigin, ct);
        Track(LookupTools.GetOrder, order);
        if (!order.Result.IsSuccess)
        {
            // No further lookups when the order does not exist or the lookup failed: do not reveal other data.
            return (new EvidenceBundle(order.Result, null, null, null), calls, evidence, null);
        }

        if (c.Channel == CaseChannel.Email && SenderProblem(c, order.Result.Data!) is { } problem)
        {
            // Stop here: no delivery status, notes or draft for an unverified sender.
            return (new EvidenceBundle(order.Result, null, null, null), calls, evidence, problem);
        }

        var delivery = await lookups.GetDeliveryStatusAsync(c.OrderId, WorkflowOrigin, ct);
        Track(LookupTools.GetDeliveryStatus, delivery);

        var notes = await lookups.GetCaseNotesAsync(c.OrderId, WorkflowOrigin, ct);
        Track(LookupTools.GetCaseNotes, notes);

        LookupResult<PolicyInfo>? policy = null;
        if (delivery.Result.IsSuccess)
        {
            var p = await lookups.GetDemoPolicyAsync(CaseClassifier.PolicyTopicFor(delivery.Result.Data), WorkflowOrigin, ct);
            Track(LookupTools.GetDemoPolicy, p);
            policy = p.Result;
        }

        return (new EvidenceBundle(order.Result, delivery.Result, notes.Result, policy), calls, evidence, null);
    }

    private static string? SenderProblem(SupportCase c, OrderInfo order)
    {
        if (!c.SenderAuthenticated)
        {
            return $"Sender could not be authenticated (DMARC/DKIM did not pass) for order {order.OrderId}. " +
                   "Do not share order details; contact the customer at the address on file.";
        }

        if (!string.Equals(EmailAddress.Normalize(c.SenderEmail), EmailAddress.Normalize(order.CustomerEmail), StringComparison.Ordinal))
        {
            return $"Sender {c.SenderEmail} is not the customer email on order {order.OrderId}. " +
                   "Do not share order details; contact the customer at the address on file.";
        }

        return null;
    }

    private async Task<WorkflowResult> GenerateAndSaveDraftAsync(
        SupportCase c,
        EvidenceBundle bundle,
        DraftKind kind,
        IReadOnlyCollection<CaseState> allowedStates,
        string actor,
        CancellationToken ct)
    {
        // For email cases, answer the latest customer message and reply in the same subject so the mail client threads it.
        var latestInbound = c.Channel == CaseChannel.Email ? (await repository.GetDetailsAsync(c.Id, ct))?.LatestInbound : null;
        var customerMessage = latestInbound?.Body ?? c.CustomerMessage;

        var context = new DraftContext(c.Id, c.OrderId, customerMessage, c.Classification, kind, bundle, demoClock.Now, lookups);
        DraftGenerationResult result;
        try
        {
            result = await generator.GenerateAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = DraftGenerationResult.Fail($"{generator.DisplayName} error: {ex.Message}");
        }

        var extraSnapshots = result.ExtraEvidence
            .Select(e => new EvidenceSnapshot(Guid.NewGuid(), c.Id, e.Trace.ToolName, e.Envelope.SourceId, e.Envelope.Status,
                LookupTools.Serialize(e.Envelope), e.Envelope.RetrievedAtUtc))
            .ToList();
        if (result.ToolCalls.Count > 0 || extraSnapshots.Count > 0)
        {
            await repository.SaveLookupDataAsync(c.Id, extraSnapshots, result.ToolCalls, ct);
        }

        if (!result.Succeeded || result.Proposal is null)
        {
            await repository.AppendAuditAsync(c.Id, "DraftFailed", generator.DisplayName, result.Error ?? "Unknown error", ct);
            return WorkflowResult.Fail(result.Error ?? "Draft generator returned no draft.");
        }

        var proposal = latestInbound is not null
            ? result.Proposal with { Subject = ReplySubject(latestInbound.Subject) }
            : result.Proposal;

        var available = bundle.SuccessfulSourceIds()
            .Concat(extraSnapshots.Where(s => s.Status == LookupStatus.Success).Select(s => s.SourceId))
            .ToHashSet(StringComparer.Ordinal);
        var validation = DraftValidator.Validate(c.OrderId, proposal, available, c.Classification, bundle.Notes?.Data);
        if (!validation.IsValid)
        {
            var message = "Draft rejected by validation: " + string.Join(" ", validation.Errors);
            await repository.AppendAuditAsync(c.Id, "DraftRejectedByValidation", WorkflowOrigin, message, ct);
            return WorkflowResult.Fail(message, validation.Errors);
        }

        var current = await repository.GetCaseAsync(c.Id, ct);
        if (current is null) return WorkflowResult.Fail("Case not found.");

        var draft = new Draft(
            Guid.NewGuid(),
            c.Id,
            current.CurrentDraftVersion + 1,
            // The recipient always comes from the order record, never from customer input or model output.
            bundle.Order.Data!.CustomerEmail,
            proposal.Subject.Trim(),
            proposal.Body.Trim(),
            generator.Source,
            kind,
            proposal.EvidenceRefs,
            validation.Warnings,
            actor,
            time.GetUtcNow());

        var saved = await repository.SaveDraftAsync(draft, current.CurrentDraftVersion, allowedStates,
            $"{kind} draft v{draft.Version} created by {generator.DisplayName}", ct);
        return saved
            ? WorkflowResult.Ok($"Draft v{draft.Version} is awaiting review.")
            : WorkflowResult.Fail("The case changed while the draft was being generated. Reload and try again.");
    }

    private static EvidenceSnapshot ToSnapshot<T>(Guid caseId, string tool, LookupResult<T> r) where T : class
    {
        var envelope = new LookupEnvelope(r.Status, r.SourceId, r.RetrievedAtUtc,
            r.Data is null ? null : JsonSerializer.SerializeToElement(r.Data, LookupTools.Json), r.Error);
        return new EvidenceSnapshot(Guid.NewGuid(), caseId, tool, r.SourceId, r.Status, LookupTools.Serialize(envelope), r.RetrievedAtUtc);
    }

    private static string ActorOrDefault(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? "demo-reviewer" : actor.Trim()[..Math.Min(actor.Trim().Length, 64)];
}

public static class EvidenceBundleFactory
{
    /// <summary>Rebuilds evidence from stored snapshots (the latest one per tool).</summary>
    public static EvidenceBundle? FromSnapshots(IReadOnlyList<EvidenceSnapshot> snapshots)
    {
        EvidenceSnapshot? Latest(string tool) => snapshots.Where(s => s.ToolName == tool).OrderBy(s => s.RetrievedAtUtc).LastOrDefault();

        LookupResult<T>? Parse<T>(EvidenceSnapshot? s) where T : class
        {
            if (s is null) return null;
            var env = LookupTools.TryParse(s.PayloadJson);
            if (env is null) return LookupResult<T>.Failed(s.SourceId, s.RetrievedAtUtc, "Unreadable snapshot.");
            T? data = env.Status == LookupStatus.Success && env.Data is { } d ? d.Deserialize<T>(LookupTools.Json) : null;
            return new LookupResult<T>(data is null && env.Status == LookupStatus.Success ? LookupStatus.Error : env.Status, data, env.SourceId, env.RetrievedAtUtc, env.Error);
        }

        var order = Parse<OrderInfo>(Latest(LookupTools.GetOrder));
        if (order is null) return null;

        return new EvidenceBundle(
            order,
            Parse<DeliveryInfo>(Latest(LookupTools.GetDeliveryStatus)),
            Parse<CaseNotesInfo>(Latest(LookupTools.GetCaseNotes)),
            Parse<PolicyInfo>(Latest(LookupTools.GetDemoPolicy)));
    }
}
