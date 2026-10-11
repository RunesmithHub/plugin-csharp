using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Engine;

/// <summary>Where a hierarchy item's symbol is declared, so it can be found again in the solution as it is when its calls or types are
/// asked for.</summary>
internal sealed record HierarchyAnchor(string FilePath, int Offset);

/// <summary>Call hierarchies of methods, constructors, properties and local functions, and type hierarchies of types, for C#.</summary>
internal static class CSharpHierarchies
{
    /// <summary>Gets the function at an offset as a hierarchy item, or nothing.</summary>
    public static async Task<IReadOnlyList<HierarchyItem>> PrepareCallsAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        var symbol = await CSharpReferences.SymbolAtAsync(document, offset, cancellationToken).ConfigureAwait(false);
        return symbol is IMethodSymbol or IPropertySymbol or IEventSymbol && Item(symbol) is { } item ? [item] : [];
    }

    /// <summary>Gets the functions that call an item's function, each with the places in it that call.</summary>
    public static async Task<IReadOnlyList<HierarchyCall>> IncomingCallsAsync(Solution solution, HierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item, cancellationToken).ConfigureAwait(false) is not { } symbol)
            return [];

        var calls = new List<HierarchyCall>();
        foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            if (!caller.IsDirect || Item(caller.CallingSymbol) is not { } callerItem)
                continue;

            calls.Add(new HierarchyCall(callerItem, CSharpReferences.ToLocations(caller.Locations)));
        }

        return Sorted(calls);
    }

    /// <summary>Gets the functions an item's function calls, each with the places in the item's function that call it.</summary>
    public static async Task<IReadOnlyList<HierarchyCall>> OutgoingCallsAsync(Solution solution, HierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item, cancellationToken).ConfigureAwait(false) is not { } symbol)
            return [];

        var sites = new Dictionary<ISymbol, List<Location>>(SymbolEqualityComparer.Default);
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var declaration = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            if (solution.GetDocument(declaration.SyntaxTree) is not { } document
                || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            {
                continue;
            }

            foreach (var node in declaration.DescendantNodes(n => n == declaration || n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or BaseTypeDeclarationSyntax)))
            {
                SyntaxNode name;
                ISymbol? called;
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        (called, name) = (model.GetSymbolInfo(invocation, cancellationToken).Symbol, NameOf(invocation.Expression));
                        break;
                    case BaseObjectCreationExpressionSyntax creation:
                        (called, name) = (model.GetSymbolInfo(creation, cancellationToken).Symbol, creation is ObjectCreationExpressionSyntax typed ? typed.Type : creation);
                        break;
                    default:
                        continue;
                }

                if (called is IMethodSymbol { ReducedFrom: { } reduced })
                    called = reduced;
                if (called is null || !called.OriginalDefinition.Locations.Any(l => l.IsInSource))
                    continue;

                if (!sites.TryGetValue(called.OriginalDefinition, out var places))
                    sites[called.OriginalDefinition] = places = [];
                places.Add(name.GetLocation());
            }
        }

        return Sorted([.. sites.Select(pair => Item(pair.Key) is { } called ? new HierarchyCall(called, CSharpReferences.ToLocations(pair.Value)) : null).OfType<HierarchyCall>()]);
    }

    /// <summary>Gets the type at an offset as a hierarchy item, or nothing.</summary>
    public static async Task<IReadOnlyList<HierarchyItem>> PrepareTypesAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        var symbol = await CSharpReferences.SymbolAtAsync(document, offset, cancellationToken).ConfigureAwait(false);
        return symbol is INamedTypeSymbol && Item(symbol) is { } item ? [item] : [];
    }

    /// <summary>Gets the base class and the interfaces an item's type names, when they are declared in source.</summary>
    public static async Task<IReadOnlyList<HierarchyItem>> SupertypesAsync(Solution solution, HierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item, cancellationToken).ConfigureAwait(false) is not INamedTypeSymbol type)
            return [];

        var bases = new List<INamedTypeSymbol>();
        if (type.BaseType is { SpecialType: SpecialType.None } parent)
            bases.Add(parent);
        bases.AddRange(type.Interfaces);
        return [.. bases.Select(b => Item(b.OriginalDefinition)).OfType<HierarchyItem>().OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Gets the types that derive from an item's type or implement it directly.</summary>
    public static async Task<IReadOnlyList<HierarchyItem>> SubtypesAsync(Solution solution, HierarchyItem item, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(solution, item, cancellationToken).ConfigureAwait(false) is not INamedTypeSymbol type)
            return [];

        var derived = new List<INamedTypeSymbol>();
        if (type.TypeKind == TypeKind.Interface)
        {
            derived.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false));
            derived.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        else
        {
            derived.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        return
        [
            .. derived.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Select(d => Item(d.OriginalDefinition)).OfType<HierarchyItem>()
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Makes a hierarchy item of a symbol declared in source; an accessor counts as its property or event.</summary>
    internal static HierarchyItem? Item(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { AssociatedSymbol: { } owner })
            symbol = owner;
        symbol = symbol.OriginalDefinition;
        if (symbol.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference || string.IsNullOrEmpty(reference.SyntaxTree.FilePath)
            || symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree == reference.SyntaxTree) is not { } name)
        {
            return null;
        }

        var whole = Location.Create(reference.SyntaxTree, reference.Span);
        return new HierarchyItem(DisplayName(symbol), CSharpSymbols.KindOf(symbol), CSharpReferences.ToLocation(whole))
        {
            Detail = (symbol.ContainingType ?? (ISymbol?)symbol.ContainingNamespace) is { } container && container is not INamespaceSymbol { IsGlobalNamespace: true }
                ? container.ToDisplayString()
                : null,
            SelectionLocation = CSharpReferences.ToLocation(name),
            Data = new HierarchyAnchor(reference.SyntaxTree.FilePath, name.SourceSpan.Start),
        };
    }

    private static string DisplayName(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor => CSharpSymbols.Signature(constructor),
        IMethodSymbol method => method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters)
            .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType)),
        INamedTypeSymbol type => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        _ => symbol.Name,
    };

    private static async Task<ISymbol?> ResolveAsync(Solution solution, HierarchyItem item, CancellationToken cancellationToken)
    {
        if (item.Data is not HierarchyAnchor anchor)
            return null;

        var id = solution.GetDocumentIdsWithFilePath(anchor.FilePath).FirstOrDefault();
        if (id is null || solution.GetDocument(id) is not { } document)
            return null;

        return await CSharpReferences.SymbolAtAsync(document, anchor.Offset, cancellationToken).ConfigureAwait(false);
    }

    private static ExpressionSyntax NameOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name,
        MemberBindingExpressionSyntax binding => binding.Name,
        _ => expression,
    };

    private static IReadOnlyList<HierarchyCall> Sorted(List<HierarchyCall> calls) =>
        [.. calls.OrderBy(c => c.Item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Item.Location.FilePath, StringComparer.Ordinal)];
}
