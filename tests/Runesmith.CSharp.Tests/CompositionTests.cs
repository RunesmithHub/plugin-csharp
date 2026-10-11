using System.Composition;
using System.Composition.Hosting;
using Runesmith.CSharp.Engine;
using Runesmith.LanguageServices;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void EveryLanguageFeatureIsExportedForCSharp()
    {
        using var container = new ContainerConfiguration().WithAssembly(typeof(CSharpAnalysis).Assembly).CreateContainer();
        var features = new Features();
        container.SatisfyImports(features);

        IEnumerable<LanguageMetadata>[] byLanguage =
        [
            features.References.Select(f => f.Metadata), features.Implementations.Select(f => f.Metadata), features.Highlights.Select(f => f.Metadata),
            features.DocumentSymbols.Select(f => f.Metadata), features.Folding.Select(f => f.Metadata), features.Selection.Select(f => f.Metadata),
            features.Tokens.Select(f => f.Metadata), features.Calls.Select(f => f.Metadata), features.Types.Select(f => f.Metadata),
        ];
        Assert.All(byLanguage, metadata => Assert.True(Assert.Single(metadata).Serves(CSharpAnalyzer.LanguageId)));
        Assert.Single(features.WorkspaceSymbols);
    }

    [Fact]
    public void TheFeaturesWorkOnTheWorkspaceOfTheAnalyzerRunesmithOpensDocumentsIn()
    {
        using var container = new ContainerConfiguration().WithAssembly(typeof(CSharpAnalysis).Assembly).CreateContainer();
        var features = new Features();
        container.SatisfyImports(features);

        var analyzer = Assert.Single(container.GetExports<ILanguageAnalyzer>());
        Assert.Same(analyzer, container.GetExport<CSharpAnalysis>().Analyzer);
        Assert.Same(features.References.Single().Value, features.Highlights.Single().Value);
        Assert.Same(features.Folding.Single().Value, features.Tokens.Single().Value);
        Assert.Same(features.Calls.Single().Value, features.Types.Single().Value);
    }

    private sealed class Features
    {
        [ImportMany]
        public IEnumerable<Lazy<IReferenceProvider, LanguageMetadata>> References { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<IImplementationProvider, LanguageMetadata>> Implementations { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<IDocumentHighlightProvider, LanguageMetadata>> Highlights { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<IDocumentSymbolProvider, LanguageMetadata>> DocumentSymbols { get; set; } = [];

        [ImportMany]
        public IEnumerable<IWorkspaceSymbolProvider> WorkspaceSymbols { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<IFoldingRangeProvider, LanguageMetadata>> Folding { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<ISelectionRangeProvider, LanguageMetadata>> Selection { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<ISemanticTokensProvider, LanguageMetadata>> Tokens { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<ICallHierarchyProvider, LanguageMetadata>> Calls { get; set; } = [];

        [ImportMany]
        public IEnumerable<Lazy<ITypeHierarchyProvider, LanguageMetadata>> Types { get; set; } = [];
    }
}
