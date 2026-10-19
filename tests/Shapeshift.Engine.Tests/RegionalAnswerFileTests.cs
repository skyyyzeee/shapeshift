using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Регион (<c>regional</c>), язык установщика, выбор редакции и ключ продукта.</summary>
public sealed class RegionalAnswerFileTests : IDisposable
{
    static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";

    readonly TempDir _dir = new();
    readonly Lazy<string> _iso;

    public RegionalAnswerFileTests() =>
        _iso = new(() => new FakeWindowsIso { Language = "ru-RU" }.WriteTo(_dir.Path));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task Russian_formats_with_english_keyboard_first() => SnapshotOf("""
        {
          "formatVersion": 1,
          "setup": { "skipOobe": true, "edition": "Professional" },
          "regional": { "formats": "ru-RU", "keyboards": ["en-US", "ru-RU"], "timeZone": "Russian Standard Time" }
        }
        """);

    [Fact]
    public async Task Installer_language_comes_from_the_image()
    {
        var (doc, error) = await Generate("""
            { "formatVersion": 1, "regional": { "formats": "de-DE" } }
            """, withIso: true);

        var winPe = Component(doc, "windowsPE", "Microsoft-Windows-International-Core-WinPE");
        Assert.Equal("ru-RU", winPe.Element(U + "SetupUILanguage")!.Element(U + "UILanguage")!.Value);
        Assert.Equal("ru-RU", winPe.Element(U + "UILanguage")!.Value);
        Assert.Equal("de-DE", winPe.Element(U + "UserLocale")!.Value);
        Assert.Equal("ru-RU", winPe.Element(U + "InputLocale")!.Value); // раскладки не заданы — язык образа
        Assert.Equal("", error);
    }

    [Fact]
    public async Task Without_the_image_the_installer_language_page_stays_and_is_warned_about()
    {
        var (doc, error) = await Generate("""
            { "formatVersion": 1, "regional": { "formats": "ru-RU" } }
            """, withIso: false);

        Assert.Empty(doc.Descendants(U + "SetupUILanguage"));
        Assert.Equal("ru-RU", Component(doc, "oobeSystem", "Microsoft-Windows-International-Core").Element(U + "UserLocale")!.Value);
        Assert.Contains("installer will still ask for its language", error);
    }

    [Fact]
    public async Task Edition_is_chosen_by_its_index_without_a_product_key()
    {
        var (doc, _) = await Generate("""
            { "formatVersion": 1, "setup": { "edition": "professional" } }
            """, withIso: true);

        var metaData = doc.Descendants(U + "MetaData").Single();
        Assert.Equal("/IMAGE/INDEX", metaData.Element(U + "Key")!.Value);
        Assert.Equal("2", metaData.Element(U + "Value")!.Value);

        var key = doc.Descendants(U + "ProductKey").Single();
        Assert.Equal("00000-00000-00000-00000-00000", key.Element(U + "Key")!.Value);
        Assert.Equal("Never", key.Element(U + "WillShowUI")!.Value);
    }

    [Fact]
    public async Task Own_product_key_is_used_as_given()
    {
        var (doc, _) = await Generate("""
            { "formatVersion": 1, "setup": { "edition": "Core", "productKey": "abcde-fghij-klmno-pqrst-uvwxy" } }
            """, withIso: true);

        var key = doc.Descendants(U + "ProductKey").Single();
        Assert.Equal("ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", key.Element(U + "Key")!.Value);
        Assert.Equal("OnError", key.Element(U + "WillShowUI")!.Value);
        Assert.Equal("1", doc.Descendants(U + "MetaData").Single().Element(U + "Value")!.Value);
    }

    [Fact]
    public async Task Own_product_key_works_without_an_image()
    {
        var (doc, _) = await Generate("""
            { "formatVersion": 1, "setup": { "productKey": "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY" } }
            """, withIso: false);

        Assert.Single(doc.Descendants(U + "ProductKey"));
        Assert.Empty(doc.Descendants(U + "InstallFrom"));
    }

    [Fact]
    public async Task Build_uses_the_same_answer_file_as_answerfile()
    {
        var output = _dir.File("result.iso");
        var profile = _dir.Write("build.json", JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            setup = new { edition = "Professional", skipOobe = true },
            regional = new { formats = "ru-RU", keyboards = new[] { "en-US", "ru-RU" } },
            source = new { iso = _iso.Value },
            output = new { iso = output },
        }));

        var build = await CliRun.RunAsync("build", "--profile", profile, "--workspace", _dir.File("ws"));
        var answer = await CliRun.RunAsync("answerfile", "--profile", profile, "--out", _dir.File("expected.xml"));

        Assert.True(build.ExitCode == Cli.ExitCodes.Success, build.Error);
        Assert.Equal(Cli.ExitCodes.Success, answer.ExitCode);
        Assert.Equal(File.ReadAllBytes(_dir.File("expected.xml")), IsoContents.ReadFile(output, "autounattend.xml"));
    }

    [Theory]
    [InlineData("Professional", false, "needs the image: pass --iso")]
    [InlineData("Enterprise", true, "edition 'Enterprise' is not in the image; available: Core, Professional")]
    public async Task Edition_must_exist_in_the_image(string edition, bool withIso, string expectedError)
    {
        var result = await Run($$"""{ "formatVersion": 1, "setup": { "edition": "{{edition}}" } }""", withIso);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    [Theory]
    [InlineData("""{ "formats": "ru" }""", "has no region")]
    [InlineData("""{ "formats": "xx-QQ" }""", "not a known culture")]
    [InlineData("""{ "keyboards": ["en-US", "EN-us"] }""", "same keyboard twice")]
    [InlineData("""{ "keyboards": ["0419:0409"] }""", "not a known culture")]
    [InlineData("""{ "timeZone": "Moscow" }""", "not a Windows time zone id")]
    public async Task Invalid_regional_settings_are_rejected(string regional, string expectedError)
    {
        var result = await Run($$"""{ "formatVersion": 1, "regional": {{regional}} }""", withIso: false);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    [Theory]
    [InlineData("""{ "productKey": "12345" }""", "must look like XXXXX")]
    [InlineData("""{ "edition": " " }""", "'setup.edition' is empty")]
    public async Task Invalid_edition_settings_are_rejected(string setup, string expectedError)
    {
        var result = await Run($$"""{ "formatVersion": 1, "setup": {{setup}} }""", withIso: false);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    static XElement Component(XDocument doc, string pass, string name) =>
        doc.Root!.Elements(U + "settings").Single(s => (string?)s.Attribute("pass") == pass)
            .Elements(U + "component").Single(c => (string?)c.Attribute("name") == name);

    async Task SnapshotOf(string profileJson, [CallerMemberName] string testName = "")
    {
        await Generate(profileJson, withIso: true);
        Snapshot.Match(await File.ReadAllTextAsync(_dir.File("autounattend.xml"), TestContext.Current.CancellationToken), "xml", testName: testName);
    }

    async Task<(XDocument Doc, string Error)> Generate(string profileJson, bool withIso)
    {
        var result = await Run(profileJson, withIso);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        var answerFile = _dir.File("autounattend.xml");
        Assert.Empty(AnswerFileSchema.Validate(answerFile));
        return (XDocument.Load(answerFile), result.Error);
    }

    Task<CliResult> Run(string profileJson, bool withIso)
    {
        var profile = _dir.Write("profile.json", profileJson);
        string[] args = ["answerfile", "--profile", profile, "--out", _dir.File("autounattend.xml")];
        return CliRun.RunAsync(withIso ? [.. args, "--iso", _iso.Value] : args);
    }
}
