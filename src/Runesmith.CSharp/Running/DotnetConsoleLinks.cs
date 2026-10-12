using System.Composition;
using System.Globalization;
using System.Text.RegularExpressions;
using Runesmith.Sdk.Running;

namespace Runesmith.CSharp.Running;

/// <summary>Finds the source locations in .NET stack traces, such as <c>in /src/Program.cs:line 12</c>, so the Run console and the terminal
/// link them. Runesmith finds the compiler's own form, <c>Program.cs(12,5)</c>, by itself.</summary>
[Export(typeof(IConsoleLinkProvider))]
public sealed partial class DotnetConsoleLinks : IConsoleLinkProvider
{
    public IReadOnlyList<ConsoleLink> FindLinks(string line)
    {
        if (!line.Contains(":line ", StringComparison.Ordinal))
            return [];

        var links = new List<ConsoleLink>();
        foreach (Match match in Frame().Matches(line))
        {
            var path = match.Groups["path"];
            if (int.TryParse(match.Groups["line"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                links.Add(new ConsoleLink(path.Index, match.Index + match.Length - path.Index, path.Value, number, 0));
        }

        return links;
    }

    [GeneratedRegex(@"\bin (?<path>(?:[A-Za-z]:)?[\\/][^:*?""<>|]+?):line (?<line>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Frame();
}
