using System.Formats.Tar;
using System.IO.Compression;

namespace Runesmith.CSharp.Sdks;

/// <summary>Unpacks the <c>.tar.gz</c> and <c>.zip</c> archives SDKs and tools ship in, keeping file modes and links, and nothing outside the
/// target.</summary>
internal static class SdkArchives
{
    private const int UnixPermissionBits = 0x1FF;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    /// <summary>Unpacks an archive into a folder, telling a zip from a gzip-compressed tar by its first bytes.</summary>
    /// <param name="refuseUnsafeEntries">Whether an entry or link that leads outside the folder stops the unpacking, rather than being
    /// skipped.</param>
    /// <exception cref="InvalidDataException">The file is neither, or it has an unsafe entry and <paramref name="refuseUnsafeEntries"/> is
    /// set.</exception>
    public static void Extract(string archivePath, string destination, CancellationToken cancellationToken, bool refuseUnsafeEntries = false)
    {
        Directory.CreateDirectory(destination);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        Span<byte> magic = stackalloc byte[2];
        using (var probe = File.OpenRead(archivePath))
            probe.ReadExactly(magic);

        var extraction = new Extraction(root, refuseUnsafeEntries);
        if (magic[0] == 'P' && magic[1] == 'K')
            extraction.Zip(archivePath, cancellationToken);
        else if (magic[0] == 0x1F && magic[1] == 0x8B)
            extraction.TarGz(archivePath, cancellationToken);
        else
            throw new InvalidDataException("The download is neither a .zip nor a .tar.gz archive.");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void SetMode(string path, int mode)
    {
        if (!OperatingSystem.IsWindows() && (mode & UnixPermissionBits) != 0)
            File.SetUnixFileMode(path, (UnixFileMode)(mode & UnixPermissionBits));
    }

    private sealed class Extraction(string root, bool strict)
    {
        public void TarGz(string archivePath, CancellationToken cancellationToken)
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry() is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Target(entry.Name) is not { } target)
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
                        Link(target, entry.LinkName);
                        break;
                    case TarEntryType.HardLink when Target(entry.LinkName) is { } source && File.Exists(source):
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target, overwrite: true);
                        SetMode(target, (int)entry.Mode);
                        break;
                }
            }
        }

        public void Zip(string archivePath, CancellationToken cancellationToken)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Target(entry.FullName) is not { } target)
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
                    Link(target, reader.ReadToEnd());
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                SetMode(target, unixMode);
            }
        }

        // Archive paths come from the download, so one that leaves the target folder is never trusted.
        private string? Target(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var full = Path.IsPathRooted(name) ? null : Path.GetFullPath(Path.Combine(root, name));
            if (full is not null && full.StartsWith(root, PathComparison) && full.Length > root.Length)
                return Path.TrimEndingDirectorySeparator(full);

            if (strict && (full is null || !(Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar).Equals(root, PathComparison)))
                throw new InvalidDataException($"The archive has an entry outside its folder, {name}, so it was not unpacked.");
            return null;
        }

        private void Link(string target, string linkTarget)
        {
            var resolved = Path.IsPathRooted(linkTarget) ? null : Target(Path.Combine(Path.GetRelativePath(root, Path.GetDirectoryName(target)!), linkTarget));
            if (resolved is null)
            {
                if (strict)
                    throw new InvalidDataException($"The archive has a link to {linkTarget}, outside its folder, so it was not unpacked.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var existing = new FileInfo(target);
            if (existing.Exists || existing.LinkTarget is not null)
                File.Delete(target);
            else if (Directory.Exists(target))
                return;
            File.CreateSymbolicLink(target, linkTarget);
        }
    }
}
