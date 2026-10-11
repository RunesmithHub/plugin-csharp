using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Runesmith.Sdk.Languages;
using TextSpan = Runesmith.Text.TextSpan;

namespace Runesmith.CSharp.Engine;

/// <summary>The lines of a C# document that fold, and the spans Expand Selection grows through.</summary>
internal static class CSharpStructure
{
    /// <summary>Gets the ranges that fold: namespaces, types, members and blocks that span lines, a run of using directives, regions and
    /// comments of several lines.</summary>
    public static async Task<IReadOnlyList<FoldingRange>> FoldingAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
            return [];

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var ranges = new List<FoldingRange>();
        int Line(int position) => text.Lines.GetLineFromPosition(position).LineNumber;
        void Add(int start, int end, FoldingRangeKind kind, string? collapsed = null)
        {
            var (first, last) = (Line(start), Line(end));
            // A line that goes on after the range, such as "} else {", stays visible.
            if (kind == FoldingRangeKind.Block && ContinuesAfter(text, end))
                last--;
            if (last > first)
                ranges.Add(new FoldingRange(first, last) { Kind = kind, CollapsedText = collapsed });
        }

        foreach (var node in root.DescendantNodes(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax
                    or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or InitializerExpressionSyntax or SwitchStatementSyntax
                    or SwitchExpressionSyntax or CollectionExpressionSyntax:
                    Add(node.SpanStart, node.Span.End, FoldingRangeKind.Block);
                    break;
                case BlockSyntax { Parent: not (BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax) } block:
                    // A block folds from the statement that owns it, such as its if or for line.
                    Add(block.Parent is StatementSyntax or ElseClauseSyntax or CatchClauseSyntax or FinallyClauseSyntax ? block.Parent.SpanStart : block.SpanStart, block.Span.End, FoldingRangeKind.Block);
                    break;
                case AccessorDeclarationSyntax { Body: not null } accessor:
                    Add(accessor.SpanStart, accessor.Span.End, FoldingRangeKind.Block);
                    break;
            }
        }

        if (root is CompilationUnitSyntax { Usings: { Count: > 1 } usings })
            Add(usings[0].SpanStart, usings[^1].Span.End, FoldingRangeKind.Imports);
        foreach (var space in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Where(n => n.Usings.Count > 1))
            Add(space.Usings[0].SpanStart, space.Usings[^1].Span.End, FoldingRangeKind.Imports);

        AddRegions(root, Add);
        AddComments(root, Add);
        return [.. ranges.DistinctBy(r => (r.StartLine, r.EndLine)).OrderBy(r => r.StartLine).ThenByDescending(r => r.EndLine)];
    }

    /// <summary>Gets the spans around an offset from the smallest to the largest: the token, then each syntax node around it.</summary>
    public static async Task<IReadOnlyList<TextSpan>> SelectionRangesAsync(Document document, int offset, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root || root.FullSpan.Length == 0)
            return [];

        var token = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End - 1));
        // Right after a word, the word is what the caret is on, not the punctuation that follows it.
        if (offset > 0 && offset == token.SpanStart && root.FindToken(offset - 1) is var before && before.Span.End == offset && !before.IsKind(SyntaxKind.None)
            && (SyntaxFacts.IsIdentifierPartCharacter(before.Text[^1]) || before.IsKind(SyntaxKind.StringLiteralToken)))
        {
            token = before;
        }

        var spans = new List<TextSpan>();
        void Add(Microsoft.CodeAnalysis.Text.TextSpan span)
        {
            if (span.Length > 0 && (spans.Count == 0 || (span.Length > spans[^1].Length && span.Start <= spans[^1].Start && span.End >= spans[^1].End)))
                spans.Add(span.ToRunesmith());
        }

        if (token.Span.Contains(offset) || token.Span.End == offset)
            Add(token.Span);
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            // The inside of a block or an argument list comes before the whole of it.
            if (node is BlockSyntax { Statements.Count: > 0 } block)
                Add(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(block.Statements[0].SpanStart, block.Statements[^1].Span.End));
            else if (node is BaseArgumentListSyntax { Arguments.Count: > 0 } arguments)
                Add(arguments.Arguments.Span);
            else if (node is BaseParameterListSyntax { Parameters.Count: > 0 } parameters)
                Add(parameters.Parameters.Span);
            Add(node.Span);
        }

        return spans;
    }

    private static bool ContinuesAfter(Microsoft.CodeAnalysis.Text.SourceText text, int end)
    {
        var line = text.Lines.GetLineFromPosition(end);
        for (var i = end; i < line.End; i++)
        {
            var character = text[i];
            if (character == '/' && i + 1 < line.End && text[i + 1] == '/')
                return false;
            if (char.IsLetter(character) || character == '{')
                return true;
        }

        return false;
    }

    private static void AddRegions(SyntaxNode root, Action<int, int, FoldingRangeKind, string?> add)
    {
        var open = new Stack<RegionDirectiveTriviaSyntax>();
        foreach (var directive in root.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsDirective).Select(t => t.GetStructure()))
        {
            if (directive is RegionDirectiveTriviaSyntax region)
            {
                open.Push(region);
            }
            else if (directive is EndRegionDirectiveTriviaSyntax end && open.TryPop(out var start))
            {
                var name = start.EndOfDirectiveToken.LeadingTrivia.ToString().Trim();
                add(start.SpanStart, end.Span.End, FoldingRangeKind.Region, name.Length > 0 ? name : "#region");
            }
        }
    }

    private static void AddComments(SyntaxNode root, Action<int, int, FoldingRangeKind, string?> add)
    {
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            SyntaxTrivia? first = null;
            SyntaxTrivia? last = null;
            foreach (var trivia in token.LeadingTrivia)
            {
                if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    add(trivia.SpanStart, trivia.Span.End, FoldingRangeKind.Comment, null);
                }
                else if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                {
                    first ??= trivia;
                    last = trivia;
                }
                else if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    Flush();
                }
            }

            Flush();

            void Flush()
            {
                if (first is { } start && last is { } end)
                {
                    // A documentation comment ends with its line break; the fold ends on its last line of text.
                    var stop = end.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ? Math.Max(end.SpanStart, end.Span.End - 1) : end.Span.End;
                    add(start.SpanStart, stop, FoldingRangeKind.Comment, null);
                }

                (first, last) = (null, null);
            }
        }
    }
}
