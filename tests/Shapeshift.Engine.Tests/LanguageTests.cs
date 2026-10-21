using System.Text.Json;
using System.Xml.Linq;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>
/// Языки интерфейса: языковые пакеты и возможности языка из выпуска Windows Update (для UUP — из того же выпуска,
/// для своего ISO — из выпуска той же базовой сборки), шаг DISM до обновлений, язык по умолчанию в файле ответов.
/// Сама установка пакетов DISM — в стенде (нужны права администратора и настоящий выпуск).
/// </summary>
public sealed class LanguageTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly FakeUupRelease _release;

    public LanguageTests() => _release = new FakeUupRelease(_dir.Path);

    public void Dispose() => _dir.Dispose();

    string Workspace => _dir.File("ws");

    string Output => _dir.File("out.iso");

    static XNamespace Unattend => "urn:schemas-microsoft-com:unattend";

    void UupProfile(string languages, bool latestUpdate = false) => _dir.Write("profile.json", $$"""
        {
          "formatVersion": 1,
          "latestUpdate": {{(latestUpdate ? "true" : "false")}},
          "editions": ["Professional"],
          "languages": {{languages}},
          "source": { "uup": { "channel": "windows11", "language": "ru-RU" } },
          "output": { "iso": {{JsonSerializer.Serialize(Output)}} }
        }
        """);

    void IsoProfile(string iso, string languages, string extra = "") => _dir.Write("profile.json", $$"""
        {
          "formatVersion": 1,
          {{extra}}
          "languages": {{languages}},
          "source": { "iso": {{JsonSerializer.Serialize(iso)}} },
          "output": { "iso": {{JsonSerializer.Serialize(Output)}} }
        }
        """);

    Task<CliResult> DryRun(FakeWindowsUpdate wu) =>
        CliRun.RunAsync(wu.Host(Workspace), ["build", "--profile", _dir.File("profile.json"), "--workspace", Workspace, "--dry-run"]);

    static IReadOnlyList<string> Steps(string output) => output.Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 3 && char.IsDigit(l[0]) && l.Contains(". "))
        .Select(l => l[(l.IndexOf(". ", StringComparison.Ordinal) + 2)..].Split("  ")[0].Trim())
        .ToList();

    [Fact]
    public async Task Languages_come_from_the_same_release_and_go_in_before_updates()
    {
        UupProfile("""{ "add": ["de-DE"], "display": "de-DE" }""", latestUpdate: true);
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await DryRun(wu);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        // Языковой пакет и возможности языка: базовые раньше рукописного ввода, который от них зависит.
        Assert.Contains("Languages: de-DE (3 packages)", result.Output);
        Assert.Equal(["Download files", "Assemble image", "Add languages", "Integrate updates", "Write answer file", "Pack ISO"], Steps(result.Output));
    }

    [Fact]
    public async Task Download_includes_the_language_files()
    {
        var wu = new FakeWindowsUpdate { Release = _release };
        async Task<long> DownloadBytes(string languages)
        {
            UupProfile(languages);
            var plan = await CliRun.RunAsync(wu.Host(Workspace), ["build", "--profile", _dir.File("profile.json"), "--workspace", Workspace, "--dry-run", "--json"]);
            Assert.True(plan.ExitCode == Cli.ExitCodes.Success, plan.Output + plan.Error);
            using var json = JsonDocument.Parse(plan.Output);
            return json.RootElement.GetProperty("steps").EnumerateArray()
                .First(s => s.GetProperty("kind").GetString() == "DownloadFiles").GetProperty("bytes").GetInt64();
        }

        var withoutLanguages = await DownloadBytes("{ }");
        var withGerman = await DownloadBytes("""{ "add": ["de-DE"] }""");

        // Плюс языковой пакет de-DE и две возможности языка.
        var languageBytes = new[] { "Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~.esd" }
            .Concat(FakeUupRelease.LanguageFeatures("de-DE")).Sum(f => (long)_release.Files[f].Length);
        Assert.Equal(withoutLanguages + languageBytes, withGerman);
    }

    [Fact]
    public async Task Download_only_fetches_the_language_files_without_building()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        IsoProfile(iso, """{ "add": ["de-DE"] }""", "\"latestUpdate\": false,");
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await CliRun.RunAsync(wu.Host(Workspace), ["build", "--profile", _dir.File("profile.json"), "--workspace", Workspace, "--download-only"]);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        var downloaded = wu.Downloads.Select(d => d.File).ToHashSet();
        Assert.Contains("Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~.esd", downloaded);
        foreach (var feature in FakeUupRelease.LanguageFeatures("de-DE"))
            Assert.Contains(feature, downloaded);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task Language_without_a_pack_in_the_release_is_rejected_before_downloading()
    {
        UupProfile("""{ "add": ["fr-FR"] }""");
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await DryRun(wu);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("there is no language pack for fr-FR in this release", result.Error);
        Assert.Contains("languages of this release: de-DE, en-US, ru-RU", result.Error);
        Assert.DoesNotContain(wu.Downloads, d => d.File.Contains("LanguagePack", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Language_without_features_is_a_clear_error()
    {
        // У en-US в подставном выпуске есть языковой пакет, но нет возможностей языка.
        UupProfile("""{ "add": ["en-US"] }""");

        var result = await DryRun(new FakeWindowsUpdate { Release = _release });

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("there are no language features (handwriting, OCR, speech) for en-US", result.Error);
    }

    [Fact]
    public async Task Display_language_must_end_up_in_the_image()
    {
        UupProfile("""{ "add": ["de-DE"], "display": "en-US" }""");
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await DryRun(wu);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("'languages.display' is en-US, but the image has only ru-RU, de-DE; add it to 'languages.add'", result.Error);
        // Ошибка профиля видна до обращения к Windows Update.
        Assert.Empty(wu.Requests);
    }

    [Fact]
    public async Task Image_language_is_not_added_again()
    {
        UupProfile("""{ "add": ["ru-RU", "de-DE"] }""");

        var result = await DryRun(new FakeWindowsUpdate { Release = _release });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("warning: ru-RU is the language of the image already", result.Error);
        Assert.Contains("Languages: de-DE (3 packages)", result.Output);
    }

    [Fact]
    public async Task Own_iso_gets_languages_and_the_update_from_a_release_of_the_same_base_build()
    {
        // Образ уже обновлён (26100.1742): обновление встраивается заново, чтобы перевести и добавленный язык.
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        IsoProfile(iso, """{ "add": ["ru-RU"], "display": "ru-RU" }""");

        var result = await DryRun(new FakeWindowsUpdate { Release = _release });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Assert.Contains($"Packages from: Windows 11, version 25H2 ({FakeUupRelease.Build}), channel windows11", result.Output);
        Assert.Contains("Languages: ru-RU (3 packages)", result.Output);
        // Без пакета включения версии (KB5000002): образ остаётся своей версии.
        Assert.Contains("Updates: Windows11.0-KB5000001-x64.msu, Windows11.0-KB5000004-x64-NDP481.cab", result.Output);
        Assert.Equal(["Download files", "Extract image", "Add languages", "Integrate updates", "Write answer file", "Pack ISO"], Steps(result.Output));
    }

    [Fact]
    public async Task Own_iso_without_the_update_warns_that_some_text_stays_untranslated()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        IsoProfile(iso, """{ "add": ["de-DE"] }""", "\"latestUpdate\": false,");

        var result = await DryRun(new FakeWindowsUpdate { Release = _release });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("some text of the added languages stays in en-US", result.Error);
        Assert.DoesNotContain("Integrate updates", result.Output);
        Assert.Contains("Add languages", result.Output);
    }

    [Fact]
    public async Task Own_iso_can_get_the_latest_update_alone()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        IsoProfile(iso, """{ }""", "\"latestUpdate\": true,");

        var result = await DryRun(new FakeWindowsUpdate { Release = _release });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(["Download files", "Extract image", "Integrate updates", "Write answer file", "Pack ISO"], Steps(result.Output));
    }

    [Fact]
    public async Task Own_iso_of_another_base_build_cannot_get_languages()
    {
        var iso = new FakeWindowsIso { Build = 22631, SpBuild = 4000 }.WriteTo(_dir.Path);
        IsoProfile(iso, """{ "add": ["de-DE"] }""");
        var wu = new FakeWindowsUpdate { Release = _release };

        var result = await DryRun(wu);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("adding languages to your own ISO works for Windows 11 24H2 and 25H2 and Windows 10 2004–22H2", result.Error);
        Assert.Empty(wu.Requests);
    }

    [Fact]
    public async Task Own_iso_without_languages_or_updates_needs_no_internet()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        IsoProfile(iso, """{ "display": "en-US" }""");
        var wu = new FakeWindowsUpdate { Offline = true };

        var result = await DryRun(wu);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Empty(wu.Requests);
        Assert.DoesNotContain("Add languages", result.Output);
    }

    [Fact]
    public async Task Answer_file_makes_the_display_language_the_system_language()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        _dir.Write("profile.json", """
            { "formatVersion": 1, "setup": { "skipOobe": true }, "regional": { "formats": "ru-RU" }, "languages": { "add": ["de-DE"], "display": "de-DE" } }
            """);

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--iso", iso, "--out", _dir.File("a.xml"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Empty(AnswerFileSchema.Validate(_dir.File("a.xml")));
        var doc = XDocument.Load(_dir.File("a.xml"));
        string? Value(string pass, string component, string element) => doc.Descendants(Unattend + "settings")
            .Where(s => (string?)s.Attribute("pass") == pass)
            .Descendants(Unattend + "component").Where(c => (string?)c.Attribute("name") == component)
            .Elements(Unattend + element).Select(e => e.Value).FirstOrDefault();
        // Установщик — на языке образа, Windows — на выбранном.
        Assert.Equal("en-US", doc.Descendants(Unattend + "SetupUILanguage").Single().Element(Unattend + "UILanguage")!.Value);
        Assert.Equal("de-DE", Value("windowsPE", "Microsoft-Windows-International-Core-WinPE", "UILanguage"));
        Assert.Equal("de-DE", Value("oobeSystem", "Microsoft-Windows-International-Core", "UILanguage"));
        Assert.Equal("ru-RU", Value("oobeSystem", "Microsoft-Windows-International-Core", "UserLocale"));
    }

    [Fact]
    public async Task Answer_file_rejects_a_display_language_the_image_will_not_have()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        _dir.Write("profile.json", """{ "formatVersion": 1, "languages": { "display": "ja-JP" } }""");

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--iso", iso, "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("'languages.display' is ja-JP, but the image has only en-US", result.Error);
    }

    [Theory]
    [InlineData("""{ "add": ["xx-YY"] }""", "'languages.add': 'xx-YY' is not a known culture")]
    [InlineData("""{ "add": ["de-DE", "de-de"] }""", "'languages.add' lists the same language twice")]
    [InlineData("""{ "display": "de" }""", "'languages.display': 'de' has no region")]
    public async Task Invalid_languages_are_rejected(string languages, string expected)
    {
        _dir.Write("profile.json", $$"""{ "formatVersion": 1, "languages": {{languages}} }""");

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task Languages_command_lists_what_can_be_added_to_an_iso()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);

        var result = await CliRun.RunAsync(new FakeWindowsUpdate { Release = _release }.Host(Workspace), ["languages", "--iso", iso, "--workspace", Workspace, "--json"]);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Output + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(["en-US"], json.RootElement.GetProperty("installed").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(FakeUupRelease.LanguagePacks, json.RootElement.GetProperty("available").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(FakeUupRelease.Build, json.RootElement.GetProperty("release").GetString());
    }

    [Fact]
    public async Task Languages_command_without_internet_says_so()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);

        var result = await CliRun.RunAsync(new FakeWindowsUpdate { Offline = true }.Host(Workspace), ["languages", "--iso", iso, "--workspace", Workspace, "--json"]);

        Assert.Equal(Cli.ExitCodes.Failure, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Contains("Check the internet connection", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }
}
