using System.Composition;
using System.Diagnostics;
using Runesmith.CSharp.Sdks;
using Runesmith.Sdk.Build;
using Runesmith.Sdk.Sdks;

namespace Runesmith.CSharp;

/// <summary>Builds a folder's .NET solution or project with <c>dotnet build</c> and reports the errors and warnings it finds.</summary>
[Export(typeof(IBuildProvider))]
public sealed class DotnetBuildProvider : IBuildProvider
{
    private static readonly string[] TargetPatterns = ["*.slnx", "*.sln", "*.csproj", "*.fsproj"];

    private readonly ISdkService? sdks;

    /// <summary>Creates the provider, which builds with the <c>dotnet</c> on the <c>PATH</c>.</summary>
    public DotnetBuildProvider()
    {
    }

    /// <summary>Creates the provider, which builds with the user's default .NET SDK when Runesmith installed it.</summary>
    [ImportingConstructor]
    public DotnetBuildProvider([Import(AllowDefault = true)] ISdkService? sdks) => this.sdks = sdks;

    public string Name => ".NET";

    public bool CanBuild(string rootPath) => FindTarget(rootPath) is not null;

    public async Task<BuildResult> BuildAsync(BuildContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = FindTarget(context.RootPath);
        if (target is null)
        {
            context.Output.AppendLine($"There is no solution or project to build in {context.RootPath}.");
            return new BuildResult(false, 0, 0, TimeSpan.Zero);
        }

        var sdk = sdks is null ? null : await sdks.GetDefaultAsync(DotnetSdkProvider.SdkKind, cancellationToken).ConfigureAwait(false);
        var host = sdk is { Source: SdkSource.Managed } ? DotnetHost.FromRoot(sdk.Path) : DotnetHost.OnPath();
        string[] arguments = ["build", target, "-nologo", "-consoleLoggerParameters:NoSummary"];
        return await DotnetBuildRunner.RunAsync(context, host, target, arguments, $"dotnet build {Path.GetFileName(target)}", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Finds what to build at the top of a folder: a solution before a project.</summary>
    public static string? FindTarget(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return null;

        foreach (var pattern in TargetPatterns)
        {
            if (Directory.EnumerateFiles(rootPath, pattern).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } file)
                return file;
        }

        return null;
    }

    /// <summary>Makes a process run the <c>dotnet</c> of a .NET root, and the SDKs and runtimes in it.</summary>
    internal static void UseRoot(ProcessStartInfo start, string root)
    {
        var host = DotnetHost.FromRoot(root);
        start.FileName = host.Program;
        start.Environment["DOTNET_ROOT"] = root;
        start.Environment["PATH"] = start.Environment.TryGetValue("PATH", out var path) && !string.IsNullOrEmpty(path) ? root + Path.PathSeparator + path : root;
    }
}
