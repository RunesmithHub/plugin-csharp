using Runesmith.CSharp.Engine;
using Runesmith.Sdk.Languages;
using Runesmith.Text;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class SemanticTokensTests
{
    private const string Source = """
        namespace Sample;

        public interface IShape { }

        public enum Color { Red }

        public struct Point { }

        public static class Maths
        {
            public const int Limit = 10;

            public static int Twice<T>(this int value, T other)
            {
                var local = value * 2;
                return local + Limit;
            }
        }

        public class Circle : IShape
        {
            public Color Color { get; set; } = Color.Red;

            public Point Center() => default;
        }
        """;

    private static Dictionary<string, string> Files() => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Shapes.cs"] = Source,
        ["Program.cs"] = "\n",
    };

    private static async Task<List<(string Text, string Type, SemanticTokenModifiers Modifiers)>> TokensAsync(TestProject project, TextSpan? span = null)
    {
        project.Open("Shapes.cs", Source);
        var snapshot = project.Snapshot("Shapes.cs");
        var tokens = await CSharpSemanticTokens.GetAsync(await project.DocumentAsync("Shapes.cs"), span, TestContext.Current.CancellationToken);
        return [.. tokens.Select(t => (snapshot.GetText(t.Span), t.Type, t.Modifiers))];
    }

    [Fact]
    public async Task NamesAreClassifiedByWhatTheyMean()
    {
        await using var project = await TestProject.CreateAsync(Files());

        var tokens = await TokensAsync(project);

        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Sample", SemanticTokenTypes.Namespace));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("IShape", SemanticTokenTypes.Interface));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Color", SemanticTokenTypes.Enum));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Red", SemanticTokenTypes.EnumMember));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Point", SemanticTokenTypes.Struct));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Circle", SemanticTokenTypes.Class));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("T", SemanticTokenTypes.TypeParameter));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("value", SemanticTokenTypes.Parameter));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("local", SemanticTokenTypes.Variable));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Center", SemanticTokenTypes.Method));
        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Color", SemanticTokenTypes.Property));
    }

    [Fact]
    public async Task StaticAndConstantNamesCarryTheirModifiers()
    {
        await using var project = await TestProject.CreateAsync(Files());

        var tokens = await TokensAsync(project);

        Assert.Contains(tokens, t => t.Text == "Twice" && t.Type == SemanticTokenTypes.Method && t.Modifiers.HasFlag(SemanticTokenModifiers.Static));
        Assert.Contains(tokens, t => t.Text == "Maths" && t.Type == SemanticTokenTypes.Class && t.Modifiers.HasFlag(SemanticTokenModifiers.Static));
        Assert.Contains(tokens, t => t.Text == "Limit" && t.Modifiers.HasFlag(SemanticTokenModifiers.Readonly));
        Assert.Contains(tokens, t => t.Text == "Center" && !t.Modifiers.HasFlag(SemanticTokenModifiers.Static));
    }

    [Fact]
    public async Task TokensAreSortedDoNotOverlapAndStayOnOneLine()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Shapes.cs", Source);
        var snapshot = project.Snapshot("Shapes.cs");

        var tokens = await CSharpSemanticTokens.GetAsync(await project.DocumentAsync("Shapes.cs"), null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(tokens);
        for (var i = 1; i < tokens.Count; i++)
            Assert.True(tokens[i].Span.Start >= tokens[i - 1].Span.End);
        Assert.All(tokens, t => Assert.DoesNotContain('\n', snapshot.GetText(t.Span)));
    }

    [Fact]
    public async Task TokensOfASpanAreOnlyThoseInsideIt()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var start = Source.IndexOf("public class Circle", StringComparison.Ordinal);
        var span = new TextSpan(start, Source.Length - start);

        var tokens = await TokensAsync(project, span);

        Assert.Contains(tokens, t => (t.Text, t.Type) == ("Circle", SemanticTokenTypes.Class));
        Assert.DoesNotContain(tokens, t => t.Text is "Maths" or "Twice" or "local");
    }
}
