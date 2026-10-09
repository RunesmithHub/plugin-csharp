using System.Diagnostics;
using Runesmith.CSharp.Running;
using Runesmith.Sdk.Build;
using Runesmith.Sdk.Running;
using Runesmith.Sdk.Shell;

namespace Runesmith.CSharp.Tests.Running;

public sealed class DotnetRunEndToEndTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("runesmith-dotnet-e2e-").FullName;

    [Fact]
    public async Task BuildsAndRunsAConsoleProject()
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(root, "Hello"));
        await File.WriteAllTextAsync(Path.Combine(root, "Hello", "Hello.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """, token);
        await File.WriteAllTextAsync(Path.Combine(root, "Hello", "Program.cs"),
            """Console.WriteLine($"Hello from Runesmith, {string.Join(' ', args)}! {Environment.GetEnvironmentVariable("GREETING")}");""", token);
        var type = new DotnetProjectRunType();
        var configuration = Assert.Single(await type.DetectAsync(root, token));
        var values = configuration.Values.Copy();
        values.Set("arguments", "dear\nreader");
        values.Set("environment", "GREETING=welcome");
        configuration = configuration with { Values = values };

        var log = new Lines();
        var build = await type.BuildAsync(configuration, new BuildContext(root, log, _ => { }), token);
        Assert.True(build?.Succeeded, string.Join('\n', log.All));

        var plan = await type.PrepareAsync(configuration, new RunContext(root, RunMode.Run), token);
        var output = await RunAsync(plan, token);

        Assert.Contains("Hello from Runesmith, dear reader! welcome", output, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static async Task<string> RunAsync(LaunchPlan plan, CancellationToken token)
    {
        var start = new ProcessStartInfo(plan.Program)
        {
            WorkingDirectory = plan.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in plan.Arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in plan.Environment)
            start.Environment[name] = value;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}

internal sealed class Lines : IOutputChannel
{
    private readonly List<string> lines = [];

    public string Name => "Test";

    public IReadOnlyList<string> All
    {
        get
        {
            lock (lines)
                return [.. lines];
        }
    }

    public void Append(string text)
    {
        lock (lines)
            lines.Add(text);
    }

    public void AppendLine(string line) => Append(line);

    public void Clear()
    {
        lock (lines)
            lines.Clear();
    }

    public void Show()
    {
    }
}
