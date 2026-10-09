using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp.Sdks;

/// <summary>Finds .NET SDKs: in Runesmith's own .NET root, <c>DOTNET_ROOT</c>, the root of the <c>dotnet</c> on the <c>PATH</c> and the
/// usual install folders.</summary>
internal static class DotnetSdkLocator
{
    /// <summary>Gets the name of the .NET host on this system.</summary>
    public static string HostName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    /// <summary>Finds every SDK, newest first; each root once, also when links lead to it by several paths.</summary>
    public static IReadOnlyList<InstalledSdk> Find(DotnetSdkEnvironment environment)
    {
        var roots = new List<(string Path, SdkSource Source)> { (environment.ManagedRoot, SdkSource.Managed) };
        if (!string.IsNullOrWhiteSpace(environment.DotnetRoot))
            roots.Add((environment.DotnetRoot, SdkSource.System));
        if (FindRootOnPath(environment.PathFolders) is { } fromPath)
            roots.Add((fromPath, SdkSource.System));
        roots.AddRange(environment.InstallFolders.Select(folder => (folder, SdkSource.System)));

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<InstalledSdk>();
        foreach (var (path, source) in roots)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!seen.Add(RealPaths.Of(root)))
                continue;

            found.AddRange(SdksIn(root, source, source == SdkSource.Managed ? environment.Rid[(environment.Rid.LastIndexOf('-') + 1)..] : null));
        }

        return [.. found.OrderByDescending(sdk => sdk.Version, VersionText.Comparer)];
    }

    /// <summary>Gets the SDK versions in a .NET root's <c>sdk</c> folder.</summary>
    public static IEnumerable<string> SdkVersions(string root)
    {
        var folder = Path.Combine(root, "sdk");
        if (!Directory.Exists(folder))
            return [];

        try
        {
            return Directory.EnumerateDirectories(folder)
                .Where(directory => File.Exists(Path.Combine(directory, "dotnet.dll")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.Length > 0 && char.IsAsciiDigit(name[0]))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<InstalledSdk> SdksIn(string root, SdkSource source, string? architecture) =>
        SdkVersions(root).Select(version => new InstalledSdk(DotnetSdkProvider.SdkKind, version, root)
        {
            Vendor = DotnetSdkProvider.Vendor,
            Source = source,
            Architecture = architecture,
        });

    // dotnet on the PATH is often a link, such as /usr/bin/dotnet to /usr/lib/dotnet/dotnet; the root is its final target's folder.
    private static string? FindRootOnPath(IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, HostName);
            if (!File.Exists(path))
                continue;

            try
            {
                var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
                return Path.GetDirectoryName(target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
