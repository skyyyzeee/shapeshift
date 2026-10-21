using System.Text.Json;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>
/// Встроенные приложения в образе из UUP: выбираются по CompDB редакции со всеми частями и фреймворками, ставятся
/// DISM при сборке; приложения из removeApps не добавляются. Сама установка — в стенде (права администратора).
/// </summary>
public sealed class UupAppsTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly FakeUupRelease _release;

    public UupAppsTests() => _release = new FakeUupRelease(_dir.Path);

    public void Dispose() => _dir.Dispose();

    string Workspace => _dir.File("ws");

    string Output => _dir.File("out.iso");

    void Profile(string removeApps = "[]") => _dir.Write("profile.json", $$"""
        {
          "formatVersion": 1,
          "latestUpdate": false,
          "editions": ["Professional"],
          "removeApps": {{removeApps}},
          "source": { "uup": { "channel": "windows11", "language": "ru-RU" } },
          "output": { "iso": {{JsonSerializer.Serialize(Output)}} }
        }
        """);

    Task<CliResult> Build(FakeWindowsUpdate wu, params string[] extra) =>
        CliRun.RunAsync(wu.Host(Workspace), ["build", "--profile", _dir.File("profile.json"), "--workspace", Workspace, .. extra]);

    static readonly string[] BingNewsFiles =
    [
        "Microsoft.BingNews_neutral.msixbundle",
        "Microsoft.BingNews_split.language-ru.appx",
        "Microsoft.BingNews_split.language-de.appx",
        "Microsoft.VCLibs.140.00_x64.appx",
    ];

    [Fact]
    public async Task Image_gets_the_apps_of_its_edition_with_their_frameworks()
    {
        Profile();
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await Build(wu, "--download-only");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Assert.True(result.Output.Contains("Apps: 1 (Microsoft.BingNews)"), result.Output + result.Error);
        Assert.Contains("Add built-in apps", result.Output);
        // Все части бандла (ресурсы всех языков) и фреймворк; приложение, которого нет в редакции (Paint), — нет.
        var downloaded = wu.Downloads.Select(d => d.File).ToHashSet();
        Assert.Superset(BingNewsFiles.ToHashSet(), downloaded);
        Assert.DoesNotContain("Microsoft.Paint_neutral.msixbundle", downloaded);
    }

    [Fact]
    public async Task Removed_apps_are_not_added_and_their_frameworks_are_not_downloaded()
    {
        Profile("""["Microsoft.BingNews"]""");
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await Build(wu, "--download-only");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Assert.DoesNotContain("Add built-in apps", result.Output);
        Assert.DoesNotContain(wu.Downloads, d => BingNewsFiles.Contains(d.File));
    }

    [Fact]
    public async Task Unknown_app_in_remove_apps_is_a_warning()
    {
        Profile("""["Contoso.NotThere"]""");

        var result = await Build(new FakeWindowsUpdate { Release = _release }, "--dry-run");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("warning: Contoso.NotThere is not among the apps of this release", result.Error);
    }

    [Fact]
    public async Task Apps_list_of_a_release_is_what_a_build_adds()
    {
        var wu = new FakeWindowsUpdate { Release = _release };
        var releases = await CliRun.RunAsync(wu.Host(Workspace), ["uup", "releases", "--workspace", Workspace]);
        Assert.True(releases.ExitCode == Cli.ExitCodes.Success, releases.Error);

        var result = await CliRun.RunAsync(wu.Host(Workspace),
            ["apps", "list", "--update", FakeUupRelease.UpdateId, "--lang", "ru-RU", "--edition", "Professional", "--workspace", Workspace, "--json"]);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Output + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        // Фреймворки (VCLibs) — не приложения: в списке для удаления их нет.
        Assert.Equal(["Microsoft.BingNews"], json.RootElement.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Downloaded_in_advance_builds_without_the_internet()
    {
        Profile();
        var download = await Build(new FakeWindowsUpdate { Release = _release }, "--download-only");
        Assert.True(download.ExitCode == Cli.ExitCodes.Success, download.Error);

        var result = await Build(new FakeWindowsUpdate { Offline = true }, "--dry-run");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Assert.Contains($"using release {FakeUupRelease.Build} from the cache", result.Error);
        Assert.Contains("Download files      0.0 MB", result.Output);
    }

    [Fact]
    public async Task Adding_apps_needs_administrator_rights()
    {
        Assert.SkipWhen(Servicing.DismServicing.IsElevated, "Runs only without administrator rights.");
        Profile();

        var result = await Build(new FakeWindowsUpdate { Release = _release });

        Assert.Equal(Cli.ExitCodes.NeedsElevation, result.ExitCode);
        Assert.False(File.Exists(Output));
    }
}