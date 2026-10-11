using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Runesmith.Sdk.Languages;
using SymbolKind = Runesmith.Sdk.Languages.SymbolKind;

namespace Runesmith.CSharp.Engine;

/// <summary>The symbols of a C# document as a tree, for the outline and the breadcrumbs, and the symbols of the solution by name.</summary>
internal static class CSharpSymbols
{
    private const int MaxWorkspaceSymbols = 500;

    private static readonly SymbolDisplayFormat MemberFormat = new(
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Gets the namespaces, types and members a document declares, each inside the one that contains it.</summary>
    public static async Task<IReadOnlyList<DocumentSymbol>> DocumentAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
            return [];

        return Children(root.Members, cancellationToken);
    }

    /// <summary>Finds the types and members of the solution's source whose names match a query, the closest names first.</summary>
    public static async Task<IReadOnlyList<WorkspaceSymbol>> WorkspaceAsync(Solution solution, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var found = new List<(WorkspaceSymbol Symbol, int Rank)>();
        foreach (var project in solution.Projects)
        {
            var declarations = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, query, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false);
            foreach (var symbol in declarations.Where(s => !s.IsImplicitlyDeclared && s is not IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.Constructor) }))
            {
                var name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? symbol.ContainingType?.Name ?? symbol.Name : symbol.Name;
                var rank = name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                foreach (var location in symbol.Locations.Where(l => l.IsInSource && !string.IsNullOrEmpty(l.SourceTree?.FilePath)))
                {
                    found.Add((new WorkspaceSymbol(name, KindOf(symbol), CSharpReferences.ToLocation(location))
                    {
                        ContainerName = (symbol.ContainingType ?? (ISymbol?)symbol.ContainingNamespace)?.ToDisplayString(),
                        IsDeprecated = IsObsolete(symbol),
                    }, rank));
                }
            }
        }

        return
        [
            .. found
                .DistinctBy(f => (f.Symbol.Name, f.Symbol.Location))
                .OrderBy(f => f.Rank)
                .ThenBy(f => f.Symbol.Name.Length)
                .ThenBy(f => f.Symbol.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxWorkspaceSymbols)
                .Select(f => f.Symbol),
        ];
    }

    /// <summary>Gets the kind Runesmith shows for a compiler symbol.</summary>
    public static SymbolKind KindOf(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => SymbolKind.Namespace,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => SymbolKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => SymbolKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => SymbolKind.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => SymbolKind.Function,
        INamedTypeSymbol => SymbolKind.Class,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => SymbolKind.Constructor,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => SymbolKind.Operator,
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => SymbolKind.Function,
        IMethodSymbol => SymbolKind.Method,
        IPropertySymbol => SymbolKind.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => SymbolKind.EnumMember,
        IFieldSymbol { IsConst: true } => SymbolKind.Constant,
        IFieldSymbol => SymbolKind.Field,
        IEventSymbol => SymbolKind.Event,
        ITypeParameterSymbol => SymbolKind.TypeParameter,
        _ => SymbolKind.Variable,
    };

    /// <summary>Gets a symbol's signature without its containing type, such as <c>int Add(int, int)</c>.</summary>
    public static string Signature(ISymbol symbol) => symbol.ToDisplayString(MemberFormat);

    internal static bool IsObsolete(ISymbol symbol) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: "ObsoleteAttribute", ContainingNamespace.Name: "System" });

    private static List<DocumentSymbol> Children(IEnumerable<MemberDeclarationSyntax> members, CancellationToken cancellationToken)
    {
        var symbols = new List<DocumentSymbol>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbols.AddRange(Declared(member, cancellationToken));
        }

        return symbols;
    }

    private static IEnumerable<DocumentSymbol> Declared(MemberDeclarationSyntax member, CancellationToken cancellationToken)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax space:
                yield return Symbol(space, space.Name.ToString(), space.Name.Span, SymbolKind.Namespace, null, Children(space.Members, cancellationToken));
                break;
            case EnumDeclarationSyntax type:
                yield return Symbol(type, type.Identifier.ValueText, type.Identifier.Span, SymbolKind.Enum, null,
                    [.. type.Members.Select(m => Symbol(m, m.Identifier.ValueText, m.Identifier.Span, SymbolKind.EnumMember, null, []))]);
                break;
            case TypeDeclarationSyntax type:
                var kind = type switch
                {
                    InterfaceDeclarationSyntax => SymbolKind.Interface,
                    StructDeclarationSyntax => SymbolKind.Struct,
                    RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => SymbolKind.Struct,
                    _ => SymbolKind.Class,
                };
                yield return Symbol(type, type.Identifier.ValueText + type.TypeParameterList, type.Identifier.Span, kind, null, Children(type.Members, cancellationToken));
                break;
            case DelegateDeclarationSyntax type:
                yield return Symbol(type, type.Identifier.ValueText + type.TypeParameterList, type.Identifier.Span, SymbolKind.Function, Parameters(type.ParameterList, type.ReturnType), []);
                break;
            case MethodDeclarationSyntax method:
                yield return Symbol(method, method.Identifier.ValueText + method.TypeParameterList, method.Identifier.Span, SymbolKind.Method, Parameters(method.ParameterList, method.ReturnType), []);
                break;
            case ConstructorDeclarationSyntax constructor:
                yield return Symbol(constructor, constructor.Identifier.ValueText, constructor.Identifier.Span, SymbolKind.Constructor, Parameters(constructor.ParameterList, null), []);
                break;
            case DestructorDeclarationSyntax destructor:
                yield return Symbol(destructor, "~" + destructor.Identifier.ValueText, destructor.Identifier.Span, SymbolKind.Method, null, []);
                break;
            case OperatorDeclarationSyntax @operator:
                yield return Symbol(@operator, "operator " + @operator.OperatorToken.Text, @operator.OperatorToken.Span, SymbolKind.Operator, Parameters(@operator.ParameterList, @operator.ReturnType), []);
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                yield return Symbol(conversion, conversion.ImplicitOrExplicitKeyword.Text + " operator " + conversion.Type, conversion.Type.Span, SymbolKind.Operator,
                    Parameters(conversion.ParameterList, null), []);
                break;
            case PropertyDeclarationSyntax property:
                yield return Symbol(property, property.Identifier.ValueText, property.Identifier.Span, SymbolKind.Property, property.Type.ToString(), []);
                break;
            case IndexerDeclarationSyntax indexer:
                yield return Symbol(indexer, "this" + indexer.ParameterList, indexer.ThisKeyword.Span, SymbolKind.Property, indexer.Type.ToString(), []);
                break;
            case EventDeclarationSyntax @event:
                yield return Symbol(@event, @event.Identifier.ValueText, @event.Identifier.Span, SymbolKind.Event, @event.Type.ToString(), []);
                break;
            case EventFieldDeclarationSyntax events:
                foreach (var variable in events.Declaration.Variables)
                    yield return Symbol(events, variable.Identifier.ValueText, variable.Identifier.Span, SymbolKind.Event, events.Declaration.Type.ToString(), []);
                break;
            case FieldDeclarationSyntax fields:
                var fieldKind = fields.Modifiers.Any(SyntaxKind.ConstKeyword) ? SymbolKind.Constant : SymbolKind.Field;
                foreach (var variable in fields.Declaration.Variables)
                    yield return Symbol(fields, variable.Identifier.ValueText, variable.Identifier.Span, fieldKind, fields.Declaration.Type.ToString(), []);
                break;
        }
    }

    private static DocumentSymbol Symbol(SyntaxNode node, string name, Microsoft.CodeAnalysis.Text.TextSpan selection, SymbolKind kind, string? detail, IReadOnlyList<DocumentSymbol> children) =>
        new(name.Length == 0 ? "?" : name, kind, node.Span.ToRunesmith(), selection.ToRunesmith())
        {
            Detail = detail,
            Children = children,
            IsDeprecated = node is MemberDeclarationSyntax member && member.AttributeLists.SelectMany(list => list.Attributes).Any(a => a.Name.ToString() is "Obsolete" or "ObsoleteAttribute" or "System.Obsolete"),
        };

    private static string Parameters(BaseParameterListSyntax parameters, TypeSyntax? returns)
    {
        var types = string.Join(", ", parameters.Parameters.Select(p => p.Type?.ToString() ?? p.Identifier.ValueText));
        return returns is null ? $"({types})" : $"({types}) : {returns}";
    }
}
