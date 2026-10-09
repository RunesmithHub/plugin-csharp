using System.Runtime.InteropServices;
using Runesmith.Sdk;

namespace Runesmith.CSharp.Sdks;

/// <summary>Where the .NET SDK provider installs, caches and looks for SDKs, and which platform it downloads for.</summary>
/// <param name="ManagedRoot">The .NET root Runesmith installs SDKs into, side by side.</param>
/// <param name="CacheFolder">The folder the release metadata is kept in.</param>
/// <param name="Rid">The runtime identifier SDKs are downloaded for, such as <c>linux-x64</c>.</param>
internal sealed record DotnetSdkEnvironment(string ManagedRoot, string CacheFolder, string Rid)
{
    /// <summary>Gets the <c>DOTNET_ROOT</c> to search, or null.</summary>
    public string? DotnetRoot { get; init; }

    /// <summary>Gets the folders searched for <c>dotnet</c>, as the <c>PATH</c> lists them.</summary>
    public IReadOnlyList<string> PathFolders { get; init; } = [];

    /// <summary>Gets the usual .NET roots of this system.</summary>
    public IReadOnlyList<string> InstallFolders { get; init; } = [];

    /// <summary>Gets the environment of this machine.</summary>
    public static DotnetSdkEnvironment Current()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> folders = [Path.Combine(home, ".dotnet")];
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (!string.IsNullOrEmpty(root))
                    folders.Add(Path.Combine(root, "dotnet"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            folders.Add("/usr/local/share/dotnet");
        }
        else
        {
            folders.AddRange(["/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet", "/usr/local/share/dotnet", "/opt/dotnet"]);
        }

        return new DotnetSdkEnvironment(Path.Combine(RunesmithPaths.Sdks, DotnetSdkProvider.SdkKind), Path.Combine(RunesmithPaths.Cache, "sdks", DotnetSdkProvider.SdkKind), CurrentRid())
        {
            DotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            PathFolders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
            InstallFolders = folders,
        };
    }

    /// <summary>Gets the runtime identifier of SDK downloads for a system and processor.</summary>
    public static string RidFor(OSPlatform system, Architecture architecture, bool musl = false)
    {
        var os = system == OSPlatform.Windows ? "win" : system == OSPlatform.OSX ? "osx" : musl ? "linux-musl" : "linux";
        var processor = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => architecture.ToString().ToLowerInvariant(),
        };
        return $"{os}-{processor}";
    }

    private static string CurrentRid()
    {
        var system = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        return RidFor(system, RuntimeInformation.OSArchitecture, RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal));
    }
}
