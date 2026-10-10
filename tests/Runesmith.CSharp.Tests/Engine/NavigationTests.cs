using Runesmith.LanguageServices;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class NavigationTests
{
    private static Dictionary<string, string> Files(string program) => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Helper.cs"] = """
            namespace Sample;

            /// <summary>Helps with sums.</summary>
            public static class Helper
            {
                /// <summary>Adds two numbers.</summary>
                public static int Add(int a, int b) => a + b;

                public static int Add(int a, int b, int c) => a + b + c;
            }
            """,
        ["Program.cs"] = program,
    };

    [Fact]
    public async Task HoverShowsTheSignatureAsCodeAndTheDocumentation()
    {
        await using var project = await TestProject.CreateAsync(Files("Sample.Helper.Add(1, 2);\n"));
        var offset = project.Open("Program.cs", "Sample.Helper.A$$dd(1, 2);\n");

        var hover = await project.Host.HoverAsync(project.FullPath("Program.cs"), project.Snapshot("Program.cs"), offset, TestContext.Current.CancellationToken);

        Assert.NotNull(hover);
        Assert.StartsWith("```csharp\n", hover.Markdown, StringComparison.Ordinal);
        Assert.Contains("Helper.Add(int a, int b)", hover.Markdown, StringComparison.Ordinal);
        Assert.Contains("Adds two numbers.", hover.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoToDefinitionFindsTheDeclarationInAnotherFile()
    {
        await using var project = await TestProject.CreateAsync(Files("Sample.Helper.Add(1, 2);\n"));
        var offset = project.Open("Program.cs", "Sample.Helper.A$$dd(1, 2);\n");

        var locations = await project.Host.DefinitionAsync(project.FullPath("Program.cs"), project.Snapshot("Program.cs"), offset, TestContext.Current.CancellationToken);

        var location = Assert.Single(locations);
        Assert.Equal(project.FullPath("Helper.cs"), location.Path);
        Assert.Equal(6, location.Start.Line);
    }

    [Fact]
    public async Task SignatureHelpShowsTheOverloadsAndTheParameterBeingTyped()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var offset = project.Open("Program.cs", "Sample.Helper.Add(1, $$\n");

        var help = await project.Host.SignatureHelpAsync(project.FullPath("Program.cs"), project.Snapshot("Program.cs"), offset, TestContext.Current.CancellationToken);

        Assert.NotNull(help);
        Assert.Equal(2, help.Signatures.Count);
        Assert.Equal(1, help.ActiveParameter);
        var signature = help.Signatures[help.ActiveSignature];
        Assert.Equal("int b", signature.Label[signature.Parameters[1].Start..signature.Parameters[1].End]);
    }

    [Fact]
    public async Task SignatureHelpWorksForTargetTypedNew()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var offset = project.Open("Program.cs", "System.Text.StringBuilder builder = new($$\n");

        var help = await project.Host.SignatureHelpAsync(project.FullPath("Program.cs"), project.Snapshot("Program.cs"), offset, TestContext.Current.CancellationToken);

        Assert.NotNull(help);
        Assert.Contains(help.Signatures, signature => signature.Label.StartsWith("StringBuilder(int capacity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProblemsAreTheCompilersErrorsWithTheirCodes()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        project.Open("Program.cs", "int number = \"text\";\n");
        var document = project.Host.GetDocument(project.FullPath("Program.cs"))!;

        var problems = await project.Analyzer.DiagnosticsAsync(document, TestContext.Current.CancellationToken);

        var problem = Assert.Single(problems, p => p.Severity == ProblemSeverity.Error);
        Assert.Equal("CS0029", problem.Code);
        Assert.Equal("\"text\"", document.Snapshot.GetText(problem.Span));
    }

    [Fact]
    public async Task ALooseFileIsNotToldItLacksAMainMethod()
    {
        await using var project = await TestProject.CreateAsync();
        project.Open("Loose.cs", "namespace Loose; public class Thing { }\n");
        var document = project.Host.GetDocument(project.FullPath("Loose.cs"))!;

        Assert.Empty(await project.Analyzer.DiagnosticsAsync(document, TestContext.Current.CancellationToken));
    }
}
