using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Languages;

/// <summary>Finds the TextMate grammar files the plugin ships, in the <c>grammars</c> folder beside its assembly.</summary>
internal static class GrammarFiles
{
    private static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(GrammarFiles).Assembly.Location) ?? "", "grammars");

    /// <summary>Gets the grammar of a scope from a file of the folder, such as <c>go/go.tmLanguage.json</c>.</summary>
    public static GrammarDefinition Find(string scopeName, string file) => new(scopeName, Path.Combine(Folder, Path.Combine(file.Split('/'))));
}
