using System.Runtime.InteropServices;

namespace Runesmith.CSharp.Debugging;

/// <summary>A netcoredbg download for one operating system and architecture.</summary>
/// <param name="Rid">The .NET runtime identifier it runs on, such as <c>linux-x64</c>.</param>
/// <param name="FileName">The release asset's file name.</param>
/// <param name="Sha256">The asset's SHA-256 checksum, as hexadecimal.</param>
internal sealed record NetcoredbgAsset(string Rid, string FileName, string Sha256)
{
    /// <summary>Gets the address of the asset on the release page.</summary>
    public Uri Uri => new($"https://github.com/Samsung/netcoredbg/releases/download/{NetcoredbgRelease.Version}/{FileName}");

    /// <summary>Gets the program's path inside the archive.</summary>
    public string Executable => Path.Combine("netcoredbg", Rid.StartsWith("win-", StringComparison.Ordinal) ? "netcoredbg.exe" : "netcoredbg");
}

/// <summary>The netcoredbg release the plugin downloads, from Samsung's GitHub releases, with the checksum of each asset.</summary>
internal static class NetcoredbgRelease
{
    /// <summary>The release tag.</summary>
    public const string Version = "3.2.0-1092";

    /// <summary>Gets the assets of the release, one per runtime identifier.</summary>
    public static IReadOnlyList<NetcoredbgAsset> Assets { get; } =
    [
        new("linux-x64", "netcoredbg-linux-amd64.tar.gz", "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa"),
        new("linux-arm64", "netcoredbg-linux-arm64.tar.gz", "065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb"),
        new("win-x64", "netcoredbg-win64.zip", "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a"),
        new("osx-arm64", "netcoredbg-osx-arm64.zip", "f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea"),
    ];

    /// <summary>Gets the hosts a download reaches: the release page and the host it redirects to.</summary>
    public static IReadOnlyList<string> Hosts { get; } = ["github.com", "release-assets.githubusercontent.com"];

    /// <summary>Gets the runtime identifier of this computer, such as <c>linux-x64</c> or <c>linux-musl-arm64</c>.</summary>
    public static string CurrentRid
    {
        get
        {
            var system = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : OperatingSystem.IsLinux() ? RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal) ? "linux-musl" : "linux"
                : RuntimeInformation.OSDescription.Split(' ')[0].ToLowerInvariant();
            return $"{system}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>Gets the asset for a runtime identifier, or null when the release has none.</summary>
    public static NetcoredbgAsset? Find(string rid) => Assets.FirstOrDefault(asset => asset.Rid == rid);
}
