using Runesmith.LanguageServices;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class CallParenthesesTests
{
    private const string Types = """

        class Point
        {
            public Point(int x, int y) { }
        }

        class Empty { }

        struct Size
        {
            public Size(int width) { }
        }

        abstract class Shape { }

        static class Helpers { }
        """;

    [Fact]
    public async Task AMethodThatTakesArgumentsPutsTheCaretBetweenItsParentheses()
    {
        var item = await ItemAsync("Console.WriteL$$\n", "WriteLine");

        Assert.True(item.IsSnippet);
        Assert.Equal("WriteLine($0)", item.InsertText);
    }

    [Fact]
    public async Task AMethodWithoutArgumentsPutsTheCaretAfterItsParentheses()
    {
        var item = await ItemAsync("var list = new List<int>();\nlist.Cle$$\n", "Clear");

        Assert.True(item.IsSnippet);
        Assert.Equal("Clear()", item.InsertText);
    }

    [Fact]
    public async Task MembersAfterADotGetParenthesesBeforeTheirNameIsTyped()
    {
        var item = await ItemAsync("Console.$$\n", "WriteLine", CompletionTriggerKind.Character, '.');

        Assert.Equal("WriteLine($0)", item.InsertText);
    }

    [Fact]
    public async Task ALocalFunctionCalledByItsNameGetsParentheses()
    {
        var item = await ItemAsync("var greeting = \"hi\";\nGree$$;\n\nvoid Greet(string name) { }\n", "Greet");

        Assert.Equal("Greet($0)", item.InsertText);
    }

    [Fact]
    public async Task AnExtensionMethodCountsItsArgumentsWithoutTheReceiver()
    {
        var item = await ItemAsync("int[] numbers = [1];\nnumbers.Revers$$\n", "Reverse<>");

        Assert.Equal("Reverse()", item.InsertText);
    }

    [Fact]
    public async Task ACallWhoseParenthesesAreTypedAlreadyGetsOnlyTheName()
    {
        var item = await ItemAsync("Console.WriteL$$(\"hi\");\n", "WriteLine");

        Assert.False(item.IsSnippet);
        Assert.Null(item.InsertText);
    }

    [Theory]
    [InlineData("var name = nameof(Console.WriteL$$);\n")]
    [InlineData("Action write = Console.WriteL$$;\n")]
    [InlineData("new List<string>().ForEach(Console.WriteL$$);\n")]
    [InlineData("Action write;\nwrite = Console.WriteL$$;\n")]
    [InlineData("Action write = (Action)Console.WriteL$$;\n")]
    public async Task AMethodGroupWhereOneIsTakenGetsOnlyTheName(string code)
    {
        var item = await ItemAsync(code, "WriteLine");

        Assert.False(item.IsSnippet);
        Assert.Null(item.InsertText);
    }

    [Fact]
    public async Task AnEventHandlerSubscribedWithPlusEqualsGetsOnlyItsName()
    {
        var item = await ItemAsync("AppDomain.CurrentDomain.ProcessExit += OnEx$$;\n\nvoid OnExit(object? sender, EventArgs e) { }\n", "OnExit");

        Assert.False(item.IsSnippet);
    }

    [Fact]
    public async Task AnArgumentOfADelegateParameterGetsOnlyTheNameBeforeItIsTyped()
    {
        var item = await ItemAsync("new List<int>().ForEach($$);\n\nvoid Print(int value) { }\n", "Print");

        Assert.False(item.IsSnippet);
    }

    [Fact]
    public async Task AnArgumentOfAnyOtherParameterIsCalled()
    {
        var item = await ItemAsync("Console.WriteLine(Coun$$);\n\nint Count() => 1;\n", "Count");

        Assert.Equal("Count()", item.InsertText);
    }

    [Theory]
    [InlineData("var p = new Poi$$", "Point", "Point($0)")]
    [InlineData("var e = new Emp$$", "Empty", "Empty()")]
    [InlineData("var s = new Siz$$", "Size", "Size($0)")]
    [InlineData("var list = new Lis$$", "List<>", "List<$1>($0)")]
    [InlineData("var a = new $$", "Action<>", "Action<$1>($0)")]
    [InlineData("var x = new System.Collections.Generic.Dictionar$$", "Dictionary<>", "Dictionary<$1>($0)")]
    public async Task ATypeAfterNewGetsTheParenthesesOfItsConstructors(string code, string label, string expected)
    {
        var item = await ItemAsync(code + ";\n" + Types, label);

        Assert.True(item.IsSnippet);
        Assert.Equal(expected, item.InsertText);
    }

    [Theory]
    [InlineData("var s = new Shap$$", "Shape")]
    [InlineData("var h = new Helper$$", "Helpers")]
    [InlineData("var d = new IDisposab$$", "IDisposable")]
    [InlineData("var p = new Poi$$ { }", "Point")]
    [InlineData("Poi$$ p = null!", "Point")]
    public async Task ATypeThatIsNotCreatedHereGetsOnlyItsName(string code, string label)
    {
        var item = await ItemAsync(code + ";\n" + Types, label);

        Assert.False(item.IsSnippet);
    }

    [Fact]
    public async Task ATypeFromANamespaceThatIsNotImportedGetsParenthesesAndItsUsing()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var offset = project.Open("Program.cs", "var text = new StringBuil$$;\n");
        var result = await project.CompleteAsync("Program.cs", offset);
        var item = Assert.Single(result.Items, item => item.Label == "StringBuilder" && item.Detail == "System.Text");

        var details = await project.Host.ResolveAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal("StringBuilder($0)", item.InsertText);
        Assert.NotNull(details);
        Assert.Contains(details.AdditionalChanges, change => change.NewText.Contains("using System.Text;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUsingDirectiveGetsNoParentheses()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var offset = project.Open("Program.cs", "using System.Tex$$\n");

        var result = await project.CompleteAsync("Program.cs", offset);

        Assert.DoesNotContain(result.Items, item => item.IsSnippet);
    }

    private static Dictionary<string, string> Files(string program) => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Program.cs"] = program,
    };

    private static async Task<CompletionEntry> ItemAsync(string code, string label, CompletionTriggerKind trigger = CompletionTriggerKind.Invoked, char? character = null)
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var offset = project.Open("Program.cs", code);

        var result = await project.CompleteAsync("Program.cs", offset, trigger, character);

        return result.Items.FirstOrDefault(item => item.Label == label)
            ?? throw new Xunit.Sdk.XunitException($"No {label} in: {string.Join(", ", result.Items.Take(20).Select(item => item.Label))}");
    }
}
