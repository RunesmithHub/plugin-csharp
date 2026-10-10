using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Runesmith.LanguageServices;

namespace Runesmith.CSharp.Engine;

/// <summary>Inlay hints for C#: the parameter names of literal arguments, and the types of variables declared with <c>var</c>.</summary>
internal static class CSharpInlayHints
{
    public static async ValueTask<IReadOnlyList<InlayHintEntry>> GetAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return [];
        }

        var hints = new List<InlayHintEntry>();
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case ArgumentListSyntax { Arguments.Count: > 0 } list when list.Arguments.Any(IsLiteral):
                    AddParameterNames(model, list, hints, cancellationToken);
                    break;
                case VariableDeclarationSyntax { Type.IsVar: true, Variables: [{ Initializer: not null } variable] } declaration:
                    AddType(model, model.GetDeclaredSymbol(variable, cancellationToken) as ILocalSymbol, variable.Identifier, declaration.Type, hints);
                    break;
                case ForEachStatementSyntax { Type.IsVar: true } loop:
                    AddType(model, model.GetDeclaredSymbol(loop, cancellationToken) as ILocalSymbol, loop.Identifier, loop.Type, hints);
                    break;
            }
        }

        return hints;
    }

    private static void AddParameterNames(SemanticModel model, ArgumentListSyntax list, List<InlayHintEntry> hints, CancellationToken cancellationToken)
    {
        if (list.Parent is null || model.GetSymbolInfo(list.Parent, cancellationToken).Symbol is not IMethodSymbol method)
            return;

        for (var index = 0; index < list.Arguments.Count; index++)
        {
            var argument = list.Arguments[index];
            if (argument.NameColon is not null || !IsLiteral(argument))
                continue;

            var parameter = index < method.Parameters.Length ? method.Parameters[index] : method.Parameters.LastOrDefault(p => p.IsParams);
            if (parameter is null || parameter.Name.Length == 0 || IsObvious(method, parameter))
                continue;

            hints.Add(new InlayHintEntry(argument.SpanStart, parameter.Name + ":", IsType: false));
        }
    }

    // A var local of a reference type is always nullable, so its annotation says nothing.
    private static void AddType(SemanticModel model, ILocalSymbol? local, SyntaxToken identifier, TypeSyntax type, List<InlayHintEntry> hints)
    {
        if (local?.Type is not { } localType || localType.IsAnonymousType || localType.TypeKind == TypeKind.Error)
            return;

        hints.Add(new InlayHintEntry(identifier.Span.End, ": " + localType.WithNullableAnnotation(NullableAnnotation.None).ToMinimalDisplayString(model, type.SpanStart), IsType: true));
    }

    private static bool IsLiteral(ArgumentSyntax argument) => argument.Expression switch
    {
        LiteralExpressionSyntax => true,
        PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } => true,
        DefaultExpressionSyntax => true,
        _ => false,
    };

    // A name the call already says, such as the value of SetValue, or the one-letter name of an only parameter.
    private static bool IsObvious(IMethodSymbol method, IParameterSymbol parameter) =>
        method.Name.EndsWith(parameter.Name, StringComparison.OrdinalIgnoreCase) || parameter.Name.Length == 1 && method.Parameters.Length == 1;
}
