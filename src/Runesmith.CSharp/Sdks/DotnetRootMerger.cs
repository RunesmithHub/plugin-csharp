
namespace Runesmith.CSharp.Sdks;

/// <summary>Adds an unpacked .NET SDK archive to a .NET root side by side with the SDKs and runtimes in it, as the official install script
/// does: versioned folders such as <c>sdk/10.0.401</c> are added and never replaced, and the files outside them, such as the
/// <c>dotnet</c> host, are replaced only by a host at least as new.</summary>
internal static class DotnetRootMerger
{
    /// <summary>Moves the files of an unpacked archive into a .NET root.</summary>
    /// <param name="unpacked">The folder the archive was unpacked into.</param>
    /// <param name="root">The .NET root.</param>
    public static void Merge(string unpacked, string root)
    {
        var replaceHost = HostVersion(root) is not { } current || VersionText.Compare(HostVersion(unpacked), current) >= 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var file in Directory.EnumerateFiles(unpacked, "*", options).ToList())
        {
            var relative = Path.GetRelativePath(unpacked, file);
            var target = Path.Combine(root, relative);
            var existing = new FileInfo(target);
            if (existing.Exists || existing.LinkTarget is not null)
            {
                if (IsVersioned(relative) || !replaceHost)
                    continue;
                File.Delete(target);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target);
        }
    }

    /// <summary>Gets the newest host resolver in a .NET root's <c>host/fxr</c>, or null when it has none.</summary>
    public static string? HostVersion(string root)
    {
        var fxr = Path.Combine(root, "host", "fxr");
        return Directory.Exists(fxr)
            ? Directory.EnumerateDirectories(fxr).Select(Path.GetFileName).OfType<string>().Max(VersionText.Comparer)
            : null;
    }

    // A file belongs to a version when a folder on its path is named like one, such as sdk/10.0.401 or shared/Microsoft.NETCore.App/10.0.5.
    private static bool IsVersioned(string relative)
    {
        var folders = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[..^1];
        return folders.Any(folder => folder.Length > 2 && char.IsAsciiDigit(folder[0]) && folder.Contains('.', StringComparison.Ordinal));
    }
}
