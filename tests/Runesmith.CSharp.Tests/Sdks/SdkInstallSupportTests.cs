using System.Text;
using Runesmith.CSharp.Sdks;

namespace Runesmith.CSharp.Tests.Sdks;

public sealed class SdkInstallSupportTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("runesmith-sdk-archives-").FullName;

    [Theory]
    [InlineData("10.0.401", "10.0.400", 1)]
    [InlineData("10.0.100", "10.0.100-rc.2.25502.107", 1)]
    [InlineData("11.0.100-rc.2.1", "11.0.100-rc.1.26425.128", 1)]
    [InlineData("10.0.1000", "10.0.999", 1)]
    [InlineData("25.0.1+8", "25.0.1+9", -1)]
    [InlineData("25.0.1", "25", 1)]
    [InlineData("8.0.392", "8.0.392.0", 0)]
    public void OrdersVersions(string newer, string older, int expected) => Assert.Equal(expected, Math.Sign(VersionText.Compare(newer, older)));

    [Fact]
    public void IgnoresTheBuildWhenAsked() => Assert.Equal(0, VersionText.Compare("25.0.1+8", "25.0.1", includeBuild: false));

    [Theory]
    [InlineData("SHA-512")]
    [InlineData("sha256")]
    [InlineData(null)]
    public async Task AcceptsAFileThatMatchesItsChecksum(string? algorithm)
    {
        var data = Encoding.UTF8.GetBytes("the SDK");
        var path = Write("good.bin", data);
        var expected = algorithm == "sha256" ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) : TestArchives.Sha512(data);

        await Checksums.VerifyAsync(path, expected, algorithm, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RejectsAFileThatDoesNotMatchItsChecksum()
    {
        var path = Write("bad.bin", Encoding.UTF8.GetBytes("a changed SDK"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Checksums.VerifyAsync(path, TestArchives.Sha512(Encoding.UTF8.GetBytes("the SDK")), "SHA-512", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsAnAlgorithmItCannotCheck() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Checksums.VerifyAsync(Write("any.bin", [1]), "abcd", "md4", TestContext.Current.CancellationToken));

    [Fact]
    public void UnpacksTarGzWithModesAndLinksAndNothingOutsideTheTarget()
    {
        var archive = Write("sdk.tar.gz", TestArchives.TarGz(
            new ArchiveEntry("jdk/bin/java", "#!", TestArchives.Executable),
            new ArchiveEntry("jdk/release", "JAVA_VERSION=\"25\""),
            new ArchiveEntry("jdk/current") { LinkTarget = "bin" },
            new ArchiveEntry("jdk/escape") { LinkTarget = "../../outside" },
            new ArchiveEntry("../outside.txt", "nope")));
        var target = Path.Combine(folder, "out");

        SdkArchives.Extract(archive, target, TestContext.Current.CancellationToken);

        Assert.Equal("#!", File.ReadAllText(Path.Combine(target, "jdk", "bin", "java")));
        Assert.False(File.Exists(Path.Combine(folder, "outside.txt")));
        Assert.False(new FileInfo(Path.Combine(target, "jdk", "escape")).Exists);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(target, "jdk", "bin", "java")).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Path.Combine(target, "jdk", "release")).HasFlag(UnixFileMode.UserExecute));
            Assert.Equal("bin", new DirectoryInfo(Path.Combine(target, "jdk", "current")).LinkTarget);
        }
    }

    [Fact]
    public void UnpacksZipWithModes()
    {
        var archive = Write("sdk.zip", TestArchives.Zip(new ArchiveEntry("dotnet", "host", TestArchives.Executable), new ArchiveEntry("sdk/10.0.401/dotnet.dll", "sdk")));
        var target = Path.Combine(folder, "zip");

        SdkArchives.Extract(archive, target, TestContext.Current.CancellationToken);

        Assert.Equal("sdk", File.ReadAllText(Path.Combine(target, "sdk", "10.0.401", "dotnet.dll")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(target, "dotnet")).HasFlag(UnixFileMode.UserExecute));
    }

    public static TheoryData<string, byte[]> UnsafeArchives => new()
    {
        { "tar.gz", TestArchives.TarGz(new ArchiveEntry("tool/run", "#!"), new ArchiveEntry("../outside.txt", "nope")) },
        { "tar.gz", TestArchives.TarGz(new ArchiveEntry("tool/run", "#!"), new ArchiveEntry("tool/../../outside.txt", "nope")) },
        { "tar.gz", TestArchives.TarGz(new ArchiveEntry("tool/run", "#!"), new ArchiveEntry("tool/escape") { LinkTarget = "../../outside.txt" }) },
        { "zip", TestArchives.Zip(new ArchiveEntry("tool/run", "#!"), new ArchiveEntry("../outside.txt", "nope")) },
    };

    [Theory]
    [MemberData(nameof(UnsafeArchives))]
    public void RefusesAnEntryOutsideTheTargetWhenAsked(string kind, byte[] data)
    {
        var archive = Write("tool." + kind, data);
        var target = Path.Combine(folder, "strict", "out");

        Assert.Throws<InvalidDataException>(() => SdkArchives.Extract(archive, target, TestContext.Current.CancellationToken, refuseUnsafeEntries: true));

        Assert.False(File.Exists(Path.Combine(folder, "strict", "outside.txt")));
        Assert.False(new FileInfo(Path.Combine(target, "tool", "escape")).Exists);
    }

    [Fact]
    public void UnpacksASafeArchiveWhenRefusingUnsafeEntries()
    {
        var archive = Write("tool.tar.gz", TestArchives.TarGz(new ArchiveEntry("tool/run", "#!", TestArchives.Executable), new ArchiveEntry("tool/./lib.dll", "dll")));
        var target = Path.Combine(folder, "safe");

        SdkArchives.Extract(archive, target, TestContext.Current.CancellationToken, refuseUnsafeEntries: true);

        Assert.Equal("dll", File.ReadAllText(Path.Combine(target, "tool", "lib.dll")));
    }

    [Fact]
    public void RejectsFilesThatAreNotArchives() =>
        Assert.Throws<InvalidDataException>(() => SdkArchives.Extract(Write("page.html", Encoding.UTF8.GetBytes("<html>")), Path.Combine(folder, "x"), TestContext.Current.CancellationToken));

    [Fact]
    public async Task KeepsFreshMetadataAndFallsBackToOldMetadataWhenOffline()
    {
        var http = new FakeHttp().Serve("https://example.test/index.json", "fresh");
        var cache = new MetadataCache(http.Client(), folder, TimeSpan.FromDays(1));
        var token = TestContext.Current.CancellationToken;

        Assert.Equal("fresh", await cache.GetAsync(new Uri("https://example.test/index.json"), "index.json", token));
        Assert.Equal("fresh", await cache.GetAsync(new Uri("https://example.test/index.json"), "index.json", token));
        Assert.Single(http.Requests);

        File.SetLastWriteTimeUtc(Path.Combine(folder, "index.json"), DateTime.UtcNow.AddDays(-2));
        var offline = new MetadataCache(new FakeHttp().Client(), folder, TimeSpan.FromDays(1));
        Assert.Equal("fresh", await offline.GetAsync(new Uri("https://example.test/index.json"), "index.json", token));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}
