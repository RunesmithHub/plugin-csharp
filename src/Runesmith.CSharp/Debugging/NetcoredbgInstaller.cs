using System.Net;
using Runesmith.CSharp.Sdks;
using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp.Debugging;

/// <summary>Downloads a netcoredbg asset, checks its SHA-256 checksum and unpacks it into <c>&lt;folder&gt;/&lt;version&gt;/&lt;rid&gt;</c>,
/// which appears whole or not at all.</summary>
/// <param name="http">A client that does not follow redirects itself, so each address can be checked.</param>
/// <param name="folder">The folder that keeps netcoredbg's versions.</param>
internal sealed class NetcoredbgInstaller(HttpClient http, string folder)
{
    private const int BufferSize = 81920;
    private const int MaxRedirects = 5;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Creates the client the installer downloads with.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Runesmith");
        return client;
    }

    /// <summary>Gets the folder an asset is unpacked into.</summary>
    public string FolderOf(NetcoredbgAsset asset) => Path.Combine(folder, NetcoredbgRelease.Version, asset.Rid);

    /// <summary>Gets the path of the netcoredbg program an asset installs.</summary>
    public string ExecutableOf(NetcoredbgAsset asset) => Path.Combine(FolderOf(asset), asset.Executable);

    /// <summary>Gets whether an asset is installed.</summary>
    public bool IsInstalled(NetcoredbgAsset asset) => File.Exists(ExecutableOf(asset));

    /// <summary>Installs an asset unless it is installed, and returns the path of its netcoredbg program.</summary>
    /// <exception cref="HttpRequestException">The download failed.</exception>
    /// <exception cref="InvalidDataException">The download does not match its checksum, or is not the archive it should be.</exception>
    /// <exception cref="IOException">The files could not be written.</exception>
    public async Task<string> InstallAsync(NetcoredbgAsset asset, IProgress<SdkInstallProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(progress);
        var executable = ExecutableOf(asset);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(executable))
                return executable;

            var target = FolderOf(asset);
            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, $".{asset.Rid}-{Guid.NewGuid():N}");
            var archive = staging + ".download";
            try
            {
                await DownloadAsync(asset.Uri, archive, progress, cancellationToken).ConfigureAwait(false);
                progress.Report(new SdkInstallProgress("Verifying", null));
                await Checksums.VerifyAsync(archive, asset.Sha256, "SHA-256", cancellationToken).ConfigureAwait(false);
                progress.Report(new SdkInstallProgress("Unpacking", null));
                await Task.Run(() => SdkArchives.Extract(archive, staging, cancellationToken, refuseUnsafeEntries: true), cancellationToken).ConfigureAwait(false);
                var staged = Path.Combine(staging, asset.Executable);
                if (!File.Exists(staged))
                    throw new InvalidDataException($"The download has no {asset.Executable}.");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(staged, File.GetUnixFileMode(staged) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

                SdkDownloads.DeleteFolder(target);
                Directory.Move(staging, target);
            }
            catch (IOException) when (File.Exists(executable))
            {
                // Another Runesmith installed it first.
            }
            finally
            {
                SdkDownloads.DeleteFolder(staging);
                DeleteFile(archive);
            }

            return executable;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task DownloadAsync(Uri uri, string path, IProgress<SdkInstallProgress> progress, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !NetcoredbgRelease.Hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"The download was sent to {uri.Host}, which is not a GitHub release host.");

            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                if (redirects == MaxRedirects)
                    throw new HttpRequestException("The download was redirected too many times.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            await SaveAsync(response, path, progress, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private static async Task SaveAsync(HttpResponseMessage response, string path, IProgress<SdkInstallProgress> progress, CancellationToken cancellationToken)
    {
        var total = response.Content.Headers.ContentLength;
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                var reported = -1.0;
                progress.Report(new SdkInstallProgress("Downloading", total is > 0 ? 0 : null));
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total is not > 0)
                        continue;

                    var fraction = Math.Min(1, (double)received / total.Value);
                    if (fraction - reported >= 0.01)
                    {
                        reported = fraction;
                        progress.Report(new SdkInstallProgress("Downloading", fraction));
                    }
                }
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

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
}
