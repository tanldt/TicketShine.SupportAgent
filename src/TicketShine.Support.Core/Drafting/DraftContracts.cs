using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Lookups;

namespace TicketShine.Support.Core.Drafting;

/// <summary>Evidence looked up for a case, used to compose the draft.</summary>
public sealed record EvidenceBundle(
    LookupResult<OrderInfo> Order,
    LookupResult<DeliveryInfo>? Delivery,
    LookupResult<CaseNotesInfo>? Notes,
    LookupResult<PolicyInfo>? Policy)
{
    public IEnumerable<string> SuccessfulSourceIds()
    {
        if (Order.IsSuccess) yield return Order.SourceId;
        if (Delivery is { IsSuccess: true }) yield return Delivery.SourceId;
        if (Notes is { IsSuccess: true }) yield return Notes.SourceId;
        if (Policy is { IsSuccess: true }) yield return Policy.SourceId;
    }
}

public sealed record DraftContext(
    Guid CaseId,
    string OrderId,
    string CustomerMessage,
    Classification Classification,
    DraftKind Kind,
    EvidenceBundle Evidence,
    DateTimeOffset Now,
    ILookupGateway Lookups);

public sealed record DraftProposal(string Subject, string Body, IReadOnlyList<string> EvidenceRefs);

public sealed record DraftGenerationResult(
    bool Succeeded,
    DraftProposal? Proposal,
    string? Error,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<RawToolInvocation> ExtraEvidence)
{
    public static DraftGenerationResult Ok(DraftProposal p, IReadOnlyList<ToolCallRecord>? calls = null, IReadOnlyList<RawToolInvocation>? extra = null) =>
        new(true, p, null, calls ?? [], extra ?? []);

    public static DraftGenerationResult Fail(string error, IReadOnlyList<ToolCallRecord>? calls = null) =>
        new(false, null, error, calls ?? [], []);
}

public interface IReplyDraftGenerator
{
    DraftSource Source { get; }

    /// <summary>Label shown in the UI, e.g. "Template demo — no LLM".</summary>
    string DisplayName { get; }

    Task<DraftGenerationResult> GenerateAsync(DraftContext context, CancellationToken ct);
}
