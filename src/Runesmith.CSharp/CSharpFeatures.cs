using System.Composition;
using Microsoft.CodeAnalysis;
using Runesmith.CSharp.Engine;
using Runesmith.Sdk.Documents;
using Runesmith.Sdk.Languages;
using Runesmith.Text;

namespace Runesmith.CSharp;

/// <summary>Finds the compiler's document for an editor's document, in the workspace the C# analyzer keeps.</summary>
internal static class CSharpRequests
{
    /// <summary>Gets the path the analyzer knows a document by; a document that was never saved has one made of its name.</summary>
    public static string PathOf(IDocument document) => document.FilePath ?? "untitled:" + document.Name;

    public static CSharpAnalyzer Analyzer(this CSharpAnalysis analysis) => (CSharpAnalyzer)analysis.Analyzer;

    public static ValueTask<Document?> FindAsync(this CSharpAnalysis analysis, IDocument document, TextSnapshot snapshot) =>
        analysis.Analyzer().FindAsync(PathOf(document), snapshot);
}

/// <summary>Find References, Go to Implementation and the occurrences of the symbol under the caret, for C#.</summary>
[Export(typeof(IReferenceProvider))]
[Export(typeof(IImplementationProvider))]
[Export(typeof(IDocumentHighlightProvider))]
[Languages(CSharpAnalyzer.LanguageId)]
[Shared]
[method: ImportingConstructor]
public sealed class CSharpNavigationProvider(CSharpAnalysis analysis) : IReferenceProvider, IImplementationProvider, IDocumentHighlightProvider
{
    public async Task<IReadOnlyList<DocumentLocation>> GetReferencesAsync(IDocument document, TextSnapshot snapshot, int offset, bool includeDeclaration,
        CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpReferences.FindAsync(found, offset, includeDeclaration, cancellationToken).ConfigureAwait(false)
            : [];

    public async Task<IReadOnlyList<DocumentLocation>> GetImplementationsAsync(IDocument document, TextSnapshot snapshot, int offset, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpReferences.ImplementationsAsync(found, offset, cancellationToken).ConfigureAwait(false)
            : [];

    public async Task<IReadOnlyList<DocumentHighlight>?> GetDocumentHighlightsAsync(IDocument document, TextSnapshot snapshot, int offset, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpReferences.HighlightsAsync(found, offset, cancellationToken).ConfigureAwait(false)
            : null;
}

/// <summary>The symbols of a C# document, for the outline, the breadcrumbs and Go to Symbol in File.</summary>
[Export(typeof(IDocumentSymbolProvider))]
[Languages(CSharpAnalyzer.LanguageId)]
[Shared]
[method: ImportingConstructor]
public sealed class CSharpDocumentSymbolProvider(CSharpAnalysis analysis) : IDocumentSymbolProvider
{
    public async Task<IReadOnlyList<DocumentSymbol>?> GetDocumentSymbolsAsync(IDocument document, TextSnapshot snapshot, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpSymbols.DocumentAsync(found, cancellationToken).ConfigureAwait(false)
            : null;
}

/// <summary>The types and members of the loaded C# projects by name, for Go to Symbol in Workspace.</summary>
[Export(typeof(IWorkspaceSymbolProvider))]
[Shared]
[method: ImportingConstructor]
public sealed class CSharpWorkspaceSymbolProvider(CSharpAnalysis analysis) : IWorkspaceSymbolProvider
{
    public async Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(string query, CancellationToken cancellationToken) =>
        await CSharpSymbols.WorkspaceAsync(await analysis.Analyzer().GetSolutionAsync().ConfigureAwait(false), query, cancellationToken).ConfigureAwait(false);
}

/// <summary>Folding, Expand Selection and semantic highlighting for C#.</summary>
[Export(typeof(IFoldingRangeProvider))]
[Export(typeof(ISelectionRangeProvider))]
[Export(typeof(ISemanticTokensProvider))]
[Languages(CSharpAnalyzer.LanguageId)]
[Shared]
public sealed class CSharpSyntaxProvider : IFoldingRangeProvider, ISelectionRangeProvider, ISemanticTokensProvider
{
    private readonly CSharpAnalysis analysis;

    [ImportingConstructor]
    public CSharpSyntaxProvider(CSharpAnalysis analysis)
    {
        this.analysis = analysis;
        // Names are classified by the projects' references, which arrive when the folder's projects finish loading.
        analysis.Analyzer().SolutionReplaced += () => changed?.Invoke(this, new LanguageFeatureChangedEventArgs(null));
    }

    private event EventHandler<LanguageFeatureChangedEventArgs>? changed;

    event EventHandler<LanguageFeatureChangedEventArgs>? ISemanticTokensProvider.Changed
    {
        add => changed += value;
        remove => changed -= value;
    }

    public async Task<IReadOnlyList<FoldingRange>?> GetFoldingRangesAsync(IDocument document, TextSnapshot snapshot, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpStructure.FoldingAsync(found, cancellationToken).ConfigureAwait(false)
            : null;

    public async Task<IReadOnlyList<TextSpan>?> GetSelectionRangesAsync(IDocument document, TextSnapshot snapshot, int offset, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpStructure.SelectionRangesAsync(found, offset, cancellationToken).ConfigureAwait(false)
            : null;

    public async Task<IReadOnlyList<SemanticToken>?> GetSemanticTokensAsync(IDocument document, TextSnapshot snapshot, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpSemanticTokens.GetAsync(found, null, cancellationToken).ConfigureAwait(false)
            : null;

    public async Task<IReadOnlyList<SemanticToken>?> GetSemanticTokensAsync(IDocument document, TextSnapshot snapshot, TextSpan span, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpSemanticTokens.GetAsync(found, span, cancellationToken).ConfigureAwait(false)
            : null;
}

/// <summary>Call hierarchies of functions and type hierarchies of types, for C#.</summary>
[Export(typeof(ICallHierarchyProvider))]
[Export(typeof(ITypeHierarchyProvider))]
[Languages(CSharpAnalyzer.LanguageId)]
[Shared]
[method: ImportingConstructor]
public sealed class CSharpHierarchyProvider(CSharpAnalysis analysis) : ICallHierarchyProvider, ITypeHierarchyProvider
{
    public async Task<IReadOnlyList<HierarchyItem>> PrepareCallHierarchyAsync(IDocument document, TextSnapshot snapshot, int offset, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpHierarchies.PrepareCallsAsync(found, offset, cancellationToken).ConfigureAwait(false)
            : [];

    public async Task<IReadOnlyList<HierarchyCall>> GetIncomingCallsAsync(HierarchyItem item, CancellationToken cancellationToken) =>
        await CSharpHierarchies.IncomingCallsAsync(await analysis.Analyzer().GetSolutionAsync().ConfigureAwait(false), item, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HierarchyCall>> GetOutgoingCallsAsync(HierarchyItem item, CancellationToken cancellationToken) =>
        await CSharpHierarchies.OutgoingCallsAsync(await analysis.Analyzer().GetSolutionAsync().ConfigureAwait(false), item, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HierarchyItem>> PrepareTypeHierarchyAsync(IDocument document, TextSnapshot snapshot, int offset, CancellationToken cancellationToken) =>
        await analysis.FindAsync(document, snapshot).ConfigureAwait(false) is { } found
            ? await CSharpHierarchies.PrepareTypesAsync(found, offset, cancellationToken).ConfigureAwait(false)
            : [];

    public async Task<IReadOnlyList<HierarchyItem>> GetSupertypesAsync(HierarchyItem item, CancellationToken cancellationToken) =>
        await CSharpHierarchies.SupertypesAsync(await analysis.Analyzer().GetSolutionAsync().ConfigureAwait(false), item, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HierarchyItem>> GetSubtypesAsync(HierarchyItem item, CancellationToken cancellationToken) =>
        await CSharpHierarchies.SubtypesAsync(await analysis.Analyzer().GetSolutionAsync().ConfigureAwait(false), item, cancellationToken).ConfigureAwait(false);
}
