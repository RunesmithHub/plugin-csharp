using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace Runesmith.CSharp.Engine;

/// <summary>Decides which suggestions insert parentheses after their name, because the code at the word must call them: methods, except
/// where a method group may stand, such as in <c>nameof</c> or where a delegate is expected, and types after <c>new</c>. Their text becomes a
/// snippet with the caret between the parentheses, or after them when nothing they name takes arguments.</summary>
/// <remarks>One is made for each computed list, for the word the list is for; it looks up the symbols it needs only when a suggestion
/// asks.</remarks>
internal sealed class CSharpCallParentheses
{
    private static readonly CSharpCallParentheses None = new(Place.None, null, 0, null, null);

    // Text with these is more than a name, such as an override's whole signature, and is left as it is.
    private static readonly SearchValues<char> NotInNames = SearchValues.Create("()$\\}<>");

    private readonly Place place;
    private readonly SemanticModel? model;
    private readonly int position;
    private readonly ExpressionSyntax? receiver;
    private readonly INamespaceOrTypeSymbol? container;
    private Dictionary<string, bool>? methods;
    private ILookup<string, INamedTypeSymbol>? types;

    private CSharpCallParentheses(Place place, SemanticModel? model, int position, ExpressionSyntax? receiver, INamespaceOrTypeSymbol? container)
    {
        this.place = place;
        this.model = model;
        this.position = position;
        this.receiver = receiver;
        this.container = container;
    }

    private enum Place
    {
        /// <summary>Nothing gets parentheses.</summary>
        None,

        /// <summary>A name that is called when it is a method, such as <c>Fo</c> in <c>var x = Fo</c>.</summary>
        Name,

        /// <summary>A member after a <c>.</c>, called when it is a method.</summary>
        Member,

        /// <summary>The type of an object creation, after <c>new</c>.</summary>
        New,
    }

