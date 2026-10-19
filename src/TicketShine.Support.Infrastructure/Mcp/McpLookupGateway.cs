using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Lookups;

namespace TicketShine.Support.Infrastructure.Mcp;

/// <summary>
/// Real MCP client: runs the MCP server as a child process and talks to it over stdio.
/// Timeouts, errors, and not-found keep their status and are never turned into successful data.
/// </summary>
public sealed class McpLookupGateway(McpServerLaunch launch, TimeProvider time, ILoggerFactory? loggerFactory = null)
    : ILookupGateway, IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<McpLookupGateway>();
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public async Task<IReadOnlyList<string>> ListToolNamesAsync(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public async Task<RawToolInvocation> CallAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        string origin,
        CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        var argsJson = JsonSerializer.Serialize(arguments, LookupTools.Json);
        var fallbackSource = $"{toolName}:{string.Join(",", arguments.Values)}";

        RawToolInvocation Fail(string error, string? sourceId = null)
        {
            var env = new LookupEnvelope(LookupStatus.Error, sourceId ?? fallbackSource, time.GetUtcNow(), null, error);
            var trace = new ToolCallRecord(toolName, argsJson, LookupStatus.Error, stopwatch.ElapsedMilliseconds, started, origin, env.SourceId, error);
            return new RawToolInvocation(env, trace, LookupTools.Serialize(env));
        }

        if (!LookupTools.Allowed.Contains(toolName))
        {
            return Fail($"Tool '{toolName}' is not on the lookup allowlist.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // Server startup time does not count toward the tool call timeout.
            var client = await GetClientAsync(cancellationToken);
            cts.CancelAfter(launch.ToolTimeout);
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cts.Token);
            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
            var envelope = LookupTools.TryParse(text);
            if (envelope is null)
            {
                return Fail(result.IsError == true ? $"Tool error: {Truncate(text)}" : "Malformed tool response.");
            }

            if (result.IsError == true && envelope.Status == LookupStatus.Success)
            {
                envelope = envelope with { Status = LookupStatus.Error, Data = null, Error = envelope.Error ?? "Tool reported an error." };
            }

            var trace = new ToolCallRecord(toolName, argsJson, envelope.Status, stopwatch.ElapsedMilliseconds, started, origin, envelope.SourceId, envelope.Error);
            return new RawToolInvocation(envelope, trace, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("MCP tool {Tool} timed out after {Timeout}", toolName, launch.ToolTimeout);
            return Fail($"Timed out after {launch.ToolTimeout.TotalSeconds:0}s.");
        }
        catch (McpException ex)
        {
            _logger.LogWarning(ex, "MCP tool {Tool} failed", toolName);
            return Fail($"MCP error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "MCP transport failure calling {Tool}", toolName);
            await ResetClientAsync();
            return Fail($"MCP transport failure: {ex.Message}");
        }
    }

    private async Task<McpClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is { } existing) return existing;

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(StartupTimeout);
        await _gate.WaitAsync(startup.Token);
        try
        {
            if (_client is { } c) return c;
            ct = startup.Token;

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ticketshine-lookups",
                Command = launch.Command,
                Arguments = [.. launch.Arguments],
                WorkingDirectory = launch.WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = launch.Environment.ToDictionary(kv => kv.Key, kv => kv.Value),
                StandardErrorLines = line => _logger.LogDebug("[mcp-server] {Line}", line),
            }, _loggerFactory);

            _client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: ct);
            _logger.LogInformation("MCP lookup server started: {Command}", launch.Command);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetClientAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is not null)
            {
                try { await _client.DisposeAsync(); } catch { /* ignore */ }
                _client = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    public async ValueTask DisposeAsync()
    {
        await ResetClientAsync();
        _gate.Dispose();
    }
}
