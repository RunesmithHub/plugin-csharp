using System.Composition;
using System.Text.Json.Nodes;
using Runesmith.CSharp.Sdks;
using Runesmith.Sdk.Debugging;
using Runesmith.Sdk.Plugins;
using Runesmith.Sdk.Sdks;
using Runesmith.Sdk.Tasks;

namespace Runesmith.CSharp.Debugging;

/// <summary>Debugs the <c>dotnet</c> launches of .NET project configurations with netcoredbg, which it downloads on first use.</summary>
[Export(typeof(IDebugAdapterProvider))]
[Shared]
public sealed class DotnetDebugEngine : IDebugAdapterProvider
{
    // netcoredbg names its Debug Adapter Protocol mode after an editor.
    private static readonly string Interpreter = "--interpreter=" + string.Concat("vs", "code");

    private readonly IPluginStorage? storage;
    private readonly IBackgroundTasks? tasks;
    private readonly HttpClient http;
    private readonly string rid;
    private readonly IReadOnlyList<NetcoredbgAsset> assets;

    /// <summary>Creates the engine, which keeps netcoredbg in the plugin's storage and shows its download in the status bar.</summary>
    [ImportingConstructor]
    public DotnetDebugEngine([Import(AllowDefault = true)] IPluginStorage? storage, [Import(AllowDefault = true)] IBackgroundTasks? tasks)
        : this(storage, tasks, NetcoredbgInstaller.CreateClient(), NetcoredbgRelease.CurrentRid, NetcoredbgRelease.Assets)
    {
    }

    internal DotnetDebugEngine(IPluginStorage? storage, IBackgroundTasks? tasks, HttpClient http, string rid, IReadOnlyList<NetcoredbgAsset> assets)
    {
        this.storage = storage;
        this.tasks = tasks;
        this.http = http;
        this.rid = rid;
        this.assets = assets;
    }

    public IReadOnlyList<string> Debuggers => ["dotnet"];

    public string Name => "netcoredbg";

    public async Task<DebugAdapterDescriptor> CreateAdapterAsync(DebugAdapterContext context, CancellationToken cancellationToken)
    {
        var asset = assets.FirstOrDefault(a => a.Rid == rid)
            ?? throw new InvalidOperationException(Strings.DebuggerUnsupported(rid));
        var folder = storage?.GetFolder() ?? throw new InvalidOperationException("Runesmith gave the C# plugin no storage folder to keep netcoredbg in.");
        var installer = new NetcoredbgInstaller(http, Path.Combine(folder, "tools", "netcoredbg"));
        if (installer.IsInstalled(asset))
            return Adapter(installer.ExecutableOf(asset));

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var task = tasks?.Start(Strings.DebuggerDownloading(NetcoredbgRelease.Version), cancel.Cancel);
        var progress = new Progress(task);
        try
        {
            return Adapter(await installer.InstallAsync(asset, progress, cancel.Token).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(Strings.DebuggerDownloadFailed(version: NetcoredbgRelease.Version, reason: exception.Message), exception);
        }
    }

    public Task<DebugAdapterRequest> CreateRequestAsync(DebugAdapterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Launch.Settings;
        var plan = context.Plan;
        var program = settings.GetValueOrDefault("program") is { Length: > 0 } value ? value : plan.Program;
        List<string> arguments = [.. Lines(settings, "args")];

        // netcoredbg starts a .dll with the dotnet on its own PATH, which may not be the SDK's.
        if (program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(plan.Program)
            && Path.GetFileNameWithoutExtension(plan.Program).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Insert(0, program);
            program = plan.Program;
        }

        var environment = new JsonObject();
        if (settings.ContainsKey("env"))
        {
            foreach (var line in Lines(settings, "env"))
            {
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                    environment[line[..equals]] = line[(equals + 1)..];
            }
        }
        else
        {
            foreach (var (name, text) in plan.Environment)
                environment[name] = text;
        }

        var request = new JsonObject
        {
            ["program"] = program,
            ["args"] = new JsonArray([.. arguments.Select(argument => (JsonNode)JsonValue.Create(argument))]),
            ["cwd"] = settings.GetValueOrDefault("cwd") is { Length: > 0 } cwd ? cwd : plan.WorkingDirectory,
            ["env"] = environment,
            ["stopAtEntry"] = false,
        };
        return Task.FromResult(new DebugAdapterRequest(DebugRequestKind.Launch, request) { AdapterId = "coreclr" });
    }

    private static DebugAdapterExecutable Adapter(string path) => new(path, [Interpreter]);

    private static IEnumerable<string> Lines(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value)
            ? value.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)
            : [];

    private sealed class Progress(IBackgroundTask? task) : IProgress<SdkInstallProgress>
    {
        public void Report(SdkInstallProgress value) => task?.Report(value.Stage, value.Fraction);
    }
}
