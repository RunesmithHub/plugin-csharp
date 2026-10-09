using System.Diagnostics;
using Runesmith.CSharp.Sdks;
using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp.Tests.Sdks;

public sealed class DotnetSdkProviderTests : IDisposable
{
    private const string Rid = "linux-x64";
    private readonly string folder = Directory.CreateTempSubdirectory("runesmith-dotnet-sdks-").FullName;
    private readonly FakeHttp http = new();

    private string ManagedRoot => Path.Combine(folder, "sdks", "dotnet");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InstallsSdksSideBySideKeepingTheNewerHost()
    {
        var provider = Provider();
        var newer = Serve("10.0.401", "10.0.12", "new host");
        var older = Serve("10.0.303", "10.0.8", "old host");
        var progress = new ProgressLog<SdkInstallProgress>();

        var installed = await provider.InstallAsync(newer, progress, Token);
        await provider.InstallAsync(older, new ProgressLog<SdkInstallProgress>(), Token);

        Assert.Equal((SdkSource.Managed, "10.0.401", "x64"), (installed.Source, installed.Version, installed.Architecture));
        Assert.Equal(Path.GetFullPath(ManagedRoot), installed.Path);
        Assert.True(File.Exists(Path.Combine(ManagedRoot, "sdk", "10.0.401", "dotnet.dll")));
        Assert.True(File.Exists(Path.Combine(ManagedRoot, "sdk", "10.0.303", "dotnet.dll")));
        Assert.True(File.Exists(Path.Combine(ManagedRoot, "shared", "Microsoft.NETCore.App", "10.0.8", "System.Private.CoreLib.dll")));
        Assert.Equal("new host", File.ReadAllText(Path.Combine(ManagedRoot, DotnetSdkLocator.HostName)));
        Assert.Equal("10.0.12", DotnetRootMerger.HostVersion(ManagedRoot));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(ManagedRoot, DotnetSdkLocator.HostName)).HasFlag(UnixFileMode.UserExecute));
        Assert.Contains(progress.Reports, p => p.Stage == "Downloading" && p.Fraction == 1);
        Assert.Equal(["Verifying", "Unpacking", "Installing"], progress.Reports.Select(p => p.Stage).Where(s => s != "Downloading"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(folder, "sdks", ".downloads")));
    }

    [Fact]
    public async Task ReplacesAnOlderHost()
    {
        var provider = Provider();
        await provider.InstallAsync(Serve("10.0.303", "10.0.8", "old host"), new ProgressLog<SdkInstallProgress>(), Token);
        await provider.InstallAsync(Serve("10.0.401", "10.0.12", "new host"), new ProgressLog<SdkInstallProgress>(), Token);

        Assert.Equal("new host", File.ReadAllText(Path.Combine(ManagedRoot, DotnetSdkLocator.HostName)));
    }

    [Fact]
    public async Task InstallsNothingWhenTheChecksumDiffers()
    {
        var release = Serve("10.0.401", "10.0.12", "host") with { Checksum = new string('0', 128) };

        await Assert.ThrowsAsync<InvalidDataException>(() => Provider().InstallAsync(release, new ProgressLog<SdkInstallProgress>(), Token));

        Assert.False(Directory.Exists(ManagedRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(folder, "sdks", ".downloads")));
    }

    [Fact]
    public async Task FindsSdksInTheManagedRootDotnetRootThePathAndTheUsualFolders()
    {
        await Provider().InstallAsync(Serve("10.0.401", "10.0.12", "host"), new ProgressLog<SdkInstallProgress>(), Token);
        var system = FakeRoot("usr-lib-dotnet", "8.0.425", "9.0.318");
        var dotnetRoot = FakeRoot("dotnet-root", "10.0.100");
        var onPath = FakeRoot("on-path", "10.0.111");
        var bin = Directory.CreateDirectory(Path.Combine(folder, "bin")).FullName;
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(Path.Combine(bin, "dotnet"), Path.Combine(onPath, "dotnet"));

        var found = await Provider(system, dotnetRoot, OperatingSystem.IsWindows() ? onPath : bin).FindInstalledAsync(Token);

        Assert.Equal(["10.0.401", "10.0.111", "10.0.100", "9.0.318", "8.0.425"], found.Select(s => s.Version));
        Assert.Equal(SdkSource.Managed, found[0].Source);
        Assert.All(found.Skip(1), sdk => Assert.Equal(SdkSource.System, sdk.Source));
        Assert.Equal(onPath, found.Single(s => s.Version == "10.0.111").Path);
        Assert.All(found, sdk => Assert.Equal("Microsoft", sdk.Vendor));
    }

    [Fact]
    public async Task ListsReleasesOfSupportedAndInstalledChannelsAndKeepsTheMetadataForADay()
    {
        ServeMetadata();
        var releases = await Provider().GetReleasesAsync(Token);

        Assert.Equal("11.0.100-rc.1.26425.128", releases[0].Version);
        Assert.Contains(releases, r => r.Version == "10.0.401");
        Assert.DoesNotContain(http.Requests, r => r.Contains("/7.0/", StringComparison.Ordinal));

        var requests = http.Requests.Count;
        FakeRoot(Path.Combine("sdks", "dotnet"), "7.0.410");
        await Provider().GetReleasesAsync(Token);
        Assert.Equal(requests + 1, http.Requests.Count);
        Assert.Contains("/7.0/", http.Requests[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsARootReachedThroughALinkOnce()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating links needs extra rights on Windows.");
        var real = FakeRoot("nix-dotnet", "10.0.401");
        var wrapper = Directory.CreateDirectory(Path.Combine(folder, "wrapped")).FullName;
        var link = Path.Combine(wrapper, "dotnet");
        Directory.CreateSymbolicLink(link, real);

        var found = await Provider(installFolder: real, dotnetRoot: link).FindInstalledAsync(Token);

        Assert.Single(found);
    }

    [Fact]
    public void FindsANewerSdkInTheSameChannel()
    {
        var releases = DotnetReleaseMetadata.ParseReleases(DotnetReleaseMetadataTests.Fixture("releases-10.0.json"), DotnetReleaseMetadataTests.Channel("10.0"), Rid);
        var provider = Provider();

        Assert.Equal("10.0.401", provider.FindUpdate(Sdk("10.0.303"), releases)?.Version);
        Assert.Equal("10.0.401", provider.FindUpdate(Sdk("10.0.100-rc.2.25502.107"), releases)?.Version);
        Assert.Null(provider.FindUpdate(Sdk("10.0.401"), releases));
        Assert.Null(provider.FindUpdate(Sdk("9.0.100"), releases));
        Assert.Null(provider.FindUpdate(Sdk("10.0.303") with { Architecture = "arm64" }, releases));
    }

    [Fact]
    public async Task RemovesManagedSdksAndTheRootWithTheLast()
    {
        var provider = Provider();
        var first = await provider.InstallAsync(Serve("10.0.401", "10.0.12", "host"), new ProgressLog<SdkInstallProgress>(), Token);
        var second = await provider.InstallAsync(Serve("10.0.303", "10.0.8", "host"), new ProgressLog<SdkInstallProgress>(), Token);

        await provider.UninstallAsync(second, Token);
        Assert.False(Directory.Exists(Path.Combine(ManagedRoot, "sdk", "10.0.303")));
        Assert.True(Directory.Exists(Path.Combine(ManagedRoot, "sdk", "10.0.401")));

        await provider.UninstallAsync(first, Token);
        Assert.False(Directory.Exists(ManagedRoot));
    }

    [Fact]
    public async Task NeverRemovesSystemSdks()
    {
        var root = FakeRoot("system", "10.0.100");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider().UninstallAsync(new InstalledSdk("dotnet", "10.0.100", root) { Source = SdkSource.System }, Token));
        Assert.True(Directory.Exists(Path.Combine(root, "sdk", "10.0.100")));
    }

    [Fact]
    public void BuildsWithTheDotnetOfAManagedRoot()
    {
        var start = new ProcessStartInfo("dotnet");
        start.Environment["PATH"] = "/usr/bin";

        DotnetBuildProvider.UseRoot(start, ManagedRoot);

        Assert.Equal(Path.Combine(ManagedRoot, DotnetSdkLocator.HostName), start.FileName);
        Assert.Equal(ManagedRoot, start.Environment["DOTNET_ROOT"]);
        Assert.Equal(ManagedRoot + Path.PathSeparator + "/usr/bin", start.Environment["PATH"]);
    }

    public void Dispose()
    {
        http.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private static InstalledSdk Sdk(string version) => new("dotnet", version, "/usr/lib/dotnet") { Architecture = "x64" };

    private DotnetSdkProvider Provider(string? installFolder = null, string? dotnetRoot = null, string? pathFolder = null) =>
        new(http.Client(), new DotnetSdkEnvironment(ManagedRoot, Path.Combine(folder, "cache"), Rid)
        {
            DotnetRoot = dotnetRoot,
            PathFolders = pathFolder is null ? [] : [pathFolder],
            InstallFolders = installFolder is null ? [] : [installFolder],
        });

    // An SDK archive laid out as Microsoft's are: the host at the top, then versioned folders.
    private SdkRelease Serve(string sdk, string runtime, string host)
    {
        var archive = TestArchives.TarGz(
            new ArchiveEntry(DotnetSdkLocator.HostName, host, TestArchives.Executable),
            new ArchiveEntry("LICENSE.txt", "license"),
            new ArchiveEntry($"host/fxr/{runtime}/libhostfxr.so", "fxr"),
            new ArchiveEntry($"shared/Microsoft.NETCore.App/{runtime}/System.Private.CoreLib.dll", "corelib"),
            new ArchiveEntry($"sdk/{sdk}/dotnet.dll", "sdk"),
            new ArchiveEntry($"sdk-manifests/10.0.100/workload/{runtime}/manifest.json", "{}"));
        var uri = $"https://builds.example.test/dotnet-sdk-{sdk}-{Rid}.tar.gz";
        http.Serve(uri, archive);
        return new SdkRelease("dotnet", sdk, "Microsoft", new Uri(uri))
        {
            Line = DotnetSdkProvider.Channel(sdk),
            Architecture = "x64",
            Checksum = TestArchives.Sha512(archive),
            ChecksumAlgorithm = "SHA-512",
        };
    }

    private void ServeMetadata()
    {
        http.Serve(DotnetReleaseMetadata.IndexUri.ToString(), DotnetReleaseMetadataTests.Fixture("releases-index.json"));
        foreach (var channel in new[] { "10.0", "11.0" })
            http.Serve($"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json", DotnetReleaseMetadataTests.Fixture($"releases-{channel}.json"));
        http.Serve("https://builds.dotnet.microsoft.com/dotnet/release-metadata/7.0/releases.json", """{ "releases": [] }""");
    }

    private string FakeRoot(string name, params string[] versions)
    {
        var root = Path.Combine(folder, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, DotnetSdkLocator.HostName), "");
        foreach (var version in versions)
        {
            Directory.CreateDirectory(Path.Combine(root, "sdk", version));
            File.WriteAllText(Path.Combine(root, "sdk", version, "dotnet.dll"), "");
        }

        Directory.CreateDirectory(Path.Combine(root, "sdk", "NuGetFallbackFolder"));
        return Path.GetFullPath(root);
    }
}
