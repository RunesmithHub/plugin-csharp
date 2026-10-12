using System.Composition;
using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp.Sdks;

/// <summary>Finds, downloads and removes .NET SDKs, from Microsoft's release metadata, into one .NET root that serves them all.</summary>
[Export(typeof(ISdkProvider))]
public sealed class DotnetSdkProvider : ISdkProvider
{
    /// <summary>The kind of the .NET SDK.</summary>
    public const string SdkKind = "dotnet";

    /// <summary>Who builds the .NET SDK.</summary>
    public const string Vendor = "Microsoft";

    private static readonly TimeSpan MetadataAge = TimeSpan.FromDays(1);

    private readonly HttpClient http;
    private readonly DotnetSdkEnvironment environment;
    private readonly MetadataCache metadata;

    /// <summary>Creates the provider for this machine.</summary>
    public DotnetSdkProvider()
        : this(SdkDownloads.Client, DotnetSdkEnvironment.Current())
    {
    }

    internal DotnetSdkProvider(HttpClient http, DotnetSdkEnvironment environment)
    {
        this.http = http;
        this.environment = environment;
        metadata = new MetadataCache(http, environment.CacheFolder, MetadataAge);
    }

    public string Kind => SdkKind;

    public string Name => Strings.DotnetSdk;

    // Every .NET SDK comes from Microsoft, so there is no vendor to choose and an SDK is named by its version.
    public bool HasVendors => false;

    public string? DownloadDescription => Strings.DotnetSdkSource;

    public Task<IReadOnlyList<InstalledSdk>> FindInstalledAsync(CancellationToken cancellationToken) =>
        Task.Run(() => DotnetSdkLocator.Find(environment), cancellationToken);

    public async Task<IReadOnlyList<SdkRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        var index = DotnetReleaseMetadata.ParseIndex(await metadata.GetAsync(DotnetReleaseMetadata.IndexUri, "releases-index.json", cancellationToken).ConfigureAwait(false));
        var installed = (await FindInstalledAsync(cancellationToken).ConfigureAwait(false)).Select(sdk => Channel(sdk.Version)).ToHashSet(StringComparer.Ordinal);
        var channels = index.Where(channel => !channel.IsEndOfLife || installed.Contains(channel.Version));
        var releases = await Task.WhenAll(channels.Select(async channel =>
        {
            var json = await metadata.GetAsync(channel.Releases, $"releases-{channel.Version}.json", cancellationToken).ConfigureAwait(false);
            return DotnetReleaseMetadata.ParseReleases(json, channel, environment.Rid);
        })).ConfigureAwait(false);
        return [.. releases.SelectMany(r => r).OrderByDescending(r => r.Version, VersionText.Comparer)];
    }

    public async Task<InstalledSdk> InstallAsync(SdkRelease release, IProgress<SdkInstallProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(progress);
        if (release.Kind != SdkKind)
            throw new ArgumentException($"The release is a {release.Kind}, not a .NET SDK.", nameof(release));

        var work = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(environment.ManagedRoot))!, ".downloads");
        var unpacked = await SdkDownloads.DownloadAndUnpackAsync(http, release, work, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!DotnetSdkLocator.SdkVersions(unpacked).Contains(release.Version, StringComparer.Ordinal))
                throw new InvalidDataException(Strings.SdkDownloadIncomplete(release.Version));

            progress.Report(new SdkInstallProgress(Strings.ProgressInstalling, null));
            Directory.CreateDirectory(environment.ManagedRoot);
            await Task.Run(() => DotnetRootMerger.Merge(unpacked, environment.ManagedRoot), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            SdkDownloads.DeleteFolder(unpacked);
        }

        return new InstalledSdk(SdkKind, release.Version, Path.GetFullPath(environment.ManagedRoot))
        {
            Vendor = Vendor,
            Source = SdkSource.Managed,
            Architecture = release.Architecture,
        };
    }

    public Task UninstallAsync(InstalledSdk sdk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ManagedRoot));
        if (sdk.Kind != SdkKind || sdk.Source != SdkSource.Managed || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sdk.Path)), root, PathComparison))
            throw new InvalidOperationException(Strings.SdkNotManaged(version: sdk.Version, path: sdk.Path));

        return Task.Run(() =>
        {
            SdkDownloads.DeleteFolder(Path.Combine(root, "sdk", sdk.Version));
            if (!DotnetSdkLocator.SdkVersions(root).Any())
                SdkDownloads.DeleteFolder(root);
        }, cancellationToken);
    }

    public SdkRelease? FindUpdate(InstalledSdk sdk, IReadOnlyList<SdkRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        ArgumentNullException.ThrowIfNull(releases);
        var channel = Channel(sdk.Version);
        var newest = releases
            .Where(r => r.Kind == SdkKind && r.Line == channel && (sdk.Architecture is null || r.Architecture is null || r.Architecture == sdk.Architecture))
            .MaxBy(r => r.Version, VersionText.Comparer);
        return newest is not null && VersionText.Compare(newest.Version, sdk.Version) > 0 ? newest : null;
    }

    /// <summary>Gets the channel of an SDK version: <c>10.0</c> for <c>10.0.401</c>.</summary>
    internal static string Channel(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
