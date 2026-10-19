using TicketShine.Support.Core.Workflow;

namespace TicketShine.Support.Core.Email;

public sealed class EmailIngestOptions
{
    public const string Section = "Mail";

    /// <summary>The support mailbox. Mail from this address is never turned into a case (prevents reply loops).</summary>
    public string SupportMailbox { get; set; } = "support@ticketshine.example";

    /// <summary>authserv-id whose Authentication-Results header is trusted (Gmail: mx.google.com).</summary>
    public string TrustedAuthServId { get; set; } = AuthenticationResultsParser.GmailAuthServId;
}

public sealed record EmailCaseRequest(
    InboundEmail Email,
    string Body,
    bool SenderAuthenticated,
    string AuthenticationSummary,
    IReadOnlyList<string> OrderIdsInMessage);

public static class IngestOutcomes
{
    public const string Duplicate = "Duplicate";
    public const string IgnoredAutomated = "Ignored: automated message";
    public const string IgnoredOwnMailbox = "Ignored: sent by the support mailbox";
    public const string CaseCreated = "CaseCreated";
    public const string AppendedToCase = "AppendedToCase";
    public const string Failed = "Failed";
}

public sealed record IngestResult(string Outcome, Guid? CaseId, string Message);

/// <summary>
/// Turns inbound emails into cases. Idempotent per provider message ID: the same email delivered twice
/// (webhook retry, worker restart) creates at most one case.
/// </summary>
public sealed class EmailIngestService(ICaseRepository repository, IMailStore mailStore, SupportWorkflow workflow, EmailIngestOptions options)
{
    public async Task<IngestResult> IngestAsync(InboundEmail email, CancellationToken ct = default)
    {
        if (!await mailStore.TryRecordInboundAsync(email, ct))
        {
            return new IngestResult(IngestOutcomes.Duplicate, null, "Already processed.");
        }

        IngestResult result;
        try
        {
            result = await ProcessAsync(email, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new IngestResult(IngestOutcomes.Failed, null, ex.Message);
        }

        await mailStore.CompleteInboundAsync(email.Provider, email.ProviderMessageId, result.Outcome, result.CaseId, ct);
        return result;
    }

    private async Task<IngestResult> ProcessAsync(InboundEmail email, CancellationToken ct)
    {
        var sender = EmailAddress.Normalize(email.FromAddress);
        if (sender == EmailAddress.Normalize(options.SupportMailbox))
        {
            return new IngestResult(IngestOutcomes.IgnoredOwnMailbox, null, "Message sent by the support mailbox.");
        }

        if (IsAutomated(email))
        {
            return new IngestResult(IngestOutcomes.IgnoredAutomated, null, "Auto-reply or bulk message; no case created.");
        }

        var auth = AuthenticationResultsParser.Parse(email.AuthenticationResults, options.TrustedAuthServId);
        var request = new EmailCaseRequest(
            email,
            email.TextBody,
            auth.IsAuthenticatedFor(sender),
            auth.Summary,
            OrderIdExtractor.FindAll(email.Subject, email.TextBody));

        // A reply on the thread of an open case belongs to that case.
        if (!string.IsNullOrEmpty(email.ThreadId) &&
            await repository.FindLatestCaseByThreadAsync(email.ThreadId, ct) is { IsTerminal: false } open)
        {
            var reply = await workflow.HandleCustomerReplyAsync(open.Id, request, ct);
            return new IngestResult(IngestOutcomes.AppendedToCase, open.Id, reply.Message);
        }

        var (caseId, created) = await workflow.CreateEmailCaseAsync(request, ct);
        return new IngestResult(IngestOutcomes.CaseCreated, caseId, created.Message);
    }

    /// <summary>RFC 3834 auto-replies and bulk mail must not create cases (and must never get an automatic answer).</summary>
    private static bool IsAutomated(InboundEmail email) =>
        (email.AutoSubmitted is { } auto && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase)) ||
        (email.Precedence is { } p && (p.Contains("bulk", StringComparison.OrdinalIgnoreCase) ||
                                       p.Contains("junk", StringComparison.OrdinalIgnoreCase) ||
                                       p.Contains("list", StringComparison.OrdinalIgnoreCase)));
}
