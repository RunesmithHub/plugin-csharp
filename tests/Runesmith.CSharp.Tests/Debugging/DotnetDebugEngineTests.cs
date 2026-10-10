using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runesmith.CSharp.Debugging;
using Runesmith.CSharp.Running;
using Runesmith.CSharp.Tests.Sdks;
using Runesmith.Sdk.Debugging;
using Runesmith.Sdk.Options;
using Runesmith.Sdk.Running;

namespace Runesmith.CSharp.Tests.Debugging;

public sealed class DotnetDebugEngineTests : IDisposable
{
    private const string Rid = "test-x64";
    private const string AssetUri = $"https://github.com/Samsung/netcoredbg/releases/download/{NetcoredbgRelease.Version}/netcoredbg-test.tar.gz";
    private const string AssetHostUri = "https://release-assets.githubusercontent.com/github-production-release-asset/1/netcoredbg-test.tar.gz";

    private static readonly string Interpreter = "--interpreter=" + string.Concat("vs", "code");

    private readonly string root = Directory.CreateTempSubdirectory("runesmith-debug-").FullName;
    private readonly TestStorage storage;

    public DotnetDebugEngineTests() => storage = new TestStorage(Path.Combine(root, "storage"));

    [Fact]
    public async Task LaunchesTheProgramWithItsArgumentsFolderAndEnvironment()
    {
        var launch = Launch("dotnet", ("program", "/work/bin/App.dll"), ("args", "one\ntwo words\r\n"), ("cwd", "/work"),
            ("env", "DOTNET_ROOT=/sdks/dotnet\nGREETING=a=b\nEMPTY="));

        var request = await Engine(new FakeHttp()).CreateRequestAsync(launch, TestContext.Current.CancellationToken);

        Assert.Equal(DebugRequestKind.Launch, request.Kind);
        Assert.Equal("coreclr", request.AdapterId);
        Assert.Equal("/work/bin/App.dll", (string?)request.Arguments["program"]);
        Assert.Equal(["one", "two words"], Strings(request.Arguments["args"]));
        Assert.Equal("/work", (string?)request.Arguments["cwd"]);
        Assert.Equal("/sdks/dotnet", (string?)request.Arguments["env"]!["DOTNET_ROOT"]);
        Assert.Equal("a=b", (string?)request.Arguments["env"]!["GREETING"]);
        Assert.Equal("", (string?)request.Arguments["env"]!["EMPTY"]);
        Assert.False((bool)request.Arguments["stopAtEntry"]!);
    }

