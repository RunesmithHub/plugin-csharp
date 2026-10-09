using System.Composition;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp;

/// <summary>The C# language: its files, highlighting, comments and brackets.</summary>
public sealed class CSharpLanguage
{
    /// <summary>The language id language servers and settings use.</summary>
    public const string Id = "csharp";

    [Export(typeof(LanguageDefinition))]
    public LanguageDefinition Definition { get; } = new(Id, "C#")
    {
        Extensions = [".cs", ".csx"],
        ScopeName = "source.cs",
        LineComment = "//",
        BlockComment = ("/*", "*/"),
        Brackets = [('(', ')'), ('[', ']'), ('{', '}')],
        Quotes = ['"', '\''],
        Icon = "file-code",
    };
}