    /// <summary>Works out what the word between two offsets is, in a document the compiler has.</summary>
    public static async Task<CSharpCallParentheses> ForAsync(Document document, int wordStart, int wordEnd, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (NextCharacter(text, wordEnd) is '(' or '<' or '[' or '{')
            return None;

        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
            return None;

        var previous = root.FindToken(wordStart, findInsideTrivia: true);
        if (previous.SpanStart >= wordStart)
            previous = previous.GetPreviousToken(includeZeroWidth: false, includeSkipped: true, includeDirectives: true, includeDocumentationComments: true);
        if (previous.Parent is not { } parent || parent.AncestorsAndSelf().Any(node => node.IsStructuredTrivia || node is UsingDirectiveSyntax))
            return None;

        var word = wordEnd > wordStart && root.FindToken(wordStart) is { RawKind: (int)SyntaxKind.IdentifierToken } identifier && identifier.SpanStart == wordStart
            ? identifier.Parent as SimpleNameSyntax
            : null;

        switch (previous.Kind())
        {
            case SyntaxKind.NewKeyword when parent is ObjectCreationExpressionSyntax:
                return await CreateAsync(document, Place.New, wordStart, null, null, cancellationToken).ConfigureAwait(false);

            case SyntaxKind.DotToken when parent is QualifiedNameSyntax qualified:
                return OutermostName(qualified).Parent is ObjectCreationExpressionSyntax creation && creation.Type == OutermostName(qualified)
                    ? await CreateAsync(document, Place.New, wordStart, null, qualified.Left, cancellationToken).ConfigureAwait(false)
                    : None;

            case SyntaxKind.DotToken or SyntaxKind.MinusGreaterThanToken when parent is MemberAccessExpressionSyntax access:
                return await CreateCallAsync(document, Place.Member, wordStart, access.Expression, access, previous, cancellationToken).ConfigureAwait(false);

            case SyntaxKind.DotToken when parent is MemberBindingExpressionSyntax binding
                && binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>(conditional => conditional.WhenNotNull.Span.Contains(binding.Span)) is { } conditional:
                return await CreateCallAsync(document, Place.Member, wordStart, conditional.Expression, conditional, previous, cancellationToken).ConfigureAwait(false);

            case SyntaxKind.DotToken or SyntaxKind.MinusGreaterThanToken or SyntaxKind.ColonColonToken:
                return None;
        }

        // A name after a modifier, such as override, declares a member instead of using one.
        if (parent is MemberDeclarationSyntax or IncompleteMemberSyntax || word is not null && SyntaxFacts.IsInTypeOnlyContext(word))
            return None;

        return await CreateCallAsync(document, Place.Name, wordStart, null, word, previous, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the snippet a suggestion inserts instead of its plain text, or null when it inserts only its name.</summary>
    public string? SnippetFor(RoslynCompletionItem item, string insert)
    {
        if (place == Place.None || insert.Length == 0 || insert.AsSpan().ContainsAny(NotInNames))
            return null;

        if (place == Place.New)
            return IsType(item.Tags) ? ConstructorSnippet(item, insert) : null;

        if (!item.Tags.Contains(WellKnownTags.Method) && !item.Tags.Contains(WellKnownTags.ExtensionMethod))
            return null;

        // A method found only by importing its namespace may take arguments; the caret goes between the parentheses then.
        return Methods().TryGetValue(insert, out var takesArguments) && !takesArguments ? insert + "()" : insert + "($0)";
    }

    private static async Task<CSharpCallParentheses> CreateAsync(Document document, Place place, int position, ExpressionSyntax? receiver, NameSyntax? qualifier,
        CancellationToken cancellationToken)
    {
        if (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            return None;

        var container = qualifier is null ? null : model.GetSymbolInfo(qualifier, cancellationToken).Symbol as INamespaceOrTypeSymbol;
        if (qualifier is not null && container is null)
            return None;

        return new CSharpCallParentheses(place, model, position, receiver, container);
    }

    // A method's name is called unless the code around it may take the method group itself.
    private static async Task<CSharpCallParentheses> CreateCallAsync(Document document, Place place, int position, ExpressionSyntax? receiver, ExpressionSyntax? expression,
        SyntaxToken previous, CancellationToken cancellationToken)
    {
        if (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            return None;

        return TakesMethodGroup(model, expression, previous, position, cancellationToken)
            ? None
            : new CSharpCallParentheses(place, model, position, receiver, null);
    }

    /// <summary>Whether the code at the word may take a method group: an argument of <c>nameof</c>, the operand of <c>&amp;</c>, or where a
    /// delegate is expected, such as an argument of a delegate parameter, the right of <c>+=</c> on an event, or a delegate's initializer.</summary>
    private static bool TakesMethodGroup(SemanticModel model, ExpressionSyntax? expression, SyntaxToken previous, int position, CancellationToken cancellationToken)
    {
        // With a word, its place is the expression's parent; without one, the token before the word says where it goes.
        var parent = expression?.Parent ?? previous.Parent;
        switch (parent)
        {
            case ArgumentSyntax argument when argument.Parent is BaseArgumentListSyntax list:
                return ArgumentTakesMethodGroup(model, list, list.Arguments.IndexOf(argument), argument.NameColon?.Name.Identifier.ValueText, cancellationToken);

            case BaseArgumentListSyntax list when expression is null:
                var index = list.Arguments.GetSeparators().Count(separator => separator.SpanStart < position);
                return ArgumentTakesMethodGroup(model, list, index, null, cancellationToken);

            case NameColonSyntax { Parent: ArgumentSyntax { Parent: BaseArgumentListSyntax list } named }:
                return ArgumentTakesMethodGroup(model, list, list.Arguments.IndexOf(named), named.NameColon?.Name.Identifier.ValueText, cancellationToken);

            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.AddressOfExpression):
                return true;

            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }:
                return !declaration.Type.IsVar && IsDelegate(model.GetTypeInfo(declaration.Type, cancellationToken).Type);

            case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property }:
                return IsDelegate(model.GetTypeInfo(property.Type, cancellationToken).Type);

            case EqualsValueClauseSyntax { Parent: ParameterSyntax { Type: { } type } }:
                return IsDelegate(model.GetTypeInfo(type, cancellationToken).Type);

            case AssignmentExpressionSyntax assignment when expression is null ? assignment.OperatorToken == previous : assignment.Right == expression:
                return IsDelegate(model.GetTypeInfo(assignment.Left, cancellationToken).Type);

            case ReturnStatementSyntax or ArrowExpressionClauseSyntax:
                return IsDelegate(ReturnType(model, parent, cancellationToken));

            case CastExpressionSyntax cast:
                return IsDelegate(model.GetTypeInfo(cast.Type, cancellationToken).Type);

            default:
                return false;
        }
    }

    // nameof takes any name; elsewhere a method group fits when every method the call could be has a delegate parameter there.
    private static bool ArgumentTakesMethodGroup(SemanticModel model, BaseArgumentListSyntax list, int index, string? name, CancellationToken cancellationToken)
    {
        if (list.Parent is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
            return true;

        if (list.Parent is not { } call)
            return false;

        var info = model.GetSymbolInfo(call, cancellationToken);
        var methods = (info.Symbol is { } symbol ? [symbol] : info.CandidateSymbols).OfType<IMethodSymbol>().ToList();
        if (methods.Count == 0)
            return false;

        foreach (var method in methods)
        {
            var parameter = name is not null
                ? method.Parameters.FirstOrDefault(p => p.Name == name)
                : index < method.Parameters.Length ? method.Parameters[index] : method.Parameters.LastOrDefault(p => p.IsParams);
            var type = parameter is { IsParams: true, Type: IArrayTypeSymbol array } && index >= method.Parameters.Length - 1 ? array.ElementType : parameter?.Type;
            if (!IsDelegate(type))
                return false;
        }

        return true;
    }

    private static ITypeSymbol? ReturnType(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax:
                    return null;
                case MethodDeclarationSyntax or LocalFunctionStatementSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax:
                    return (model.GetDeclaredSymbol(ancestor, cancellationToken) as IMethodSymbol)?.ReturnType;
                case BasePropertyDeclarationSyntax:
                    return (model.GetDeclaredSymbol(ancestor, cancellationToken) as IPropertySymbol)?.Type;
            }
        }

        return null;
    }

