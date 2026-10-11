using Runesmith.CSharp.Engine;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class SymbolsTests
{
    private const string Library = """
        namespace Sample;

        public enum Color
        {
            Red,
            Green,
        }

        public interface IShape
        {
            double Area();
        }

        public class Circle : IShape
        {
            public const double Pi = 3.14;
            private readonly double radius;

            public Circle(double radius) => this.radius = radius;

            public double Radius => radius;

            public event System.EventHandler? Changed;

            [System.Obsolete]
            public double Area() => Pi * radius * radius;

            public static Circle operator +(Circle a, Circle b) => new(a.radius + b.radius);

            public struct Point
            {
                public int X;
            }
        }
        """;

    private static Dictionary<string, string> Files() => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Library.cs"] = Library,
        ["Program.cs"] = "System.Console.WriteLine(new Sample.Circle(1).Area());\n",
    };

    [Fact]
    public async Task TheSymbolsOfADocumentAreATreeOfNamespacesTypesAndMembers()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Library.cs", Library);

        var symbols = await CSharpSymbols.DocumentAsync(await project.DocumentAsync("Library.cs"), TestContext.Current.CancellationToken);

        var space = Assert.Single(symbols);
        Assert.Equal(("Sample", SymbolKind.Namespace), (space.Name, space.Kind));
        Assert.Equal(
            [("Color", SymbolKind.Enum), ("IShape", SymbolKind.Interface), ("Circle", SymbolKind.Class)],
            space.Children.Select(s => (s.Name, s.Kind)));
        Assert.Equal(["Red", "Green"], space.Children[0].Children.Select(s => s.Name));
        Assert.All(space.Children[0].Children, s => Assert.Equal(SymbolKind.EnumMember, s.Kind));

        var circle = space.Children[2];
        Assert.Equal(
            [
                ("Pi", SymbolKind.Constant), ("radius", SymbolKind.Field), ("Circle", SymbolKind.Constructor), ("Radius", SymbolKind.Property),
                ("Changed", SymbolKind.Event), ("Area", SymbolKind.Method), ("operator +", SymbolKind.Operator), ("Point", SymbolKind.Struct),
            ],
            circle.Children.Select(s => (s.Name, s.Kind)));
        Assert.Equal("() : double", circle.Children[5].Detail);
        Assert.True(circle.Children[5].IsDeprecated);
        Assert.False(circle.Children[3].IsDeprecated);
        Assert.Equal("X", Assert.Single(circle.Children[7].Children).Name);
    }

    [Fact]
    public async Task ASymbolsSelectionIsItsNameInsideItsWholeDeclaration()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Library.cs", Library);
        var snapshot = project.Snapshot("Library.cs");

        var symbols = await CSharpSymbols.DocumentAsync(await project.DocumentAsync("Library.cs"), TestContext.Current.CancellationToken);

        var area = symbols[0].Children[2].Children[5];
        Assert.Equal("Area", snapshot.GetText(area.SelectionRange));
        Assert.StartsWith("[System.Obsolete]", snapshot.GetText(area.Range), StringComparison.Ordinal);
        Assert.EndsWith("radius * radius;", snapshot.GetText(area.Range), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentWithOnlyStatementsHasNoSymbols()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Program.cs", "System.Console.WriteLine(1);\n");

        Assert.Empty(await CSharpSymbols.DocumentAsync(await project.DocumentAsync("Program.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SymbolsOfTheWorkspaceAreFoundByNameWithTheClosestFirst()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var solution = await project.Analyzer.GetSolutionAsync();

        var found = await CSharpSymbols.WorkspaceAsync(solution, "circle", TestContext.Current.CancellationToken);
        var area = await CSharpSymbols.WorkspaceAsync(solution, "Are", TestContext.Current.CancellationToken);

        Assert.Equal(("Circle", SymbolKind.Class, "Sample"), (found[0].Name, found[0].Kind, found[0].ContainerName));
        Assert.Equal(project.FullPath("Library.cs"), found[0].Location.FilePath);
        Assert.Equal(13, found[0].Location.Start.Line);
        Assert.Equal(
            [("Area", "Sample.Circle", true), ("Area", "Sample.IShape", false)],
            area.Select(s => (s.Name, s.ContainerName!, s.IsDeprecated)).Order());
        Assert.All(area, s => Assert.Equal(SymbolKind.Method, s.Kind));
    }

    [Fact]
    public async Task AnEmptyQueryFindsNothing()
    {
        await using var project = await TestProject.CreateAsync(Files());

        Assert.Empty(await CSharpSymbols.WorkspaceAsync(await project.Analyzer.GetSolutionAsync(), " ", TestContext.Current.CancellationToken));
    }
}
