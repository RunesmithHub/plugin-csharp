using System.Globalization;

namespace Runesmith.CSharp.Sdks;

/// <summary>Orders version texts such as <c>10.0.401</c>, <c>10.0.100-rc.1.25451.107</c> or <c>25.0.1+8</c>: numbers by value, a
/// prerelease before its release, then the build after a <c>+</c>.</summary>
internal static class VersionText
{
    /// <summary>Gets a comparer that orders versions from oldest to newest.</summary>
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create((left, right) => Compare(left, right));

    /// <summary>Compares two versions; a negative result means <paramref name="left"/> is older.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <param name="includeBuild">Whether the build after a <c>+</c> counts; without it, <c>25.0.1+8</c> and <c>25.0.1</c> are equal.</param>
    public static int Compare(string? left, string? right, bool includeBuild = true)
    {
        var a = Parse(left ?? "");
        var b = Parse(right ?? "");
        var result = CompareNumbers(a.Core, b.Core);
        if (result != 0)
            return result;

        result = (a.Prerelease.Length == 0, b.Prerelease.Length == 0) switch
        {
            (true, false) => 1,
            (false, true) => -1,
            _ => CompareIdentifiers(a.Prerelease, b.Prerelease),
        };
        return result != 0 || !includeBuild ? result : CompareIdentifiers(a.Build, b.Build);
    }

    /// <summary>Gets whether a version is a prerelease, such as <c>11.0.100-rc.1.25451.107</c>.</summary>
    public static bool IsPrerelease(string version) => Parse(version).Prerelease.Length > 0;

    private static (string[] Core, string[] Prerelease, string[] Build) Parse(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var build = plus < 0 ? "" : version[(plus + 1)..];
        var rest = plus < 0 ? version : version[..plus];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        var prerelease = dash < 0 ? "" : rest[(dash + 1)..];
        var core = dash < 0 ? rest : rest[..dash];
        return (Split(core), Split(prerelease), Split(build));
    }

    private static string[] Split(string text) => text.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);

    private static int CompareNumbers(string[] a, string[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var result = Number(i < a.Length ? a[i] : "0").CompareTo(Number(i < b.Length ? b[i] : "0"));
            if (result != 0)
                return result;
        }

        return 0;
    }

    private static int CompareIdentifiers(string[] a, string[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var numeric = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x) & long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var result = numeric ? x.CompareTo(y) : string.CompareOrdinal(a[i], b[i]);
            if (result != 0)
                return result;
        }

        return a.Length.CompareTo(b.Length);
    }

    private static long Number(string part)
    {
        var end = 0;
        while (end < part.Length && char.IsAsciiDigit(part[end]))
            end++;
        return end > 0 && long.TryParse(part.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
