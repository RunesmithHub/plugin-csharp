using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Runesmith.CSharp.Engine;

/// <summary>Finds and loads the C# projects of a folder: its solution (<c>.slnx</c>, then <c>.sln</c>), or else its project files.</summary>
internal static class ProjectLoader
{
    private const int ProjectSearchDepth = 3;

    private static readonly string[] SolutionPatterns = ["*.slnx", "*.sln"];

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", "node_modules", ".vs", ".idea" };

    /// <summary>Gets the solution file at the top of a folder, a <c>.slnx</c> before a <c>.sln</c>, or null.</summary>
    public static string? FindSolution(string root) =>
        SolutionPatterns
            .Select(pattern => Directory.EnumerateFiles(root, pattern).Order(StringComparer.Ordinal).FirstOrDefault())
            .FirstOrDefault(path => path is not null);

    /// <summary>Gets the C# project files in a folder and its subfolders, a few levels deep.</summary>
    public static IReadOnlyList<string> FindProjects(string root)
    {
        var projects = new List<string>();
        void Search(string folder, int depth)
        {
            projects.AddRange(Directory.EnumerateFiles(folder, "*.csproj"));
            if (depth == ProjectSearchDepth)
                return;

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                if (!SkippedFolders.Contains(Path.GetFileName(child)))
                    Search(child, depth + 1);
            }
        }

        Search(root, 0);
        projects.Sort(StringComparer.Ordinal);
        return projects;
    }

    /// <summary>Loads a folder's projects, or returns null when it has none or loading failed; progress and problems go to the log.</summary>
    public static async Task<Solution?> LoadAsync(MSBuildWorkspace workspace, string root, Action<string> log, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<ProjectLoadProgress>(step =>
        {
            if (step.Operation == ProjectLoadOperation.Resolve)
                log($"Loaded {Path.GetFileName(step.FilePath)} ({step.TargetFramework}) in {step.ElapsedTime.TotalMilliseconds:0} ms");
        });

        try
        {
            if (FindSolution(root) is { } solutionPath)
            {
                log($"Loading {Path.GetFileName(solutionPath)}");
                await workspace.OpenSolutionAsync(solutionPath, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var projects = FindProjects(root);
                if (projects.Count == 0)
                    return null;

                foreach (var project in projects)
                {
                    if (workspace.CurrentSolution.Projects.Any(p => string.Equals(p.FilePath, project, StringComparison.Ordinal)))
                        continue;
                    log($"Loading {Path.GetRelativePath(root, project)}");
                    await workspace.OpenProjectAsync(project, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log($"The C# projects could not be loaded: {exception.Message}");
            return null;
        }

        foreach (var diagnostic in workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure))
            log(diagnostic.Message);

        var solution = workspace.CurrentSolution;
        log($"Loaded {solution.Projects.Count()} C# projects in {stopwatch.Elapsed.TotalSeconds:0.0} s");
        return solution;
    }
}
