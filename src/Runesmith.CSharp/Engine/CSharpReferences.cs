using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Runesmith.Sdk.Languages;
using Runesmith.Text;

namespace Runesmith.CSharp.Engine;

/// <summary>Find References, Go to Implementation and the occurrences of the symbol under the caret, for C# documents.</summary>
internal static class CSharpReferences
{
    /// <summary>Finds every use of the symbol at an offset in the solution, each as the span of the name.</summary>
    public static async Task<IReadOnlyList<DocumentLocation>> FindAsync(Document document, int offset, bool includeDeclaration, CancellationToken cancellationToken)
    {
        if (await SymbolAtAsync(document, offset, cancellationToken).ConfigureAwait(false) is not { } symbol)
            return [];

        var found = new List<Location>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, cancellationToken).ConfigureAwait(false))
        {
            if (includeDeclaration)
                found.AddRange(referenced.Definition.Locations);
            found.AddRange(referenced.Locations.Where(location => !location.IsImplicit).Select(location => location.Location));
        }

        return ToLocations(found);
    }

    /// <summary>Finds what implements or overrides the symbol at an offset: the types that implement an interface or derive from a class,
    /// and the members that implement or override a member.</summary>
    public static async Task<IReadOnlyList<DocumentLocation>> ImplementationsAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        if (await SymbolAtAsync(document, offset, cancellationToken).ConfigureAwait(false) is not { } symbol)
            return [];

        var solution = document.Project.Solution;
        var found = new List<ISymbol>();
        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } type:
                found.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                found.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            case INamedTypeSymbol type:
                found.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            default:
                if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
                    found.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                if (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride)
                    found.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
        }

        // A symbol that nothing implements, or that is one itself, is its own implementation.
        if (found.Count == 0 && !symbol.IsAbstract && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Interface })
            found.Add(symbol);
        return ToLocations(found.SelectMany(implementation => implementation.Locations));
    }

    /// <summary>Finds the occurrences of the symbol at an offset in its own document, with whether each one writes it.</summary>
    public static async Task<IReadOnlyList<DocumentHighlight>> HighlightsAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        if (await SymbolAtAsync(document, offset, cancellationToken).ConfigureAwait(false) is not { } symbol
            || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return [];
        }

        var tree = root.SyntaxTree;
        var highlights = new Dictionary<Microsoft.CodeAnalysis.Text.TextSpan, DocumentHighlightKind>();
        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, ImmutableHashSet.Create(document), cancellationToken).ConfigureAwait(false);
        foreach (var referenced in references)
        {
            foreach (var declaration in referenced.Definition.Locations.Where(location => location.SourceTree == tree))
                highlights[declaration.SourceSpan] = DocumentHighlightKind.Write;
            foreach (var reference in referenced.Locations.Where(location => !location.IsImplicit && location.Location.SourceTree == tree))
                highlights.TryAdd(reference.Location.SourceSpan, IsWritten(root, reference.Location.SourceSpan) ? DocumentHighlightKind.Write : DocumentHighlightKind.Read);
        }

        return [.. highlights.OrderBy(pair => pair.Key.Start).Select(pair => new DocumentHighlight(pair.Key.ToRunesmith(), pair.Value))];
    }

    /// <summary>Gets the symbol at an offset as its original definition, with an extension method as it is declared.</summary>
    internal static async Task<ISymbol?> SymbolAtAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, cancellationToken).ConfigureAwait(false);
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
            symbol = reduced;
        return symbol is null or INamespaceSymbol { IsGlobalNamespace: true } ? null : symbol.OriginalDefinition;
    }

    /// <summary>Turns compiler locations into places in files, in file and text order, each once.</summary>
    internal static IReadOnlyList<DocumentLocation> ToLocations(IEnumerable<Location> locations) =>
    [
        .. locations
            .Where(location => location.IsInSource && !string.IsNullOrEmpty(location.SourceTree?.FilePath))
            .Select(ToLocation)
            .Distinct()
            .OrderBy(location => location.FilePath, StringComparer.Ordinal)
            .ThenBy(location => location.Start.Line)
            .ThenBy(location => location.Start.Column),
    ];

    internal static DocumentLocation ToLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new DocumentLocation(
            span.Path,
            new TextPosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
            new TextPosition(span.EndLinePosition.Line, span.EndLinePosition.Character));
    }

    private static bool IsWritten(SyntaxNode root, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        while (node.Parent is MemberAccessExpressionSyntax access && access.Name == node)
            node = access;

        return node.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == node,
            PrefixUnaryExpressionSyntax unary => unary.IsKind(SyntaxKind.PreIncrementExpression) || unary.IsKind(SyntaxKind.PreDecrementExpression),
            PostfixUnaryExpressionSyntax unary => unary.IsKind(SyntaxKind.PostIncrementExpression) || unary.IsKind(SyntaxKind.PostDecrementExpression),
            ArgumentSyntax argument => argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword) || argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword),
            _ => false,
        };
    }
}
