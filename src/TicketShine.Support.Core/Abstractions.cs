using TicketShine.Support.Core.Domain;

namespace TicketShine.Support.Core;

/// <summary>Fixed business clock so classification does not depend on the machine's current date.</summary>
public interface IDemoClock
{
    DateTimeOffset Now { get; }
}

public sealed class FixedDemoClock(DateTimeOffset now) : IDemoClock
{
    public static readonly DateTimeOffset Default = new(2026, 10, 17, 18, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now { get; } = now;
}

public enum ApprovalOutcome
{
    Approved,
    Rejected,
    Conflict,
    NotFound,
}

public sealed record ReviewResult(ApprovalOutcome Outcome, string Message);

public sealed record NewCaseRequest(string OrderId, string CustomerMessage, string Actor);

/// <summary>Persistence. All state-changing operations are conditional (state + version) and run in a transaction.</summary>
public interface ICaseRepository
{
    Task InitializeAsync(CancellationToken ct = default);

    Task CreateCaseAsync(SupportCase supportCase, string actor, CancellationToken ct = default);

    Task<SupportCase?> GetCaseAsync(Guid caseId, CancellationToken ct = default);

    Task<CaseDetails?> GetDetailsAsync(Guid caseId, CancellationToken ct = default);

    Task<IReadOnlyList<SupportCase>> ListCasesAsync(int limit, CancellationToken ct = default);

    /// <summary>Transitions state if the current state is in <paramref name="from"/>. Returns false if it does not match.</summary>
    Task<bool> TransitionAsync(
        Guid caseId,
        IReadOnlyCollection<CaseState> from,
        CaseState to,
        Classification? classification,
        string? reason,
        string actor,
        string auditDetail,
        CancellationToken ct = default);

    Task SaveLookupDataAsync(
        Guid caseId,
        IReadOnlyList<EvidenceSnapshot> evidence,
        IReadOnlyList<ToolCallRecord> toolCalls,
        CancellationToken ct = default);

    /// <summary>
    /// Saves draft version = expectedCurrentVersion + 1 and moves the case to AwaitingReview,
    /// only if the state is in <paramref name="allowedStates"/> and the current version matches.
    /// </summary>
    Task<bool> SaveDraftAsync(
        Draft draft,
        int expectedCurrentVersion,
        IReadOnlyCollection<CaseState> allowedStates,
        string auditDetail,
        CancellationToken ct = default);

    Task<ReviewResult> ApproveAsync(Guid caseId, int draftVersion, string reviewerLabel, DateTimeOffset at, CancellationToken ct = default);

    Task<ReviewResult> RejectAsync(Guid caseId, int draftVersion, string reviewerLabel, string? reason, DateTimeOffset at, CancellationToken ct = default);

    Task<IReadOnlyList<OutboxMessage>> GetPendingOutboxAsync(CancellationToken ct = default);

    /// <summary>Writes to the simulated mailbox by idempotency key. Returns false if the key already exists.</summary>
    Task<bool> WriteSimulatedMessageAsync(SimulatedMessage message, CancellationToken ct = default);

    /// <summary>
    /// Marks the outbox job as sent, records the outbound message in the conversation and moves the case to
    /// SimulatedSent (simulated sender) or Sent (real sender).
    /// </summary>
    Task<bool> MarkOutboxSentAsync(Guid outboxId, DateTimeOffset at, string senderName, bool simulated, string? providerMessageId, CancellationToken ct = default);

    Task AddCaseMessageAsync(CaseMessage message, CancellationToken ct = default);

    /// <summary>Most recent case for a mail thread (any state), or null.</summary>
    Task<SupportCase?> FindLatestCaseByThreadAsync(string threadId, CancellationToken ct = default);

    Task<IReadOnlyList<SimulatedMessage>> ListSimulatedMessagesAsync(Guid? caseId = null, CancellationToken ct = default);

    Task AppendAuditAsync(Guid caseId, string eventType, string actor, string detail, CancellationToken ct = default);
}

public sealed record SendResult(bool Delivered, string? ProviderMessageId, string? Error)
{
    public static SendResult Ok(string? providerMessageId = null) => new(true, providerMessageId, null);

    public static SendResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Sends an approved reply. Implementations: the simulated mailbox (default) or Gmail.
/// Must be idempotent per <see cref="OutboxMessage.IdempotencyKey"/>.
/// </summary>
public interface IReplySender
{
    string Name { get; }

    bool IsSimulated { get; }

    Task<SendResult> SendAsync(OutboxMessage message, CancellationToken ct = default);
}

/// <summary>Notifies the worker that a new outbox job exists (optional).</summary>
public interface IOutboxSignal
{
    void Notify();
}

public sealed class NoopOutboxSignal : IOutboxSignal
{
    public void Notify() { }
}
