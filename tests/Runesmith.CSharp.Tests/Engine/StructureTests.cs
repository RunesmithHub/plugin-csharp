using Runesmith.CSharp.Engine;
using Runesmith.Sdk.Languages;

namespace Runesmith.CSharp.Tests.Engine;

public sealed class StructureTests
{
    private const string Source = """
        using System;
        using System.Linq;

        namespace Sample;

        /// <summary>
        /// A counter.
        /// </summary>
        public class Counter
        {
            #region State
            private int count;
            #endregion

            // The first line.
            // The second line.
            public int Next(int step)
            {
                if (step > 0)
                {
                    count += step;
                }

                return count;
            }

            public int Count => count;
        }
        """;

    private static Dictionary<string, string> Files() => new()
    {
        ["App.csproj"] = TestProject.ProjectFile(),
        ["Counter.cs"] = Source,
        ["Program.cs"] = "\n",
    };

    [Fact]
    public async Task TypesMembersBlocksUsingsRegionsAndCommentsFold()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Counter.cs", Source);

        var ranges = await CSharpStructure.FoldingAsync(await project.DocumentAsync("Counter.cs"), TestContext.Current.CancellationToken);

        Assert.Contains(ranges, r => (r.StartLine, r.EndLine, r.Kind) == (0, 1, FoldingRangeKind.Imports));
        Assert.Contains(ranges, r => (r.StartLine, r.EndLine, r.Kind) == (5, 7, FoldingRangeKind.Comment));
        Assert.Contains(ranges, r => (r.StartLine, r.EndLine, r.Kind) == (8, 27, FoldingRangeKind.Block));
        Assert.Contains(ranges, r => (r.StartLine, r.EndLine, r.Kind, r.CollapsedText) == (10, 12, FoldingRangeKind.Region, "State"));
        Assert.Contains(ranges, r => (r.StartLine, r.EndLine, r.Kind) == (14, 15, FoldingRangeKind.Comment));
        Assert.Contains(ranges, r => (r.StartLine, r.EndLine, r.Kind) == (16, 24, FoldingRangeKind.Block));
        Assert.Contains(ranges, r => (r.StartLine, r.EndLine, r.Kind) == (18, 21, FoldingRangeKind.Block));
    }

    [Fact]
    public async Task WhatFitsOnOneLineDoesNotFold()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Counter.cs", Source);

        var ranges = await CSharpStructure.FoldingAsync(await project.DocumentAsync("Counter.cs"), TestContext.Current.CancellationToken);

        Assert.All(ranges, r => Assert.True(r.EndLine > r.StartLine));
        Assert.DoesNotContain(ranges, r => r.StartLine == 26);
        Assert.Equal(ranges.Count, ranges.Select(r => (r.StartLine, r.EndLine)).Distinct().Count());
    }

    [Fact]
    public async Task ExpandSelectionGrowsFromTheWordThroughEachPartAroundIt()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var offset = project.Open("Counter.cs", Source.Replace("count += step;", "count += st$$ep;", StringComparison.Ordinal));
        var snapshot = project.Snapshot("Counter.cs");

        var spans = await CSharpStructure.SelectionRangesAsync(await project.DocumentAsync("Counter.cs"), offset, TestContext.Current.CancellationToken);
        var texts = spans.Select(s => snapshot.GetText(s)).ToList();

        Assert.Equal("step", texts[0]);
        Assert.Equal("count += step", texts[1]);
        Assert.Equal("count += step;", texts[2]);
        Assert.Contains(texts, t => t.StartsWith("if (step > 0)", StringComparison.Ordinal) && t.EndsWith('}'));
        Assert.Contains(texts, t => t.StartsWith("public class Counter", StringComparison.Ordinal) || t.StartsWith("/// <summary>", StringComparison.Ordinal));
        Assert.Equal(snapshot.Length, spans[^1].Length + spans[^1].Start);
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.True(spans[i].Length > spans[i - 1].Length);
            Assert.True(spans[i].Start <= spans[i - 1].Start && spans[i].End >= spans[i - 1].End);
        }
    }

    [Fact]
    public async Task ExpandSelectionRightAfterAWordStartsWithThatWord()
    {
        await using var project = await TestProject.CreateAsync(Files());
        var offset = project.Open("Counter.cs", Source.Replace("count += step;", "count += step$$;", StringComparison.Ordinal));

        var spans = await CSharpStructure.SelectionRangesAsync(await project.DocumentAsync("Counter.cs"), offset, TestContext.Current.CancellationToken);

        Assert.Equal("step", project.Snapshot("Counter.cs").GetText(spans[0]));
    }

    [Fact]
    public async Task AnEmptyDocumentHasNothingToFoldOrSelect()
    {
        await using var project = await TestProject.CreateAsync(Files());
        project.Open("Program.cs", "");
        var document = await project.DocumentAsync("Program.cs");

        Assert.Empty(await CSharpStructure.FoldingAsync(document, TestContext.Current.CancellationToken));
        Assert.Empty(await CSharpStructure.SelectionRangesAsync(document, 0, TestContext.Current.CancellationToken));
    }
}
