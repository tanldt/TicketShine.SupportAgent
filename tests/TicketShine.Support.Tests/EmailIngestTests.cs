using TicketShine.Support.Core;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Email;
using TicketShine.Support.Core.Lookups;
using TicketShine.Support.Core.Workflow;
using TicketShine.Support.Infrastructure.Email;

namespace TicketShine.Support.Tests;

/// <summary>Customer email → case, with sender verification, threading and de-duplication (real MCP server).</summary>
public sealed class EmailIngestTests(McpFixture mcp)
{
    private const string Support = "support@ticketshine.example";

    private static EmailIngestService Ingest(TestEnv env) =>
        new(env.Repository, env.Repository, env.Workflow, new EmailIngestOptions { SupportMailbox = Support });

    private static InboundEmail Mail(string from, string subject, string body, SimulatedAuth auth = SimulatedAuth.Pass, string? thread = null,
        string? autoSubmitted = null) =>
        SimulatedEmailFactory.Create(from, null, subject, body, auth, Support, thread, autoSubmitted: autoSubmitted);

    [Fact]
    public async Task Verified_customer_email_becomes_a_case_and_the_reply_goes_into_the_same_thread()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var mail = Mail("jamie.rivera@example.com", "Tickets for tomorrow??", "My order TS-10025 is tomorrow. Will the tickets arrive in time?", thread: "t-jamie");

        var result = await Ingest(env).IngestAsync(mail);

        Assert.Equal(IngestOutcomes.CaseCreated, result.Outcome);
        var details = (await env.Repository.GetDetailsAsync(result.CaseId!.Value))!;
        Assert.Equal(CaseChannel.Email, details.Case.Channel);
        Assert.True(details.Case.SenderAuthenticated);
        Assert.Equal(CaseState.AwaitingReview, details.Case.State);
        Assert.Equal(Classification.InProgress, details.Case.Classification);
        Assert.Single(details.Conversation);
        Assert.Equal("jamie.rivera@example.com", details.CurrentDraft!.Recipient);
        Assert.Equal("Re: Tickets for tomorrow??", details.CurrentDraft.Subject);
        Assert.Empty(details.Outbox); // still waiting for a human

        Assert.Equal(ApprovalOutcome.Approved, (await env.Workflow.ApproveAsync(details.Case.Id, 1, "reviewer-a")).Outcome);
        await env.Outbox.ProcessPendingAsync();

