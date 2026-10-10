using Runesmith.LanguageServices;

namespace Runesmith.CSharp.Tests.Engine;

/// <summary>Member lists speculated while an identifier is typed, before its <c>.</c> is.</summary>
public sealed class AheadOfTimeTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Program.cs"] = "\n",
    };

    [Fact]
    public async Task ASpeculationIsNotUsedWhenSomethingElseIsTyped()
    {
        await using var project = await TestProject.CreateAsync(Files);
        var offset = project.Open("Program.cs", "var numbers = new List<int>();\n$$\n");
        foreach (var character in "numbers")
            offset = project.Insert("Program.cs", offset, character.ToString());
        await WaitUntilQuietAsync(project);

        offset = project.Insert("Program.cs", offset, " ");
        offset = project.Insert("Program.cs", offset, ".");
        var result = await project.CompleteAsync("Program.cs", offset, CompletionTriggerKind.Character, '.');

        Assert.Equal(0, project.Analyzer.SpeculationsUsed);
        Assert.Contains(result.Items, item => item.Label == "Add");
    }

    // Work ahead of time runs in the background; this waits until no list was computed for a while, however slow the machine is.
    private static async Task WaitUntilQuietAsync(TestProject project)
    {
        var last = -1;
        for (var quiet = 0; quiet < 8;)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var now = project.Analyzer.ComputedCompletionLists;
            quiet = now == last ? quiet + 1 : 0;
            last = now;
        }
    }
}
