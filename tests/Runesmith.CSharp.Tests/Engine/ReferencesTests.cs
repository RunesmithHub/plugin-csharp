using Runesmith.CSharp.Engine;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class ReferencesTests
{
    private const string Shapes = """
        namespace Sample;

        public interface IShape
        {
            double Area();
        }

        public class Circle(double radius) : IShape
        {
            public virtual double Area() => 3.14 * radius * radius;
        }

        public sealed class Disc(double radius) : Circle(radius)
        {
            public override double Area() => base.Area();
        }
        """;

    private static Dictionary<string, string> Files(string program) => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Shapes.cs"] = Shapes,
        ["Program.cs"] = program,
    };

    [Fact]
    public async Task ReferencesAreFoundInEveryFileOfTheSolution()
    {
        const string Program = "var circle = new Sample.Circle(1);\nSystem.Console.WriteLine(circle.Area());\nSample.IShape shape = circle;\nshape.Area();\n";
        await using var project = await TestProject.CreateAsync(Files(Program));
        var offset = project.Open("Shapes.cs", Shapes.Replace("double Area();", "double Ar$$ea();", StringComparison.Ordinal));

        var without = await CSharpReferences.FindAsync(await project.DocumentAsync("Shapes.cs"), offset, includeDeclaration: false, TestContext.Current.CancellationToken);
        var with = await CSharpReferences.FindAsync(await project.DocumentAsync("Shapes.cs"), offset, includeDeclaration: true, TestContext.Current.CancellationToken);

        Assert.Contains(without, l => l.FilePath == project.FullPath("Program.cs") && l.Start.Line == 3);
        Assert.DoesNotContain(without, l => l.FilePath == project.FullPath("Shapes.cs") && l.Start.Line == 4);
        Assert.Contains(with, l => l.FilePath == project.FullPath("Shapes.cs") && l.Start.Line == 4);
        Assert.All(with, l => Assert.Equal(4, l.End!.Value.Column - l.Start.Column));
    }

    [Fact]
    public async Task ImplementationsOfAnInterfaceMemberAreTheMembersThatImplementIt()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var offset = project.Open("Shapes.cs", Shapes.Replace("double Area();", "double Ar$$ea();", StringComparison.Ordinal));

        var found = await CSharpReferences.ImplementationsAsync(await project.DocumentAsync("Shapes.cs"), offset, TestContext.Current.CancellationToken);

        Assert.Contains(found, l => l.Start.Line == 9);
    }

    [Fact]
    public async Task ImplementationsOfATypeAreTheTypesThatImplementOrDeriveFromIt()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var onInterface = project.Open("Shapes.cs", Shapes.Replace("interface IShape", "interface ISh$$ape", StringComparison.Ordinal));
        var document = await project.DocumentAsync("Shapes.cs");

        var ofInterface = await CSharpReferences.ImplementationsAsync(document, onInterface, TestContext.Current.CancellationToken);
        var ofClass = await CSharpReferences.ImplementationsAsync(document, Shapes.IndexOf("Circle(double", StringComparison.Ordinal), TestContext.Current.CancellationToken);

        Assert.Equal([7, 12], ofInterface.Select(l => l.Start.Line));
        Assert.Equal([12], ofClass.Select(l => l.Start.Line));
    }

    [Fact]
    public async Task OccurrencesInTheDocumentSayWhetherTheyWrite()
    {
        const string Program = "var count = 1;\ncount = count + 2;\ncount++;\nSystem.Console.WriteLine(count);\n";
        await using var project = await TestProject.CreateAsync(Files("\n"));
        var offset = project.Open("Program.cs", Program.Replace("var count", "var co$$unt", StringComparison.Ordinal));

        var highlights = await CSharpReferences.HighlightsAsync(await project.DocumentAsync("Program.cs"), offset, TestContext.Current.CancellationToken);

        Assert.Equal(
            [DocumentHighlightKind.Write, DocumentHighlightKind.Write, DocumentHighlightKind.Read, DocumentHighlightKind.Write, DocumentHighlightKind.Read],
            highlights.Select(h => h.Kind));
        Assert.All(highlights, h => Assert.Equal("count", project.Snapshot("Program.cs").GetText(h.Span)));
    }

    [Fact]
    public async Task NothingIsFoundWhereNoSymbolIs()
    {
        await using var project = await TestProject.CreateAsync(Files("\n"));
        project.Open("Program.cs", "// only a comment\n");
        var document = await project.DocumentAsync("Program.cs");

        Assert.Empty(await CSharpReferences.FindAsync(document, 5, includeDeclaration: true, TestContext.Current.CancellationToken));
        Assert.Empty(await CSharpReferences.ImplementationsAsync(document, 5, TestContext.Current.CancellationToken));
        Assert.Empty(await CSharpReferences.HighlightsAsync(document, 5, TestContext.Current.CancellationToken));
    }
}
