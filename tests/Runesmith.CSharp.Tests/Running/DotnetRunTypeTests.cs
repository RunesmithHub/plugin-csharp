using Runesmith.CSharp.Running;
using Runesmith.Sdk.Options;
using Runesmith.Sdk.Running;

namespace Runesmith.CSharp.Tests.Running;

public sealed class DotnetRunTypeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("runesmith-dotnet-run-").FullName;

    [Fact]
    public async Task DetectsConsoleWebAndTestProjectsSeparately()
    {
        Project("src/App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>");
        Project("src/Site/Site.csproj", "Microsoft.NET.Sdk.Web", "<TargetFramework>net10.0</TargetFramework>");
        Project("src/Lib/Lib.csproj", "Microsoft.NET.Sdk", "<TargetFramework>net10.0</TargetFramework>");
        Project("tests/App.Tests/App.Tests.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>",
            "<ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup>");
        Project("tests/Other.Tests/Other.Tests.csproj", "Microsoft.NET.Sdk", "<IsTestProject>true</IsTestProject>");
        Project("src/App/bin/Debug/Copy.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType>");

        var runs = await new DotnetProjectRunType().DetectAsync(root, TestContext.Current.CancellationToken);
        var tests = await new DotnetTestsRunType().DetectAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(["App", "Site"], runs.Select(c => c.Name));
        Assert.All(runs, c => Assert.True(c.IsDetected));
        Assert.All(runs, c => Assert.Equal(DotnetProjectRunType.TypeId, c.TypeId));
        Assert.Equal("src/App/App.csproj", runs[0].Values.Get("project"));
        Assert.Equal("net10.0", runs[0].Values.Get("framework"));
        Assert.Equal("Debug", runs[0].Values.Get("configuration"));
        Assert.Equal(["App.Tests", "Other.Tests"], tests.Select(c => c.Name));
        Assert.Empty(await new DotnetCommandRunType().DetectAsync(root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NamesProjectsThatShareANameByTheirFolder()
    {
        Project("a/App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType>");
        Project("b/App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>WinExe</OutputType>");

        var runs = await new DotnetProjectRunType().DetectAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(["App (a/App)", "App (b/App)"], runs.Select(c => c.Name));
    }

    [Fact]
    public async Task OffersEachProjectsFrameworksAndLaunchProfiles()
    {
        Project("App/App.csproj", "Microsoft.NET.Sdk.Web", "<TargetFrameworks>net10.0;net9.0</TargetFrameworks>");
        Write("App/Properties/launchSettings.json", """
            {
              // Comments are allowed.
              "profiles": {
                "http": { "commandName": "Project", "applicationUrl": "http://localhost:5000" },
                "IIS Express": { "commandName": "IISExpress" },
                "https": { "commandName": "Project" },
              }
            }
            """);
        Project("Tool/Tool.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>");
        var type = new DotnetProjectRunType();

        var runs = await type.DetectAsync(root, TestContext.Current.CancellationToken);
        var options = await type.GetOptionsAsync(root, TestContext.Current.CancellationToken);

        var project = options.Options.Single(o => o.Id == "project");
        Assert.Equal(OptionKind.Choice, project.Kind);
        Assert.Equal(["App/App.csproj", "Tool/Tool.csproj"], project.Choices.Select(c => c.Value));
        var profiles = options.Options.Single(o => o.Id == "launchProfile");
        Assert.Equal(["", "http", "https"], profiles.Choices.Select(c => c.Value));
        Assert.Equal(["App/App.csproj"], profiles.VisibleWhen!.Values);
        var frameworks = options.Options.Where(o => o.Id == "framework").ToList();
        Assert.Equal(["", "net10.0", "net9.0"], frameworks[0].Choices.Select(c => c.Value));
        Assert.Equal(["net10.0"], frameworks[1].Choices.Select(c => c.Value));
        Assert.Equal("http", runs[0].Values.Get("launchProfile"));
        Assert.Empty(options.Validate(runs[0].Values));
    }

    [Fact]
    public async Task WithoutProjectsTheProjectIsAFile()
    {
        var options = await new DotnetProjectRunType().GetOptionsAsync(root, TestContext.Current.CancellationToken);
        var project = options.Options.Single(o => o.Id == "project");

        Assert.Equal(OptionKind.Path, project.Kind);
        Assert.Equal(PathKind.File, project.PathKind);
    }

    [Fact]
    public async Task RunsTheProjectWithDotnetRun()
    {
        Project("App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType><TargetFrameworks>net10.0;net9.0</TargetFrameworks>");
        Write("App/Properties/launchSettings.json", """{ "profiles": { "dev": { "commandName": "Project" } } }""");
        var configuration = Configuration(("project", "App/App.csproj"), ("framework", "net9.0"), ("launchProfile", "dev"), ("configuration", "Release"),
            ("arguments", "--name\nTwo words"), ("environment", "GREETING=hello\nEMPTY="));

        var plan = await new DotnetProjectRunType().PrepareAsync(configuration, new RunContext(root, RunMode.Run), TestContext.Current.CancellationToken);

        var project = Path.Combine(root, "App", "App.csproj");
        Assert.Equal(["run", "--project", project, "--no-build", "-c", "Release", "-f", "net9.0", "--launch-profile", "dev", "--", "--name", "Two words"], plan.Arguments);
        Assert.Equal(Path.Combine(root, "App"), plan.WorkingDirectory);
        Assert.Equal("hello", plan.Environment["GREETING"]);
        Assert.Equal("", plan.Environment["EMPTY"]);
        Assert.Null(plan.Debug);
    }

    [Fact]
    public async Task FallsBackWhenTheValuesDoNotFitTheProject()
    {
        Project("App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>");
        var stale = Configuration(("project", "App/App.csproj"), ("framework", "net8.0"), ("launchProfile", "gone"), ("workingDirectory", "data"));
        var none = Configuration(("project", "App/App.csproj"), ("launchProfile", ""));

        var plan = await new DotnetProjectRunType().PrepareAsync(stale, new RunContext(root, RunMode.Run), TestContext.Current.CancellationToken);
        var withoutProfile = await new DotnetProjectRunType().PrepareAsync(none, new RunContext(root, RunMode.Run), TestContext.Current.CancellationToken);

        Assert.Equal(["run", "--project", Path.Combine(root, "App", "App.csproj"), "--no-build", "-c", "Debug", "-f", "net10.0"], plan.Arguments);
        Assert.Equal(Path.Combine(root, "data"), plan.WorkingDirectory);
        Assert.Contains("--no-launch-profile", withoutProfile.Arguments);
    }

    [Fact]
    public async Task ReadsTheFrameworkFromDirectoryBuildProps()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Project("src/App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType>");

        var plan = await new DotnetProjectRunType().PrepareAsync(Configuration(("project", "src/App/App.csproj")), new RunContext(root, RunMode.Run),
            TestContext.Current.CancellationToken);

        Assert.Equal(["-f", "net10.0"], plan.Arguments.Skip(6).Take(2));
    }

    [Fact]
    public async Task UsesTheDotnetOfTheChosenSdk()
    {
        Project("App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>");
        var sdk = Path.Combine(root, "sdk-root");
        Write(Path.Combine("sdk-root", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");

        var plan = await new DotnetProjectRunType().PrepareAsync(Configuration(("project", "App/App.csproj"), ("sdk", sdk)), new RunContext(root, RunMode.Run),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(sdk, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), plan.Program);
        Assert.Equal(sdk, plan.Environment["DOTNET_ROOT"]);
        Assert.StartsWith(sdk + Path.PathSeparator, plan.Environment["PATH"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebuggingStartsTheBuiltAssembly()
    {
        Project("App/App.csproj", "Microsoft.NET.Sdk", "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>hello</AssemblyName>");
        var configuration = Configuration(("project", "App/App.csproj"), ("arguments", "a\nb"), ("environment", "X=1"));

        var plan = await new DotnetProjectRunType().PrepareAsync(configuration, new RunContext(root, RunMode.Debug), TestContext.Current.CancellationToken);

        var program = Path.Combine(root, "App", "bin", "Debug", "net10.0", "hello.dll");
        Assert.Equal([program, "a", "b"], plan.Arguments);
        Assert.NotNull(plan.Debug);
        Assert.Equal("dotnet", plan.Debug.Debugger);
        Assert.Equal(program, plan.Debug.Settings["program"]);
        Assert.Equal("a\nb", plan.Debug.Settings["args"]);
        Assert.Equal(Path.Combine(root, "App"), plan.Debug.Settings["cwd"]);
        Assert.Contains("X=1", plan.Debug.Settings["env"].Split('\n'));
    }

    [Fact]
    public async Task TestsRunWithDotnetTestAndTheirFilter()
    {
        Project("App.Tests/App.Tests.csproj", "Microsoft.NET.Sdk", "<IsTestProject>true</IsTestProject>");
        var configuration = new RunConfiguration(DotnetTestsRunType.TypeId, "App.Tests", Values(("project", "App.Tests/App.Tests.csproj"), ("filter", "Name~Parser")));

        var plan = await new DotnetTestsRunType().PrepareAsync(configuration, new RunContext(root, RunMode.Run), TestContext.Current.CancellationToken);

        Assert.Equal(["test", Path.Combine(root, "App.Tests", "App.Tests.csproj"), "--no-build", "-c", "Debug", "--filter", "Name~Parser"], plan.Arguments);
        Assert.False(new DotnetTestsRunType().CanDebug);
    }

    [Fact]
    public async Task CommandsSplitTheirLineAndRunInTheFolder()
    {
        var configuration = new RunConfiguration(DotnetCommandRunType.TypeId, "Format", Values(("command", "dotnet format --include \"My File.cs\" 'a b'")));
        var type = new DotnetCommandRunType();

        var plan = await type.PrepareAsync(configuration, new RunContext(root, RunMode.Run), TestContext.Current.CancellationToken);

        Assert.Equal(["format", "--include", "My File.cs", "a b"], plan.Arguments);
        Assert.Equal(root, plan.WorkingDirectory);
        Assert.Null(await type.BuildAsync(configuration, new Sdk.Build.BuildContext(root, new Lines(), _ => { }), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingProjectSaysSo()
    {
        var configuration = Configuration(("project", "Gone/Gone.csproj"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetProjectRunType().PrepareAsync(configuration, new RunContext(root, RunMode.Run), TestContext.Current.CancellationToken));

        Assert.Contains("Gone/Gone.csproj", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static RunConfiguration Configuration(params (string Id, string Value)[] values) =>
        new(DotnetProjectRunType.TypeId, "App", Values(values));

    private static OptionValues Values(params (string Id, string Value)[] values)
    {
        var result = new OptionValues();
        foreach (var (id, value) in values)
            result.Set(id, value);
        return result;
    }

    private void Project(string relativePath, string sdk, string properties, string items = "") =>
        Write(relativePath, $"<Project Sdk=\"{sdk}\"><PropertyGroup>{properties}</PropertyGroup>{items}</Project>");

    private void Write(string relativePath, string text)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
