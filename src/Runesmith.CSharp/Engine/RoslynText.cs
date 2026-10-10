using RoslynSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using RoslynTextChange = Microsoft.CodeAnalysis.Text.TextChange;
using TextChange = Runesmith.Text.TextChange;
using TextSpan = Runesmith.Text.TextSpan;

namespace Runesmith.CSharp.Engine;

/// <summary>Converts between Runesmith's text types and the compiler's.</summary>
internal static class RoslynText
{
    public static RoslynSpan ToRoslyn(this TextSpan span) => new(span.Start, span.Length);

    public static TextSpan ToRunesmith(this RoslynSpan span) => new(span.Start, span.Length);

    public static RoslynTextChange ToRoslyn(this TextChange change) => new(change.Span.ToRoslyn(), change.NewText);

    public static TextChange ToRunesmith(this RoslynTextChange change) => new(change.Span.ToRunesmith(), change.NewText ?? "");
}
