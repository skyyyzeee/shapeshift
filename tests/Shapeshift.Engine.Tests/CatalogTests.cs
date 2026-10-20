using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Каталог твиков: <c>catalog validate</c> и превращение твиков в файл ответов.</summary>
public sealed class CatalogTests : IDisposable
{
    static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";

    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Shipped_catalog_is_valid()
    {
        var result = await CliRun.RunAsync("catalog", "validate");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Matches(@"^ok: \d+ tweaks in \d+ categories\.", result.Output);
    }

    [Fact]
    public async Task List_json_is_the_contract_for_the_app()
    {
        var catalog = WriteCatalog(
            Tweak("custom.everywhere", Registry("A"), conflicts: """["custom.only-new"]"""),
            Tweak("custom.only-new", Registry("B"), releases: """{ "products": ["windows11"], "minBuild": 26100 }"""));
        var iso = new FakeWindowsIso { Build = 22631, SpBuild = 4460 }.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("catalog", "list", "--catalog", catalog, "--iso", iso);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Snapshot.Match(result.Output, "json");
    }

    [Fact]
    public async Task List_without_an_image_leaves_applicability_open()
    {
        var result = await CliRun.RunAsync("catalog", "list");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        var tweaks = json.RootElement.GetProperty("tweaks").EnumerateArray().ToList();
        Assert.NotEmpty(tweaks);
        Assert.All(tweaks, t => Assert.Equal(System.Text.Json.JsonValueKind.Null, t.GetProperty("applicable").ValueKind));
        Assert.All(tweaks, t => Assert.False(string.IsNullOrEmpty(t.GetProperty("name").GetProperty("ru").GetString())));
    }

    [Fact]
    public Task Setup_stage_tweaks() => SnapshotOf("""
        {
          "formatVersion": 1,
          "tweaks": [
            "system.no-hibernation",
            "interface.classic-context-menu",
            "privacy.suggestions",
            "privacy.telemetry"
          ]
        }
        """);

    [Fact]
    public async Task User_settings_go_to_the_default_profile_between_load_and_unload()
    {
        var doc = await Generate("""
            { "formatVersion": 1, "tweaks": ["privacy.telemetry", "interface.file-extensions"] }
            """);

        var paths = SpecializeCommands(doc);
        Assert.Equal(4, paths.Count);
        Assert.StartsWith("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection\"", paths[0]);
        Assert.StartsWith("cmd.exe /c reg.exe load \"HKU\\DefaultUser\"", paths[1]);
        Assert.StartsWith("reg.exe add \"HKU\\DefaultUser\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v HideFileExt", paths[2]);
        Assert.Equal("reg.exe unload \"HKU\\DefaultUser\"", paths[3]);
    }

    [Fact]
    public async Task First_logon_tweaks_run_before_signing_out_for_the_new_password()
    {
        var catalog = WriteCatalog(Tweak("custom.first-logon", """
            { "type": "registry", "stage": "firstLogon", "hive": "HKCU", "key": "Software\\Shapeshift", "name": "Hello", "valueType": "string", "value": "world" }
            """));

        var doc = await Generate("""
            { "formatVersion": 1, "tweaks": ["custom.first-logon"], "setup": { "user": { "name": "Alex" } } }
            """, catalog);

        var commands = doc.Descendants(U + "SynchronousCommand")
            .Select(c => (Line: c.Element(U + "CommandLine")!.Value, Description: c.Element(U + "Description")!.Value))
            .ToList();
        Assert.Equal("reg.exe add \"HKCU\\Software\\Shapeshift\" /v Hello /t REG_SZ /d \"world\" /f", commands[0].Line);
        Assert.Equal("custom.first-logon", commands[0].Description);
        Assert.Equal("shutdown.exe /l", commands[^1].Line);
    }

    [Fact]
    public async Task Tweak_that_does_not_apply_to_the_image_is_rejected()
    {
        var iso = new FakeWindowsIso { Build = 19045, SpBuild = 5011 }.WriteTo(_dir.Path);
        var profile = _dir.Write("profile.json", """{ "formatVersion": 1, "tweaks": ["bypass.tpm", "interface.file-extensions"] }""");

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--iso", iso, "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("tweak 'bypass.tpm' does not apply to Windows 10 22H2 (19045.5011)", result.Error);
        Assert.DoesNotContain("interface.file-extensions", result.Error);
    }

    [Fact]
    public async Task Conflicting_tweaks_are_rejected()
    {
        var catalog = WriteCatalog(
            Tweak("custom.light", Registry("Light"), conflicts: """["custom.dark"]"""),
            Tweak("custom.dark", Registry("Dark")));
        var profile = _dir.Write("profile.json", """{ "formatVersion": 1, "tweaks": ["custom.dark", "custom.light"] }""");

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--catalog", catalog, "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("tweaks 'custom.light' and 'custom.dark' conflict", result.Error);
    }

