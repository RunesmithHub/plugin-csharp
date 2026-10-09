using System.Globalization;
using System.Text.Json;
using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp.Sdks;

/// <summary>A .NET release channel from Microsoft's release index, such as 10.0.</summary>
/// <param name="Version">The channel, such as <c>10.0</c>.</param>
/// <param name="SupportPhase">The support phase: <c>preview</c>, <c>go-live</c>, <c>active</c>, <c>maintenance</c> or <c>eol</c>.</param>
/// <param name="ReleaseType"><c>lts</c> or <c>sts</c>.</param>
/// <param name="LatestSdk">The newest SDK in the channel.</param>
/// <param name="Releases">The channel's <c>releases.json</c>.</param>
internal sealed record DotnetChannel(string Version, string SupportPhase, string ReleaseType, string? LatestSdk, Uri Releases)
{
    /// <summary>Gets whether the channel has not been released yet.</summary>
    public bool IsPreview => SupportPhase is "preview" or "go-live";

    /// <summary>Gets whether Microsoft no longer supports the channel.</summary>
    public bool IsEndOfLife => SupportPhase == "eol";
}

/// <summary>Reads Microsoft's .NET release metadata: the release index and each channel's <c>releases.json</c>.</summary>
internal static class DotnetReleaseMetadata
{
    /// <summary>The release index, which lists the channels.</summary>
    public static readonly Uri IndexUri = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json");

    /// <summary>Reads the channels of the release index.</summary>
    public static IReadOnlyList<DotnetChannel> ParseIndex(string json)
    {
        using var document = JsonDocument.Parse(json);
        var channels = new List<DotnetChannel>();
        foreach (var channel in document.RootElement.GetProperty("releases-index").EnumerateArray())
        {
            if (Text(channel, "channel-version") is not { } version || Text(channel, "releases.json") is not { } releases)
                continue;

            channels.Add(new DotnetChannel(version, Text(channel, "support-phase") ?? "", Text(channel, "release-type") ?? "", Text(channel, "latest-sdk"), new Uri(releases)));
        }

        return channels;
    }

    /// <summary>Reads the SDKs of a channel's <c>releases.json</c> that have an archive for a runtime identifier, newest first.</summary>
    /// <param name="json">The channel's <c>releases.json</c>.</param>
    /// <param name="channel">The channel it belongs to.</param>
    /// <param name="rid">The runtime identifier, such as <c>linux-x64</c>.</param>
    public static IReadOnlyList<SdkRelease> ParseReleases(string json, DotnetChannel channel, string rid)
    {
        using var document = JsonDocument.Parse(json);
        var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var architecture = rid[(rid.LastIndexOf('-') + 1)..];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var releases = new List<SdkRelease>();
        foreach (var release in document.RootElement.GetProperty("releases").EnumerateArray())
        {
            DateOnly? date = DateOnly.TryParseExact(Text(release, "release-date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            foreach (var sdk in Sdks(release))
            {
                if (Text(sdk, "version") is not { } version || !seen.Add(version) || (VersionText.IsPrerelease(version) && !channel.IsPreview))
                    continue;

                if (Archive(sdk, rid, extension) is not { } file || Text(file, "url") is not { } url)
                    continue;

                var hash = Text(file, "hash");
                releases.Add(new SdkRelease(DotnetSdkProvider.SdkKind, version, DotnetSdkProvider.Vendor, new Uri(url))
                {
                    Line = channel.Version,
                    Architecture = architecture,
                    IsLongTermSupport = channel.ReleaseType == "lts",
                    SupportPhase = channel.SupportPhase,
                    Released = date,
                    Checksum = string.IsNullOrEmpty(hash) ? null : hash,
                    ChecksumAlgorithm = string.IsNullOrEmpty(hash) ? null : "SHA-512",
                });
            }
        }

        releases.Sort((a, b) => VersionText.Compare(b.Version, a.Version));
        return releases;
    }

    private static IEnumerable<JsonElement> Sdks(JsonElement release)
    {
        if (release.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object)
            yield return sdk;
        if (release.TryGetProperty("sdks", out var sdks) && sdks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sdks.EnumerateArray())
                yield return item;
        }
    }

    private static JsonElement? Archive(JsonElement sdk, string rid, string extension)
    {
        if (!sdk.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var file in files.EnumerateArray())
        {
            if (Text(file, "rid") == rid && Text(file, "name")?.EndsWith(extension, StringComparison.OrdinalIgnoreCase) == true)
                return file;
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
