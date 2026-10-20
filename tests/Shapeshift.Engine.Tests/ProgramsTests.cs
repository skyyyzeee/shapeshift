using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Предустановка программ: установщики и сценарий в sources\$OEM$, запуск сценария при первом входе.</summary>
public sealed class ProgramsTests : IDisposable
{
    const string Media = @"sources\$OEM$\$$\Setup\Shapeshift";
    const string Command = @"cmd.exe /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""%WINDIR%\Setup\Shapeshift\install-programs.ps1""";

    readonly TempDir _dir = new();
    readonly string _source;

    public ProgramsTests()
    {
        _source = new FakeWindowsIso().WriteTo(_dir.Path);
        _dir.Write(@"dist\marker.cmd", "@echo off\r\necho installed> %1\r\n");
        _dir.Write(@"dist\office\setup.exe", "fake setup");
        _dir.Write(@"dist\office\configuration.xml", "<Configuration />");
        _dir.Write(@"dist\office\data\cab.dat", new string('x', 1000));
    }

    public void Dispose() => _dir.Dispose();

    // Пути установщиков — относительно папки профиля.
    string Profile(string programs, string extra = "") => _dir.Write("profile.json", $$"""
        {
          "formatVersion": 1,
          "programs": {{programs}},
          {{extra}}
          "source": { "iso": {{JsonSerializer.Serialize(_source)}} },
          "output": { "iso": {{JsonSerializer.Serialize(_dir.File("out.iso"))}} }
        }
        """);

    const string ThreePrograms = """
        [
          { "winget": "Telegram.TelegramDesktop" },
          { "path": "dist/marker.cmd", "args": "C:\\ProgramData\\marker.txt", "name": "Marker 'test'" },
          { "path": "dist/office", "run": "setup.exe", "args": "/configure configuration.xml", "name": "Office" }
        ]
        """;

    Task<CliResult> Build(params string[] extra) =>
        CliRun.RunAsync(["build", "--profile", _dir.File("profile.json"), "--workspace", _dir.File("ws"), .. extra]);

    [Fact]
    public async Task Build_puts_installers_and_script_into_the_image()
    {
        Profile(ThreePrograms);

        var result = await Build();

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        var files = IsoContents.Files(_dir.File("out.iso"));
        Assert.Contains($@"{Media}\install-programs.ps1", files);
        Assert.Contains($@"{Media}\Programs\01\marker.cmd", files);
        Assert.Contains($@"{Media}\Programs\02\setup.exe", files);
        Assert.Contains($@"{Media}\Programs\02\data\cab.dat", files);
        Assert.Equal(
            File.ReadAllBytes(_dir.File(@"dist\office\configuration.xml")),
            IsoContents.ReadFile(_dir.File("out.iso"), $@"{Media}\Programs\02\configuration.xml"));
    }

    [Fact]
    public async Task Install_script_lists_programs_in_order()
    {
        Profile(ThreePrograms);

        var result = await Build();

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        var script = IsoContents.ReadFile(_dir.File("out.iso"), $@"{Media}\install-programs.ps1");
        Assert.Equal([0xEF, 0xBB, 0xBF], script[..3]); // BOM: Windows PowerShell 5.1 иначе читает в ANSI
        Snapshot.Match(Encoding.UTF8.GetString(script[3..]), "ps1");
    }

    [Fact]
    public async Task Answer_file_runs_the_script_at_first_logon_before_signing_out()
    {
        Profile(ThreePrograms, "\"setup\": { \"user\": { \"name\": \"Anna\" } },");

        var result = await Build();

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        var answers = XDocument.Parse(Encoding.UTF8.GetString(IsoContents.ReadFile(_dir.File("out.iso"), "autounattend.xml")));
        var commands = answers.Descendants().Where(e => e.Name.LocalName == "CommandLine").Select(e => e.Value).ToList();
        var install = commands.IndexOf(Command);
        Assert.True(install >= 0, string.Join("\n", commands));
        Assert.True(install < commands.IndexOf("shutdown.exe /l"), "programs must be installed before signing out");
    }