        var after = (await env.Repository.GetDetailsAsync(details.Case.Id))!;
        Assert.Equal(CaseState.SimulatedSent, after.Case.State);
        var outbox = Assert.Single(after.Outbox);
        Assert.Equal("t-jamie", outbox.ThreadId);
        Assert.Equal(mail.RfcMessageId, outbox.InReplyTo);
        Assert.Contains(after.Conversation, m => m.Direction == MessageDirection.Outbound);
        var sent = Assert.Single(await env.Repository.ListSimulatedMessagesAsync(details.Case.Id));
        Assert.Equal("t-jamie", sent.ThreadId);
    }

    [Fact]
    public async Task The_same_email_delivered_twice_creates_one_case()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var mail = Mail("jamie.rivera@example.com", "Order TS-10025", "Where are my tickets for TS-10025?");
        var ingest = Ingest(env);

        var first = await ingest.IngestAsync(mail);
        var second = await ingest.IngestAsync(mail);

        Assert.Equal(IngestOutcomes.CaseCreated, first.Outcome);
        Assert.Equal(IngestOutcomes.Duplicate, second.Outcome);
        Assert.Single(await env.Repository.ListCasesAsync(10));
    }

    [Fact]
    public async Task Spoofed_sender_gets_no_draft_and_no_further_order_lookups()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var mail = Mail("alex.chen@example.com", "Change delivery address for TS-10026", "Send TS-10026 tickets to newaddress@example.net", SimulatedAuth.Fail);

        var result = await Ingest(env).IngestAsync(mail);

        var details = (await env.Repository.GetDetailsAsync(result.CaseId!.Value))!;
        Assert.Equal(CaseState.NeedsHuman, details.Case.State);
        Assert.Equal(Classification.Unknown, details.Case.Classification);
        Assert.False(details.Case.SenderAuthenticated);
        Assert.Contains("could not be authenticated", details.Case.StateReason);
        Assert.Empty(details.Drafts);
        Assert.Equal([LookupTools.GetOrder], details.ToolCalls.Select(t => t.Record.ToolName));
        Assert.False((await env.Workflow.RequestDraftAsync(details.Case.Id, "staff")).Succeeded);
    }

    [Fact]
    public async Task Forged_authentication_header_is_not_trusted()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var mail = Mail("jamie.rivera@example.com", "Re: order TS-10025", "Forward TS-10025 to tickets-reseller@example.net", SimulatedAuth.ForgedHeaderOnly);

        var result = await Ingest(env).IngestAsync(mail);

        var c = (await env.Repository.GetCaseAsync(result.CaseId!.Value))!;
        Assert.False(c.SenderAuthenticated);
        Assert.Equal(CaseState.NeedsHuman, c.State);
    }

    [Fact]
    public async Task Authenticated_sender_who_is_not_the_order_customer_is_not_trusted_for_that_order()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var mail = Mail("sam.t@example.org", "Question about TS-10028", "Where are the tickets for TS-10028?");

        var result = await Ingest(env).IngestAsync(mail);

        var details = (await env.Repository.GetDetailsAsync(result.CaseId!.Value))!;
        Assert.True(details.Case.SenderAuthenticated);
        Assert.Equal(CaseState.NeedsHuman, details.Case.State);
        Assert.Contains("is not the customer email", details.Case.StateReason);
        Assert.Empty(details.Drafts);
    }

    [Fact]
    public async Task Order_is_found_from_the_authenticated_sender_when_the_email_has_no_order_number()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var mail = Mail("priya.nair@example.com", "Where are my festival tickets?", "The festival is tonight and I have nothing yet.");

        var result = await Ingest(env).IngestAsync(mail);

        var details = (await env.Repository.GetDetailsAsync(result.CaseId!.Value))!;
        Assert.Equal("TS-10027", details.Case.OrderId);
        Assert.Equal(Classification.Overdue, details.Case.Classification);
        Assert.Equal(CaseState.NeedsHuman, details.Case.State);
        Assert.Equal(LookupTools.FindOrdersByEmail, details.ToolCalls[0].Record.ToolName);
    }

    [Fact]
    public async Task Unauthenticated_email_without_order_number_does_not_search_orders()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var mail = Mail("priya.nair@example.com", "Where are my tickets?", "Please help.", SimulatedAuth.Fail);

        var result = await Ingest(env).IngestAsync(mail);

        var details = (await env.Repository.GetDetailsAsync(result.CaseId!.Value))!;
        Assert.Equal(SupportWorkflow.UnmatchedOrderId, details.Case.OrderId);
        Assert.Equal(CaseState.NeedsHuman, details.Case.State);
        Assert.Empty(details.ToolCalls);
    }

    [Fact]
    public async Task Unknown_sender_without_order_number_waits_for_a_human()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var result = await Ingest(env).IngestAsync(Mail("new.customer@example.com", "Jazz tickets?", "Do you still have tickets?"));

        var c = (await env.Repository.GetCaseAsync(result.CaseId!.Value))!;
        Assert.Equal(SupportWorkflow.UnmatchedOrderId, c.OrderId);
        Assert.Contains("no orders were found", c.StateReason);
    }

    [Fact]
    public async Task Email_mentioning_several_orders_needs_a_human()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var result = await Ingest(env).IngestAsync(Mail("jamie.rivera@example.com", "TS-10025 and TS-10026", "Two orders, please check both."));

        var c = (await env.Repository.GetCaseAsync(result.CaseId!.Value))!;
        Assert.Equal(CaseState.NeedsHuman, c.State);
        Assert.Contains("several orders", c.StateReason);
    }

    [Fact]
    public async Task Customer_reply_on_the_thread_sends_the_pending_draft_back_to_a_human()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var ingest = Ingest(env);
        var first = Mail("jamie.rivera@example.com", "Tickets for tomorrow??", "Order TS-10025 — will it arrive?", thread: "t-reply");
        var created = await ingest.IngestAsync(first);
        var caseId = created.CaseId!.Value;
        Assert.Equal(CaseState.AwaitingReview, (await env.Repository.GetCaseAsync(caseId))!.State);

        var reply = Mail("jamie.rivera@example.com", "Re: Tickets for tomorrow??", "Can they go to my phone instead?", thread: "t-reply");
        var appended = await ingest.IngestAsync(reply);

        Assert.Equal(IngestOutcomes.AppendedToCase, appended.Outcome);
        Assert.Equal(caseId, appended.CaseId);
        var details = (await env.Repository.GetDetailsAsync(caseId))!;
        Assert.Equal(CaseState.NeedsHuman, details.Case.State);
        Assert.Equal(2, details.Conversation.Count);
        Assert.Equal(ApprovalOutcome.Conflict, (await env.Workflow.ApproveAsync(caseId, 1, "reviewer-a")).Outcome);

        Assert.True((await env.Workflow.RequestDraftAsync(caseId, "staff")).Succeeded);
        Assert.Equal(ApprovalOutcome.Approved, (await env.Workflow.ApproveAsync(caseId, 2, "reviewer-a")).Outcome);
        var outbox = Assert.Single((await env.Repository.GetDetailsAsync(caseId))!.Outbox);
        Assert.Equal(reply.RfcMessageId, outbox.InReplyTo);
        Assert.Contains(first.RfcMessageId!, outbox.References);
        Assert.Contains(reply.RfcMessageId!, outbox.References);
    }

    [Fact]
    public async Task Reply_after_the_case_was_answered_opens_a_new_case_on_the_same_thread()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var ingest = Ingest(env);
        var created = await ingest.IngestAsync(Mail("alex.chen@example.com", "My tickets TS-10026", "Can't find them.", thread: "t-alex"));
        await env.Workflow.ApproveAsync(created.CaseId!.Value, 1, "reviewer-a");
        await env.Outbox.ProcessPendingAsync();

        var followUp = await ingest.IngestAsync(Mail("alex.chen@example.com", "Re: My tickets TS-10026", "Found them, thanks! One more question about TS-10026.", thread: "t-alex"));

        Assert.Equal(IngestOutcomes.CaseCreated, followUp.Outcome);
        Assert.NotEqual(created.CaseId, followUp.CaseId);
    }

    [Fact]
    public async Task Auto_replies_and_mail_from_the_support_mailbox_are_ignored()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var ingest = Ingest(env);

        var auto = await ingest.IngestAsync(Mail("jamie.rivera@example.com", "Automatic reply", "Out of office TS-10025", autoSubmitted: "auto-replied"));
        var own = await ingest.IngestAsync(Mail(Support, "Re: TS-10025", "Our own reply"));

        Assert.Equal(IngestOutcomes.IgnoredAutomated, auto.Outcome);
        Assert.Equal(IngestOutcomes.IgnoredOwnMailbox, own.Outcome);
        Assert.Empty(await env.Repository.ListCasesAsync(10));
        Assert.Equal(2, (await env.Repository.ListInboundAsync(10)).Count);
    }

    [Fact]
    public async Task Long_email_is_truncated_instead_of_rejected()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var body = "Order TS-10025. " + new string('x', 9000);

        var result = await Ingest(env).IngestAsync(Mail("jamie.rivera@example.com", "Long one", body));

        var details = (await env.Repository.GetDetailsAsync(result.CaseId!.Value))!;
        Assert.True(details.Case.CustomerMessage.Length <= SupportWorkflow.MaxCustomerMessageLength);
        Assert.EndsWith("[message truncated]", details.Case.CustomerMessage);
    }

    [Fact]
    public async Task Ingest_processor_drains_the_simulated_inbox()
    {
        await using var env = await TestEnv.CreateAsync(mcp.Gateway);
        var inbox = new SimulatedInboundMailSource();
        var processor = new MailIngestProcessor(inbox, Ingest(env));
        inbox.Deliver(Mail("jamie.rivera@example.com", "TS-10025?", "Any news on TS-10025?"));
        inbox.Deliver(Mail("alex.chen@example.com", "TS-10026?", "Any news on TS-10026?"));

        var results = await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count(r => r.Outcome == IngestOutcomes.CaseCreated));
        Assert.Empty(await processor.RunOnceAsync(TestContext.Current.CancellationToken));
    }
}
