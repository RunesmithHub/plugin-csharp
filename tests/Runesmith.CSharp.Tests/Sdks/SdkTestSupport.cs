using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Runesmith.CSharp.Tests.Sdks;

/// <summary>A file in a test archive: its content, or the target when it is a link.</summary>
internal sealed record ArchiveEntry(string Path, string Content = "", UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
{
    public string? LinkTarget { get; init; }
}

/// <summary>Builds small SDK archives for the tests.</summary>
internal static class TestArchives
{
    public const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static byte[] TarGz(params ArchiveEntry[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var entry in entries)
            {
                if (entry.LinkTarget is { } target)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, entry.Path) { LinkName = target });
                    continue;
                }

                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.Path) { Mode = entry.Mode, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(entry.Content)) });
            }
        }

        return output.ToArray();
    }

    public static byte[] Zip(params ArchiveEntry[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var item = archive.CreateEntry(entry.Path);
                var type = entry.LinkTarget is null ? 0x8000 : 0xA000;
                item.ExternalAttributes = (type | (int)entry.Mode) << 16;
                using var writer = new StreamWriter(item.Open());
                writer.Write(entry.LinkTarget ?? entry.Content);
            }
        }

        return output.ToArray();
    }

    public static string Sha512(byte[] data) => Convert.ToHexStringLower(SHA512.HashData(data));
}

/// <summary>Serves fixed responses by address, and remembers what was asked.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> responses = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public FakeHttp Serve(string uri, byte[] content)
    {
        responses[uri] = content;
        return this;
    }

    public FakeHttp Serve(string uri, string content) => Serve(uri, Encoding.UTF8.GetBytes(content));

    public HttpClient Client() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.ToString();
        lock (Requests)
            Requests.Add(uri);
        return Task.FromResult(responses.TryGetValue(uri, out var content)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Collects reported progress.</summary>
internal sealed class ProgressLog<T> : IProgress<T>
{
    public List<T> Reports { get; } = [];

    public void Report(T value)
    {
        lock (Reports)
            Reports.Add(value);
    }
}
