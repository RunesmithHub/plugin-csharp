using System.Composition;
using Runesmith.Languages.CSharp;
using Runesmith.LanguageServices;

namespace Runesmith.CSharp;

/// <summary>Runesmith's own C# analyzer, on the C# compiler: completion, hover, go to definition, signature help and problems.</summary>
[Shared]
public sealed class CSharpAnalysis
{
    [Export(typeof(ILanguageAnalyzer))]
    public ILanguageAnalyzer Analyzer { get; } = new CSharpAnalyzer();
}
