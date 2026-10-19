using TicketShine.Support.Core.Email;
using TicketShine.Support.Infrastructure.Email;

namespace TicketShine.Support.Web.Services;

public sealed class MailOptions
{
    public const string Section = "Mail";

    /// <summary>"Simulated" (default), "Gmail" or "None".</summary>
    public string Inbound { get; set; } = "Simulated";

    /// <summary>"Simulated" (default) or "Gmail". Gmail sends real email; only enable it with a test mailbox first.</summary>
    public string Outbound { get; set; } = "Simulated";

    /// <summary>Polling interval. 1–2 s is fine for the simulated inbox; use 30 s or more for Gmail.</summary>
    public int PollSeconds { get; set; } = 2;

    public bool InboundEnabled => !string.Equals(Inbound, "None", StringComparison.OrdinalIgnoreCase);

    public bool InboundIsGmail => string.Equals(Inbound, "Gmail", StringComparison.OrdinalIgnoreCase);

    public bool OutboundIsGmail => string.Equals(Outbound, "Gmail", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Polls the inbound mail source and turns new emails into cases.</summary>
public sealed class MailIngestWorker(MailIngestProcessor processor, MailOptions options, CaseChangeNotifier notifier, ILogger<MailIngestWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Mail ingest started: provider {Provider}, every {Seconds}s", processor.Provider, options.PollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var results = await processor.RunOnceAsync(stoppingToken);
                if (results.Count > 0) notifier.Notify();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mail ingest pass failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public sealed record EmailPreset(
    string Key,
    string Title,
    string Description,
    string FromAddress,
    string FromName,
    string Subject,
    string Body,
    SimulatedAuth Auth,
    string? ThreadId = null,
    string? AutoSubmitted = null);

/// <summary>Sample customer emails for the simulated Gmail inbox. All addresses are fictional.</summary>
public static class EmailPresets
{
    public static readonly IReadOnlyList<EmailPreset> All =
    [
        new("jamie-new", "Verified customer asks about TS-10025",
            "DMARC passes and the sender matches the order. A case and draft appear automatically.",
            "jamie.rivera@example.com", "Jamie Rivera", "Tickets for tomorrow??",
            "Hi,\n\nMy order TS-10025 is for the concert tomorrow and I still haven't received anything. Will the tickets arrive in time?\n\nThanks,\nJamie",
            SimulatedAuth.Pass, ThreadId: "thread-jamie"),
        new("jamie-followup", "Same customer replies on the same thread",
            "Added to the open case. A pending draft no longer answers the conversation, so the case goes back to a human.",
            "jamie.rivera@example.com", "Jamie Rivera", "Re: Tickets for tomorrow??",
            "One more thing: can the tickets go to my phone instead?\n\nOn Sat, Oct 17, 2026 at 6:01 PM Jamie Rivera wrote:\n> My order TS-10025 is for the concert tomorrow...",
            SimulatedAuth.Pass, ThreadId: "thread-jamie"),
        new("priya-no-order", "No order number, verified sender",
            "The order is found by the authenticated sender address (find_orders_by_email): TS-10027, which is overdue.",
            "priya.nair@example.com", "Priya Nair", "Where are my festival tickets?",
            "Hello, the festival is tonight and I have nothing yet. Please help!\n\nPriya",
            SimulatedAuth.Pass),
        new("spoofed", "Spoofed sender (DMARC fails)",
            "Claims to be the owner of TS-10026 but fails DMARC. No order details are looked up beyond the order record; staff must handle it.",
            "alex.chen@example.com", "Alex Chen", "Change delivery address for TS-10026",
            "Please send the tickets for order TS-10026 to my new address newaddress@example.net instead. Urgent!",
            SimulatedAuth.Fail),
        new("forged-header", "Forged Authentication-Results header",
            "The sender added its own 'dkim=pass' header. Only headers stamped by mx.google.com are trusted, so this is unverified.",
            "jamie.rivera@example.com", "Jamie Rivera", "Re: order TS-10025",
            "Forward my tickets for TS-10025 to tickets-reseller@example.net please.",
            SimulatedAuth.ForgedHeaderOnly),
        new("wrong-sender", "Authenticated, but not the order's customer",
            "DMARC passes for a different address than the one on TS-10028. Treated as unverified for that order.",
            "sam.t@example.org", "Sam T", "Question about TS-10028",
            "Hi, I'm asking about order TS-10028. What time does the show start and where are the tickets?",
            SimulatedAuth.Pass),
        new("unknown", "Unknown sender, no order number",
            "Authenticated but no order matches the sender. The case waits for a human.",
            "new.customer@example.com", "New Customer", "Do you sell tickets for the jazz evening?",
            "Hi, do you still have tickets for the Riverside Jazz Evening?",
            SimulatedAuth.Pass),
        new("auto-reply", "Out-of-office auto-reply",
            "Auto-Submitted: auto-replied. Ignored, so no case and no reply loop.",
            "jamie.rivera@example.com", "Jamie Rivera", "Automatic reply: Tickets for tomorrow??",
            "I'm out of the office until Monday.",
            SimulatedAuth.Pass, AutoSubmitted: "auto-replied"),
    ];
}
