using Runesmith.LanguageServices;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class ProjectTests
{
    [Fact]
    public async Task ASlnxSolutionLoadsWithItsProjectReferences()
    {
        await using var project = await TestProject.CreateAsync(new Dictionary<string, string>
        {
            ["Sample.slnx"] = """
                <Solution>
                  <Project Path="Lib/Lib.csproj" />
                  <Project Path="App/App.csproj" />
                </Solution>
                """,
            ["Lib/Lib.csproj"] = TestProject.ProjectFile().Replace("<OutputType>Exe</OutputType>", "", StringComparison.Ordinal),
            ["Lib/Shapes.cs"] = "namespace Lib; public sealed class Circle { public double Radius { get; init; } }\n",
            ["App/App.csproj"] = TestProject.ProjectFile(references: """<ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>"""),
            ["App/Program.cs"] = "\n",
        });

        Assert.Equal(2, project.Analyzer.Solution.Projects.Count(p => p.FilePath is not null));
        var offset = project.Open("App/Program.cs", "var circle = new Lib.Circle();\ncircle.$$\n");
        var result = await project.CompleteAsync("App/Program.cs", offset, CompletionTriggerKind.Character, '.');

        Assert.Contains(result.Items, item => item.Label == "Radius");
    }

    [Fact]
    public async Task TheNewestLanguageFeaturesHaveNoProblemsAndGetCompletion()
    {
        const string Code = """
            var person = new Person { Name = "Ada" };
            person?.Name = "Grace";
            Console.WriteLine("abc".Twice);

            public sealed class Person
            {
                public string Name { get => field; set => field = value.Trim(); }
            }

            public static class TextExtensions
            {
                extension(string text)
                {
                    public int Twice => text.Length * 2;
                }
            }
            """;
        await using var project = await TestProject.CreateAsync(new Dictionary<string, string>
        {
            ["App.csproj"] = TestProject.ProjectFile(),
            ["Program.cs"] = Code,
        });
        project.Open("Program.cs", Code);
        var document = project.Host.GetDocument(project.FullPath("Program.cs"))!;

        var problems = await project.Analyzer.DiagnosticsAsync(document, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(problems, p => p.Severity == ProblemSeverity.Error);

        var offset = project.Insert("Program.cs", Code.IndexOf("Console", StringComparison.Ordinal), "\"x\".");
        var result = await project.CompleteAsync("Program.cs", offset, CompletionTriggerKind.Character, '.');
        Assert.Contains(result.Items, item => item.Label == "Twice");
    }

    [Fact]
    public async Task AnOlderLanguageVersionReportsNewerFeatures()
    {
        const string Code = "public record Point(int X, int Y);\n";
        await using var project = await TestProject.CreateAsync(new Dictionary<string, string>
        {
            ["App.csproj"] = TestProject.ProjectFile(languageVersion: "7.3").Replace("<OutputType>Exe</OutputType>", "", StringComparison.Ordinal),
            ["Point.cs"] = Code,
        });
        project.Open("Point.cs", Code);
        var document = project.Host.GetDocument(project.FullPath("Point.cs"))!;

        var problems = await project.Analyzer.DiagnosticsAsync(document, TestContext.Current.CancellationToken);

        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Error && p.Message.Contains("7.3", StringComparison.Ordinal));
    }
}
