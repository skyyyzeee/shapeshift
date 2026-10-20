using System.Net;
using System.Text.Json;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Выпуски Windows прямо из Windows Update: каналы, языки и редакции, сетевые сбои.</summary>
public sealed class UupTests : IDisposable
{
    const string Windows11Latest = "f4ad9cf8-3ef1-40e7-8a1d-f3034b1ded62";

    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    Task<CliResult> Run(FakeWindowsUpdate wu, params string[] args) => CliRun.RunAsync(wu.Host(_dir.Path), args);

    [Fact]
    public async Task Lists_current_releases_by_channel()
    {
        var result = await Run(new FakeWindowsUpdate(), "uup", "releases", "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Snapshot.Match(result.Output, "json");
    }

    [Fact]
    public async Task Insider_channels_only_in_advanced_mode()
    {
        var wu = new FakeWindowsUpdate();

        var normal = await Run(wu, "uup", "releases", "--json");
        var advanced = await Run(wu, "uup", "releases", "--advanced", "--json");

        Assert.DoesNotContain("canary", normal.Output);
        using var json = JsonDocument.Parse(advanced.Output);
        var insider = json.RootElement.GetProperty("releases").EnumerateArray().Where(r => r.GetProperty("advanced").GetBoolean())
            .Select(r => r.GetProperty("channel").GetString()).ToList();
        Assert.Equal(["beta", "dev", "canary"], insider);
    }

    [Fact]
    public async Task Asks_windows_update_directly_as_an_x64_device()
    {
        var wu = new FakeWindowsUpdate();

        await Run(wu, "uup", "releases");

        Assert.All(wu.Requests, r => Assert.Equal("https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx", r.Uri.ToString()));
        Assert.All(wu.Requests, r => Assert.StartsWith("Windows-Update-Agent/", r.UserAgent));
        var sync = wu.Requests.Select(r => WebUtility.HtmlDecode(r.Body)).Where(b => b.Contains("<SyncUpdates")).ToList();
        Assert.NotEmpty(sync);
        Assert.All(sync, b => Assert.Contains("OSArchitecture=amd64", b));
        // Обычные каналы — как розничное устройство (Windows 11 — двумя запросами), Release Preview — как участник программы.
        Assert.Equal(4, sync.Count(b => b.Contains("FlightRing=Retail&")));
        Assert.Single(sync, b => b.Contains("FlightingBranchName=ReleasePreview&"));
    }

    [Fact]
    public async Task Release_lists_languages_and_ready_editions()
    {
        await Run(new FakeWindowsUpdate(), "uup", "releases");

        var result = await Run(new FakeWindowsUpdate(), "uup", "release", "--update", Windows11Latest, "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("Windows 11, version 25H2", json.RootElement.GetProperty("title").GetString());
        var languages = json.RootElement.GetProperty("languages").EnumerateArray().ToList();
        Assert.Equal(38, languages.Count);
        var russian = languages.Single(l => l.GetProperty("code").GetString() == "ru-RU");
        Assert.Equal(["Core", "Professional"], russian.GetProperty("editions").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(languages, l => l.GetProperty("code").GetString() == "sr-Latn-RS");
    }

    [Fact]
    public async Task Release_must_be_listed_first()
    {
        var result = await Run(new FakeWindowsUpdate(), "uup", "release", "--update", Windows11Latest);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("run 'uup releases' first", result.Error);
    }

    [Fact]
    public async Task Without_internet_suggests_own_iso()
    {
        var result = await Run(new FakeWindowsUpdate { Offline = true }, "uup", "releases", "--json");

        Assert.Equal(Cli.ExitCodes.Failure, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("network", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        var message = json.RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("cannot reach Windows Update", message);
        Assert.Contains("use your own ISO", message);
    }

    [Fact]
    public async Task A_failing_channel_does_not_hide_the_others()
    {
        var result = await Run(new FakeWindowsUpdate { FailingRings = ["CanaryChannel"] }, "uup", "releases", "--advanced", "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.DoesNotContain(json.RootElement.GetProperty("releases").EnumerateArray(), r => r.GetProperty("channel").GetString() == "canary");
        var problem = json.RootElement.GetProperty("problems").EnumerateArray().Single();
        Assert.Equal("canary", problem.GetProperty("channel").GetString());
        Assert.Contains("500", problem.GetProperty("message").GetString());
    }

    // Живой Windows Update: SHAPESHIFT_TEST_LIVE=1. Ответы службы меняются — проверяем только суть.
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_windows_update_offers_windows_11_with_russian()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SHAPESHIFT_TEST_LIVE") == "1", "SHAPESHIFT_TEST_LIVE is not set.");
        var host = new EngineHost { DefaultWorkspace = _dir.Path };

        var releases = await CliRun.RunAsync(host, "uup", "releases", "--json");

        Assert.True(releases.ExitCode == Cli.ExitCodes.Success, releases.Error + releases.Output);
        using var json = JsonDocument.Parse(releases.Output);
        var windows11 = json.RootElement.GetProperty("releases").EnumerateArray().First(r => r.GetProperty("channel").GetString() == "windows11");
        Assert.True(int.Parse(windows11.GetProperty("build").GetString()!.Split('.')[0]) >= 26100);

        var release = await CliRun.RunAsync(host, "uup", "release", "--update", windows11.GetProperty("updateId").GetString()!, "--json");
        Assert.Contains("\"ru-RU\"", release.Output);
    }
}
