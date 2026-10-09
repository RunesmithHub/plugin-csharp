namespace Runesmith.CSharp.Sdks;

/// <summary>Resolves links in paths, so one SDK reached through different links is recognized as one.</summary>
internal static class RealPaths
{
    private const int MaxLinks = 40;

    /// <summary>Gets a full path with every link in it resolved, or the full path itself where a part cannot be read.</summary>
    public static string Of(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? "";
        var parts = new Queue<string>(full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        var current = root;
        var links = 0;
        while (parts.TryDequeue(out var part))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            string? target;
            try
            {
                target = info.LinkTarget;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                target = null;
            }

            if (target is null || ++links > MaxLinks)
            {
                current = next;
                continue;
            }

            // A link's target can itself hold links, so the rest of the path is resolved again from its full form.
            var resolved = Path.GetFullPath(target, current);
            var rest = parts.ToArray();
            var resolvedRoot = Path.GetPathRoot(resolved) ?? "";
            parts = new Queue<string>(resolved[resolvedRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Concat(rest));
            current = resolvedRoot;
        }

        return current;
    }
}