    [Fact]
    public async Task StartsTheAssemblyWithTheSdksOwnDotnet()
    {
        var dotnet = Path.Combine(root, "sdk", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var launch = Launch(dotnet, ("program", "/work/bin/App.dll"), ("args", "one"), ("cwd", "/work"), ("env", $"DOTNET_ROOT={Path.GetDirectoryName(dotnet)}"));

        var request = await Engine(new FakeHttp()).CreateRequestAsync(launch, TestContext.Current.CancellationToken);

        Assert.Equal(dotnet, (string?)request.Arguments["program"]);
        Assert.Equal(["/work/bin/App.dll", "one"], Strings(request.Arguments["args"]));
        Assert.Equal(Path.GetDirectoryName(dotnet), (string?)request.Arguments["env"]!["DOTNET_ROOT"]);
    }

    [Fact]
    public async Task DebugsWhatTheProjectConfigurationPrepares()
    {
        Directory.CreateDirectory(Path.Combine(root, "App"));
        File.WriteAllText(Path.Combine(root, "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var values = new OptionValues();
        values.Set("project", "App/App.csproj");
        values.Set("arguments", "a\nb");
        values.Set("environment", "X=1");
        var plan = await new DotnetProjectRunType().PrepareAsync(new RunConfiguration(DotnetProjectRunType.TypeId, "App", values), new RunContext(root, RunMode.Debug),
            TestContext.Current.CancellationToken);

        var request = await Engine(new FakeHttp()).CreateRequestAsync(new DebugAdapterContext(plan, plan.Debug!, root), TestContext.Current.CancellationToken);

        var assembly = Path.Combine(root, "App", "bin", "Debug", "net10.0", "App.dll");
        var arguments = Strings(request.Arguments["args"]);
        string[] expected = Path.IsPathRooted(plan.Program) ? [assembly, "a", "b"] : ["a", "b"];
        Assert.Equal(expected, arguments);
        Assert.Equal(Path.IsPathRooted(plan.Program) ? plan.Program : assembly, (string?)request.Arguments["program"]);
        Assert.Equal(Path.Combine(root, "App"), (string?)request.Arguments["cwd"]);
        Assert.Equal("1", (string?)request.Arguments["env"]!["X"]);
    }

    [Fact]
    public async Task DownloadsNetcoredbgOnFirstUseOnly()
    {
        var archive = TestArchives.TarGz(new ArchiveEntry("netcoredbg/netcoredbg", "#!"), new ArchiveEntry("netcoredbg/ManagedPart.dll", "dll"));
        var http = new FakeHttp().Redirect(AssetUri, AssetHostUri).Serve(AssetHostUri, archive);
        var tasks = new TestBackgroundTasks();
        var engine = Engine(http, Asset(archive), tasks);

        var first = Assert.IsType<DebugAdapterExecutable>(await engine.CreateAdapterAsync(Context(), TestContext.Current.CancellationToken));
        var second = Assert.IsType<DebugAdapterExecutable>(await engine.CreateAdapterAsync(Context(), TestContext.Current.CancellationToken));

        var expected = Path.Combine(storage.Folder, "tools", "netcoredbg", NetcoredbgRelease.Version, Rid, "netcoredbg", "netcoredbg");
        Assert.Equal(expected, first.Command);
        Assert.Equal([Interpreter], first.Arguments);
        Assert.Equal(expected, second.Command);
        Assert.Equal<string>([AssetUri, AssetHostUri], http.Requests);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(Path.GetDirectoryName(expected)!, "ManagedPart.dll")));
        Assert.Equal([Rid], Directory.GetFileSystemEntries(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(expected)))!).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(expected).HasFlag(UnixFileMode.UserExecute));
        var task = Assert.Single(tasks.Started);
        Assert.Contains(NetcoredbgRelease.Version, task.Title, StringComparison.Ordinal);
        Assert.Contains("Downloading", task.Details);
        Assert.True(task.Disposed);
    }

    [Fact]
    public async Task RefusesAPlatformWithoutABuild()
    {
        var engine = new DotnetDebugEngine(storage, null, new FakeHttp().Client(), "freebsd-x64", NetcoredbgRelease.Assets);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateAdapterAsync(Context(), TestContext.Current.CancellationToken));

        Assert.Contains("freebsd-x64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesADownloadThatDoesNotMatchItsChecksum()
    {
        var archive = TestArchives.TarGz(new ArchiveEntry("netcoredbg/netcoredbg", "#!"));
        var http = new FakeHttp().Serve(AssetUri, TestArchives.TarGz(new ArchiveEntry("netcoredbg/netcoredbg", "changed")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Engine(http, Asset(archive)).CreateAdapterAsync(Context(), TestContext.Current.CancellationToken));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(VersionFolder()));
    }

    [Fact]
    public async Task RefusesAnArchiveThatWritesOutsideItsFolder()
    {
        var archive = TestArchives.TarGz(new ArchiveEntry("netcoredbg/netcoredbg", "#!"), new ArchiveEntry("../../escaped", "nope"));
        var http = new FakeHttp().Serve(AssetUri, archive);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Engine(http, Asset(archive)).CreateAdapterAsync(Context(), TestContext.Current.CancellationToken));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Empty(Directory.GetFileSystemEntries(VersionFolder()));
        Assert.False(File.Exists(Path.Combine(storage.Folder, "tools", "netcoredbg", "escaped")));
    }

    [Fact]
    public async Task RefusesARedirectAwayFromGitHub()
    {
        var archive = TestArchives.TarGz(new ArchiveEntry("netcoredbg/netcoredbg", "#!"));
        var http = new FakeHttp().Redirect(AssetUri, "https://example.com/netcoredbg.tar.gz").Serve("https://example.com/netcoredbg.tar.gz", archive);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(http, Asset(archive)).CreateAdapterAsync(Context(), TestContext.Current.CancellationToken));

        Assert.Equal<string>([AssetUri], http.Requests);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static NetcoredbgAsset Asset(byte[] archive) => new(Rid, "netcoredbg-test.tar.gz", Convert.ToHexStringLower(SHA256.HashData(archive)));

    private static DebugAdapterContext Context() => Launch("dotnet", ("program", "/work/App.dll"));

    private static DebugAdapterContext Launch(string dotnet, params (string Key, string Value)[] settings)
    {
        var debug = new DebugLaunch("dotnet", settings.ToDictionary(s => s.Key, s => s.Value));
        return new DebugAdapterContext(new LaunchPlan(dotnet, [], "/work") { Debug = debug }, debug, "/work");
    }

    private static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(node => node!.GetValue<string>())];

    private DotnetDebugEngine Engine(FakeHttp http, NetcoredbgAsset? asset = null, TestBackgroundTasks? tasks = null) =>
        new(storage, tasks, http.Client(), Rid, asset is null ? [] : [asset]);

    private string VersionFolder() => Path.Combine(storage.Folder, "tools", "netcoredbg", NetcoredbgRelease.Version);
}

public sealed class NetcoredbgReleaseTests
{
    [Fact]
    public void ListsEachAssetOnceWithItsChecksum()
    {
        Assert.NotEmpty(NetcoredbgRelease.Assets);
        Assert.Equal(NetcoredbgRelease.Assets.Count, NetcoredbgRelease.Assets.Select(a => a.Rid).Distinct().Count());
        Assert.All(NetcoredbgRelease.Assets, asset =>
        {
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
            Assert.Equal("github.com", asset.Uri.Host);
            Assert.Equal(Uri.UriSchemeHttps, asset.Uri.Scheme);
            Assert.EndsWith($"/releases/download/{NetcoredbgRelease.Version}/{asset.FileName}", asset.Uri.AbsolutePath, StringComparison.Ordinal);
        });
        Assert.Equal(["linux-arm64", "linux-x64", "osx-arm64", "win-x64"], NetcoredbgRelease.Assets.Select(a => a.Rid).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HasABuildForThisComputer()
    {
        var asset = NetcoredbgRelease.Find(NetcoredbgRelease.CurrentRid);

        Assert.NotNull(asset);
        Assert.Equal(OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg", Path.GetFileName(asset.Executable));
    }

    [Fact]
    public void DeclaresEveryHostTheDownloadReaches()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(folder.FullName, "plugin.json")))
            folder = folder.Parent!;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.FullName, "plugin.json")));

        var hosts = manifest.RootElement.GetProperty("networkHosts").EnumerateArray().Select(host => host.GetString()).ToList();

        Assert.All(NetcoredbgRelease.Hosts, host => Assert.Contains(host, hosts));
    }
}
