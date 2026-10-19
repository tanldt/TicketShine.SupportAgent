using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Lookups;
using TicketShine.Support.Infrastructure.Mcp;

namespace TicketShine.Support.Tests;

/// <summary>MCP client lists/calls tools over stdio against the real server (child process).</summary>
public sealed class McpStdioTests(McpFixture mcp)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_exactly_the_lookup_allowlist_and_nothing_that_sends_or_changes_state()
    {
        var tools = await mcp.Gateway.ListToolNamesAsync(Ct);

        Assert.Equal(LookupTools.Allowed.OrderBy(n => n, StringComparer.Ordinal), tools);
        Assert.DoesNotContain(tools, t => t.Contains("send") || t.Contains("approve") || t.Contains("refund") || t.Contains("cancel") || t.Contains("sql"));
    }

    [Fact]
    public async Task Get_order_returns_structured_data_with_source_and_retrieved_at()
    {
        var order = await mcp.Gateway.GetOrderAsync("TS-10025", "test", Ct);

        Assert.Equal(LookupStatus.Success, order.Result.Status);
        Assert.Equal("order:TS-10025", order.Result.SourceId);
        Assert.Equal("jamie.rivera@example.com", order.Result.Data!.CustomerEmail);
        Assert.True(order.Result.RetrievedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(LookupTools.GetOrder, order.Trace.ToolName);
    }

    [Fact]
    public async Task Delivery_lookup_masks_the_recipient_address()
    {
        var delivery = await mcp.Gateway.GetDeliveryStatusAsync("TS-10026", "test", Ct);

        Assert.True(delivery.Result.IsSuccess);
        Assert.Equal("a***@example.com", delivery.Result.Data!.MaskedRecipient);
    }

    [Fact]
    public async Task Not_found_is_never_turned_into_success()
    {
        var order = await mcp.Gateway.GetOrderAsync("TS-99999", "test", Ct);

        Assert.Equal(LookupStatus.NotFound, order.Result.Status);
        Assert.False(order.Result.IsSuccess);
        Assert.Null(order.Result.Data);
    }

    [Fact]
    public async Task Timeout_is_reported_as_error_not_success()
    {
        var shortTimeout = mcp.Launch with { ToolTimeout = TimeSpan.FromSeconds(1) };
        await using var gateway = new McpLookupGateway(shortTimeout, TimeProvider.System);

        var delivery = await gateway.GetDeliveryStatusAsync("TS-10030", "test", Ct);

        Assert.Equal(LookupStatus.Error, delivery.Result.Status);
        Assert.Null(delivery.Result.Data);
        Assert.Contains("Timed out", delivery.Result.Error);
        Assert.Equal(LookupStatus.Error, delivery.Trace.Status);
    }

    [Fact]
    public async Task Simulated_server_error_is_reported_as_error()
    {
        await using var gateway = new McpLookupGateway(mcp.Launch, TimeProvider.System); // new server => fresh fault counter
        var first = await gateway.GetDeliveryStatusAsync("TS-10029", "test", Ct);
        var second = await gateway.GetDeliveryStatusAsync("TS-10029", "test", Ct);

        Assert.Equal(LookupStatus.Error, first.Result.Status);
        Assert.Null(first.Result.Data);
        Assert.Equal(LookupStatus.Success, second.Result.Status);
    }

    [Fact]
    public async Task Gateway_refuses_tools_outside_the_allowlist_without_calling_the_server()
    {
        var result = await mcp.Gateway.CallAsync("send_reply", new Dictionary<string, object?> { ["orderId"] = "TS-10025" }, "test", Ct);

        Assert.Equal(LookupStatus.Error, result.Envelope.Status);
        Assert.Contains("allowlist", result.Envelope.Error);
    }

    [Fact]
    public void Child_process_environment_does_not_include_secrets()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "should-not-leak");
        try
        {
            var launch = McpServerLocator.Resolve(new McpServerOptions());
            Assert.DoesNotContain(launch.Environment.Keys, k => k.Contains("ANTHROPIC", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }
}
