using System.Text;
using Runesmith.CSharp.Sdks;
using Runesmith.Sdk.Options;
using Runesmith.Sdk.Running;

namespace Runesmith.CSharp.Running;

/// <summary>The option ids the .NET configuration types share, the options they build from a folder's projects, and how their values are read.</summary>
internal static class DotnetRunOptions
{
    public const string Project = "project";
    public const string Framework = "framework";
    public const string LaunchProfile = "launchProfile";
    public const string Configuration = "configuration";
    public const string Arguments = "arguments";
    public const string Environment = "environment";
    public const string WorkingDirectory = "workingDirectory";
    public const string SdkPath = "sdk";
    public const string Filter = "filter";
    public const string Command = "command";

    /// <summary>Gets the project option: a choice of the projects found, or a file field when none were found yet.</summary>
    public static Option ProjectOption(IReadOnlyList<DotnetProject> projects, string description) => projects.Count > 0
        ? new Option(Project, "Project", OptionKind.Choice)
        {
            Choices = [.. projects.Select(p => new OptionChoice(p.RelativePath, p.Name) { Description = p.RelativePath })],
            Default = projects[0].RelativePath,
            IsRequired = true,
            Description = description,
        }
        : new Option(Project, "Project", OptionKind.Path)
        {
            PathKind = PathKind.File,
            IsRequired = true,
            Placeholder = "A .csproj or .fsproj file",
            Description = description,
        };

    public static Option ConfigurationOption() => new(Configuration, "Configuration", OptionKind.Choice)
    {
        Choices = [new("Debug", "Debug"), new("Release", "Release")],
        Default = "Debug",
    };

    public static Option EnvironmentOption() => new(Environment, "Environment variables", OptionKind.List) { IsKeyValueList = true };

    public static Option WorkingDirectoryOption(string placeholder) => new(WorkingDirectory, "Working directory", OptionKind.Path)
    {
        PathKind = PathKind.Folder,
        Group = OptionGroup.Advanced,
        Placeholder = placeholder,
    };

    public static Option SdkOption() => new(SdkPath, ".NET SDK", OptionKind.Sdk)
    {
        SdkKind = DotnetSdkProvider.SdkKind,
        Group = OptionGroup.Advanced,
        Description = "Empty uses the default .NET SDK.",
    };

    /// <summary>Gets the configuration's project, read from its file, or throws with a message for the user.</summary>
    public static DotnetProject ReadProject(RunConfiguration configuration, string rootPath)
    {
        var value = configuration.Values.Get(Project);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{configuration.Name} has no project. Choose one in Run › Edit Configurations.");

        var path = Path.GetFullPath(Path.Combine(rootPath, value));
        if (!File.Exists(path))
            throw new InvalidOperationException($"The project {value} of {configuration.Name} does not exist.");

        return DotnetProjects.Read(path, rootPath) ?? throw new InvalidOperationException($"The project {value} could not be read.");
    }

    /// <summary>Gets the build configuration, Debug unless the values say otherwise.</summary>
    public static string BuildConfiguration(OptionValues values) => values.Get(Configuration) is { Length: > 0 } value ? value : "Debug";

    /// <summary>Gets the working directory: the configuration's own, relative to the folder, or the fallback.</summary>
    public static string WorkingDirectoryOf(OptionValues values, string rootPath, string fallback) =>
        values.Get(WorkingDirectory) is { Length: > 0 } folder && !string.IsNullOrWhiteSpace(folder) ? Path.GetFullPath(Path.Combine(rootPath, folder.Trim())) : fallback;

    /// <summary>Gets the host's environment with the configuration's variables over it.</summary>
    public static Dictionary<string, string> EnvironmentOf(OptionValues values, DotnetHost host)
    {
        var environment = new Dictionary<string, string>(host.Environment, StringComparer.Ordinal);
        foreach (var (name, value) in values.GetPairs(Environment))
        {
            if (name.Trim().Length > 0)
                environment[name.Trim()] = value;
        }

        return environment;
    }

    /// <summary>Gives each configuration a unique name, adding the project's folder to names that two projects share.</summary>
    public static IReadOnlyList<RunConfiguration> Detected(string typeId, IReadOnlyList<DotnetProject> projects, Func<DotnetProject, OptionValues> values)
    {
        var shared = projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. projects.Select(project => new RunConfiguration(typeId,
                shared.Contains(project.Name) ? $"{project.Name} ({Path.GetDirectoryName(project.RelativePath)?.Replace('\\', '/')})" : project.Name,
                values(project))
            {
                IsDetected = true,
            }),
        ];
    }

    /// <summary>Splits a command line into arguments: spaces separate them, and single or double quotes keep spaces in one.</summary>
    public static IReadOnlyList<string> SplitCommandLine(string text)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var hasArgument = false;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } open)
            {
                if (c == open)
                    quote = null;
                else if (c == '\\' && open == '"' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                    current.Append(text[++i]);
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                hasArgument = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasArgument)
                    arguments.Add(current.ToString());
                current.Clear();
                hasArgument = false;
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }

        if (hasArgument)
            arguments.Add(current.ToString());
        return arguments;
    }

    /// <summary>Writes a command line the way <see cref="SplitCommandLine"/> reads it, for output.</summary>
    public static string JoinCommandLine(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? a : $"\"{a.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""));
}
