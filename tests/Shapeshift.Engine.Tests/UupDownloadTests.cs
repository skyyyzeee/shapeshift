using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Скачивание выпуска UUP: какие файлы нужны, кэш, докачка, проверка хешей, истёкшие ссылки.</summary>
public sealed class UupDownloadTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly FakeUupRelease _release;

    public UupDownloadTests() => _release = new FakeUupRelease(_dir.Path);

    public void Dispose() => _dir.Dispose();

    string Workspace => _dir.File("ws");

    async Task<FakeWindowsUpdate> Listed(FakeWindowsUpdate? wu = null)
    {
        wu ??= new FakeWindowsUpdate { Release = _release };
        var releases = await CliRun.RunAsync(wu.Host(Workspace), "uup", "releases");
        Assert.True(releases.ExitCode == Cli.ExitCodes.Success, releases.Error);
        return wu;
    }

    static string[] Select(string command, params string[] editions) =>
        ["uup", command, "--update", FakeUupRelease.UpdateId, "--lang", "ru-RU", .. editions.SelectMany(e => new[] { "--edition", e })];

    string CachedPath(string name) => Path.Combine(Workspace, "cache", "uup", "files", FakeUupRelease.Sha1(_release.Files[name]));

    [Fact]
    public async Task Picks_the_files_of_the_language_and_edition()
    {
        var wu = await Listed();

        var result = await CliRun.RunAsync(wu.Host(Workspace), [.. Select("files", "Professional"), "--list"]);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        // Имена — с отступом в два пробела; строки прогресса скачивания метаданных — с большим.
        var listed = result.Output.Split('\n').Where(l => l.StartsWith("  ") && !l.StartsWith("   ")).Select(l => l.Trim()).ToList();
        Assert.Equal(FakeUupRelease.ProfessionalRussian.Order(StringComparer.OrdinalIgnoreCase), listed.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Several_editions_share_their_common_files()
    {
        var wu = await Listed();

        var result = await CliRun.RunAsync(wu.Host(Workspace), [.. Select("files", "Professional", "Core"), "--json"]);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Output);
        using var json = JsonDocument.Parse(result.Output);
        // Pro + Core: образ и пакет редакции Core добавляются, общее не повторяется.
        Assert.Equal(FakeUupRelease.ProfessionalRussian.Length + 2, json.RootElement.GetProperty("files").GetInt32());
        var expected = FakeUupRelease.ProfessionalRussian.Concat(["core_ru-ru.esd", "Microsoft-Windows-EditionSpecific-Core-Package.ESD"]).Sum(n => (long)_release.Files[n].Length);
        Assert.Equal(expected, json.RootElement.GetProperty("bytes").GetInt64());
        // Метаданные скачаны, чтобы составить список, — они уже в кэше.
        Assert.Equal(_release.Files[$"{FakeUupRelease.UpdateId}.AggregatedMetadata.cab"].Length, json.RootElement.GetProperty("cachedBytes").GetInt64());
    }

    [Theory]
    [InlineData("de-DE", "Professional", "language 'de-DE' is not in this release; available: en-US, ru-RU.")]
    [InlineData("en-US", "Core", "edition 'Core' is not available in en-US; available: Professional.")]
    public async Task Unavailable_language_or_edition_is_rejected(string language, string edition, string expected)
    {
        var wu = await Listed();

        var result = await CliRun.RunAsync(wu.Host(Workspace), "uup", "files", "--update", FakeUupRelease.UpdateId, "--lang", language, "--edition", edition);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task Downloads_verified_files_into_the_cache_once()
    {
        var wu = await Listed();

        var first = await CliRun.RunAsync(wu.Host(Workspace), Select("download", "Professional"));
        var downloadsAfterFirst = wu.Downloads.Count;
        var second = await CliRun.RunAsync(wu.Host(Workspace), Select("download", "Professional"));

        Assert.True(first.ExitCode == Cli.ExitCodes.Success, first.Error);
        Assert.True(second.ExitCode == Cli.ExitCodes.Success, second.Error);
        foreach (var name in FakeUupRelease.ProfessionalRussian)
            Assert.Equal(_release.Files[name], File.ReadAllBytes(CachedPath(name)));
        Assert.Equal(FakeUupRelease.ProfessionalRussian.Length, downloadsAfterFirst);
        Assert.Equal(downloadsAfterFirst, wu.Downloads.Count); // второй раз — всё из кэша
        Assert.Contains("100%", first.Output);
    }

    [Fact]
    public async Task Resumes_after_the_connection_drops()
    {
        var wu = await Listed(new FakeWindowsUpdate { Release = _release, DropOnceAfter = { ["professional_ru-ru.esd"] = 1 << 20 } });

        var result = await CliRun.RunAsync(wu.Host(Workspace), Select("download", "Professional"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(_release.Files["professional_ru-ru.esd"], File.ReadAllBytes(CachedPath("professional_ru-ru.esd")));
        var requests = wu.Downloads.Where(d => d.File == "professional_ru-ru.esd").Select(d => d.From).ToList();
        Assert.Equal([null, 1 << 20], requests);
    }

    [Fact]
    public async Task A_silent_server_is_reconnected_and_the_file_resumed()
    {
        var wu = await Listed(new FakeWindowsUpdate { Release = _release, StallOnceAfter = { ["professional_ru-ru.esd"] = 1 << 20 } });
        var host = new EngineHost { HttpHandler = () => wu, DefaultWorkspace = Workspace, DownloadStallTimeout = TimeSpan.FromMilliseconds(500) };

        var result = await CliRun.RunAsync(host, Select("download", "Professional"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(_release.Files["professional_ru-ru.esd"], File.ReadAllBytes(CachedPath("professional_ru-ru.esd")));
        Assert.Equal([null, 1 << 20], wu.Downloads.Where(d => d.File == "professional_ru-ru.esd").Select(d => d.From));
    }

    [Fact]
    public async Task Continues_a_download_left_from_a_previous_run()
    {
        var wu = await Listed();
        var name = "professional_ru-ru.esd";
        Directory.CreateDirectory(Path.GetDirectoryName(CachedPath(name))!);
        File.WriteAllBytes(CachedPath(name) + ".part", _release.Files[name][..12345]);

        var result = await CliRun.RunAsync(wu.Host(Workspace), Select("download", "Professional"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(_release.Files[name], File.ReadAllBytes(CachedPath(name)));
        Assert.Equal(12345, wu.Downloads.Single(d => d.File == name).From);
        Assert.False(File.Exists(CachedPath(name) + ".part"));
    }

    [Fact]
    public async Task A_server_without_resume_sends_the_whole_file_again()
    {
        var wu = await Listed(new FakeWindowsUpdate { Release = _release, IgnoresRange = true });
        var name = "professional_ru-ru.esd";
        Directory.CreateDirectory(Path.GetDirectoryName(CachedPath(name))!);
        File.WriteAllBytes(CachedPath(name) + ".part", _release.Files[name][..12345]);

        var result = await CliRun.RunAsync(wu.Host(Workspace), Select("download", "Professional"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(_release.Files[name], File.ReadAllBytes(CachedPath(name)));
    }

    [Fact]
    public async Task A_damaged_file_is_downloaded_again()
    {
        var wu = await Listed(new FakeWindowsUpdate { Release = _release, CorruptOnce = { "Windows11.0-KB5000001-x64.msu" } });

        var result = await CliRun.RunAsync(wu.Host(Workspace), Select("download", "Professional"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(_release.Files["Windows11.0-KB5000001-x64.msu"], File.ReadAllBytes(CachedPath("Windows11.0-KB5000001-x64.msu")));
        Assert.Equal(2, wu.Downloads.Count(d => d.File == "Windows11.0-KB5000001-x64.msu"));
    }

    [Fact]
    public async Task Expired_links_are_requested_again()
    {
        var wu = await Listed(new FakeWindowsUpdate { Release = _release, FirstLinksExpired = true });

        var result = await CliRun.RunAsync(wu.Host(Workspace), Select("download", "Professional"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(2, wu.LinkRequests); // первая выдача истекла — вторая, одна на все файлы
    }

    [Fact]
    public async Task Not_enough_space_stops_before_downloading()
    {
        var wu = await Listed();
        var host = new EngineHost { HttpHandler = () => wu, DefaultWorkspace = Workspace, AvailableFreeSpace = _ => 1 << 20 };

        var result = await CliRun.RunAsync(host, Select("download", "Professional"));

        Assert.Equal(Cli.ExitCodes.Failure, result.ExitCode);
        Assert.Contains("not enough space", result.Error);
    }

    [Fact]
    public async Task Reports_bytes_speed_and_time_left_over_the_pipe_and_can_be_cancelled()
    {
        var hold = new SemaphoreSlim(0);
        var wu = await Listed(new FakeWindowsUpdate { Release = _release });
        // Метаданные — заранее, чтобы задержать именно файлы.
        Assert.Equal(Cli.ExitCodes.Success, (await CliRun.RunAsync(wu.Host(Workspace), Select("files", "Professional"))).ExitCode);
        var held = new FakeWindowsUpdate { Release = _release, HoldDownloads = hold };
        await Listed(held);
        var ct = TestContext.Current.CancellationToken;
        var name = "sst-" + Guid.NewGuid().ToString("N");

        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var download = CliRun.RunAsync(held.Host(Workspace), [.. Select("download", "Professional"), "--pipe", name]);
        await server.WaitForConnectionAsync(ct);

        var messages = new List<JsonElement>();
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var message = JsonDocument.Parse(line).RootElement.Clone();
            messages.Add(message);
            var type = message.GetProperty("type").GetString();
            if (type == "step" && message.GetProperty("kind").GetString() == "DownloadFiles")
            {
                await server.WriteAsync(Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}\n"), ct);
                await server.FlushAsync(ct);
                hold.Release();
            }
            if (type == "exit")
                break;
        }

        var result = await download;
        Assert.Equal(Cli.ExitCodes.Cancelled, result.ExitCode);
        var transfer = messages.First(m => m.GetProperty("type").GetString() == "transfer");
        Assert.True(transfer.GetProperty("total").GetInt64() > 0);
        Assert.True(transfer.TryGetProperty("bytesPerSecond", out _));
        Assert.True(transfer.TryGetProperty("secondsLeft", out _));
        Assert.Contains(messages, m => m.GetProperty("type").GetString() == "cancelled");
    }
}
