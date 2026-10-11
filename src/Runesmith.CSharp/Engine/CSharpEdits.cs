using Microsoft.CodeAnalysis;
using Runesmith.LanguageServices;
using Runesmith.Text;

namespace Runesmith.CSharp.Engine;

/// <summary>Turns the difference between two solutions into file edits by line and column.</summary>
internal static class CSharpEdits
{
    /// <summary>Gets the edits that turn the documents of <paramref name="before"/> into those of <paramref name="after"/>.</summary>
    /// <param name="versionOf">Gets the open document version a path's text in <paramref name="before"/> is, or null for a file on disk.</param>
    /// <exception cref="AnalyzerRefusalException">The change adds or removes files.</exception>
    public static async Task<IReadOnlyList<FileEdit>> DiffAsync(Solution before, Solution after, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var edits = new List<FileEdit>();
        foreach (var project in after.GetChanges(before).GetProjectChanges())
        {
            if (project.GetAddedDocuments().Any() || project.GetRemovedDocuments().Any())
                throw new AnalyzerRefusalException(Strings.EditChangesFiles);

            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var old = before.GetDocument(id);
                var changed = after.GetDocument(id);
                if (old?.FilePath is not { } path || changed is null)
                    continue;

                var text = await old.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var changes = await changed.GetTextChangesAsync(old, cancellationToken).ConfigureAwait(false);
                var positions = changes.Select(change =>
                {
                    var span = text.Lines.GetLinePositionSpan(change.Span);
                    return new PositionEdit(
                        new TextPosition(span.Start.Line, span.Start.Character),
                        new TextPosition(span.End.Line, span.End.Character),
                        change.NewText ?? "");
                }).ToList();
                if (positions.Count > 0)
                    edits.Add(new FileEdit(path, versionOf(path), positions));
            }
        }

        return edits;
    }
}
