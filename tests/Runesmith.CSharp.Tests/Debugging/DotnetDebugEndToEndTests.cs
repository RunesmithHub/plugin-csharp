using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Runesmith.CSharp.Debugging;
using Runesmith.CSharp.Running;
using Runesmith.CSharp.Tests.Running;
using Runesmith.Sdk.Build;
using Runesmith.Sdk.Debugging;
using Runesmith.Sdk.Running;

namespace Runesmith.CSharp.Tests.Debugging;

/// <summary>Downloads the real netcoredbg and debugs a console app with it; runs only when RUNESMITH_TEST_DEBUGGER is 1.</summary>
public sealed class DotnetDebugEndToEndTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("runesmith-debug-e2e-").FullName;

    [Fact]
    public async Task StopsAtABreakpointShowsVariablesAndRunsToTheEnd()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RUNESMITH_TEST_DEBUGGER") == "1",
            "Set RUNESMITH_TEST_DEBUGGER=1 to download netcoredbg and debug a console app.");
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(root, "Hello", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(Path.Combine(root, "Hello", "Hello.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, token);
        await File.WriteAllTextAsync(source, """
            var greeting = "Hello, " + System.Environment.GetEnvironmentVariable("NAME");
            var count = args.Length;
            System.Console.WriteLine(greeting + " " + count);
            """, token);
        var type = new DotnetProjectRunType();
        var configuration = Assert.Single(await type.DetectAsync(root, token));
        var values = configuration.Values.Copy();
        values.Set("arguments", "one\ntwo");
        values.Set("environment", "NAME=debugger");
        configuration = configuration with { Values = values };
        var log = new Lines();
        Assert.True((await type.BuildAsync(configuration, new BuildContext(root, log, _ => { }), token))?.Succeeded, string.Join('\n', log.All));
        var plan = await type.PrepareAsync(configuration, new RunContext(root, RunMode.Debug), token);
        var context = new DebugAdapterContext(plan, plan.Debug!, root);

        var engine = new DotnetDebugEngine(new TestStorage(Path.Combine(root, "storage")), null);
        var adapter = Assert.IsType<DebugAdapterExecutable>(await engine.CreateAdapterAsync(context, token));
        var request = await engine.CreateRequestAsync(context, token);
        await using var session = DapSession.Start(adapter);

        await session.RequestAsync("initialize", new JsonObject { ["adapterID"] = request.AdapterId, ["linesStartAt1"] = true, ["columnsStartAt1"] = true, ["pathFormat"] = "path" }, token);
        var launched = session.RequestAsync("launch", request.Arguments, token);
        await session.WaitForEventAsync("initialized", token);
        await session.RequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = source },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 3 }),
        }, token);
        await session.RequestAsync("configurationDone", [], token);
        await launched;

        var stopped = await session.WaitForEventAsync("stopped", token);
        Assert.Equal("breakpoint", (string?)stopped["reason"]);
        var frames = await session.RequestAsync("stackTrace", new JsonObject { ["threadId"] = (int)stopped["threadId"]! }, token);
        var frame = frames["stackFrames"]![0]!;
        Assert.Equal(3, (int)frame["line"]!);
        var scopes = await session.RequestAsync("scopes", new JsonObject { ["frameId"] = (int)frame["id"]! }, token);
        var variables = await session.RequestAsync("variables", new JsonObject { ["variablesReference"] = (int)scopes["scopes"]![0]!["variablesReference"]! }, token);
        var shown = variables["variables"]!.AsArray().ToDictionary(v => (string)v!["name"]!, v => (string?)v!["value"]);
        Assert.Equal("\"Hello, debugger\"", shown["greeting"]);
        Assert.Equal("2", shown["count"]);

        await session.RequestAsync("continue", new JsonObject { ["threadId"] = (int)stopped["threadId"]! }, token);
        var exited = await session.WaitForEventAsync("exited", token);
        Assert.Equal(0, (int)exited["exitCode"]!);
        Assert.Contains("Hello, debugger 2", session.Output, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Talks to a debug adapter over its standard input and output.</summary>
    private sealed class DapSession : IAsyncDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        private readonly Process process;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
        private readonly Channel<JsonObject> events = Channel.CreateUnbounded<JsonObject>();
        private readonly StringBuilder output = new();
        private readonly Task reading;
        private int sequence;

        private DapSession(Process process)
        {
            this.process = process;
            reading = Task.Run(ReadAsync);
        }

        public string Output
        {
            get
            {
                lock (output)
                    return output.ToString();
            }
        }

        public static DapSession Start(DebugAdapterExecutable adapter)
        {
            var start = new ProcessStartInfo(adapter.Command)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in adapter.Arguments)
                start.ArgumentList.Add(argument);
            var process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return new DapSession(process);
        }

        public async Task<JsonObject> RequestAsync(string command, JsonObject arguments, CancellationToken token)
        {
            var seq = Interlocked.Increment(ref sequence);
            var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[seq] = reply;
            var message = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = arguments.DeepClone() };
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            var stream = process.StandardInput.BaseStream;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), token);
            await stream.WriteAsync(body, token);
            await stream.FlushAsync(token);
            var response = await reply.Task.WaitAsync(Timeout, token);
            Assert.True((bool)response["success"]!, $"{command} failed: {response["message"]}");
            return response["body"] as JsonObject ?? [];
        }

        public async Task<JsonObject> WaitForEventAsync(string name, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Timeout);
            while (true)
            {
                var message = await events.Reader.ReadAsync(timeout.Token);
                if ((string?)message["event"] == name)
                    return message["body"] as JsonObject ?? [];
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await reading;
            process.Dispose();
        }

        private async Task ReadAsync()
        {
            var stream = process.StandardOutput.BaseStream;
            try
            {
                while (await ReadHeaderAsync(stream) is { } length)
                {
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body);
                    if (JsonNode.Parse(body) is not JsonObject message)
                        continue;

                    switch ((string?)message["type"])
                    {
                        case "response" when pending.TryRemove((int)message["request_seq"]!, out var reply):
                            reply.SetResult(message);
                            break;
                        case "event":
                            if ((string?)message["event"] == "output")
                            {
                                lock (output)
                                    output.Append((string?)message["body"]!["output"]);
                            }

                            events.Writer.TryWrite(message);
                            break;
                    }
                }
            }
            catch (EndOfStreamException)
            {
            }
        }

        private static async Task<int?> ReadHeaderAsync(Stream stream)
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one) == 0)
                    return null;
                header.Append((char)one[0]);
            }

            var line = header.ToString().Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            return int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
