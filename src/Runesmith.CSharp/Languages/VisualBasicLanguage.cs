using System.Composition;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Languages;

/// <summary>Visual Basic: its files, highlighting, comments and brackets.</summary>
public sealed class VisualBasicLanguage
{
    [Export(typeof(LanguageDefinition))]
    public LanguageDefinition Vb { get; } = new("vb", "Visual Basic")
    {
        Extensions = [".vb", ".brs", ".vbs", ".bas"],
        ScopeName = "source.asp.vb.net",
        LineComment = "'",
        Brackets = [('{', '}'), ('[', ']'), ('(', ')'), ('<', '>')],
        Icon = "file-code",
    };

    [Export(typeof(GrammarDefinition))]
    public GrammarDefinition VbGrammar { get; } = GrammarFiles.Find("source.asp.vb.net", "vb/asp-vb-net.tmlanguage.json");
}
