namespace Runesmith.CSharp.Sdks;

/// <summary>Keeps the vendors' release metadata on disk for a while, so the SDKs page reaches their services at most once in that time.</summary>
/// <param name="http">The client to download with.</param>
/// <param name="folder">The folder the copies are kept in.</param>
/// <param name="maxAge">How long a copy is used before it is downloaded again.</param>
internal sealed class MetadataCache(HttpClient http, string folder, TimeSpan maxAge)
{
    /// <summary>Gets a document: the kept copy while it is fresh, else a new download, else the old copy when the download fails.</summary>
    /// <param name="uri">Where the document is.</param>
    /// <param name="name">The name of its copy on disk.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    public async Task<string> GetAsync(Uri uri, string name, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, name);
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge)
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        string text;
        try
        {
            text = await http.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(folder);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return text;
    }
}
