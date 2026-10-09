using System.Globalization;
using System.Text.RegularExpressions;
using Runesmith.Sdk.Build;
using Runesmith.Text;

namespace Runesmith.CSharp;

/// <summary>Reads the errors and warnings MSBuild writes, such as <c>Program.cs(3,5): error CS0103: The name 'x' does not exist [App.csproj]</c>.</summary>
public static partial class MSBuildDiagnosticParser
{
    /// <summary>The source diagnostics from a build carry.</summary>
    public const string Source = "build";

    /// <summary>Parses one line of build output, or returns null when it is not a diagnostic.</summary>
    /// <param name="target">The solution or project being built; relative paths are resolved against its folder when the line names no
    /// project, and errors of tools such as <c>MSBUILD : error ...</c> are reported on it.</param>
    public static Diagnostic? Parse(string line, string target)
    {
        var match = Pattern().Match(line);
        if (!match.Success)
            return null;

        var project = match.Groups["project"].Success ? match.Groups["project"].Value.Trim() : null;
        var baseDirectory = project is not null && Path.IsPathRooted(project) ? Path.GetDirectoryName(project)! : Path.GetDirectoryName(target)!;
        var path = match.Groups["path"].Value.Trim();
        var isTool = !Path.IsPathRooted(path) && !Path.HasExtension(path);
        path = isTool ? project ?? target : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

        var start = new TextPosition(Number(match, "line"), Number(match, "column"));
        TextPosition? end = match.Groups["endLine"].Success ? new TextPosition(Number(match, "endLine"), Number(match, "endColumn")) : null;
        var severity = match.Groups["severity"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
        return new Diagnostic(path, start, end, severity, match.Groups["message"].Value.Trim(), Source) { Code = match.Groups["code"].Value };
    }

    // MSBuild positions start at 1; a missing position means the start of the file.
    private static int Number(Match match, string group) =>
        match.Groups[group].Success ? Math.Max(0, int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) - 1) : 0;

    [GeneratedRegex(
        @"^\s*(?<path>[^\s(][^(]*?)(\((?<line>\d+)(,(?<column>\d+))?(,(?<endLine>\d+),(?<endColumn>\d+))?\))?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(\s+\[(?<project>[^\[\]]+)\])?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
