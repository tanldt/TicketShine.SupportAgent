using System.Runtime.InteropServices;

namespace TicketShine.Support.Infrastructure.Mcp;

public sealed class McpServerOptions
{
    public const string Section = "Mcp";

    /// <summary>Server launch command. Leave empty to auto-locate dotnet + TicketShine.Support.McpServer.dll in the solution.</summary>
    public string? Command { get; set; }

    public List<string> Arguments { get; set; } = [];

    /// <summary>Seed data path. Leave empty to use the solution's data/demo-seed.json.</summary>
    public string? SeedPath { get; set; }

    public int ToolTimeoutSeconds { get; set; } = 5;
}

public sealed record McpServerLaunch(
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan ToolTimeout);

public static class McpServerLocator
{
    public const string SolutionFile = "TicketShine.SupportAgent.sln";
    private const string ServerProject = "TicketShine.Support.McpServer";

    /// <summary>Only these environment variables are passed to the child process (no API keys or secrets).</summary>
    private static readonly string[] PassThroughVariables =
    [
        "SystemRoot", "windir", "TEMP", "TMP", "PATH", "USERPROFILE", "HOME", "LOCALAPPDATA", "APPDATA", "ProgramData",
    ];

    public static McpServerLaunch Resolve(McpServerOptions options, string? startDirectory = null)
    {
        var root = FindSolutionRoot(startDirectory ?? AppContext.BaseDirectory);
        var seed = options.SeedPath is { Length: > 0 } s
            ? Path.GetFullPath(s)
            : root is not null ? Path.Combine(root, "data", "demo-seed.json") : throw new InvalidOperationException("Set Mcp:SeedPath.");

        string command;
        List<string> args;
        if (options.Command is { Length: > 0 })
        {
            command = options.Command;
            args = [.. options.Arguments];
        }
        else
        {
            if (root is null) throw new InvalidOperationException($"Could not find {SolutionFile}; set Mcp:Command explicitly.");
            command = DotnetHostPath();
            args = [FindServerDll(root), "--seed", seed];
        }

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PassThroughVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value) env[name] = value;
        }

        env["DOTNET_ROOT"] = Path.GetDirectoryName(command) is { } dir && File.Exists(command) ? dir : Environment.GetEnvironmentVariable("DOTNET_ROOT");
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["DOTNET_NOLOGO"] = "1";
        env["TS_SEED_PATH"] = seed;

        return new McpServerLaunch(command, args, root, env, TimeSpan.FromSeconds(Math.Max(1, options.ToolTimeoutSeconds)));
    }

    public static string? FindSolutionRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile))) return dir.FullName;
        }

        return null;
    }

    /// <summary>Use the dotnet host running the current runtime to guarantee .NET 10 is available.</summary>
    public static string DotnetHostPath()
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory(); // .../dotnet/shared/Microsoft.NETCore.App/10.0.x/
        var candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", exe));
        if (File.Exists(candidate)) return candidate;

        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { } hostPath && File.Exists(hostPath)) return hostPath;
        return "dotnet";
    }

    private static string FindServerDll(string root)
    {
        var bin = Path.Combine(root, "src", ServerProject, "bin");
#if DEBUG
        string[] preferred = ["Debug", "Release"];
#else
        string[] preferred = ["Release", "Debug"];
#endif
        foreach (var configuration in preferred)
        {
            var dll = Path.Combine(bin, configuration, "net10.0", $"{ServerProject}.dll");
            if (File.Exists(dll)) return dll;
        }

        throw new FileNotFoundException($"{ServerProject}.dll not found under {bin}. Run 'dotnet build' for the solution first.");
    }
}
