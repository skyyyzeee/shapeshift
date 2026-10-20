using System.Text.Json;
using Shapeshift.Engine.Servicing;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Встроенные приложения: список из образа без прав администратора, разметка, шаг удаления в плане.</summary>
public sealed class AppsTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    static readonly FakeWindowsIso WithApps = new()
    {
        WindowsApps =
        [
            ("Microsoft.BingNews_4.11.6001.0_neutral_~_8wekyb3d8bbwe", false),
            ("Microsoft.BingNews_1.0.2.0_x64__8wekyb3d8bbwe", false),
            ("Microsoft.BingNews_1.0.2.0_neutral_split.scale-100_8wekyb3d8bbwe", false),
            ("Microsoft.SecHealthUI_1000.26100.9168.0_x64__8wekyb3d8bbwe", false),
            ("Contoso.Unknown_1.0.0.0_x64__abcdefghijklm", false),
            ("Microsoft.UI.Xaml.2.8_8.2511.26001.0_x64__8wekyb3d8bbwe", true),
            ("Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe", true),
        ],
    };

    [Fact]
    public async Task Lists_apps_without_frameworks_or_resource_packages()
    {
        var iso = WithApps.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("apps", "list", "--iso", iso, "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Snapshot.Match(result.Output, "json");
    }

    [Fact]
    public async Task Lists_the_requested_edition()
    {
        var iso = WithApps.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("apps", "list", "--iso", iso, "--edition", "Professional", "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("Professional", json.RootElement.GetProperty("edition").GetProperty("editionId").GetString());
    }

    [Fact]
    public async Task Unknown_edition_is_rejected()
    {
        var iso = WithApps.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("apps", "list", "--iso", iso, "--edition", "Enterprise");

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("'Enterprise' is not in the image", result.Error);
    }

    [Fact]
    public async Task Removing_apps_adds_a_step_and_needs_administrator_rights()
    {
        Assert.SkipWhen(DismServicing.IsElevated, "Runs only without administrator rights.");
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        var profile = _dir.Write("profile.json", JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            removeApps = new[] { "Microsoft.BingNews" },
            source = new { iso },
            output = new { iso = _dir.File("out.iso") },
        }));
        string[] build = ["build", "--profile", profile, "--workspace", _dir.File("ws")];

        var dryRun = await CliRun.RunAsync([.. build, "--dry-run"]);
        var real = await CliRun.RunAsync(build);

        Assert.True(dryRun.ExitCode == Cli.ExitCodes.Success, dryRun.Error);
        Assert.Contains("Remove built-in apps", dryRun.Output);
        Assert.Equal(Cli.ExitCodes.NeedsElevation, real.ExitCode);
        Assert.Contains("needs administrator rights", real.Error);
        Assert.False(File.Exists(_dir.File("out.iso")));
    }

    [Theory]
    [InlineData("""{ "removeApps": ["A.B", "a.b"] }""", "same name twice")]
    [InlineData("""{ "removeApps": [""] }""", "empty name")]
    public async Task Invalid_remove_apps_are_rejected(string body, string expectedError)
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
        json["formatVersion"] = JsonSerializer.SerializeToElement(1);
        var profile = _dir.Write("profile.json", JsonSerializer.Serialize(json));

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    public static TheoryData<string, string> InvalidMarkup => new()
    {
        { """{ "name": "A.B", "title": { "en": "A" }, "safety": "safe" }""", "title has no 'ru' translation" },
        { """{ "name": "A.B", "title": { "en": "A", "ru": "А" }, "safety": "risky", "recommended": true }""", "risky app cannot be recommended" },
        { """{ "name": "A B", "title": { "en": "A", "ru": "А" }, "safety": "safe" }""", "package name without version" },
        { """{ "name": "A.B", "title": { "en": "A", "ru": "А" }, "safety": "maybe" }""", "unknown safety 'maybe'" },
    };

    [Theory]
    [MemberData(nameof(InvalidMarkup))]
    public async Task Invalid_app_markup_is_rejected(string app, string expectedError)
    {
        var catalog = _dir.Write("catalog.json", $$"""{ "formatVersion": 1, "categories": [], "tweaks": [], "apps": [{{app}}] }""");

        var result = await CliRun.RunAsync("catalog", "validate", catalog);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }
}
