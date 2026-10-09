using Runesmith.CSharp.Sdks;
using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp;

/// <summary>The <c>dotnet</c> to start and the environment it needs, such as <c>DOTNET_ROOT</c> for an SDK outside the <c>PATH</c>.</summary>
/// <param name="Program">The full path of the host, or <c>dotnet</c> when none was found.</param>
/// <param name="Environment">Variables to set for it.</param>
internal sealed record DotnetHost(string Program, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>Gets the <c>dotnet</c> on the <c>PATH</c>.</summary>
    public static DotnetHost OnPath() => new(FindOnPath(), new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Gets the <c>dotnet</c> of a .NET root, with <c>DOTNET_ROOT</c> and the <c>PATH</c> pointing at the root.</summary>
    public static DotnetHost FromRoot(string root)
    {
        var path = System.Environment.GetEnvironmentVariable("PATH");
        return new(Path.Combine(root, DotnetSdkLocator.HostName), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = root,
            ["PATH"] = string.IsNullOrEmpty(path) ? root : root + Path.PathSeparator + path,
        });
    }

    /// <summary>Gets the host of an SDK the user chose, or of the default SDK when the choice is empty; the <c>dotnet</c> on the <c>PATH</c>
    /// when that SDK's folder has no host.</summary>
    public static async Task<DotnetHost> ResolveAsync(ISdkService? sdks, string? sdkPath, CancellationToken cancellationToken)
    {
        var root = sdkPath;
        if (string.IsNullOrWhiteSpace(root) && sdks is not null)
            root = (await sdks.GetDefaultAsync(DotnetSdkProvider.SdkKind, cancellationToken).ConfigureAwait(false))?.Path;
        return !string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, DotnetSdkLocator.HostName)) ? FromRoot(root) : OnPath();
    }

    /// <summary>Finds the <c>dotnet</c> that runs Runesmith's tools, or the first on the <c>PATH</c>.</summary>
    public static string FindOnPath()
    {
        if (System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
            return host;

        foreach (var directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, DotnetSdkLocator.HostName);
            if (File.Exists(candidate))
                return candidate;
        }

        return DotnetSdkLocator.HostName;
    }
}
