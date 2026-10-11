using Runesmith.CSharp.Engine;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class HierarchyTests
{
    private const string Shapes = """
        namespace Sample;

        public interface IShape
        {
            double Area();
        }

        public interface IRound : IShape { }

        public class Circle(double radius) : IRound
        {
            public virtual double Area() => Maths.Square(radius) * 3.14;
        }

        public sealed class Disc(double radius) : Circle(radius)
        {
            public double Twice() => Area() + Area();
        }

        public static class Maths
        {
            public static double Square(double value) => value * value;

            public static double Sum(Disc disc) => disc.Twice() + new Circle(1).Area();
        }
        """;

    private static Dictionary<string, string> Files() => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Shapes.cs"] = Shapes,
        ["Program.cs"] = "System.Console.WriteLine(Sample.Maths.Square(2));\n",
    };

    private static async Task<(TestProject Project, HierarchyItem Item)> CallsAtAsync(string marked)
    {
        var project = await TestProject.CreateAsync(Files());
        var offset = project.Open("Shapes.cs", marked);
        var items = await CSharpHierarchies.PrepareCallsAsync(await project.DocumentAsync("Shapes.cs"), offset, TestContext.Current.CancellationToken);
        return (project, Assert.Single(items));
    }

    [Fact]
    public async Task AFunctionIsPreparedWithItsNameKindAndPlace()
    {
        var (project, item) = await CallsAtAsync(Shapes.Replace("double Square(", "double Squ$$are(", StringComparison.Ordinal));
        await using var _ = project;

        Assert.Equal(("Square(double)", SymbolKind.Method, "Sample.Maths"), (item.Name, item.Kind, item.Detail));
        Assert.Equal(project.FullPath("Shapes.cs"), item.Location.FilePath);
        Assert.Equal(21, item.Location.Start.Line);
        Assert.Equal(21, item.SelectionLocation!.Start.Line);
    }

    [Fact]
    public async Task IncomingCallsAreTheFunctionsThatCallWithWhereTheyDo()
    {
        var (project, item) = await CallsAtAsync(Shapes.Replace("public double Twice()", "public double Twi$$ce()", StringComparison.Ordinal));
        await using var _ = project;
        var solution = await project.Analyzer.GetSolutionAsync();

        var calls = await CSharpHierarchies.IncomingCallsAsync(solution, item, TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal("Sum(Disc)", call.Item.Name);
        Assert.Equal(23, Assert.Single(call.CallSites).Start.Line);
    }

    [Fact]
    public async Task OutgoingCallsAreTheFunctionsCalledWithEachPlace()
    {
        var (project, item) = await CallsAtAsync(Shapes.Replace("public double Twice()", "public double Twi$$ce()", StringComparison.Ordinal));
        await using var _ = project;
        var solution = await project.Analyzer.GetSolutionAsync();

        var calls = await CSharpHierarchies.OutgoingCallsAsync(solution, item, TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal("Area()", call.Item.Name);
        Assert.Equal(2, call.CallSites.Count);
        Assert.All(call.CallSites, site => Assert.Equal(16, site.Start.Line));
    }

    [Fact]
    public async Task OutgoingCallsIncludeConstructorsAndLeaveOutWhatIsNotInSource()
    {
        var (project, item) = await CallsAtAsync(Shapes.Replace("double Sum(", "double Su$$m(", StringComparison.Ordinal));
        await using var _ = project;
        var solution = await project.Analyzer.GetSolutionAsync();

        var calls = await CSharpHierarchies.OutgoingCallsAsync(solution, item, TestContext.Current.CancellationToken);

        Assert.Equal(["Area()", "Circle(double)", "Twice()"], calls.Select(c => c.Item.Name));
    }

    [Fact]
    public async Task ATypeIsNotAFunctionAndAFunctionIsNotAType()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var onType = project.Open("Shapes.cs", Shapes.Replace("class Disc", "class Di$$sc", StringComparison.Ordinal));
        var document = await project.DocumentAsync("Shapes.cs");
        var onMethod = Shapes.IndexOf("Twice()", StringComparison.Ordinal);

        Assert.Empty(await CSharpHierarchies.PrepareCallsAsync(document, onType, TestContext.Current.CancellationToken));
        Assert.Empty(await CSharpHierarchies.PrepareTypesAsync(document, onMethod, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SupertypesAreTheBaseClassAndTheInterfacesNamed()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var offset = project.Open("Shapes.cs", Shapes.Replace("class Disc", "class Di$$sc", StringComparison.Ordinal));
        var solution = await project.Analyzer.GetSolutionAsync();

        var disc = Assert.Single(await CSharpHierarchies.PrepareTypesAsync(await project.DocumentAsync("Shapes.cs"), offset, TestContext.Current.CancellationToken));
        var parents = await CSharpHierarchies.SupertypesAsync(solution, disc, TestContext.Current.CancellationToken);
        var grandparents = await CSharpHierarchies.SupertypesAsync(solution, Assert.Single(parents), TestContext.Current.CancellationToken);

        Assert.Equal(("Disc", SymbolKind.Class, "Sample"), (disc.Name, disc.Kind, disc.Detail));
        Assert.Equal(["Circle"], parents.Select(p => p.Name));
        Assert.Equal([("IRound", SymbolKind.Interface)], grandparents.Select(p => (p.Name, p.Kind)));
    }

    [Fact]
    public async Task SubtypesAreTheTypesThatDeriveOrImplementDirectly()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var offset = project.Open("Shapes.cs", Shapes.Replace("interface IShape", "interface ISh$$ape", StringComparison.Ordinal));
        var solution = await project.Analyzer.GetSolutionAsync();

        var shape = Assert.Single(await CSharpHierarchies.PrepareTypesAsync(await project.DocumentAsync("Shapes.cs"), offset, TestContext.Current.CancellationToken));
        var children = await CSharpHierarchies.SubtypesAsync(solution, shape, TestContext.Current.CancellationToken);
        var round = Assert.Single(children);
        var grandchildren = await CSharpHierarchies.SubtypesAsync(solution, round, TestContext.Current.CancellationToken);
        var last = await CSharpHierarchies.SubtypesAsync(solution, Assert.Single(grandchildren), TestContext.Current.CancellationToken);

        Assert.Equal("IRound", round.Name);
        Assert.Equal(["Circle"], grandchildren.Select(c => c.Name));
        Assert.Equal(["Disc"], last.Select(c => c.Name));
    }

    [Fact]
    public async Task AnItemFromElsewhereGivesNothing()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var solution = await project.Analyzer.GetSolutionAsync();
        var foreign = new HierarchyItem("Other", SymbolKind.Method, new DocumentLocation(project.FullPath("Shapes.cs"), new Runesmith.Text.TextPosition(0, 0)));

        Assert.Empty(await CSharpHierarchies.IncomingCallsAsync(solution, foreign, TestContext.Current.CancellationToken));
        Assert.Empty(await CSharpHierarchies.OutgoingCallsAsync(solution, foreign, TestContext.Current.CancellationToken));
        Assert.Empty(await CSharpHierarchies.SupertypesAsync(solution, foreign, TestContext.Current.CancellationToken));
        Assert.Empty(await CSharpHierarchies.SubtypesAsync(solution, foreign, TestContext.Current.CancellationToken));
    }
}
