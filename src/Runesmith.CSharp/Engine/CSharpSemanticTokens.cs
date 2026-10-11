using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Runesmith.Sdk.Languages;
using RoslynSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace Runesmith.CSharp.Engine;

/// <summary>The names in a C# document classified by what they mean: types by their kind, members, parameters, locals and the keywords
/// that are only keywords in their context.</summary>
/// <remarks>Punctuation, strings, numbers and comments are left to the grammar, which colors them the same.</remarks>
internal static class CSharpSemanticTokens
{
    private static readonly Dictionary<string, (string Type, SemanticTokenModifiers Modifiers)> Types = new(StringComparer.Ordinal)
    {
        [ClassificationTypeNames.NamespaceName] = (SemanticTokenTypes.Namespace, SemanticTokenModifiers.None),
        [ClassificationTypeNames.ClassName] = (SemanticTokenTypes.Class, SemanticTokenModifiers.None),
        [ClassificationTypeNames.RecordClassName] = (SemanticTokenTypes.Class, SemanticTokenModifiers.None),
        [ClassificationTypeNames.StructName] = (SemanticTokenTypes.Struct, SemanticTokenModifiers.None),
        [ClassificationTypeNames.RecordStructName] = (SemanticTokenTypes.Struct, SemanticTokenModifiers.None),
        [ClassificationTypeNames.InterfaceName] = (SemanticTokenTypes.Interface, SemanticTokenModifiers.None),
        [ClassificationTypeNames.EnumName] = (SemanticTokenTypes.Enum, SemanticTokenModifiers.None),
        [ClassificationTypeNames.DelegateName] = (SemanticTokenTypes.Type, SemanticTokenModifiers.None),
        [ClassificationTypeNames.TypeParameterName] = (SemanticTokenTypes.TypeParameter, SemanticTokenModifiers.None),
        [ClassificationTypeNames.MethodName] = (SemanticTokenTypes.Method, SemanticTokenModifiers.None),
        [ClassificationTypeNames.ExtensionMethodName] = (SemanticTokenTypes.Method, SemanticTokenModifiers.Static),
        [ClassificationTypeNames.PropertyName] = (SemanticTokenTypes.Property, SemanticTokenModifiers.None),
        [ClassificationTypeNames.FieldName] = (SemanticTokenTypes.Property, SemanticTokenModifiers.None),
        [ClassificationTypeNames.ConstantName] = (SemanticTokenTypes.Variable, SemanticTokenModifiers.Readonly),
        [ClassificationTypeNames.EnumMemberName] = (SemanticTokenTypes.EnumMember, SemanticTokenModifiers.Readonly),
        [ClassificationTypeNames.EventName] = (SemanticTokenTypes.Event, SemanticTokenModifiers.None),
        [ClassificationTypeNames.LocalName] = (SemanticTokenTypes.Variable, SemanticTokenModifiers.None),
        [ClassificationTypeNames.ParameterName] = (SemanticTokenTypes.Parameter, SemanticTokenModifiers.None),
        [ClassificationTypeNames.LabelName] = (SemanticTokenTypes.Label, SemanticTokenModifiers.None),
        [ClassificationTypeNames.Keyword] = (SemanticTokenTypes.Keyword, SemanticTokenModifiers.None),
        [ClassificationTypeNames.ControlKeyword] = (SemanticTokenTypes.Keyword, SemanticTokenModifiers.None),
    };

    /// <summary>Gets the tokens of a document, or of the part of it a span covers, sorted by where they start.</summary>
    public static async Task<IReadOnlyList<SemanticToken>> GetAsync(Document document, Runesmith.Text.TextSpan? span, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var range = span is { } part
            ? RoslynSpan.FromBounds(Math.Clamp(part.Start, 0, text.Length), Math.Clamp(part.End, 0, text.Length))
            : new RoslynSpan(0, text.Length);
        var classified = await Classifier.GetClassifiedSpansAsync(document, range, cancellationToken).ConfigureAwait(false);

        var tokens = new SortedDictionary<int, SemanticToken>();
        var statics = new HashSet<RoslynSpan>();
        foreach (var item in classified)
        {
            if (item.ClassificationType == ClassificationTypeNames.StaticSymbol)
            {
                statics.Add(item.TextSpan);
            }
            else if (Types.TryGetValue(item.ClassificationType, out var known) && item.TextSpan.Length > 0
                && text.Lines.GetLineFromPosition(item.TextSpan.Start).End >= item.TextSpan.End)
            {
                // A name is classified once by the grammar's rules and once by its meaning; the meaning comes last and wins.
                tokens[item.TextSpan.Start] = new SemanticToken(item.TextSpan.ToRunesmith(), known.Type, known.Modifiers);
            }
        }

        return
        [
            .. tokens.Values.Select(token => statics.Contains(token.Span.ToRoslyn()) ? token with { Modifiers = token.Modifiers | SemanticTokenModifiers.Static } : token),
        ];
    }
}
