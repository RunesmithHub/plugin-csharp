using System.Formats.Tar;
using System.IO.Compression;

namespace Runesmith.CSharp.Sdks;

/// <summary>Unpacks the <c>.tar.gz</c> and <c>.zip</c> archives SDKs ship in, keeping file modes and links, and nothing outside the target.</summary>
internal static class SdkArchives
{
    private const int UnixPermissionBits = 0x1FF;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    /// <summary>Unpacks an archive into a folder, telling a zip from a gzip-compressed tar by its first bytes.</summary>
    /// <exception cref="InvalidDataException">The file is neither.</exception>
    public static void Extract(string archivePath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        Span<byte> magic = stackalloc byte[2];
        using (var probe = File.OpenRead(archivePath))
            probe.ReadExactly(magic);

        if (magic[0] == 'P' && magic[1] == 'K')
            ExtractZip(archivePath, root, cancellationToken);
        else if (magic[0] == 0x1F && magic[1] == 0x8B)
            ExtractTarGz(archivePath, root, cancellationToken);
        else
            throw new InvalidDataException("The download is neither a .zip nor a .tar.gz archive.");
    }

    private static void ExtractTarGz(string archivePath, string root, CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Target(root, entry.Name) is not { } target)
                continue;

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    SetMode(target, (int)entry.Mode);
                    break;
                case TarEntryType.SymbolicLink:
                    Link(root, target, entry.LinkName);
                    break;
                case TarEntryType.HardLink when Target(root, entry.LinkName) is { } source && File.Exists(source):
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: true);
                    SetMode(target, (int)entry.Mode);
                    break;
            }
        }
    }

    private static void ExtractZip(string archivePath, string root, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Target(root, entry.FullName) is not { } target)
                continue;

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if ((unixMode & UnixFileTypeMask) == UnixSymbolicLink && !OperatingSystem.IsWindows())
            {
                using var reader = new StreamReader(entry.Open());
                Link(root, target, reader.ReadToEnd());
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            SetMode(target, unixMode);
        }
    }

    // Archive paths come from the download, so one that leaves the target folder is skipped rather than trusted.
    private static string? Target(string root, string name)
    {
        if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, name));
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && full.Length > root.Length
            ? Path.TrimEndingDirectorySeparator(full)
            : null;
    }

    private static void Link(string root, string target, string linkTarget)
    {
        var resolved = Path.IsPathRooted(linkTarget) ? null : Target(root, Path.Combine(Path.GetRelativePath(root, Path.GetDirectoryName(target)!), linkTarget));
        if (resolved is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var existing = new FileInfo(target);
        if (existing.Exists || existing.LinkTarget is not null)
            File.Delete(target);
        else if (Directory.Exists(target))
            return;
        File.CreateSymbolicLink(target, linkTarget);
    }

    private static void SetMode(string path, int mode)
    {
        if (!OperatingSystem.IsWindows() && (mode & UnixPermissionBits) != 0)
            File.SetUnixFileMode(path, (UnixFileMode)(mode & UnixPermissionBits));
    }
}
