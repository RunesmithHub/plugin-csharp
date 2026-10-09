using System.Composition;
using Runesmith.Sdk.Build;
using Runesmith.Sdk.Options;
using Runesmith.Sdk.Running;
using Runesmith.Sdk.Sdks;
using static Runesmith.CSharp.Running.DotnetRunOptions;

namespace Runesmith.CSharp.Running;

/// <summary>Runs a test project with <c>dotnet test</c>: one configuration per project that sets <c>IsTestProject</c> or references a test
/// framework such as xUnit, NUnit or MSTest.</summary>
/// <remarks>Tests run with <c>--no-build</c>, since the Build step before them builds the project.</remarks>
[Export(typeof(IRunConfigurationType))]
[Shared]
public sealed class DotnetTestsRunType : IRunConfigurationType
{
    /// <summary>The type's id.</summary>
    public const string TypeId = "dotnet.tests";

    private readonly ISdkService? sdks;

    /// <summary>Creates the type, which runs with the <c>dotnet</c> on the <c>PATH</c>.</summary>
    public DotnetTestsRunType()
    {
    }

    /// <summary>Creates the type, which runs with the SDK a configuration picks or the user's default .NET SDK.</summary>
    [ImportingConstructor]
    public DotnetTestsRunType([Import(AllowDefault = true)] ISdkService? sdks) => this.sdks = sdks;

    public string Id => TypeId;

    public string Name => ".NET tests";

    public string Icon => "check-circle";

    public string Description => "Runs a test project's tests with dotnet test, optionally only those a filter selects.";

    public Task<OptionSet> GetOptionsAsync(string rootPath, CancellationToken cancellationToken) =>
        Task.Run(() => CreateOptions(FindProjects(rootPath, cancellationToken)), cancellationToken);

    public bool CanDebug => false;

    public Task<IReadOnlyList<RunConfiguration>> DetectAsync(string rootPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var projects = FindProjects(rootPath, cancellationToken);
            var set = CreateOptions(projects);
            return Detected(TypeId, projects, project =>
            {
                var values = set.Defaults();
                values.Set(Project, project.RelativePath);
                return values;
            });
        }, cancellationToken);

    public async Task<LaunchPlan> PrepareAsync(RunConfiguration configuration, RunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var values = configuration.Values;
        var project = ReadProject(configuration, context.RootPath);
        var host = await DotnetHost.ResolveAsync(sdks, values.Get(SdkPath), cancellationToken).ConfigureAwait(false);
        List<string> arguments = ["test", project.FullPath, "--no-build", "-c", BuildConfiguration(values)];
        if (values.Get(Filter) is { } filter && !string.IsNullOrWhiteSpace(filter))
            arguments.AddRange(["--filter", filter.Trim()]);
        return new LaunchPlan(host.Program, arguments, project.Folder) { Environment = EnvironmentOf(values, host) };
    }

    public async Task<BuildResult?> BuildAsync(RunConfiguration configuration, BuildContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var project = ReadProject(configuration, context.RootPath);
        var host = await DotnetHost.ResolveAsync(sdks, configuration.Values.Get(SdkPath), cancellationToken).ConfigureAwait(false);
        string[] arguments = ["build", project.FullPath, "-nologo", "-consoleLoggerParameters:NoSummary", "-c", BuildConfiguration(configuration.Values)];
        var display = "dotnet " + JoinCommandLine(arguments.Select((a, i) => i == 1 ? project.RelativePath : a));
        return await DotnetBuildRunner.RunAsync(context, host, project.FullPath, arguments, display, cancellationToken).ConfigureAwait(false);
    }

    private static List<DotnetProject> FindProjects(string rootPath, CancellationToken cancellationToken) =>
        [.. DotnetProjects.Find(rootPath, cancellationToken).Where(p => p.Kind == DotnetProjectKind.Test)];

    internal static OptionSet CreateOptions(IReadOnlyList<DotnetProject> projects) => new(
    [
        ProjectOption(projects, "The test project."),
        new Option(Filter, "Filter", OptionKind.Text)
        {
            Placeholder = "FullyQualifiedName~Parser",
            Description = "Runs only the tests that match, as dotnet test --filter selects them; empty runs every test.",
        },
        ConfigurationOption(),
        SdkOption(),
    ]);
}
