using System.Collections.Concurrent;
using System.Text.Json;

namespace TicketShine.Support.McpServer;

public sealed record SeedOrder(
    string OrderId,
    string EventName,
    string Venue,
    DateTimeOffset EventTimeUtc,
    DateTimeOffset DeliveryDeadlineUtc,
    string OrderStatus,
    string CustomerReference,
    string CustomerName,
    string CustomerEmail,
    int TicketCount,
    string? FaultMode);

public sealed record SeedDelivery(string OrderId, string Status, string Method, DateTimeOffset UpdatedAtUtc, string RecipientAddress, string? Detail);

public sealed record SeedNote(string OrderId, DateTimeOffset AtUtc, string Author, string Text);

public sealed record SeedPolicy(string PolicyId, string Topic, string Title, string Text);

public sealed record SeedFile(
    string SeedVersion,
    List<SeedOrder> Orders,
    List<SeedDelivery> Deliveries,
    List<SeedNote> CaseNotes,
    List<SeedPolicy> Policies);

/// <summary>Read-only fake data store. This is the "data adapter" to replace when integrating with the real system.</summary>
public sealed class DemoDataStore
{
    private readonly ConcurrentDictionary<string, int> _faultCounters = new(StringComparer.OrdinalIgnoreCase);

    private DemoDataStore(SeedFile seed)
    {
        Seed = seed;
        Orders = seed.Orders.ToDictionary(o => o.OrderId, StringComparer.OrdinalIgnoreCase);
        Deliveries = seed.Deliveries.ToDictionary(d => d.OrderId, StringComparer.OrdinalIgnoreCase);
        Policies = seed.Policies.ToDictionary(p => p.Topic, StringComparer.OrdinalIgnoreCase);
    }

    public SeedFile Seed { get; }

    public IReadOnlyDictionary<string, SeedOrder> Orders { get; }

    public IReadOnlyDictionary<string, SeedDelivery> Deliveries { get; }

    public IReadOnlyDictionary<string, SeedPolicy> Policies { get; }

    public IEnumerable<SeedNote> NotesFor(string orderId) =>
        Seed.CaseNotes.Where(n => string.Equals(n.OrderId, orderId, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.AtUtc);

    /// <summary>Call counter for the "error-alternate" fault: odd calls fail, even calls succeed.</summary>
    public int NextFaultAttempt(string key) => _faultCounters.AddOrUpdate(key, 1, (_, n) => n + 1);

    public static DemoDataStore Load(string path)
    {
        using var stream = File.OpenRead(path);
        var seed = JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? throw new InvalidOperationException($"Seed file '{path}' is empty.");
        return new DemoDataStore(seed);
    }
}
