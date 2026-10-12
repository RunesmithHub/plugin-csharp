using System.Composition;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Languages;

/// <summary>F#: its files, highlighting, comments and brackets.</summary>
public sealed class FSharpLanguage
{
    [Export(typeof(LanguageDefinition))]
    public LanguageDefinition Fsharp { get; } = new("fsharp", "F#")
    {
        Extensions = [".fs", ".fsi", ".fsx", ".fsscript"],
        ScopeName = "source.fsharp",
        LineComment = "//",
        BlockComment = ("(*", "*)"),
        Brackets = [('{', '}'), ('[', ']'), ('(', ')')],
        Icon = "file-code",
    };

    [Export(typeof(GrammarDefinition))]
    public GrammarDefinition FsharpGrammar { get; } = GrammarFiles.Find("source.fsharp", "fsharp/fsharp.tmLanguage.json");
}
