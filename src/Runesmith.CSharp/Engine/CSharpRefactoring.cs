using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Runesmith.LanguageServices;
using RoslynSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using TextChange = Runesmith.Text.TextChange;
using TextSpan = Runesmith.Text.TextSpan;

namespace Runesmith.CSharp.Engine;

/// <summary>Rename and formatting for C#, through the compiler's renamer and formatter.</summary>
internal static class CSharpRefactoring
{
    /// <summary>Finds the declared or referenced symbol whose name is at an offset.</summary>
    /// <exception cref="AnalyzerRefusalException">The symbol is not declared in source, so it cannot be renamed.</exception>
    public static async Task<RenameSite?> PrepareRenameAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        if (await FindAsync(document, offset, cancellationToken).ConfigureAwait(false) is not var (token, symbol))
            return null;

        if (!symbol.Locations.Any(location => location.IsInSource))
            throw new AnalyzerRefusalException(Strings.RenameInLibrary(symbol.Name));

        return new RenameSite(new TextSpan(token.Span.Start, token.Span.Length), token.ValueText);
    }

    /// <summary>Renames the symbol at an offset everywhere in the solution.</summary>
    public static async Task<IReadOnlyList<FileEdit>> RenameAsync(Document document, SourceDocument source, int offset, string newName,
        Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName) && !(newName.StartsWith('@') && SyntaxFacts.IsValidIdentifier(newName[1..])))
            throw new AnalyzerRefusalException(Strings.RenameInvalidName(newName));

        if (await FindAsync(document, offset, cancellationToken).ConfigureAwait(false) is not var (_, symbol) || !symbol.Locations.Any(l => l.IsInSource))
            return [];

        var solution = document.Project.Solution;
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, cancellationToken).ConfigureAwait(false);
        return await CSharpEdits.DiffAsync(solution, renamed, path =>
            CSharpDocuments.PathComparer.Equals(path, source.Path) ? source.Version : versionOf(path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Formats a document, or a span of it, with the editor's indentation.</summary>
    public static async Task<IReadOnlyList<TextChange>> FormatAsync(Document document, TextSpan? span, int tabSize, bool insertSpaces, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return [];

        var options = (await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false))
            .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, !insertSpaces)
            .WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, tabSize)
            .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, tabSize)
            .WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n");
        var services = document.Project.Solution.Workspace;
        var changes = span is { } within
            ? Formatter.GetFormattedTextChanges(root, new RoslynSpan(within.Start, within.Length), services, options, cancellationToken)
            : Formatter.GetFormattedTextChanges(root, services, options, cancellationToken);
        return [.. changes.Select(change => change.ToRunesmith())];
    }

    private static async Task<(SyntaxToken Token, ISymbol Symbol)?> FindAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
            return null;

        var token = root.FindToken(offset);
        if (!token.IsKind(SyntaxKind.IdentifierToken) && offset > 0)
            token = root.FindToken(offset - 1);
        if (!token.IsKind(SyntaxKind.IdentifierToken) || offset < token.Span.Start || offset > token.Span.End)
            return null;

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, token.SpanStart, cancellationToken).ConfigureAwait(false);
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
            symbol = reduced;
        return symbol is null ? null : (token, symbol);
    }
}
