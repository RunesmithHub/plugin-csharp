using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using Runesmith.CSharp.Engine;
using RoslynTextChange = Microsoft.CodeAnalysis.Text.TextChange;
using TextSpan = Runesmith.Text.TextSpan;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class AdditionalChangesTests
{
    [Fact]
    public void AUsingMergedWithTheInsertedWordIsSplitOff()
    {
        var old = SourceText.From("class C { StringBu }");
        var word = new TextSpan(10, "StringBu".Length);
        var merged = new RoslynTextChange(new Microsoft.CodeAnalysis.Text.TextSpan(0, 18), "using System.Text;\nclass C { StringBuilder");

        var changes = CSharpCompletion.AdditionalChanges(old, [merged], word, "StringBuilder");

        var change = Assert.Single(changes);
        Assert.Equal(0, change.Span.Start);
        Assert.Equal("using System.Text;\n", change.NewText);
    }

    [Fact]
    public void InsertingOnlyTheWordHasNoAdditionalChanges()
    {
        var old = SourceText.From("x.Wri");
        var change = new RoslynTextChange(new Microsoft.CodeAnalysis.Text.TextSpan(2, 3), "WriteLine");

        Assert.Empty(CSharpCompletion.AdditionalChanges(old, ImmutableArray.Create(change), new TextSpan(2, 3), "WriteLine"));
    }

    [Fact]
    public void AChangeThatDoesNotEndInTheInsertedWordIsLeftAlone()
    {
        var old = SourceText.From("override ToStr");
        var change = new RoslynTextChange(new Microsoft.CodeAnalysis.Text.TextSpan(0, 14), "public override string ToString() => base.ToString();");

        Assert.Empty(CSharpCompletion.AdditionalChanges(old, ImmutableArray.Create(change), new TextSpan(9, 5), "ToString()"));
    }
}