    [Fact]
    public async Task Without_programs_nothing_is_added()
    {
        Profile("[]");

        var result = await Build();

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.DoesNotContain(IsoContents.Files(_dir.File("out.iso")), f => f.StartsWith(@"sources\$OEM$", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("install-programs", Encoding.UTF8.GetString(IsoContents.ReadFile(_dir.File("out.iso"), "autounattend.xml")));
    }

    [Fact]
    public async Task Dry_run_shows_the_step_and_counts_the_space()
    {
        Profile(ThreePrograms);

        var result = await Build("--dry-run", "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Output);
        using var plan = JsonDocument.Parse(result.Output);
        var step = plan.RootElement.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "AddPrograms");
        Assert.True(step.GetProperty("bytes").GetInt64() >= 1000);
    }

    [Fact]
    public async Task Only_winget_programs_still_add_the_script()
    {
        Profile("""[{ "winget": "7zip.7zip" }]""");

        var result = await Build();

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains($@"{Media}\install-programs.ps1", IsoContents.Files(_dir.File("out.iso")));
    }

    [Theory]
    [InlineData("""[{ "path": "dist/missing.exe" }]""", "does not exist")]
    [InlineData("""[{ "path": "dist/office" }]""", "is a folder; say what to run in it")]
    [InlineData("""[{ "path": "dist/office", "run": "install.exe" }]""", "'install.exe' is not in")]
    [InlineData("""[{ "path": "dist/office/configuration.xml" }]""", "cannot be run")]
    [InlineData("""[{ "path": "dist/marker.cmd", "run": "x.exe" }]""", "'run' is only for a folder")]
    public async Task Bad_installer_paths_are_rejected_before_building(string programs, string expectedError)
    {
        Profile(programs);

        var result = await Build();

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
        Assert.False(File.Exists(_dir.File("out.iso")));
    }

    [Theory]
    [InlineData("""[{ "winget": "Telegram.TelegramDesktop", "path": "a.exe" }]""", "exactly one of 'winget'")]
    [InlineData("""[{ "name": "x" }]""", "exactly one of 'winget'")]
    [InlineData("""[{ "winget": "not an id" }]""", "is not a winget package id")]
    [InlineData("""[{ "winget": "7zip.7zip", "args": "/S" }]""", "'run' and 'args' are only for 'path'")]
    [InlineData("""[{ "winget": "7zip.7zip" }, { "winget": "7ZIP.7zip" }]""", "same winget package twice")]
    [InlineData("""[{ "path": "a.exe" }, { "path": "./a.exe" }]""", "same installer twice")]
    [InlineData("""[{ "path": "dist", "run": "../evil.exe" }]""", "must be a path inside the folder")]
    [InlineData("""[{ "path": "dist", "run": "readme.txt" }]""", "files can be run")]
    [InlineData("""[{ "path": "a.exe", "args": "/S\n/x" }]""", "args must be one line")]
    public async Task Invalid_programs_are_rejected(string programs, string expectedError)
    {
        Profile(programs);

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public async Task Answer_file_alone_warns_that_it_does_not_carry_the_installers()
    {
        Profile(ThreePrograms);

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--out", _dir.File("a.xml"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("this answer file alone does not install them", result.Error);
        Assert.Contains("install-programs.ps1", File.ReadAllText(_dir.File("a.xml")));
    }

    [Fact]
    public async Task Removing_app_installer_warns_that_winget_programs_will_be_skipped()
    {
        Profile("""[{ "winget": "7zip.7zip" }]""", "\"removeApps\": [\"Microsoft.DesktopAppInstaller\"],");

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--out", _dir.File("a.xml"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("winget programs need App Installer", result.Error);
    }

    [Fact]
    public async Task Catalog_suggests_popular_winget_programs()
    {
        var result = await CliRun.RunAsync("catalog", "list");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        using var catalog = JsonDocument.Parse(result.Output);
        var telegram = catalog.RootElement.GetProperty("programs").EnumerateArray()
            .Single(p => p.GetProperty("winget").GetString() == "Telegram.TelegramDesktop");
        Assert.Equal("Telegram", telegram.GetProperty("title").GetProperty("ru").GetString());
    }

    [Fact]
    public async Task Catalog_rejects_a_bad_program_id()
    {
        var catalog = _dir.Write("catalog.json",
            """{ "formatVersion": 1, "categories": [], "tweaks": [], "programs": [{ "winget": "bad id", "title": { "en": "A", "ru": "А" } }] }""");

        var result = await CliRun.RunAsync("catalog", "validate", catalog);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("program 'bad id': not a winget package id", result.Error);
    }
}
