using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Runesmith.LanguageServices;
using TextSpan = Runesmith.Text.TextSpan;

namespace Runesmith.CSharp.Engine;

/// <summary>The signatures of the call around an offset: method calls, object creations including <c>new(...)</c>, constructor initializers,
/// primary constructor base calls and attributes.</summary>
internal static class CSharpSignatureHelp
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> LibrarySummaries = new(StringComparer.Ordinal);

    private static readonly SymbolDisplayFormat ConstructorFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    public static async ValueTask<SignatureHelpResult?> GetAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null || FindCall(root, offset) is not { } call)
            return null;

        var (methods, chosen) = Candidates(model, call.Owner, offset, cancellationToken);
        if (methods.Count == 0)
            return null;

        var argument = call.Separators.Count(separator => separator.SpanStart < offset);
        var signatures = methods.Select(method => ToSignature(method, model, offset)).ToList();
        var active = chosen is not null ? methods.IndexOf(chosen) : -1;
        if (active < 0)
            active = Math.Max(0, methods.FindIndex(method => method.Parameters.Length > argument || method.Parameters.LastOrDefault()?.IsParams == true));

        var parameters = methods[active].Parameters;
        var activeParameter = parameters.Length == 0 ? 0
            : argument >= parameters.Length && parameters[^1].IsParams ? parameters.Length - 1
            : argument;
        return new SignatureHelpResult(signatures, active, activeParameter);
    }

    private static Call? FindCall(SyntaxNode root, int offset)
    {
        // The token before the caret, so a call whose closing parenthesis is not typed yet is still found.
        var token = root.FindToken(Math.Max(0, offset - 1));

        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case ArgumentListSyntax list when Inside(list.OpenParenToken, list.CloseParenToken, offset) && list.Parent is { } owner:
                    return new Call(owner, list.Arguments.GetSeparators().ToList());
                case AttributeArgumentListSyntax list when Inside(list.OpenParenToken, list.CloseParenToken, offset) && list.Parent is { } attribute:
                    return new Call(attribute, list.Arguments.GetSeparators().ToList());
                case StatementSyntax or MemberDeclarationSyntax when node is not LocalFunctionStatementSyntax:
                    return null;
            }
        }

        return null;
    }

    private static bool Inside(SyntaxToken open, SyntaxToken close, int offset) =>
        offset > open.SpanStart && (close.IsMissing || offset <= close.SpanStart);

    private static (List<IMethodSymbol> Methods, IMethodSymbol? Chosen) Candidates(SemanticModel model, SyntaxNode owner, int offset, CancellationToken cancellationToken)
    {
        IEnumerable<IMethodSymbol> methods;
        switch (owner)
        {
            case InvocationExpressionSyntax invocation:
                methods = model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>();
                break;
            case BaseObjectCreationExpressionSyntax creation:
                var type = model.GetTypeInfo(creation, cancellationToken) is var info && info.Type is INamedTypeSymbol created ? created : info.ConvertedType as INamedTypeSymbol;
                methods = type?.InstanceConstructors ?? [];
                break;
            case ConstructorInitializerSyntax initializer:
                var containing = model.GetEnclosingSymbol(initializer.SpanStart, cancellationToken)?.ContainingType;
                methods = (initializer.IsKind(SyntaxKind.BaseConstructorInitializer) ? containing?.BaseType : containing)?.InstanceConstructors ?? [];
                break;
            case PrimaryConstructorBaseTypeSyntax baseType:
                methods = (model.GetTypeInfo(baseType.Type, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? [];
                break;
            case AttributeSyntax attribute:
                methods = (model.GetTypeInfo(attribute, cancellationToken).Type as INamedTypeSymbol)?.InstanceConstructors ?? [];
                break;
            default:
                return ([], null);
        }

        var accessible = methods.Where(method => model.IsAccessible(offset, method)).ToList();
        var symbol = model.GetSymbolInfo(owner, cancellationToken);
        var chosen = (symbol.Symbol ?? symbol.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
        return (accessible, chosen is null ? null : accessible.FirstOrDefault(method => SymbolEqualityComparer.Default.Equals(method, chosen)));
    }

    private static SignatureEntry ToSignature(IMethodSymbol method, SemanticModel model, int offset)
    {
        var label = new StringBuilder();
        if (method.MethodKind == MethodKind.Constructor)
        {
            label.Append(method.ContainingType.ToDisplayString(ConstructorFormat));
        }
        else
        {
            label.Append(method.ReturnType.ToMinimalDisplayString(model, offset, TypeFormat)).Append(' ').Append(method.Name);
            if (method.TypeParameters.Length > 0)
                label.Append('<').AppendJoin(", ", method.TypeParameters.Select(parameter => parameter.Name)).Append('>');
        }

        label.Append('(');
        var spans = new List<TextSpan>();
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0)
                label.Append(", ");
            var start = label.Length;
            label.Append(Parameter(method.Parameters[i], model, offset));
            spans.Add(TextSpan.FromBounds(start, label.Length));
        }

        label.Append(')');
        return new SignatureEntry(label.ToString(), spans, Documentation(method));
    }

    private static string Parameter(IParameterSymbol parameter, SemanticModel model, int offset)
    {
        var text = new StringBuilder();
        if (parameter.IsParams)
            text.Append("params ");
        text.Append(parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => "",
        });
        text.Append(parameter.Type.ToMinimalDisplayString(model, offset, TypeFormat)).Append(' ').Append(parameter.Name);
        if (parameter.HasExplicitDefaultValue)
            text.Append(" = ").Append(parameter.ExplicitDefaultValue switch { null => "null", string value => $"\"{value}\"", bool value => value ? "true" : "false", var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) });
        return text.ToString();
    }

    // Reading a library's documentation file is slow and its text never changes, so library summaries are kept by documentation id.
    private static string? Documentation(IMethodSymbol method)
    {
        if (!method.Locations.All(location => location.IsInMetadata) || method.GetDocumentationCommentId() is not { } id)
            return Summary(method.GetDocumentationCommentXml());
        return LibrarySummaries.GetOrAdd(id, _ => Summary(method.GetDocumentationCommentXml()));
    }

    /// <summary>Gets the text of a documentation comment's summary, or null.</summary>
    internal static string? Summary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary is null)
                return null;

            var text = new StringBuilder();
            foreach (var node in summary.DescendantNodes())
            {
                if (node is XText value)
                    text.Append(value.Value);
                else if (node is XElement { Name.LocalName: "see" or "seealso" } reference && !reference.Nodes().Any())
                    text.Append((string?)reference.Attribute("langword") ?? ((string?)reference.Attribute("cref"))?.Split(':', '.')[^1]);
                else if (node is XElement { Name.LocalName: "paramref" or "typeparamref" } parameter)
                    text.Append((string?)parameter.Attribute("name"));
            }

            return string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private sealed record Call(SyntaxNode Owner, List<SyntaxToken> Separators);
}
