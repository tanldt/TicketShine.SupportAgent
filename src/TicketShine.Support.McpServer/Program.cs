using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketShine.Support.McpServer;

// MCP stdio server: stdout is reserved for the protocol, all logs go to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var seedPath = builder.Configuration["seed"]
               ?? Environment.GetEnvironmentVariable("TS_SEED_PATH")
               ?? Path.Combine(AppContext.BaseDirectory, "demo-seed.json");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(DemoDataStore.Load(seedPath));
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "ticketshine-support-lookups", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<LookupToolset>();

await builder.Build().RunAsync();