    private static bool IsDelegate(ITypeSymbol? type) =>
        type is { TypeKind: TypeKind.Delegate } or { SpecialType: SpecialType.System_Delegate or SpecialType.System_MulticastDelegate };

    private static bool IsType(IEnumerable<string> tags) =>
        tags.Any(tag => tag is WellKnownTags.Class or WellKnownTags.Structure or WellKnownTags.Delegate);

    private static NameSyntax OutermostName(QualifiedNameSyntax name)
    {
        NameSyntax outermost = name;
        while (outermost.Parent is QualifiedNameSyntax parent && parent.Left == outermost)
            outermost = parent;
        return outermost;
    }

    private static char NextCharacter(Microsoft.CodeAnalysis.Text.SourceText text, int offset)
    {
        for (var i = offset; i < text.Length; i++)
        {
            if (text[i] is not (' ' or '\t'))
                return text[i];
        }

        return '\0';
    }

    // new Name($0) when a constructor the code may call takes arguments, new Name() when none does, and nothing for types that cannot be
    // created, such as interfaces, abstract and static classes. A generic type gets a place for its type arguments first.
    private string? ConstructorSnippet(RoslynCompletionItem item, string insert)
    {
        var isGeneric = item.DisplayTextSuffix == "<>";
        if (FindType(item, insert, isGeneric) is not { } type || !CanCreate(type, out var takesArguments))
            return null;

        var arguments = takesArguments ? "($0)" : "()";
        return isGeneric ? $"{insert}<$1>{arguments}" : takesArguments ? insert + arguments : insert + "()";
    }

    private INamedTypeSymbol? FindType(RoslynCompletionItem item, string name, bool isGeneric)
    {
        types ??= (container is null ? model!.LookupNamespacesAndTypes(position) : model!.LookupNamespacesAndTypes(position, container))
            .OfType<INamedTypeSymbol>()
            .ToLookup(type => type.Name, StringComparer.Ordinal);

        if (types[name].FirstOrDefault(type => type.Arity > 0 == isGeneric) is { } found)
            return found;

        // A type from a namespace that is not imported yet, which accepting the suggestion imports; its namespace is shown beside it.
        if (string.IsNullOrEmpty(item.InlineDescription))
            return null;

        var compilation = model!.Compilation;
        if (!isGeneric)
            return compilation.GetTypeByMetadataName($"{item.InlineDescription}.{name}");

        for (var arity = 1; arity <= 4; arity++)
        {
            if (compilation.GetTypeByMetadataName($"{item.InlineDescription}.{name}`{arity}") is { } generic)
                return generic;
        }

        return null;
    }

    private bool CanCreate(INamedTypeSymbol type, out bool takesArguments)
    {
        takesArguments = false;
        switch (type.TypeKind)
        {
            case TypeKind.Delegate:
                takesArguments = true;
                return true;
            case TypeKind.Struct:
                takesArguments = type.InstanceConstructors.Any(c => c.Parameters.Length > 0 && model!.IsAccessible(position, c));
                return true;
            case TypeKind.Class when !type.IsAbstract && !type.IsStatic:
                var constructors = type.InstanceConstructors.Where(c => model!.IsAccessible(position, c)).ToList();
                takesArguments = constructors.Any(c => c.Parameters.Length > 0);
                return constructors.Count > 0;
            default:
                return false;
        }
    }

    // Each name's methods at the word, and whether any of them takes arguments.
    private Dictionary<string, bool> Methods()
    {
        if (methods is not null)
            return methods;

        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var method in Candidates().OfType<IMethodSymbol>())
            found[method.Name] = found.GetValueOrDefault(method.Name) || method.Parameters.Length > 0;
        return methods = found;
    }

    private ImmutableArray<ISymbol> Candidates()
    {
        var semantics = model!;
        if (receiver is null)
            return semantics.LookupSymbols(position);

        if (receiver is BaseExpressionSyntax)
            return semantics.LookupBaseMembers(position);

        // A type's name before the dot reaches its static members.
        if (semantics.GetSymbolInfo(receiver).Symbol is INamedTypeSymbol named)
            return semantics.LookupStaticMembers(position, named);

        var type = semantics.GetTypeInfo(receiver).Type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        // An instance reaches its type's instance members and extension methods, not its static ones.
        return type is null
            ? []
            : semantics.LookupSymbols(position, type, includeReducedExtensionMethods: true)
                .RemoveAll(symbol => symbol.IsStatic && symbol is not IMethodSymbol { MethodKind: MethodKind.ReducedExtension });
    }
}
