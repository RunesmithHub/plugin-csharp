using System.ComponentModel;
using System.Diagnostics;
using Runesmith.Sdk.Build;

namespace Runesmith.CSharp;

/// <summary>Runs <c>dotnet build</c>, writing its output to a build's channel and reporting the errors and warnings it finds.</summary>
internal static class DotnetBuildRunner
{
    /// <summary>Builds and returns the outcome; canceling the token kills the build.</summary>
    /// <param name="host">The <c>dotnet</c> to run and the environment it needs.</param>
    /// <param name="target">The solution or project built, which diagnostics without a project are reported on.</param>
    /// <param name="arguments">The arguments after <c>dotnet</c>.</param>
    /// <param name="display">The command as the output shows it.</param>
    public static async Task<BuildResult> RunAsync(BuildContext context, DotnetHost host, string target, IReadOnlyList<string> arguments, string display,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var start = new ProcessStartInfo(host.Program)
        {
            WorkingDirectory = context.RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in host.Environment)
            start.Environment[name] = value;

        context.Output.AppendLine($"> {display}");
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            context.Output.AppendLine($"Could not start dotnet ({exception.Message}). Install the .NET SDK from https://dot.net and make sure dotnet is on the PATH.");
            return new BuildResult(false, 0, 0, stopwatch.Elapsed);
        }

        var collector = new DiagnosticCollector(context, target);
        using var registration = cancellationToken.Register(() => Kill(process));
        await Task.WhenAll(ReadAsync(process.StandardOutput, collector), ReadAsync(process.StandardError, collector)).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var succeeded = process.ExitCode == 0;
        context.Output.AppendLine(succeeded
            ? $"Build succeeded with {collector.Warnings} warning(s) in {stopwatch.Elapsed.TotalSeconds:0.0} s."
            : $"Build failed with {collector.Errors} error(s) and {collector.Warnings} warning(s) in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
        return new BuildResult(succeeded, collector.Errors, collector.Warnings, stopwatch.Elapsed);
    }

    private static async Task ReadAsync(StreamReader reader, DiagnosticCollector collector)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            collector.Add(line);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The build had exited already.
        }
    }

    // Both output streams feed it from their own threads, and MSBuild repeats some diagnostics, so it locks and de-duplicates.
    private sealed class DiagnosticCollector(BuildContext context, string target)
    {
        private readonly HashSet<Diagnostic> seen = [];
        private readonly Lock gate = new();

        public int Errors { get; private set; }

        public int Warnings { get; private set; }

        public void Add(string line)
        {
            context.Output.AppendLine(line);
            if (MSBuildDiagnosticParser.Parse(line, target) is not { } diagnostic)
                return;

            lock (gate)
            {
                if (!seen.Add(diagnostic))
                    return;

                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    Errors++;
                else
                    Warnings++;
            }

            context.ReportDiagnostic(diagnostic);
        }
    }
}
