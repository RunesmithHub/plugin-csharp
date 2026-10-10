using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Runesmith.LanguageServices;
using Runesmith.Text;

namespace Runesmith.CSharp.Engine;

/// <summary>Hover, go to definition and problems for C# documents.</summary>
internal static class CSharpNavigation
{
    /// <summary>Describes the symbol at an offset: its signature as code, then its documentation.</summary>
    public static async ValueTask<HoverResult?> HoverAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        if (QuickInfoService.GetService(document) is not { } service
            || await service.GetQuickInfoAsync(document, offset, cancellationToken).ConfigureAwait(false) is not { } info)
        {
            return null;
        }

        var markdown = new StringBuilder();
        foreach (var section in info.Sections)
        {
            var text = section.Text.Trim();
            if (text.Length == 0)
                continue;

            if (markdown.Length > 0)
                markdown.Append("\n\n");
            if (section.Kind == QuickInfoSectionKinds.Description)
                markdown.Append("```csharp\n").Append(text).Append("\n```");
            else
                markdown.Append(Escape(text));
        }

        return markdown.Length == 0 ? null : new HoverResult(markdown.ToString(), info.Span.ToRunesmith());
    }

    /// <summary>Finds where the symbol at an offset is declared in source.</summary>
    public static async ValueTask<IReadOnlyList<SourceLocation>> DefinitionAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, cancellationToken).ConfigureAwait(false);
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
            symbol = reduced;
        if (symbol is null)
            return [];

        return
        [
            .. symbol.OriginalDefinition.Locations
                .Where(location => location.IsInSource && !string.IsNullOrEmpty(location.SourceTree?.FilePath))
                .Select(location =>
                {
                    var span = location.GetLineSpan();
                    return new SourceLocation(
                        span.Path,
                        new TextPosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                        new TextPosition(span.EndLinePosition.Line, span.EndLinePosition.Character));
                }),
        ];
    }

    /// <summary>Gets the compiler's problems in a document, without analyzers, one member at a time with <paramref name="yield"/> in between,
    /// so a request the user waits for does not wait for the whole document.</summary>
    public static async ValueTask<IReadOnlyList<Problem>> DiagnosticsAsync(
        Document document, bool isAdhoc, Func<CancellationToken, ValueTask> yield, CancellationToken cancellationToken)
    {
        if (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            return [];

        var root = await model.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<Diagnostic>(model.SyntaxTree.GetDiagnostics(cancellationToken));
        found.AddRange(model.GetDeclarationDiagnostics(cancellationToken: cancellationToken));
        foreach (var span in BodyUnits(root))
        {
            await yield(cancellationToken).ConfigureAwait(false);
            found.AddRange(model.GetMethodBodyDiagnostics(span, cancellationToken));
        }

        return
        [
            .. found
                .Where(d => d.Severity != DiagnosticSeverity.Hidden && d.Location.IsInSource && !(isAdhoc && AdhocProject.IgnoredProblems.Contains(d.Id)))
                .DistinctBy(d => (d.Id, d.Location.SourceSpan))
                .Select(d => new Problem(
                    d.Location.SourceSpan.ToRunesmith(),
                    d.Severity switch
                    {
                        DiagnosticSeverity.Error => ProblemSeverity.Error,
                        DiagnosticSeverity.Warning => ProblemSeverity.Warning,
                        _ => ProblemSeverity.Information,
                    },
                    d.GetMessage(System.Globalization.CultureInfo.CurrentCulture),
                    d.Id)),
        ];
    }

    // Members that are not types: each one's bodies are bound on their own, so the work can pause between them.
    private static IEnumerable<Microsoft.CodeAnalysis.Text.TextSpan> BodyUnits(SyntaxNode root) =>
        root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .Where(member => member is not BaseNamespaceDeclarationSyntax and not BaseTypeDeclarationSyntax)
            .Select(member => member.FullSpan);

    // Documentation is plain text from the compiler; these characters would otherwise format it as Markdown.
    private static string Escape(string text) =>
        text.Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);
}
