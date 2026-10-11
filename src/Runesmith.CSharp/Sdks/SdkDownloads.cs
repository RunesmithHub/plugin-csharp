using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp.Sdks;

/// <summary>Downloads an SDK archive, checks it and unpacks it into a new folder, leaving nothing behind when that fails.</summary>
internal static class SdkDownloads
{
    private const int BufferSize = 81920;

    /// <summary>Gets the client SDK providers download with.</summary>
    public static HttpClient Client { get; } = CreateClient();

    /// <summary>Downloads a release, checks its checksum and unpacks it into a new folder inside <paramref name="workFolder"/>.</summary>
    /// <returns>The folder the archive was unpacked into; the caller moves its contents and deletes it.</returns>
    public static async Task<string> DownloadAndUnpackAsync(
        HttpClient http, SdkRelease release, string workFolder, IProgress<SdkInstallProgress> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workFolder);
        var archive = Path.Combine(workFolder, $"download-{Guid.NewGuid():N}");
        var unpacked = Path.Combine(workFolder, $"unpack-{Guid.NewGuid():N}");
        try
        {
            await DownloadAsync(http, release.Download, archive, release.Size, progress, cancellationToken).ConfigureAwait(false);
            progress.Report(new SdkInstallProgress(Strings.ProgressVerifying, null));
            await Checksums.VerifyAsync(archive, release.Checksum, release.ChecksumAlgorithm, cancellationToken).ConfigureAwait(false);
            progress.Report(new SdkInstallProgress(Strings.ProgressUnpacking, null));
            await Task.Run(() => SdkArchives.Extract(archive, unpacked, cancellationToken), cancellationToken).ConfigureAwait(false);
            return unpacked;
        }
        catch
        {
            DeleteFolder(unpacked);
            throw;
        }
        finally
        {
            DeleteFile(archive);
        }
    }

    /// <summary>Deletes a folder and everything in it, without following links; does nothing when it is gone.</summary>
    public static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task DownloadAsync(
        HttpClient http, Uri uri, string path, long? expectedSize, IProgress<SdkInstallProgress> progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                var reported = -1.0;
                progress.Report(new SdkInstallProgress(Strings.ProgressDownloading, total is > 0 ? 0 : null));
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total is not > 0)
                        continue;

                    var fraction = Math.Min(1, (double)received / total.Value);
                    if (fraction - reported >= 0.005)
                    {
                        reported = fraction;
                        progress.Report(new SdkInstallProgress(Strings.ProgressDownloading, fraction));
                    }
                }
            }
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Runesmith");
        return client;
    }
}
