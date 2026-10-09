using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Runesmith.CSharp.Running;

/// <summary>What a project file builds.</summary>
internal enum DotnetProjectKind
{
    Library,

    /// <summary>A program: <c>OutputType</c> <c>Exe</c> or <c>WinExe</c>, or a web or worker SDK.</summary>
    Runnable,

    /// <summary>A test project, which runs with <c>dotnet test</c> even when it is a program.</summary>
    Test,
}

/// <summary>A .NET project as the run configurations see it.</summary>
/// <param name="FullPath">The project file.</param>
/// <param name="RelativePath">The project file relative to the open folder, with <c>/</c> between folders; configurations save this.</param>
/// <param name="Name">The file name without its extension.</param>
/// <param name="TargetFrameworks">The target frameworks, the first one the default; empty when the project does not say.</param>
/// <param name="AssemblyName">The name of the built assembly.</param>
/// <param name="LaunchProfiles">The profiles of <c>Properties/launchSettings.json</c> that run the project.</param>
internal sealed record DotnetProject(string FullPath, string RelativePath, string Name, DotnetProjectKind Kind, IReadOnlyList<string> TargetFrameworks,
    string AssemblyName, IReadOnlyList<string> LaunchProfiles)
{
    /// <summary>Gets the project's folder.</summary>
    public string Folder => Path.GetDirectoryName(FullPath)!;

    /// <summary>Gets a framework the project targets: the one asked for when it targets it, otherwise its first.</summary>
    public string? PickFramework(string? requested) =>
        !string.IsNullOrWhiteSpace(requested) && TargetFrameworks.Contains(requested, StringComparer.OrdinalIgnoreCase) ? requested
        : TargetFrameworks.Count > 0 ? TargetFrameworks[0]
        : string.IsNullOrWhiteSpace(requested) ? null : requested;
}

/// <summary>Finds and reads the .NET projects of a folder without MSBuild: the project file, the <c>Directory.Build.props</c> files above it
/// for the target framework, and <c>launchSettings.json</c>.</summary>
internal static class DotnetProjects
{
    private const int MaximumDepth = 6;

    private static readonly string[] Extensions = [".csproj", ".fsproj"];
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "artifacts"];
    private static readonly string[] ProgramSdks = ["Microsoft.NET.Sdk.Web", "Microsoft.NET.Sdk.Worker", "Microsoft.NET.Sdk.BlazorWebAssembly"];
    private static readonly string[] TestPackages = ["xunit", "xunit.v3", "nunit", "MSTest", "MSTest.TestFramework", "Microsoft.NET.Test.Sdk", "TUnit"];

    /// <summary>Finds the projects under a folder, sorted by their relative path.</summary>
    public static IReadOnlyList<DotnetProject> Find(string rootPath, CancellationToken cancellationToken = default)
    {
        var found = new List<DotnetProject>();
        if (Directory.Exists(rootPath))
            Search(Path.GetFullPath(rootPath), Path.GetFullPath(rootPath), 0, found, cancellationToken);
        return [.. found.OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Reads one project file, or returns null when it cannot be read.</summary>
    public static DotnetProject? Read(string projectPath, string rootPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        var project = document.Root;
        if (project is null)
            return null;

        var properties = project.Elements().Where(e => e.Name.LocalName == "PropertyGroup").SelectMany(g => g.Elements()).ToList();
        string? Property(string name) =>
            properties.LastOrDefault(p => p.Name.LocalName == name && !p.Value.Contains("$(", StringComparison.Ordinal))?.Value.Trim();

        var sdks = new List<string>();
        if (project.Attribute("Sdk")?.Value is { } sdkAttribute)
            sdks.AddRange(sdkAttribute.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Split('/')[0]));
        sdks.AddRange(project.Elements().Where(e => e.Name.LocalName == "Sdk").Select(e => e.Attribute("Name")?.Value ?? ""));
        var packages = project.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => e.Attribute("Include")?.Value ?? "").ToList();

        var isTest = string.Equals(Property("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
            || packages.Any(p => TestPackages.Contains(p, StringComparer.OrdinalIgnoreCase));
        var isProgram = Property("OutputType") is { } outputType && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
            || sdks.Any(s => ProgramSdks.Contains(s, StringComparer.OrdinalIgnoreCase));
        var kind = isTest ? DotnetProjectKind.Test : isProgram ? DotnetProjectKind.Runnable : DotnetProjectKind.Library;

        var name = Path.GetFileNameWithoutExtension(projectPath);
        var frameworks = Frameworks(Property("TargetFrameworks"), Property("TargetFramework"));
        if (frameworks.Count == 0)
            frameworks = FrameworksFromProps(Path.GetDirectoryName(projectPath)!, rootPath);
        var relative = Path.GetRelativePath(rootPath, projectPath).Replace('\\', '/');
        return new DotnetProject(Path.GetFullPath(projectPath), relative, name, kind, frameworks, Property("AssemblyName") ?? name,
            LaunchProfiles(Path.GetDirectoryName(projectPath)!));
    }

    /// <summary>Gets the names of the profiles in a project folder's <c>Properties/launchSettings.json</c> whose <c>commandName</c> is
    /// <c>Project</c>, in file order.</summary>
    public static IReadOnlyList<string> LaunchProfiles(string projectFolder)
    {
        var path = Path.Combine(projectFolder, "Properties", "launchSettings.json");
        try
        {
            if (!File.Exists(path))
                return [];

            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                return [];

            return
            [
                .. profiles.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("commandName", out var command)
                        && command.ValueKind == JsonValueKind.String && command.GetString() == "Project")
                    .Select(p => p.Name),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Search(string rootPath, string folder, int depth, List<DotnetProject> found, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && Read(file, rootPath) is { } project)
                    found.Add(project);
            }

            if (depth >= MaximumDepth)
                return;

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    Search(rootPath, child, depth + 1, found, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<string> Frameworks(string? several, string? one) =>
        several is { Length: > 0 } ? [.. several.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        : one is { Length: > 0 } ? [one]
        : [];

    // Repositories often set the framework once for every project, in a Directory.Build.props above them.
    private static List<string> FrameworksFromProps(string projectFolder, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        for (var folder = projectFolder; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var props = Path.Combine(folder, "Directory.Build.props");
            if (File.Exists(props))
            {
                try
                {
                    var properties = XDocument.Load(props).Root?.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && e.Attribute("Condition") is null)
                        .SelectMany(g => g.Elements()).Where(e => !e.Value.Contains("$(", StringComparison.Ordinal)).ToList() ?? [];
                    var frameworks = Frameworks(properties.LastOrDefault(p => p.Name.LocalName == "TargetFrameworks")?.Value.Trim(),
                        properties.LastOrDefault(p => p.Name.LocalName == "TargetFramework")?.Value.Trim());
                    if (frameworks.Count > 0)
                        return frameworks;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
                {
                }
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(folder), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
        }

        return [];
    }
}
