using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>
/// Протокол канала движок ↔ окно и сводка для окна. Тест играет роль окна:
/// поднимает сервер канала и запускает <c>build --pipe</c> через шов CLI.
/// </summary>
public sealed class PipeProtocolTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly string _workspace;
    readonly string _source;
    readonly string _output;

    public PipeProtocolTests()
    {
        _workspace = Path.Combine(_dir.Path, "workspace");
        _source = new FakeWindowsIso().WriteTo(_dir.Path);
        _output = _dir.File("result.iso");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Build_reports_steps_progress_and_result()
    {
        var (messages, exitCode) = await BuildOverPipe(WriteProfile());

        Assert.Equal(Cli.ExitCodes.Success, exitCode);
        var types = messages.Select(m => m.GetProperty("type").GetString()).ToList();

        Assert.Equal(["ExtractImage", "WriteAnswerFile", "PackIso"],
            messages.Where(m => Type(m) == "step").Select(m => m.GetProperty("kind").GetString()));
        Assert.Contains("progress", types);
        Assert.All(messages.Where(m => Type(m) == "progress"),
            m => Assert.InRange(m.GetProperty("fraction").GetDouble(), 0, 1));
        Assert.Contains(messages, m => Type(m) == "log" && m.GetProperty("text").GetString()!.StartsWith("Image: "));
        Assert.Equal(_output, messages.Single(m => Type(m) == "done").GetProperty("outputIso").GetString());
        Assert.Equal("exit", types[^1]);
        Assert.Equal(0, messages[^1].GetProperty("code").GetInt32());
        Assert.True(File.Exists(_output));
    }

    [Fact]
    public async Task Warnings_and_errors_arrive_as_their_own_messages()
    {
        var profile = _dir.Write("warn.json", JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            setup = new { wipeDisk0 = true },
            source = new { iso = _source },
            output = new { iso = _output },
        }));

        var (warned, _) = await BuildOverPipe(profile);
        var (failed, exitCode) = await BuildOverPipe(_dir.Write("bad.json", """{ "formatVersion": 1 }"""));

        Assert.Contains(warned, m => Type(m) == "warning" && m.GetProperty("text").GetString()!.Contains("erase disk 0"));
        Assert.Equal(Cli.ExitCodes.InvalidInput, exitCode);
        Assert.Contains(failed, m => Type(m) == "error" && m.GetProperty("text").GetString() == "profile has no 'source.iso'.");
        Assert.Equal(Cli.ExitCodes.InvalidInput, failed[^1].GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Cancel_command_stops_the_build_and_leaves_nothing_behind()
    {
        var (messages, exitCode) = await BuildOverPipe(WriteProfile(), cancelAtConnect: true);

        Assert.Equal(Cli.ExitCodes.Cancelled, exitCode);
        Assert.Contains(messages, m => Type(m) == "cancelled");
        Assert.DoesNotContain(messages, m => Type(m) == "done");
        Assert.False(File.Exists(_output));
        Assert.False(Directory.Exists(_workspace) && Directory.EnumerateFileSystemEntries(_workspace).Any());
    }

    [Fact]
    public async Task Closing_the_app_cancels_the_build()
    {
        var name = PipeName();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var build = CliRun.RunAsync("build", "--profile", WriteProfile(), "--workspace", _workspace, "--pipe", name);

        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        server.Dispose(); // окно закрыли

        var result = await build;
        Assert.Equal(Cli.ExitCodes.Cancelled, result.ExitCode);
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task Dry_run_json_is_the_summary_for_the_app()
    {
        var result = await CliRun.RunAsync("build", "--profile", WriteProfile(), "--workspace", _workspace, "--dry-run", "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var plan = json.RootElement;
        Assert.Equal("Windows 11 24H2 (26100.1742)", plan.GetProperty("image").GetProperty("release").GetProperty("name").GetString());
        Assert.Equal(["ExtractImage", "WriteAnswerFile", "PackIso"],
            plan.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));
        Assert.Equal(["bypass.tpm"], plan.GetProperty("tweaks").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(_output, plan.GetProperty("outputIso").GetString());
        Assert.True(plan.GetProperty("space").GetArrayLength() > 0);
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task Dry_run_json_reports_problems_with_a_code()
    {
        var host = new EngineHost { AvailableFreeSpace = _ => 1024 };

        var result = await CliRun.RunAsync(host, "build", "--profile", WriteProfile(), "--workspace", _workspace, "--dry-run", "--json");

        Assert.Equal(Cli.ExitCodes.NotEnoughSpace, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("not-enough-space", error.GetProperty("code").GetString());
        Assert.Contains("not enough space", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Json_without_dry_run_is_refused()
    {
        var result = await CliRun.RunAsync("build", "--profile", WriteProfile(), "--json");

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("--json works only with --dry-run", result.Error);
    }

    static string? Type(JsonElement message) => message.GetProperty("type").GetString();

    static string PipeName() => "shapeshift-test-" + Guid.NewGuid().ToString("N");

    async Task<(List<JsonElement> Messages, int ExitCode)> BuildOverPipe(string profile, bool cancelAtConnect = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = PipeName();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var build = CliRun.RunAsync("build", "--profile", profile, "--workspace", _workspace, "--pipe", name);

        await server.WaitForConnectionAsync(ct);
        if (cancelAtConnect)
        {
            var cancel = Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}\n");
            await server.WriteAsync(cancel, ct);
            await server.FlushAsync(ct);
        }

        var messages = new List<JsonElement>();
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var message = JsonDocument.Parse(line).RootElement.Clone();
            messages.Add(message);
            if (Type(message) == "exit")
                break;
        }

        var result = await build;
        Assert.Equal(result.ExitCode, messages[^1].GetProperty("code").GetInt32());
        return (messages, result.ExitCode);
    }

    string WriteProfile() => _dir.Write("profile.json", JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        tweaks = new[] { "bypass.tpm" },
        source = new { iso = _source },
        output = new { iso = _output },
    }));
}
