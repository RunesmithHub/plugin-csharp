using System.Composition;
using Runesmith.Sdk.Build;
using Runesmith.Sdk.Options;
using Runesmith.Sdk.Running;
using Runesmith.Sdk.Sdks;
using static Runesmith.CSharp.Running.DotnetRunOptions;

namespace Runesmith.CSharp.Running;

/// <summary>Runs a .NET program: one configuration per project whose <c>OutputType</c> is <c>Exe</c> or <c>WinExe</c>, or that uses a web or
/// worker SDK.</summary>
/// <remarks>Running starts <c>dotnet run --project &lt;project&gt; --no-build -c &lt;configuration&gt; -f &lt;framework&gt;</c>, so the
/// launch profile's environment and URLs apply as they do on the command line; the Build step before it builds the project. Debugging
/// starts the built assembly with <c>dotnet</c> instead, which a debugger can start itself.</remarks>
[Export(typeof(IRunConfigurationType))]
[Shared]
public sealed class DotnetProjectRunType : IRunConfigurationType
{
    /// <summary>The type's id.</summary>
    public const string TypeId = "dotnet.project";

    private readonly ISdkService? sdks;

    /// <summary>Creates the type, which runs with the <c>dotnet</c> on the <c>PATH</c>.</summary>
    public DotnetProjectRunType()
    {
    }

    /// <summary>Creates the type, which runs with the SDK a configuration picks or the user's default .NET SDK.</summary>
    [ImportingConstructor]
    public DotnetProjectRunType([Import(AllowDefault = true)] ISdkService? sdks) => this.sdks = sdks;

    public string Id => TypeId;

    public string Name => ".NET project";

    public string Icon => "play";

    public string Description => "Runs a console, desktop or web project with dotnet run.";

    public Task<OptionSet> GetOptionsAsync(string rootPath, CancellationToken cancellationToken) =>
        Task.Run(() => CreateOptions(FindProjects(rootPath, cancellationToken)), cancellationToken);

    public bool CanDebug => true;

    public Task<IReadOnlyList<RunConfiguration>> DetectAsync(string rootPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var projects = FindProjects(rootPath, cancellationToken);
            return DetectedConfigurations(projects, CreateOptions(projects));
        }, cancellationToken);

    public async Task<LaunchPlan> PrepareAsync(RunConfiguration configuration, RunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var values = configuration.Values;
        var project = ReadProject(configuration, context.RootPath);
        var host = await DotnetHost.ResolveAsync(sdks, values.Get(SdkPath), cancellationToken).ConfigureAwait(false);
        var buildConfiguration = BuildConfiguration(values);
        var framework = project.PickFramework(values.Get(Framework));
        var arguments = values.GetList(Arguments);
        var workingDirectory = WorkingDirectoryOf(values, context.RootPath, project.Folder);
        var environment = EnvironmentOf(values, host);

        if (context.Mode == RunMode.Debug)
        {
            var program = Path.Combine(project.Folder, "bin", buildConfiguration, framework ?? "", project.AssemblyName + ".dll");
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["program"] = program,
                ["args"] = string.Join('\n', arguments),
                ["cwd"] = workingDirectory,
                ["env"] = string.Join('\n', environment.Select(pair => $"{pair.Key}={pair.Value}")),
            };
            return new LaunchPlan(host.Program, [program, .. arguments], workingDirectory)
            {
                Environment = environment,
                Debug = new DebugLaunch("dotnet", settings),
            };
        }

        List<string> run = ["run", "--project", project.FullPath, "--no-build", "-c", buildConfiguration];
        if (framework is not null)
            run.AddRange(["-f", framework]);
        switch (values.Get(LaunchProfile))
        {
            case "":
                run.Add("--no-launch-profile");
                break;
            case { } profile when project.LaunchProfiles.Contains(profile, StringComparer.Ordinal):
                run.AddRange(["--launch-profile", profile]);
                break;
        }

        if (arguments.Count > 0)
            run.AddRange(["--", .. arguments]);
        return new LaunchPlan(host.Program, run, workingDirectory) { Environment = environment };
    }

    public async Task<BuildResult?> BuildAsync(RunConfiguration configuration, BuildContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var project = ReadProject(configuration, context.RootPath);
        var host = await DotnetHost.ResolveAsync(sdks, configuration.Values.Get(SdkPath), cancellationToken).ConfigureAwait(false);
        List<string> arguments = ["build", project.FullPath, "-nologo", "-consoleLoggerParameters:NoSummary", "-c", BuildConfiguration(configuration.Values)];
        if (project.PickFramework(configuration.Values.Get(Framework)) is { } framework)
            arguments.AddRange(["-f", framework]);
        var display = "dotnet " + JoinCommandLine(arguments.Select((a, i) => i == 1 ? project.RelativePath : a));
        return await DotnetBuildRunner.RunAsync(context, host, project.FullPath, arguments, display, cancellationToken).ConfigureAwait(false);
    }

    private static List<DotnetProject> FindProjects(string rootPath, CancellationToken cancellationToken) =>
        [.. DotnetProjects.Find(rootPath, cancellationToken).Where(p => p.Kind == DotnetProjectKind.Runnable)];

    internal static OptionSet CreateOptions(IReadOnlyList<DotnetProject> projects)
    {
        List<Option> list = [ProjectOption(projects, "The project to run.")];
        if (projects.Count == 0)
        {
            list.Add(new Option(Framework, "Target framework", OptionKind.Text) { Placeholder = "The project's first framework" });
            list.Add(new Option(LaunchProfile, "Launch profile", OptionKind.Text) { Placeholder = "The first profile in launchSettings.json" });
        }

        foreach (var project in projects)
        {
            var visible = new OptionCondition(Project, [project.RelativePath]);
            if (project.TargetFrameworks.Count > 0)
            {
                List<OptionChoice> frameworks = [.. project.TargetFrameworks.Select(f => new OptionChoice(f, f))];
                if (frameworks.Count > 1)
                    frameworks.Insert(0, new OptionChoice("", "Project default") { Description = $"The first framework, {project.TargetFrameworks[0]}." });
                list.Add(new Option(Framework, "Target framework", OptionKind.Choice) { Choices = frameworks, VisibleWhen = visible });
            }

            if (project.LaunchProfiles.Count > 0)
            {
                list.Add(new Option(LaunchProfile, "Launch profile", OptionKind.Choice)
                {
                    Choices = [new OptionChoice("", "None") { Description = "Run without a launch profile." }, .. project.LaunchProfiles.Select(p => new OptionChoice(p, p))],
                    VisibleWhen = visible,
                    Description = "A profile from Properties/launchSettings.json, with its environment and URLs.",
                });
            }
        }

        list.Add(ConfigurationOption());
        list.Add(new Option(Arguments, "Program arguments", OptionKind.List) { Placeholder = "Argument" });
        list.Add(EnvironmentOption());
        list.Add(WorkingDirectoryOption("The project's folder"));
        list.Add(SdkOption());
        return new OptionSet(list);
    }

    private static IReadOnlyList<RunConfiguration> DetectedConfigurations(IReadOnlyList<DotnetProject> projects, OptionSet set) =>
        Detected(TypeId, projects, project =>
        {
            var values = set.Defaults();
            values.Set(Project, project.RelativePath);
            if (project.TargetFrameworks.Count > 0)
                values.Set(Framework, project.TargetFrameworks[0]);
            if (project.LaunchProfiles.Count > 0)
                values.Set(LaunchProfile, project.LaunchProfiles[0]);
            return values;
        });
}
