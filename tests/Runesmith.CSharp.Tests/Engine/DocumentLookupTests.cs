using Runesmith.Text;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class DocumentLookupTests
{
    private static Dictionary<string, string> Files() => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Helper.cs"] = "namespace Sample;\n\npublic static class Helper { }\n",
        ["Program.cs"] = "\n",
    };

    private static async Task<string> TextAsync(Microsoft.CodeAnalysis.Document? document) =>
        (await Assert.IsAssignableFrom<Microsoft.CodeAnalysis.Document>(document).GetTextAsync(TestContext.Current.CancellationToken)).ToString();

    [Fact]
    public async Task AnOpenDocumentIsFoundForTheSnapshotAskedAbout()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Program.cs", "var a = 1;\n");
        var first = project.Snapshot("Program.cs");
        project.Insert("Program.cs", first.Length, "var b = 2;\n");
        var path = project.FullPath("Program.cs");

        Assert.Equal("var a = 1;\n", await TextAsync(await project.Analyzer.FindAsync(path, first)));
        Assert.Equal("var a = 1;\nvar b = 2;\n", await TextAsync(await project.Analyzer.FindAsync(path, project.Snapshot("Program.cs"))));
        Assert.Equal("var a = 1;\nvar b = 2;\n", await TextAsync(await project.Analyzer.FindAsync(path, null)));
    }

    [Fact]
    public async Task ASnapshotTheAnalyzerHasNotSeenGivesADocumentWithItsText()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Program.cs", "var a = 1;\n");
        var ahead = TextSnapshot.Create("var a = 1;\nvar c = 3;\n");

        var found = await project.Analyzer.FindAsync(project.FullPath("Program.cs"), ahead);

        Assert.Equal("var a = 1;\nvar c = 3;\n", await TextAsync(found));
        Assert.Equal("App", found!.Project.Name);
    }

    [Fact]
    public async Task AFileThatIsNotOpenIsTheProjectsFileWithTheTextAskedAbout()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var path = project.FullPath("Helper.cs");

        Assert.Contains("class Helper", await TextAsync(await project.Analyzer.FindAsync(path, null)), StringComparison.Ordinal);
        Assert.Equal("// edited\n", await TextAsync(await project.Analyzer.FindAsync(path, TextSnapshot.Create("// edited\n"))));
    }

    [Fact]
    public async Task ADocumentKnownByAnotherNameIsFoundByItsText()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Program.cs", "var a = 1;\n");

        var found = await project.Analyzer.FindAsync("untitled:Somewhere else", project.Snapshot("Program.cs"));

        Assert.Equal(project.FullPath("Program.cs"), found?.FilePath);
    }

    [Fact]
    public async Task AFileNoProjectHasIsNotFound()
    {
        await using var project = await TestProject.CreateAsync(Files());

        Assert.Null(await project.Analyzer.FindAsync(project.FullPath("Missing.cs"), null));
        Assert.Null(await project.Analyzer.FindAsync(project.FullPath("Missing.cs"), TextSnapshot.Create("class A { }\n")));
    }
}
