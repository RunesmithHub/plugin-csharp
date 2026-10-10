using Runesmith.LanguageServices;
using Runesmith.Text;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class EditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Files(string program) => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Counter.cs"] = """
            namespace Sample;

            public static class Counter
            {
                public static int Next(int count) => count + 1;
            }
            """,
        ["Program.cs"] = program,
    };

    [Fact]
    public async Task OffersTheCompilersFixForAMissingUsingAndItsEditAddsIt()
    {
        await using var project = await TestProject.CreateAsync(Files("var text = new StringBuilder();\n"));
        var path = project.FullPath("Program.cs");
        project.Open("Program.cs", "var text = new StringBuilder();\n");
        var snapshot = project.Snapshot("Program.cs");

        var actions = await project.Host.CodeActionsAsync(path, snapshot, new TextSpan(0, 30), includeRefactorings: false, Token);
        var addUsing = Assert.Single(actions, a => a.Title.Contains("using System.Text", StringComparison.Ordinal));
        Assert.False(addUsing.IsRefactoring);

        var edits = await project.Host.ResolveCodeActionAsync(addUsing, Token);

        var edit = Assert.Single(edits);
        Assert.Equal(path, edit.Path);
        Assert.Equal(project.Host.GetDocument(path)!.Version, edit.Version);
        Assert.Contains(edit.Edits, e => e.NewText.Contains("using System.Text;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefactoringsComeOnlyWhenTheUserAsks()
    {
        const string Program = "int Twice(int value) => value * 2;\nSystem.Console.WriteLine(Twice(4));\n";
        await using var project = await TestProject.CreateAsync(Files(Program));
        var path = project.FullPath("Program.cs");
        var offset = project.Open("Program.cs", "int Twice(int value) => $$value * 2;\nSystem.Console.WriteLine(Twice(4));\n");
        var span = new TextSpan(offset, 9);

        var quick = await project.Host.CodeActionsAsync(path, project.Snapshot("Program.cs"), span, includeRefactorings: false, Token);
        var all = await project.Host.CodeActionsAsync(path, project.Snapshot("Program.cs"), span, includeRefactorings: true, Token);

        Assert.DoesNotContain(quick, a => a.IsRefactoring);
        Assert.Contains(all, a => a.IsRefactoring);
    }

    [Fact]
    public async Task RenamesASymbolInEveryFileThatUsesIt()
    {
        const string Program = "System.Console.WriteLine(Sample.Counter.Next(1));\n";
        await using var project = await TestProject.CreateAsync(Files(Program));
        var path = project.FullPath("Program.cs");
        var offset = project.Open("Program.cs", "System.Console.WriteLine(Sample.Counter.Ne$$xt(1));\n");
        var snapshot = project.Snapshot("Program.cs");

        var site = await project.Host.PrepareRenameAsync(path, snapshot, offset, Token);
        var edits = await project.Host.RenameAsync(path, snapshot, offset, "Increment", Token);

        Assert.Equal("Next", site?.Name);
        Assert.Equal(new TextSpan(40, 4), site?.Span);
        Assert.Equal([project.FullPath("Counter.cs"), path], edits!.Select(e => e.Path).Order(StringComparer.Ordinal));
        var counter = edits!.Single(e => e.Path == project.FullPath("Counter.cs"));
        Assert.Null(counter.Version);
        Assert.Contains("public static int Increment(int count)", Apply(TextSnapshot.Create(await File.ReadAllTextAsync(counter.Path, Token)), counter), StringComparison.Ordinal);
        Assert.Equal("System.Console.WriteLine(Sample.Counter.Increment(1));\n", Apply(snapshot, edits!.Single(e => e.Path == path)));
    }

    private static string Apply(TextSnapshot snapshot, FileEdit edit) =>
        snapshot.Apply([.. edit.Edits.Select(e => new TextChange(TextSpan.FromBounds(snapshot.GetOffset(e.Start), snapshot.GetOffset(e.End)), e.NewText)).OrderBy(c => c.Span.Start)]).GetText();

    [Fact]
    public async Task RefusesToRenameWhatALibraryDefinesOrToANameThatIsNotCSharp()
    {
        const string Program = "System.Console.WriteLine(Sample.Counter.Next(1));\n";
        await using var project = await TestProject.CreateAsync(Files(Program));
        var path = project.FullPath("Program.cs");
        var offset = project.Open("Program.cs", "System.Console.Write$$Line(Sample.Counter.Next(1));\n");
        var snapshot = project.Snapshot("Program.cs");
        var next = Program.IndexOf("Next", StringComparison.Ordinal);

        var library = await Assert.ThrowsAsync<AnalyzerRefusalException>(() => project.Host.PrepareRenameAsync(path, snapshot, offset, Token));
        var invalid = await Assert.ThrowsAsync<AnalyzerRefusalException>(() => project.Host.RenameAsync(path, snapshot, next, "two words", Token));
        var nothing = await project.Host.PrepareRenameAsync(path, snapshot, Program.IndexOf("1)", StringComparison.Ordinal), Token);

        Assert.Contains("WriteLine", library.Message, StringComparison.Ordinal);
        Assert.Contains("two words", invalid.Message, StringComparison.Ordinal);
        Assert.Null(nothing);
    }

    [Fact]
    public async Task FormatsWithTheEditorsIndentation()
    {
        const string Program = "class C\n{\nvoid M( ){int x=1;}\n}\n";
        await using var project = await TestProject.CreateAsync(Files(Program));
        var path = project.FullPath("Program.cs");
        project.Open("Program.cs", Program);
        var snapshot = project.Snapshot("Program.cs");

        var spaces = await project.Host.FormatAsync(path, snapshot, null, 2, insertSpaces: true, Token);
        var tabs = await project.Host.FormatAsync(path, snapshot, null, 4, insertSpaces: false, Token);

        Assert.Equal("class C\n{\n  void M() { int x = 1; }\n}\n", snapshot.Apply(spaces!).GetText());
        Assert.Equal("class C\n{\n\tvoid M() { int x = 1; }\n}\n", snapshot.Apply(tabs!).GetText());
    }

    [Fact]
    public async Task HintsTheParameterNamesOfLiteralArgumentsAndTheTypesOfVar()
    {
        const string Program = "var total = Sample.Counter.Next(41);\nforeach (var word in new[] { \"a\" })\n    System.Console.WriteLine(word, total);\nvar named = Sample.Counter.Next(count: 2);\n";
        await using var project = await TestProject.CreateAsync(Files(Program));
        var path = project.FullPath("Program.cs");
        project.Open("Program.cs", Program);

        var hints = await project.Host.InlayHintsAsync(path, project.Snapshot("Program.cs"), Token);

        Assert.Equal(
        [
            new InlayHintEntry(Program.IndexOf(" = Sample", StringComparison.Ordinal), ": int", IsType: true),
            new InlayHintEntry(Program.IndexOf("41", StringComparison.Ordinal), "count:", IsType: false),
            new InlayHintEntry(Program.IndexOf(" in new", StringComparison.Ordinal), ": string", IsType: true),
            new InlayHintEntry(Program.IndexOf(" = Sample.Counter.Next(count", StringComparison.Ordinal), ": int", IsType: true),
        ], hints);
    }
}
