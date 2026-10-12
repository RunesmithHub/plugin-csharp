using System.Composition;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Languages;

/// <summary>Razor: its files, highlighting, comments and brackets.</summary>
public sealed class RazorLanguage
{
    [Export(typeof(LanguageDefinition))]
    public LanguageDefinition Razor { get; } = new("razor", "Razor")
    {
        Extensions = [".cshtml", ".razor"],
        ScopeName = "text.html.cshtml",
        BlockComment = ("<!--", "-->"),
        Brackets = [('{', '}'), ('(', ')')],
        Icon = "code",
    };

    [Export(typeof(GrammarDefinition))]
    public GrammarDefinition RazorGrammar { get; } = GrammarFiles.Find("text.html.cshtml", "razor/cshtml.tmLanguage.json");
}
