using System.Text.RegularExpressions;

namespace TicketShine.Support.Core.Email;

/// <summary>
/// A customer email as received from a mail provider (Gmail or the simulated inbox).
/// Everything in here is untrusted input, including the From header.
/// </summary>
public sealed record InboundEmail(
    string Provider,
    string ProviderMessageId,
    string? ThreadId,
    string? RfcMessageId,
    string? InReplyTo,
    string? References,
    string FromAddress,
    string? FromName,
    string ToAddress,
    string Subject,
    string TextBody,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<string> AuthenticationResults,
    string? AutoSubmitted = null,
    string? Precedence = null);

public sealed record InboundRecord(
    string Provider,
    string ProviderMessageId,
    string? ThreadId,
    string FromAddress,
    string Subject,
    DateTimeOffset ReceivedAtUtc,
    string Outcome,
    Guid? CaseId,
    DateTimeOffset? ProcessedAtUtc);

/// <summary>Source of new inbound emails (Gmail API or the simulated inbox).</summary>
public interface IInboundMailSource
{
    string Provider { get; }

    Task<IReadOnlyList<InboundEmail>> FetchNewAsync(CancellationToken ct);

    /// <summary>Lets the provider record that a message was processed (Gmail: adds a label).</summary>
    Task MarkProcessedAsync(InboundEmail email, string outcome, CancellationToken ct);
}

/// <summary>Storage for inbound message de-duplication and provider sync state.</summary>
public interface IMailStore
{
    /// <summary>Records the message. Returns false if this provider message ID was already recorded.</summary>
    Task<bool> TryRecordInboundAsync(InboundEmail email, CancellationToken ct = default);

    Task CompleteInboundAsync(string provider, string providerMessageId, string outcome, Guid? caseId, CancellationToken ct = default);

    Task<IReadOnlyList<InboundRecord>> ListInboundAsync(int limit, CancellationToken ct = default);

    Task<string?> GetSyncStateAsync(string key, CancellationToken ct = default);

    Task SetSyncStateAsync(string key, string value, CancellationToken ct = default);
}

public static partial class OrderIdExtractor
{
    [GeneratedRegex(@"\bTS-\d{3,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static IReadOnlyList<string> FindAll(params string?[] texts) =>
        texts.Where(t => !string.IsNullOrEmpty(t))
            .SelectMany(t => Pattern().Matches(t!).Select(m => m.Value.ToUpperInvariant()))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

public static class EmailAddress
{
    public static string Normalize(string? address) => (address ?? string.Empty).Trim().Trim('<', '>').ToLowerInvariant();

    public static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address[(at + 1)..].Trim().ToLowerInvariant();
    }
}
