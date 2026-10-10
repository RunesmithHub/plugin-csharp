using System.Diagnostics;
using Runesmith.CSharp.Engine;
using Runesmith.LanguageServices;
using Runesmith.Text;

namespace Runesmith.CSharp.Tests.Engine;

/// <summary>A folder with C# projects on disk, and the analyzer working on it through the language service host.</summary>
internal sealed class TestProject : IAsyncDisposable
{
    public const string Caret = "$$";

    private readonly Dictionary<string, TextSnapshot> open = new(StringComparer.Ordinal);

    private TestProject(string? root, LanguageServiceHost host, CSharpAnalyzer analyzer)
    {
        Root = root;
        Host = host;
        Analyzer = analyzer;
    }

    public string? Root { get; }

    public LanguageServiceHost Host { get; }

    public CSharpAnalyzer Analyzer { get; }

    public List<string> Log { get; } = [];

    /// <summary>Writes the files, restores the projects and loads them; without files, no folder is opened.</summary>
    public static async Task<TestProject> CreateAsync(IReadOnlyDictionary<string, string>? files = null)
    {
        string? root = null;
        if (files is not null)
        {
            root = Path.Combine(Path.GetTempPath(), "runesmith-csharp-tests", Guid.NewGuid().ToString("N"));
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content.Replace(Caret, "", StringComparison.Ordinal));
            }

            if (files.Keys.Any(path => path.EndsWith(".csproj", StringComparison.Ordinal) || path.EndsWith(".slnx", StringComparison.Ordinal)))
                await RestoreAsync(root);
        }

        var log = new List<string>();
        var host = new LanguageServiceHost(Path.Combine(Path.GetTempPath(), "runesmith-csharp-tests", "cache"), (_, message) =>
        {
            lock (log)
                log.Add(message);
        });
        var analyzer = new CSharpAnalyzer();
        host.Add(analyzer);
        var project = new TestProject(root, host, analyzer);
        await host.OpenWorkspaceAsync(root);
        await analyzer.Loaded;
        project.Log.AddRange(log);
        return project;
    }

    /// <summary>Opens a file with its text, where <see cref="Caret"/> marks an offset, and returns the offset.</summary>
    public int Open(string path, string text)
    {
        var offset = text.IndexOf(Caret, StringComparison.Ordinal);
        var snapshot = TextSnapshot.Create(text.Replace(Caret, "", StringComparison.Ordinal));
        var full = FullPath(path);
        open[full] = snapshot;
        Host.Open(full, CSharpAnalyzer.LanguageId, snapshot);
        return offset;
    }

    /// <summary>Inserts text into an open file and returns the offset after it.</summary>
    public int Insert(string path, int offset, string text)
    {
        var full = FullPath(path);
        var changes = new[] { new TextChange(new TextSpan(offset, 0), text) };
        var snapshot = open[full].Apply(changes);
        open[full] = snapshot;
        Host.Change(full, snapshot, changes);
        return offset + text.Length;
    }

    /// <summary>Removes text from an open file.</summary>
    public void Remove(string path, int offset, int length)
    {
        var full = FullPath(path);
        var changes = new[] { new TextChange(new TextSpan(offset, length), "") };
        var snapshot = open[full].Apply(changes);
        open[full] = snapshot;
        Host.Change(full, snapshot, changes);
    }

    public TextSnapshot Snapshot(string path) => open[FullPath(path)];

    public Task<CompletionResult> CompleteAsync(string path, int offset, CompletionTriggerKind trigger = CompletionTriggerKind.Invoked, char? character = null) =>
        Host.CompleteAsync(FullPath(path), Snapshot(path), offset, trigger, character, TestContext.Current.CancellationToken);

    public string FullPath(string path) => Root is null ? Path.Combine(Path.GetTempPath(), "runesmith-loose", path) : Path.Combine(Root, path);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (Root is not null && Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    /// <summary>A project file for .NET 10, with an optional language version.</summary>
    public static string ProjectFile(string? languageVersion = null, string? references = null) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>Exe</OutputType>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            {(languageVersion is null ? "" : $"<LangVersion>{languageVersion}</LangVersion>")}
          </PropertyGroup>
          {references}
        </Project>
        """;

    private static async Task RestoreAsync(string root)
    {
        var target = Directory.EnumerateFiles(root, "*.slnx").FirstOrDefault() ?? Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).First();
        using var process = Process.Start(new ProcessStartInfo("dotnet", ["restore", target, "--verbosity", "quiet"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet restore failed: {output}{await process.StandardError.ReadToEndAsync()}");
    }
}
