using System.Runtime.InteropServices;
using Runesmith.CSharp.Sdks;

namespace Runesmith.CSharp.Tests.Sdks;

public sealed class DotnetReleaseMetadataTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sdks", "Fixtures", name));

    internal static DotnetChannel Channel(string version) => DotnetReleaseMetadata.ParseIndex(Fixture("releases-index.json")).Single(c => c.Version == version);

    [Fact]
    public void ReadsTheChannelsOfTheIndex()
    {
        var channels = DotnetReleaseMetadata.ParseIndex(Fixture("releases-index.json"));

        Assert.Equal(["11.0", "10.0", "7.0"], channels.Select(c => c.Version));
        Assert.True(channels[0].IsPreview);
        Assert.Equal(("active", "lts", "10.0.401"), (channels[1].SupportPhase, channels[1].ReleaseType, channels[1].LatestSdk));
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", channels[1].Releases.ToString());
        Assert.True(channels[2].IsEndOfLife);
    }

    [Fact]
    public void ReadsEverySdkOfAChannelNewestFirstWithoutPrereleasesOfReleasedChannels()
    {
        var releases = DotnetReleaseMetadata.ParseReleases(Fixture("releases-10.0.json"), Channel("10.0"), "linux-x64");

        Assert.Equal(["10.0.401", "10.0.400", "10.0.303", "10.0.112", "10.0.111"], releases.Select(r => r.Version));
        var latest = releases[0];
        Assert.Equal(("dotnet", "Microsoft", "10.0", "x64"), (latest.Kind, latest.Vendor, latest.Line, latest.Architecture));
        Assert.True(latest.IsLongTermSupport);
        Assert.Equal("active", latest.SupportPhase);
        Assert.Equal(new DateOnly(2026, 9, 8), latest.Released);
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz", latest.Download.ToString());
        Assert.Equal(("SHA-512", 128), (latest.ChecksumAlgorithm, latest.Checksum!.Length));
    }

    [Fact]
    public void KeepsPrereleasesOfPreviewChannels()
    {
        var release = Assert.Single(DotnetReleaseMetadata.ParseReleases(Fixture("releases-11.0.json"), Channel("11.0"), "linux-x64"));

        Assert.Equal("11.0.100-rc.1.26425.128", release.Version);
        Assert.Equal("go-live", release.SupportPhase);
        Assert.False(release.IsLongTermSupport);
    }

    [Theory]
    [InlineData("linux-x64", "dotnet-sdk-10.0.401-linux-x64.tar.gz")]
    [InlineData("linux-arm64", "dotnet-sdk-10.0.401-linux-arm64.tar.gz")]
    [InlineData("linux-musl-x64", "dotnet-sdk-10.0.401-linux-musl-x64.tar.gz")]
    [InlineData("osx-arm64", "dotnet-sdk-10.0.401-osx-arm64.tar.gz")]
    [InlineData("osx-x64", "dotnet-sdk-10.0.401-osx-x64.tar.gz")]
    [InlineData("win-x64", "dotnet-sdk-10.0.401-win-x64.zip")]
    [InlineData("win-arm64", "dotnet-sdk-10.0.401-win-arm64.zip")]
    public void ChoosesTheArchiveOfEachPlatform(string rid, string file)
    {
        var latest = DotnetReleaseMetadata.ParseReleases(Fixture("releases-10.0.json"), Channel("10.0"), rid)[0];

        Assert.EndsWith("/" + file, latest.Download.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(rid[(rid.LastIndexOf('-') + 1)..], latest.Architecture);
    }

    [Fact]
    public void OffersNothingForAPlatformWithoutArchives() => Assert.Empty(DotnetReleaseMetadata.ParseReleases(Fixture("releases-10.0.json"), Channel("10.0"), "freebsd-x64"));

    [Theory]
    [InlineData("Linux", Architecture.X64, false, "linux-x64")]
    [InlineData("Linux", Architecture.Arm64, true, "linux-musl-arm64")]
    [InlineData("OSX", Architecture.Arm64, false, "osx-arm64")]
    [InlineData("Windows", Architecture.X64, false, "win-x64")]
    [InlineData("Windows", Architecture.Arm64, false, "win-arm64")]
    public void NamesThePlatform(string system, Architecture architecture, bool musl, string expected) =>
        Assert.Equal(expected, DotnetSdkEnvironment.RidFor(OSPlatform.Create(system.ToUpperInvariant()), architecture, musl));
}
