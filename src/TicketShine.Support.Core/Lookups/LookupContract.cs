using System.Text.Json;
using System.Text.Json.Serialization;
using TicketShine.Support.Core.Domain;

namespace TicketShine.Support.Core.Lookups;

/// <summary>
/// Contract shared between the MCP server and client. Each tool returns a JSON envelope:
/// status + source identifier + retrieved-at + data or error.
/// </summary>
public sealed record LookupEnvelope(
    LookupStatus Status,
    string SourceId,
    DateTimeOffset RetrievedAtUtc,
    JsonElement? Data,
    string? Error);

public static class LookupTools
{
    public const string GetOrder = "get_order";
    public const string GetDeliveryStatus = "get_delivery_status";
    public const string GetCaseNotes = "get_case_notes";
    public const string GetDemoPolicy = "get_demo_policy";

    /// <summary>
    /// Finds order IDs whose customer email matches. Used only by email ingestion, and only after the sender
    /// has been authenticated (DMARC/DKIM). It is not offered to the model.
    /// </summary>
    public const string FindOrdersByEmail = "find_orders_by_email";

    /// <summary>The single allowlist. There are no tools to send replies, approve, refund, cancel, or run SQL.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        GetOrder, GetDeliveryStatus, GetCaseNotes, GetDemoPolicy, FindOrdersByEmail,
    };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(LookupEnvelope envelope) => JsonSerializer.Serialize(envelope, Json);

    public static LookupEnvelope? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LookupEnvelope>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record RawToolInvocation(LookupEnvelope Envelope, ToolCallRecord Trace, string RawText);

/// <summary>Lookup gateway. The real implementation goes through MCP stdio; the agent never calls the repository directly.</summary>
public interface ILookupGateway
{
    Task<IReadOnlyList<string>> ListToolNamesAsync(CancellationToken cancellationToken);

    Task<RawToolInvocation> CallAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        string origin,
        CancellationToken cancellationToken);
}

public static class LookupGatewayExtensions
{
    public static Task<ToolInvocation<OrderInfo>> GetOrderAsync(this ILookupGateway gateway, string orderId, string origin, CancellationToken ct) =>
        gateway.CallTypedAsync<OrderInfo>(LookupTools.GetOrder, new Dictionary<string, object?> { ["orderId"] = orderId }, origin, ct);

    public static Task<ToolInvocation<DeliveryInfo>> GetDeliveryStatusAsync(this ILookupGateway gateway, string orderId, string origin, CancellationToken ct) =>
        gateway.CallTypedAsync<DeliveryInfo>(LookupTools.GetDeliveryStatus, new Dictionary<string, object?> { ["orderId"] = orderId }, origin, ct);

    public static Task<ToolInvocation<CaseNotesInfo>> GetCaseNotesAsync(this ILookupGateway gateway, string orderId, string origin, CancellationToken ct) =>
        gateway.CallTypedAsync<CaseNotesInfo>(LookupTools.GetCaseNotes, new Dictionary<string, object?> { ["orderId"] = orderId }, origin, ct);

    public static Task<ToolInvocation<PolicyInfo>> GetDemoPolicyAsync(this ILookupGateway gateway, string topic, string origin, CancellationToken ct) =>
        gateway.CallTypedAsync<PolicyInfo>(LookupTools.GetDemoPolicy, new Dictionary<string, object?> { ["topic"] = topic }, origin, ct);

    public static Task<ToolInvocation<OrderMatchesInfo>> FindOrdersByEmailAsync(this ILookupGateway gateway, string email, string origin, CancellationToken ct) =>
        gateway.CallTypedAsync<OrderMatchesInfo>(LookupTools.FindOrdersByEmail, new Dictionary<string, object?> { ["email"] = email }, origin, ct);

    public static async Task<ToolInvocation<T>> CallTypedAsync<T>(
        this ILookupGateway gateway,
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        string origin,
        CancellationToken ct) where T : class
    {
        var raw = await gateway.CallAsync(toolName, args, origin, ct);
        var env = raw.Envelope;
        if (env.Status != LookupStatus.Success)
        {
            return new(new LookupResult<T>(env.Status, null, env.SourceId, env.RetrievedAtUtc, env.Error), raw.Trace);
        }

        T? data = null;
        try
        {
            data = env.Data is { } element ? element.Deserialize<T>(LookupTools.Json) : null;
        }
        catch (JsonException)
        {
            data = null;
        }

        if (data is null)
        {
            // Malformed data is never treated as success.
            var trace = raw.Trace with { Status = LookupStatus.Error, Error = "Malformed lookup payload." };
            return new(LookupResult<T>.Failed(env.SourceId, env.RetrievedAtUtc, "Malformed lookup payload."), trace);
        }

        return new(new LookupResult<T>(LookupStatus.Success, data, env.SourceId, env.RetrievedAtUtc), raw.Trace);
    }
}
