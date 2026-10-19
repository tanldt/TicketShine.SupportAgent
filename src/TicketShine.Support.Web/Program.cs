using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TicketShine.Support.Core;
using TicketShine.Support.Core.Drafting;
using TicketShine.Support.Core.Email;
using TicketShine.Support.Core.Lookups;
using TicketShine.Support.Gmail;
using TicketShine.Support.Infrastructure.Email;
using TicketShine.Support.Core.Workflow;
using TicketShine.Support.Infrastructure.Drafting;
using TicketShine.Support.Infrastructure.Mcp;
using TicketShine.Support.Infrastructure.Persistence;
using TicketShine.Support.Infrastructure.Sending;
using TicketShine.Support.Web.Components;
using TicketShine.Support.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var demo = builder.Configuration.GetSection(DemoOptions.Section).Get<DemoOptions>() ?? new DemoOptions();
var mcpOptions = builder.Configuration.GetSection(McpServerOptions.Section).Get<McpServerOptions>() ?? new McpServerOptions();
var liveOptions = builder.Configuration.GetSection(LiveAiOptions.Section).Get<LiveAiOptions>() ?? new LiveAiOptions();

builder.Services.AddSingleton(demo);
builder.Services.AddSingleton(liveOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDemoClock>(new FixedDemoClock(demo.ParsedClock()));

var dbPath = Path.IsPathRooted(demo.DatabasePath) ? demo.DatabasePath : Path.Combine(builder.Environment.ContentRootPath, demo.DatabasePath);
builder.Services.AddSingleton(new SqliteCaseRepository(dbPath));
builder.Services.AddSingleton<ICaseRepository>(sp => sp.GetRequiredService<SqliteCaseRepository>());

// The MCP server runs as a local child process, communicating over stdio.
builder.Services.AddSingleton(_ => McpServerLocator.Resolve(mcpOptions, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<ILookupGateway>(sp =>
    new McpLookupGateway(sp.GetRequiredService<McpServerLaunch>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>()));

builder.Services.AddSingleton<IReplyDraftGenerator>(_ =>
    string.Equals(demo.DraftMode, "LiveAi", StringComparison.OrdinalIgnoreCase)
        ? new AnthropicReplyDraftGenerator(liveOptions)
        : new TemplateReplyDraftGenerator());

builder.Services.AddSingleton<ChannelOutboxSignal>();
builder.Services.AddSingleton<IOutboxSignal>(sp => sp.GetRequiredService<ChannelOutboxSignal>());
builder.Services.AddSingleton<CaseChangeNotifier>();
builder.Services.AddSingleton<SupportWorkflow>();
// ---- Email channel: inbound (simulated inbox or Gmail) and outbound (simulated mailbox or Gmail) ----
var mail = builder.Configuration.GetSection(MailOptions.Section).Get<MailOptions>() ?? new MailOptions();
var ingestOptions = builder.Configuration.GetSection(EmailIngestOptions.Section).Get<EmailIngestOptions>() ?? new EmailIngestOptions();
var gmailOptions = builder.Configuration.GetSection(GmailOptions.Section).Get<GmailOptions>() ?? new GmailOptions();
if (string.IsNullOrWhiteSpace(gmailOptions.Mailbox)) gmailOptions.Mailbox = ingestOptions.SupportMailbox;

builder.Services.AddSingleton(mail);
builder.Services.AddSingleton(ingestOptions);
builder.Services.AddSingleton(gmailOptions);
builder.Services.AddSingleton<IMailStore>(sp => sp.GetRequiredService<SqliteCaseRepository>());
builder.Services.AddSingleton(_ => new GmailServiceFactory(gmailOptions));
builder.Services.AddSingleton<SimulatedInboundMailSource>();
builder.Services.AddSingleton<EmailIngestService>();

if (mail.InboundIsGmail)
{
    builder.Services.AddSingleton<IInboundMailSource, GmailInboundSource>();
}
else
{
    builder.Services.AddSingleton<IInboundMailSource>(sp => sp.GetRequiredService<SimulatedInboundMailSource>());
}

builder.Services.AddSingleton<MailIngestProcessor>();
if (mail.InboundEnabled)
{
    builder.Services.AddHostedService<MailIngestWorker>();
}

if (mail.OutboundIsGmail)
{
    builder.Services.AddSingleton<IReplySender, GmailReplySender>();
}
else
{
    builder.Services.AddSingleton<IReplySender, SimulatedSender>();
}

builder.Services.AddSingleton(sp => new OutboxProcessor(
    sp.GetRequiredService<ICaseRepository>(), sp.GetRequiredService<IReplySender>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<OutboxProcessor>>()));
builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();

await app.Services.GetRequiredService<SqliteCaseRepository>().InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// The demo binds to loopback only by default (no login/authorization).
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
    var exposed = addresses.Where(a => !IsLoopback(a)).ToList();
    if (exposed.Count > 0 && !demo.AllowNonLoopback)
    {
        app.Logger.LogCritical("Refusing to run on non-loopback address(es) {Addresses}: this demo has no login. Use http://127.0.0.1.", exposed);
        app.Lifetime.StopApplication();
        return;
    }

    app.Logger.LogInformation("Ticket Shine support demo running at {Addresses} — draft mode: {Mode}; mail in: {Inbound}; mail out: {Outbound}",
        string.Join(", ", addresses), app.Services.GetRequiredService<IReplyDraftGenerator>().DisplayName, mail.Inbound,
        app.Services.GetRequiredService<IReplySender>().Name);
});

app.Run();

static bool IsLoopback(string address)
{
    if (!Uri.TryCreate(address.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"), UriKind.Absolute, out var uri)) return false;
    return uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}

public partial class Program;