    public static TheoryData<string, string> InvalidCatalogs => new()
    {
        { Catalog(Tweak("custom.a", Registry("A")), Tweak("custom.a", Registry("B"))), "tweak 'custom.a' is defined more than once" },
        { Catalog(Tweak("custom.a", Registry("A"), category: "nope")), "unknown category 'nope'" },
        { Catalog(Tweak("custom.a", Registry("A"), conflicts: """["custom.b"]""")), "conflicts with unknown tweak 'custom.b'" },
        { Catalog(Tweak("custom.a", Registry("A"), name: """{ "en": "A" }""")), "name has no 'ru' translation" },
        { Catalog(Tweak("Custom_A", Registry("A"))), "id must look like 'category.name'" },
        { Catalog(Tweak("custom.a", Registry("A"), releases: """{ "minBuild": 26100, "maxBuild": 22000 }""")), "minBuild 26100 is greater than maxBuild 22000" },
        { Catalog(Tweak("custom.a", "")), "has no actions" },
        { Catalog(Tweak("custom.a", Registry("A", stage: "installer", hive: "HKCU"))), "installer stage has no user registry" },
        { Catalog(Tweak("custom.a", Registry("A", valueType: "string", value: "\"a%b\""))), "contains \" or %" },
        { Catalog(Tweak("custom.a", Registry("A", valueType: "string", value: "\"C:\\\\\""))), "cannot end with a backslash" },
        { Catalog(Tweak("custom.a", $$"""{ "type": "command", "stage": "setup", "command": "{{new string('x', 300)}}" }""")), "command is 300 characters, the limit for this stage is 259" },
        { Catalog(Tweak("custom.a", Registry("A", stage: "later"))), "unknown stage 'later'" },
        { Catalog(Tweak("custom.a", Registry("A", valueType: "dword", value: "\"one\""))), "does not fit valueType 'Dword'" },
        { Catalog(Tweak("custom.a", Registry("A"), extra: "\"colour\": \"red\",")), "colour" },
        { """{ "formatVersion": 2 }""", "Unsupported catalog format version 2" },
    };

    [Theory]
    [MemberData(nameof(InvalidCatalogs))]
    public async Task Invalid_catalog_is_rejected(string catalogJson, string expectedError)
    {
        var catalog = _dir.Write("catalog.json", catalogJson);

        var result = await CliRun.RunAsync("catalog", "validate", catalog);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public async Task Answerfile_refuses_an_invalid_catalog()
    {
        var catalog = _dir.Write("catalog.json", Catalog(Tweak("custom.a", "")));
        var profile = _dir.Write("profile.json", """{ "formatVersion": 1 }""");

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--catalog", catalog, "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("has no actions", result.Error);
    }

    static List<string> SpecializeCommands(XDocument doc) =>
        doc.Root!.Elements(U + "settings").Single(s => (string?)s.Attribute("pass") == "specialize")
            .Descendants(U + "Path").Select(p => p.Value).ToList();

    static string Registry(
        string name, string stage = "setup", string hive = "HKLM", string valueType = "dword", string value = "1") => $$"""
        { "type": "registry", "stage": "{{stage}}", "hive": "{{hive}}", "key": "SOFTWARE\\Shapeshift", "name": "{{name}}", "valueType": "{{valueType}}", "value": {{value}} }
        """;

    static string Tweak(
        string id, string actions, string category = "custom", string? conflicts = null, string? releases = null,
        string name = """{ "en": "Name", "ru": "Имя" }""", string extra = "") => $$"""
        {
          "id": "{{id}}", "category": "{{category}}", {{extra}}
          "name": {{name}},
          "description": { "en": "Description", "ru": "Описание" },
          {{(releases is null ? "" : $"\"releases\": {releases},")}}
          {{(conflicts is null ? "" : $"\"conflicts\": {conflicts},")}}
          "actions": [{{actions}}]
        }
        """;

    static string Catalog(params string[] tweaks) => $$"""
        {
          "formatVersion": 1,
          "categories": [{ "id": "custom", "name": { "en": "Custom", "ru": "Своё" } }],
          "tweaks": [{{string.Join(",", tweaks)}}]
        }
        """;

    string WriteCatalog(params string[] tweaks) => _dir.Write("catalog.json", Catalog(tweaks));

    async Task SnapshotOf(string profileJson, [CallerMemberName] string testName = "")
    {
        await Generate(profileJson);
        Snapshot.Match(await File.ReadAllTextAsync(_dir.File("autounattend.xml"), TestContext.Current.CancellationToken), "xml", testName: testName);
    }

    async Task<XDocument> Generate(string profileJson, string? catalog = null)
    {
        var profile = _dir.Write("profile.json", profileJson);
        var answerFile = _dir.File("autounattend.xml");
        string[] args = ["answerfile", "--profile", profile, "--out", answerFile];

        var result = await CliRun.RunAsync(catalog is null ? args : [.. args, "--catalog", catalog]);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Empty(AnswerFileSchema.Validate(answerFile));
        return XDocument.Load(answerFile);
    }
}
