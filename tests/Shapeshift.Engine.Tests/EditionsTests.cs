using System.Text.Json;
using System.Xml.Linq;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Раздел профиля <c>editions</c>: в результате остаются только выбранные редакции.</summary>
public sealed class EditionsTests : IDisposable
{
    static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";

    readonly TempDir _dir = new();
    readonly string _source;
    readonly string _output;

    public EditionsTests()
    {
        _source = new FakeWindowsIso
        {
            Editions = [("Core", "Windows 11 Home"), ("Education", "Windows 11 Education"), ("Professional", "Windows 11 Pro")],
        }.WriteTo(_dir.Path);
        _output = _dir.File("result.iso");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Only_the_selected_editions_remain_in_source_order()
    {
        var result = await Build(new { editions = new[] { "Professional", "Core" } });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(["Core", "Professional"], EditionsInResult());
        Assert.Contains("Keeping 2 of 3 editions: Core, Professional", result.Output);

        var inspect = await CliRun.RunAsync("inspect", "--iso", _output, "--json");
        using var json = JsonDocument.Parse(inspect.Output);
        Assert.Equal([1, 2], json.RootElement.GetProperty("editions").EnumerateArray().Select(e => e.GetProperty("index").GetInt32()));
    }

    [Fact]
    public async Task Edition_to_install_is_chosen_by_its_new_index()
    {
        var result = await Build(new
        {
            editions = new[] { "Core", "Professional" },
            setup = new { edition = "Professional" },
        });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        var answerFile = XDocument.Parse(System.Text.Encoding.UTF8.GetString(IsoContents.ReadFile(_output, "autounattend.xml")));
        Assert.Equal("2", answerFile.Descendants(U + "MetaData").Single().Element(U + "Value")!.Value); // было 3
    }

    [Fact]
    public async Task Keeping_every_edition_adds_no_step()
    {
        var all = await Build(new { editions = new[] { "Core", "Education", "Professional" } }, "--dry-run");
        var some = await Build(new { editions = new[] { "Education" } }, "--dry-run");

        Assert.Equal(Cli.ExitCodes.Success, all.ExitCode);
        Assert.DoesNotContain("Keep selected editions", all.Output);
        Assert.Contains("2. Keep selected editions", some.Output);
    }

    [Fact]
    public async Task Without_editions_the_image_keeps_everything()
    {
        var result = await Build(new { });

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(["Core", "Education", "Professional"], EditionsInResult());
    }

    [Theory]
    [InlineData("""{ "editions": ["Enterprise"] }""", "'editions' lists 'Enterprise', which is not in the image; available: Core, Education, Professional")]
    [InlineData("""{ "editions": ["Core"], "setup": { "edition": "Professional" } }""", "'editions' removes it from the image")]
    [InlineData("""{ "editions": [] }""", "'editions' is empty")]
    [InlineData("""{ "editions": ["Core", "core"] }""", "same edition twice")]
    public async Task Invalid_editions_are_rejected(string body, string expectedError)
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
        json["formatVersion"] = JsonSerializer.SerializeToElement(1);
        json["source"] = JsonSerializer.SerializeToElement(new { iso = _source });
        json["output"] = JsonSerializer.SerializeToElement(new { iso = _output });
        var profile = _dir.Write("bad.json", JsonSerializer.Serialize(json));

        var result = await CliRun.RunAsync("build", "--profile", profile, "--workspace", _dir.File("ws"), "--dry-run");

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    IReadOnlyList<string> EditionsInResult()
    {
        var wim = _dir.File("result-install.wim");
        File.WriteAllBytes(wim, IsoContents.ReadFile(_output, @"sources\install.wim"));
        return FakeWindowsIso.EditionIds(wim);
    }

    Task<CliResult> Build(object profile, params string[] extra)
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(profile))!;
        json["formatVersion"] = JsonSerializer.SerializeToElement(1);
        json["source"] = JsonSerializer.SerializeToElement(new { iso = _source });
        json["output"] = JsonSerializer.SerializeToElement(new { iso = _output });
        var path = _dir.Write("profile.json", JsonSerializer.Serialize(json));
        return CliRun.RunAsync(["build", "--profile", path, "--workspace", _dir.File("ws"), .. extra]);
    }
}
