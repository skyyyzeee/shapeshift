using System.Text.Json;
using ManagedWimLib;
using Shapeshift.Engine.Images;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Сборка из выпуска UUP: скачивание нужного, носитель из образов редакций и пакетов (wimlib), дальше — как с ISO.</summary>
public sealed class UupBuildTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly FakeUupRelease _release;

    public UupBuildTests() => _release = new FakeUupRelease(_dir.Path);

    public void Dispose() => _dir.Dispose();

    string Workspace => _dir.File("ws");

    string Output => _dir.File("out.iso");

    // Без встраивания обновлений по умолчанию: оно требует DISM и прав администратора (отдельные тесты ниже).
    string Profile(string editions = "[\"Professional\"]", string uup = """{ "channel": "windows11", "language": "ru-RU" }""", bool latestUpdate = false) => _dir.Write("profile.json", $$"""
        {
          "formatVersion": 1,
          "latestUpdate": {{(latestUpdate ? "true" : "false")}},
          "editions": {{editions}},
          "setup": { "skipOobe": true },
          "source": { "uup": {{uup}} },
          "output": { "iso": {{JsonSerializer.Serialize(Output)}} }
        }
        """);

    Task<CliResult> Build(FakeWindowsUpdate wu, params string[] extra) =>
        CliRun.RunAsync(wu.Host(Workspace), ["build", "--profile", _dir.File("profile.json"), "--workspace", Workspace, .. extra]);

    // Файл из образа WIM внутри ISO.
    string ReadFromImage(string wimInIso, int image, string path)
    {
        var wim = _dir.File(Path.GetFileName(wimInIso) + "-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllBytes(wim, IsoContents.ReadFile(Output, wimInIso));
        var target = _dir.File("x-" + Guid.NewGuid().ToString("N")[..6]);
        WimLib.EnsureInitialized();
        using var w = Wim.OpenWim(wim, OpenFlags.None);
        w.ExtractPaths(image, target, [path], ExtractFlags.NoAcls | ExtractFlags.NoPreserveDirStructure);
        return File.ReadAllText(Path.Combine(target, Path.GetFileName(path)));
    }

    (int Images, int BootIndex, IReadOnlyList<string> EditionIds) Describe(string wimInIso)
    {
        var wim = _dir.File("d-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllBytes(wim, IsoContents.ReadFile(Output, wimInIso));
        WimLib.EnsureInitialized();
        using var w = Wim.OpenWim(wim, OpenFlags.None);
        var info = w.GetWimInfo();
        var ids = Enumerable.Range(1, (int)info.ImageCount).Select(i => w.GetImageProperty(i, "WINDOWS/EDITIONID") ?? "").ToList();
        return ((int)info.ImageCount, (int)info.BootIndex, ids);
    }

    [Fact]
    public async Task Builds_installation_media_from_the_release()
    {
        Profile();

        var result = await Build(new FakeWindowsUpdate { Release = _release });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        var files = IsoContents.Files(Output);
        Assert.Contains("setup.exe", files);
        Assert.Contains(@"sources\install.wim", files);
        Assert.Contains(@"sources\boot.wim", files);
        Assert.Contains("autounattend.xml", files);
        Assert.Equal(["Professional"], Describe(@"sources\install.wim").EditionIds);
        // Данные Windows собраны из пакетов: из WIM и из CAB.
        foreach (var (path, content) in FakeUupRelease.WindowsFiles)
            Assert.Equal(content, ReadFromImage(@"sources\install.wim", 1, path));
        Assert.Equal("professional", ReadFromImage(@"sources\install.wim", 1, @"Windows\edition.txt"));
    }

    [Fact]
    public async Task Boot_image_starts_setup_and_windows_has_the_recovery_environment()
    {
        Profile();

        var result = await Build(new FakeWindowsUpdate { Release = _release });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        var boot = Describe(@"sources\boot.wim");
        Assert.Equal(2, boot.Images);
        Assert.Equal(2, boot.BootIndex);
        Assert.Equal("setup", ReadFromImage(@"sources\boot.wim", 2, "setup.exe"));
        Assert.Equal("setup engine", ReadFromImage(@"sources\boot.wim", 2, @"sources\setup.dll"));
        Assert.Throws<WimLibException>(() => ReadFromImage(@"sources\boot.wim", 2, @"Windows\System32\winpeshl.ini"));
        Assert.Equal("recovery", ReadFromImage(@"sources\boot.wim", 2, @"Windows\System32\winre.txt"));
        Assert.Contains("MSWIM", ReadFromImage(@"sources\install.wim", 1, @"Windows\System32\Recovery\Winre.wim"));
    }

    [Fact]
    public async Task Several_editions_go_into_one_image()
    {
        Profile(editions: "[\"Professional\", \"Core\"]");

        var result = await Build(new FakeWindowsUpdate { Release = _release });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Assert.Equal(["Professional", "Core"], Describe(@"sources\install.wim").EditionIds);
        Assert.Equal("core", ReadFromImage(@"sources\install.wim", 2, @"Windows\edition.txt"));
    }

    [Fact]
    public async Task Downloads_only_what_the_media_needs()
    {
        Profile();
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await Build(wu);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        var downloaded = wu.Downloads.Select(d => d.File).ToList();
        Assert.Contains("professional_ru-ru.esd", downloaded);
        Assert.Contains("Microsoft-Windows-Fake-FOD-Package~31bf3856ad364e35~amd64~~.cab", downloaded);
        // Приложения и обновления ставятся при обслуживании — для носителя не нужны.
        Assert.DoesNotContain(downloaded, f => f.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msu", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dry_run_plans_download_and_assembly_of_the_base_build()
    {
        Profile();

        var result = await Build(new FakeWindowsUpdate { Release = _release }, "--dry-run", "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Output);
        using var plan = JsonDocument.Parse(result.Output);
        var steps = plan.RootElement.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["DownloadFiles", "AssembleImage", "WriteAnswerFile", "PackIso"], steps);
        // Образ — исходная сборка (из CompDB), обновления не встроены.
        Assert.Equal(26100, plan.RootElement.GetProperty("image").GetProperty("release").GetProperty("build").GetInt32());
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task Latest_update_is_integrated_by_default_in_order()
    {
        _dir.Write("profile.json", $$"""
            { "formatVersion": 1, "source": { "uup": { "channel": "windows11", "language": "ru-RU" } }, "output": { "iso": {{JsonSerializer.Serialize(Output)}} } }
            """);

        var result = await Build(new FakeWindowsUpdate { Release = _release }, "--dry-run");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        // Накопительное, пакет включения, .NET; обновление среды восстановления — не для Windows.
        Assert.Contains("Updates: Windows11.0-KB5000001-x64.msu, Windows11.0-KB5000002-x64.cab, Windows11.0-KB5000004-x64-NDP481.cab", result.Output);
        Assert.Contains("Integrate updates", result.Output);
        // С обновлениями образ — сборка выпуска, а не исходная.
        Assert.Contains($"Image: Windows 11 25H2 ({FakeUupRelease.Build})", result.Output);
    }

    [Fact]
    public async Task Integrating_updates_needs_administrator_rights()
    {
        Assert.SkipWhen(Servicing.DismServicing.IsElevated, "Runs only without administrator rights.");
        Profile(latestUpdate: true);

        var result = await Build(new FakeWindowsUpdate { Release = _release });

        Assert.Equal(Cli.ExitCodes.NeedsElevation, result.ExitCode);
        Assert.Contains("integrating updates", result.Error);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task Without_latest_update_the_image_stays_at_the_original_build()
    {
        Profile(latestUpdate: false);

        var result = await Build(new FakeWindowsUpdate { Release = _release }, "--dry-run");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.DoesNotContain("Integrate updates", result.Output);
        Assert.Contains("Image: Windows 11 24H2 (26100.1)", result.Output);
    }

    [Fact]
    public async Task Latest_update_for_own_iso_is_not_supported_yet()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        _dir.Write("profile.json", $$"""
            { "formatVersion": 1, "latestUpdate": true, "source": { "iso": {{JsonSerializer.Serialize(iso)}} }, "output": { "iso": {{JsonSerializer.Serialize(Output)}} } }
            """);

        var result = await CliRun.RunAsync("build", "--profile", _dir.File("profile.json"), "--workspace", Workspace, "--dry-run");

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("works only with a release from Windows Update", result.Error);
    }

    [Theory]
    [InlineData("""{ "channel": "nightly", "language": "ru-RU" }""", "unknown channel 'nightly'")]
    [InlineData("""{ "channel": "windows11", "language": "de-DE" }""", "language 'de-DE' is not in this release")]
    public async Task Bad_uup_source_is_rejected(string uup, string expected)
    {
        Profile(uup: uup);

        var result = await Build(new FakeWindowsUpdate { Release = _release });

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task Without_internet_suggests_own_iso()
    {
        Profile();

        var result = await Build(new FakeWindowsUpdate { Offline = true });

        Assert.Equal(Cli.ExitCodes.Failure, result.ExitCode);
        Assert.Contains("use your own ISO", result.Error);
    }

    [Fact]
    public async Task Profile_needs_either_iso_or_uup()
    {
        _dir.Write("profile.json", """{ "formatVersion": 1, "source": { "iso": "a.iso", "uup": { "channel": "windows11", "language": "ru-RU" } } }""");

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("either 'iso' or 'uup'", result.Error);
    }
}
