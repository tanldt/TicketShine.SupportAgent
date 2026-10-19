namespace TicketShine.Support.Core.Domain;

public enum CaseState
{
    Created,
    LookingUp,
    AwaitingReview,
    NeedsHuman,
    LookupFailed,
    Approved,
    SimulatedSent,
    Rejected,

    /// <summary>Sent through a real channel (for example Gmail). Only used when a real sender is configured.</summary>
    Sent,
}

/// <summary>How the case entered the system.</summary>
public enum CaseChannel
{
    Manual,
    Email,
}

public enum MessageDirection
{
    Inbound,
    Outbound,
}

public enum Classification
{
    Unknown,
    InProgress,
    Delivered,
    Overdue,
    NotFound,
}

public enum DraftSource
{
    Template,
    LiveAi,
    StaffEdit,
}

public enum DraftKind
{
    Standard,
    Holding,
}

public sealed record SupportCase(
    Guid Id,
    string OrderId,
    string CustomerMessage,
    CaseState State,
    Classification Classification,
    string? StateReason,
    int CurrentDraftVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CaseChannel Channel = CaseChannel.Manual,
    string? ChannelThreadId = null,
    string? SenderEmail = null,
    bool SenderAuthenticated = false)
{
    public bool IsTerminal => State is CaseState.SimulatedSent or CaseState.Sent or CaseState.Rejected;
}

/// <summary>One email in a case conversation (inbound from the customer or the reply we sent).</summary>
public sealed record CaseMessage(
    Guid Id,
    Guid CaseId,
    MessageDirection Direction,
    string Provider,
    string? ProviderMessageId,
    string? RfcMessageId,
    string FromAddress,
    string ToAddress,
    string Subject,
    string Body,
    string? AuthenticationSummary,
    DateTimeOffset AtUtc);

public sealed record Draft(
    Guid Id,
    Guid CaseId,
    int Version,
    string Recipient,
    string Subject,
    string Body,
    DraftSource Source,
    DraftKind Kind,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> Warnings,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record EvidenceSnapshot(
    Guid Id,
    Guid CaseId,
    string ToolName,
    string SourceId,
    LookupStatus Status,
    string PayloadJson,
    DateTimeOffset RetrievedAtUtc);

public sealed record StoredToolCall(Guid Id, Guid CaseId, ToolCallRecord Record);

public sealed record ApprovalEvent(
    Guid Id,
    Guid CaseId,
    Guid DraftId,
    int DraftVersion,
    string Decision,
    string ReviewerLabel,
    string? Reason,
    DateTimeOffset AtUtc);

public sealed record OutboxMessage(
    Guid Id,
    Guid CaseId,
    Guid DraftId,
    int DraftVersion,
    string IdempotencyKey,
    string Recipient,
    string Subject,
    string Body,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SentAtUtc,
    string? ThreadId = null,
    string? InReplyTo = null,
    string? References = null);

public sealed record SimulatedMessage(
    string IdempotencyKey,
    Guid CaseId,
    string Recipient,
    string Subject,
    string Body,
    DateTimeOffset WrittenAtUtc,
    string? ThreadId = null,
    string? InReplyTo = null);

public sealed record AuditEvent(
    long Id,
    Guid CaseId,
    string EventType,
    string Actor,
    string Detail,
    DateTimeOffset AtUtc);

public static class OutboxStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
}

/// <summary>All information about a case for display in the workspace.</summary>
public sealed record CaseDetails(
    SupportCase Case,
    IReadOnlyList<EvidenceSnapshot> Evidence,
    IReadOnlyList<StoredToolCall> ToolCalls,
    IReadOnlyList<Draft> Drafts,
    IReadOnlyList<ApprovalEvent> Approvals,
    IReadOnlyList<OutboxMessage> Outbox,
    IReadOnlyList<AuditEvent> Audit,
    IReadOnlyList<CaseMessage>? Messages = null)
{
    public IReadOnlyList<CaseMessage> Conversation => Messages ?? [];

    public CaseMessage? LatestInbound => Conversation.Where(m => m.Direction == MessageDirection.Inbound).MaxBy(m => m.AtUtc);

    public Draft? CurrentDraft => Drafts.FirstOrDefault(d => d.Version == Case.CurrentDraftVersion);
}
