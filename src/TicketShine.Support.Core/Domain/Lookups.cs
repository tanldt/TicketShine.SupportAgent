using System.Text.Json.Serialization;

namespace TicketShine.Support.Core.Domain;

/// <summary>Result of a lookup tool. NotFound/Error is never treated as successful data.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LookupStatus>))]
public enum LookupStatus
{
    Success,
    NotFound,
    Error,
}

public sealed record LookupResult<T>(
    LookupStatus Status,
    T? Data,
    string SourceId,
    DateTimeOffset RetrievedAtUtc,
    string? Error = null)
    where T : class
{
    public bool IsSuccess => Status == LookupStatus.Success && Data is not null;

    public static LookupResult<T> Failed(string sourceId, DateTimeOffset at, string error) =>
        new(LookupStatus.Error, null, sourceId, at, error);
}

public sealed record OrderInfo(
    string OrderId,
    string EventName,
    string Venue,
    DateTimeOffset EventTimeUtc,
    DateTimeOffset DeliveryDeadlineUtc,
    string OrderStatus,
    string CustomerReference,
    string CustomerName,
    string CustomerEmail,
    int TicketCount);

public sealed record DeliveryInfo(
    string OrderId,
    string Status,
    string Method,
    DateTimeOffset UpdatedAtUtc,
    string MaskedRecipient,
    string? Detail);

public sealed record CaseNote(DateTimeOffset AtUtc, string Author, string Text);

public sealed record CaseNotesInfo(string OrderId, IReadOnlyList<CaseNote> Notes);

public sealed record PolicyInfo(string PolicyId, string Topic, string Title, string Text, bool IsDemoPolicy);

/// <summary>Order IDs for a customer email (no other order data, to keep the search minimal).</summary>
public sealed record OrderMatchesInfo(string Email, IReadOnlyList<string> OrderIds);

public static class DeliveryStatuses
{
    public const string Delivered = "Delivered";
    public const string Pending = "Pending";
}

/// <summary>Trace of a tool call, stored for UI display and audit.</summary>
public sealed record ToolCallRecord(
    string ToolName,
    string ArgumentsJson,
    LookupStatus Status,
    long DurationMs,
    DateTimeOffset StartedAtUtc,
    string Origin,
    string? SourceId,
    string? Error);

public sealed record ToolInvocation<T>(LookupResult<T> Result, ToolCallRecord Trace) where T : class;
