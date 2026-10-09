using System.Composition;
using Runesmith.Sdk.Build;
using Runesmith.Sdk.Options;
using Runesmith.Sdk.Running;
using Runesmith.Sdk.Sdks;
using static Runesmith.CSharp.Running.DotnetRunOptions;

namespace Runesmith.CSharp.Running;

/// <summary>Runs any <c>dotnet</c> command line, such as <c>ef database update</c> or <c>format</c>; it is never detected.</summary>
[Export(typeof(IRunConfigurationType))]
[Shared]
public sealed class DotnetCommandRunType : IRunConfigurationType
{
    /// <summary>The type's id.</summary>
    public const string TypeId = "dotnet.command";

    private readonly ISdkService? sdks;

    /// <summary>Creates the type, which runs the <c>dotnet</c> on the <c>PATH</c>.</summary>
    public DotnetCommandRunType()
    {
    }

    /// <summary>Creates the type, which runs the SDK a configuration picks or the user's default .NET SDK.</summary>
    [ImportingConstructor]
    public DotnetCommandRunType([Import(AllowDefault = true)] ISdkService? sdks) => this.sdks = sdks;

    public string Id => TypeId;

    public string Name => ".NET command";

    public string Icon => "terminal";

    public string Description => "Runs a dotnet command line in the folder, such as format or ef database update.";

    private static readonly OptionSet Options = new(
    [
        new Option(Command, "Command", OptionKind.Text)
        {
            IsRequired = true,
            Placeholder = "build --no-incremental",
            Description = "What follows dotnet; quote an argument that has spaces.",
        },
        EnvironmentOption(),
        WorkingDirectoryOption("The open folder"),
        SdkOption(),
    ]);

    public Task<OptionSet> GetOptionsAsync(string rootPath, CancellationToken cancellationToken) => Task.FromResult(Options);

    public bool CanDebug => false;

    public Task<IReadOnlyList<RunConfiguration>> DetectAsync(string rootPath, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunConfiguration>>([]);

    public async Task<LaunchPlan> PrepareAsync(RunConfiguration configuration, RunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var values = configuration.Values;
        var arguments = SplitCommandLine(values.Get(Command) ?? "");
        if (arguments is [] or ["dotnet"])
            throw new InvalidOperationException($"{configuration.Name} has no command. Type one in Run › Edit Configurations.");

        var host = await DotnetHost.ResolveAsync(sdks, values.Get(SdkPath), cancellationToken).ConfigureAwait(false);
        var withoutHost = arguments[0] == "dotnet" ? arguments.Skip(1).ToList() : arguments;
        return new LaunchPlan(host.Program, withoutHost, WorkingDirectoryOf(values, context.RootPath, context.RootPath)) { Environment = EnvironmentOf(values, host) };
    }

    public Task<BuildResult?> BuildAsync(RunConfiguration configuration, BuildContext context, CancellationToken cancellationToken) =>
        Task.FromResult<BuildResult?>(null);
}
