using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Runesmith.CSharp.Engine;

/// <summary>The project of C# files that belong to no loaded project: the newest language version, the .NET reference assemblies and the
/// implicit usings of a console app, so a lone file gets completion and problems from the start.</summary>
internal static class AdhocProject
{
    public const string Name = "Miscellaneous Files";

    /// <summary>Problems that only say the lone files are not a complete program.</summary>
    public static readonly IReadOnlySet<string> IgnoredProblems = new HashSet<string>(StringComparer.Ordinal) { "CS5001", "CS8805" };

    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    /// <summary>Adds the project to a solution, with its implicit usings.</summary>
    public static (Solution Solution, ProjectId Project) AddTo(Solution solution)
    {
        var id = ProjectId.CreateNewId(Name);
        var info = ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            Name,
            Name,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: References.Value);
        solution = solution.AddProject(info);
        solution = solution.AddDocument(DocumentId.CreateNewId(id), "GlobalUsings.g.cs", SourceText.From(GlobalUsings), filePath: null);
        return (solution, id);
    }

    // The reference pack has the XML documentation that hover shows; the runtime's own assemblies are the fallback.
    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var folder = ReferencePackFolder();
        var paths = folder is not null
            ? Directory.EnumerateFiles(folder, "*.dll")
            : (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(path => Path.GetFileName(path) is var name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "System.dll" or "netstandard.dll" or "mscorlib.dll" or "Microsoft.CSharp.dll" or "Microsoft.VisualBasic.dll" or "Microsoft.Win32.Primitives.dll"));

        return [.. paths.Select(path =>
        {
            var xml = Path.ChangeExtension(path, ".xml");
            return (MetadataReference)MetadataReference.CreateFromFile(path, documentation: File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null);
        })];
    }

    private static string? ReferencePackFolder()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var version = Path.GetFileName(runtime);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime)));
        var packs = root is null ? null : Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        if (packs is null || !Directory.Exists(packs))
            return null;

        var major = version.Split('.')[0];
        var pack = Directory.EnumerateDirectories(packs)
            .Where(directory => Path.GetFileName(directory).StartsWith(major + ".", StringComparison.Ordinal))
            .OrderByDescending(directory => Version.TryParse(Path.GetFileName(directory).Split('-')[0], out var parsed) ? parsed : new Version())
            .FirstOrDefault();
        var target = pack is null ? null : Path.Combine(pack, "ref", $"net{major}.0");
        return target is not null && Directory.Exists(target) ? target : null;
    }
}
