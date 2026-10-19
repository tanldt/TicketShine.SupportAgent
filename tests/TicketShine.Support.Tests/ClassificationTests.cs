using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Lookups;
using TicketShine.Support.Infrastructure.Mcp;

namespace TicketShine.Support.Tests;

/// <summary>Sample orders return the correct data and are classified by DemoClock.</summary>
public sealed class ClassificationTests(McpFixture mcp)
{
    [Theory]
    [InlineData("TS-10025", CaseState.AwaitingReview, Classification.InProgress)]
    [InlineData("TS-10026", CaseState.AwaitingReview, Classification.Delivered)]
    [InlineData("TS-10027", CaseState.NeedsHuman, Classification.Overdue)]
    [InlineData("TS-10028", CaseState.AwaitingReview, Classification.Delivered)]
    public async Task Sample_orders_are_classified_by_demo_clock(string orderId, CaseState expectedState, Classification expected)
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var id = await env.CreateCaseAsync(orderId);

        var details = (await env.Repository.GetDetailsAsync(id))!;
        Assert.Equal(expectedState, details.Case.State);
        Assert.Equal(expected, details.Case.Classification);
        Assert.Contains(details.Evidence, e => e.SourceId == $"order:{orderId}" && e.Status == LookupStatus.Success);
    }

    [Fact]
    public async Task In_progress_draft_uses_evidence_and_does_not_promise_a_time_or_refund()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var id = await env.CreateCaseAsync("TS-10025");

        var draft = (await env.Repository.GetDetailsAsync(id))!.CurrentDraft!;
        Assert.Equal(1, draft.Version);
        Assert.Equal("jamie.rivera@example.com", draft.Recipient);
        Assert.Contains("Sun, Oct 18 2026 at 07:00 UTC", draft.Body); // deadline comes from the data
        Assert.Contains("order:TS-10025", draft.EvidenceRefs);
        Assert.Contains("delivery:TS-10025", draft.EvidenceRefs);
        Assert.DoesNotContain("refund", draft.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DraftSource.Template, draft.Source);
    }

    [Fact]
    public async Task Delivered_draft_points_to_the_delivery_record()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var id = await env.CreateCaseAsync("TS-10026");

        var draft = (await env.Repository.GetDetailsAsync(id))!.CurrentDraft!;
        Assert.Contains("a***@example.com", draft.Body);
        Assert.Contains("Your tickets are ready", draft.Body);
    }

    [Fact]
    public async Task Same_order_becomes_overdue_when_demo_clock_passes_the_deadline()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway, demoNow: new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
        var id = await env.CreateCaseAsync("TS-10025");

        var c = (await env.Repository.GetCaseAsync(id))!;
        Assert.Equal(Classification.Overdue, c.Classification);
        Assert.Equal(CaseState.NeedsHuman, c.State);
    }

    [Fact]
    public async Task Unknown_order_reveals_nothing_and_needs_a_human()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var id = await env.CreateCaseAsync("TS-99999");

        var details = (await env.Repository.GetDetailsAsync(id))!;
        Assert.Equal(CaseState.NeedsHuman, details.Case.State);
        Assert.Equal(Classification.NotFound, details.Case.Classification);
        Assert.Empty(details.Drafts);
        // Only get_order is called; no other data is looked up.
        Assert.Single(details.ToolCalls);
        Assert.Equal(LookupTools.GetOrder, details.ToolCalls[0].Record.ToolName);

        var request = await env.Workflow.RequestDraftAsync(id, "test-agent");
        Assert.False(request.Succeeded);
    }

    [Fact]
    public async Task Mcp_failure_shows_lookup_failed_and_retry_recovers_without_inventing_status()
    {
        await using var gateway = new McpLookupGateway(mcp.Launch, TimeProvider.System); // dedicated server => first call fails
        await using var env = await TestEnv.CreateAsync(gateway);
        var id = await env.CreateCaseAsync("TS-10029");

        var failed = (await env.Repository.GetDetailsAsync(id))!;
        Assert.Equal(CaseState.LookupFailed, failed.Case.State);
        Assert.Empty(failed.Drafts);
        Assert.Contains(failed.ToolCalls, t => t.Record.ToolName == LookupTools.GetDeliveryStatus && t.Record.Status == LookupStatus.Error);

        var retry = await env.Workflow.RetryLookupAsync(id, "test-agent");
        Assert.True(retry.Succeeded, retry.Message);
        var recovered = (await env.Repository.GetDetailsAsync(id))!;
        Assert.Equal(CaseState.AwaitingReview, recovered.Case.State);
        Assert.Equal(Classification.InProgress, recovered.Case.Classification);
    }

    [Fact]
    public async Task Lookup_timeout_moves_case_to_lookup_failed()
    {
        await using var gateway = new McpLookupGateway(mcp.Launch with { ToolTimeout = TimeSpan.FromSeconds(1) }, TimeProvider.System);
        await using var env = await TestEnv.CreateAsync(gateway);
        var id = await env.CreateCaseAsync("TS-10030");

        var c = (await env.Repository.GetDetailsAsync(id))!;
        Assert.Equal(CaseState.LookupFailed, c.Case.State);
        Assert.Contains("Timed out", c.Case.StateReason);
        Assert.Empty(c.Drafts);
    }
}
