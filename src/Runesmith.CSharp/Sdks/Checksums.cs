using System.Security.Cryptography;

namespace Runesmith.CSharp.Sdks;

/// <summary>Checks a downloaded file against the checksum its vendor publishes.</summary>
internal static class Checksums
{
    /// <summary>Checks a file's checksum; does nothing when there is none to check against.</summary>
    /// <param name="path">The file.</param>
    /// <param name="expected">The checksum as hexadecimal, or null.</param>
    /// <param name="algorithm">The algorithm, such as <c>SHA-512</c> or <c>sha256</c>, or null to tell it from the checksum's length.</param>
    /// <param name="cancellationToken">Stops reading the file.</param>
    /// <exception cref="InvalidDataException">The file does not match the checksum.</exception>
    /// <exception cref="NotSupportedException">The algorithm is not one Runesmith checks.</exception>
    public static async Task VerifyAsync(string path, string? expected, string? algorithm, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return;

        expected = expected.Trim();
        var name = Algorithm(string.IsNullOrWhiteSpace(algorithm) ? null : algorithm, expected.Length);
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await CryptographicOperations.HashDataAsync(name, stream, cancellationToken).ConfigureAwait(false);
            if (!Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The download does not match its published {name.Name} checksum, so it was not installed.");
        }
    }

    private static HashAlgorithmName Algorithm(string? algorithm, int hexLength) =>
        (algorithm?.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant(), hexLength) switch
        {
            ("SHA1", _) or (null, 40) => HashAlgorithmName.SHA1,
            ("SHA256", _) or (null, 64) => HashAlgorithmName.SHA256,
            ("SHA384", _) or (null, 96) => HashAlgorithmName.SHA384,
            ("SHA512", _) or (null, 128) => HashAlgorithmName.SHA512,
            _ => throw new NotSupportedException($"Runesmith cannot check a {algorithm ?? "checksum of this length"} checksum."),
        };
}
