using TicketShine.Support.Core;
using TicketShine.Support.Core.Drafting;
using TicketShine.Support.Core.Lookups;
using TicketShine.Support.Core.Workflow;
using TicketShine.Support.Infrastructure.Drafting;
using TicketShine.Support.Infrastructure.Mcp;
using TicketShine.Support.Infrastructure.Persistence;
using TicketShine.Support.Infrastructure.Sending;

[assembly: AssemblyFixture(typeof(TicketShine.Support.Tests.McpFixture))]

namespace TicketShine.Support.Tests;

/// <summary>A real MCP server (child process, stdio) shared across the whole test assembly.</summary>
public sealed class McpFixture : IAsyncLifetime
{
    public McpLookupGateway Gateway { get; private set; } = null!;

    public McpServerLaunch Launch { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        Launch = McpServerLocator.Resolve(new McpServerOptions { ToolTimeoutSeconds = 3 });
        Gateway = new McpLookupGateway(Launch, TimeProvider.System);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Gateway.DisposeAsync();
}

/// <summary>Test environment: temporary SQLite file + real workflow.</summary>
public sealed class TestEnv : IAsyncDisposable
{
    private TestEnv(string dbPath, SqliteCaseRepository repository, SupportWorkflow workflow, OutboxProcessor outbox)
    {
        DbPath = dbPath;
        Repository = repository;
        Workflow = workflow;
        Outbox = outbox;
    }

    public string DbPath { get; }

    public SqliteCaseRepository Repository { get; }

    public SupportWorkflow Workflow { get; }

    public OutboxProcessor Outbox { get; }

    public static async Task<TestEnv> CreateAsync(
        ILookupGateway lookups,
        IReplyDraftGenerator? generator = null,
        DateTimeOffset? demoNow = null,
        string? dbPath = null,
        bool deleteOnDispose = true)
    {
        dbPath ??= Path.Combine(Path.GetTempPath(), "ts-support-tests", $"{Guid.NewGuid():N}.db");
        var repo = new SqliteCaseRepository(dbPath);
        await repo.InitializeAsync();
        var workflow = new SupportWorkflow(repo, lookups, generator ?? new TemplateReplyDraftGenerator(),
            new FixedDemoClock(demoNow ?? FixedDemoClock.Default), TimeProvider.System, new NoopOutboxSignal());
        var outbox = new OutboxProcessor(repo, new SimulatedSender(repo, TimeProvider.System), TimeProvider.System);
        return new TestEnv(dbPath, repo, workflow, outbox) { DeleteOnDispose = deleteOnDispose };
    }

    private bool DeleteOnDispose { get; init; } = true;

    public async Task<Guid> CreateCaseAsync(string orderId, string message = "Where are my tickets?")
    {
        var (id, _) = await Workflow.CreateCaseAsync(new NewCaseRequest(orderId, message, "test-agent"));
        return id!.Value;
    }

    public ValueTask DisposeAsync()
    {
        if (!DeleteOnDispose) return ValueTask.CompletedTask;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch (IOException)
        {
            // Temporary file; ignore if still locked.
        }

        return ValueTask.CompletedTask;
    }
}
