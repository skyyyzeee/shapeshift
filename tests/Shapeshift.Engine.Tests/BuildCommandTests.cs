using System.Security.Cryptography;
using System.Text.Json;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

public sealed class BuildCommandTests : IDisposable
{
    const int BiosPlatform = 0;
    const int UefiPlatform = 0xEF;

    readonly TempDir _dir = new();
    readonly string _workspace;
    readonly string _source;
    readonly string _output;

    public BuildCommandTests()
    {
        _workspace = Path.Combine(_dir.Path, "workspace");
        _source = new FakeWindowsIso().WriteTo(_dir.Path);
        _output = _dir.File(@"out\result.iso");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Builds_iso_with_answer_file_at_root()
    {
        var profile = WriteProfile();

        var result = await Build(profile);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.True(File.Exists(_output));

        var expected = await AnswerFileFor(profile);
        Assert.Equal(expected, IsoContents.ReadFile(_output, "autounattend.xml"));
    }

    [Fact]
    public async Task Keeps_every_file_of_the_source_image()
    {
        var result = await Build(WriteProfile());
        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);

        var expectedFiles = IsoContents.Files(_source).Append("autounattend.xml").Order(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectedFiles, IsoContents.Files(_output));

        foreach (var (path, content) in FakeWindowsIso.PlainFiles)
            Assert.Equal(content, IsoContents.ReadFile(_output, path));
        Assert.Equal(IsoContents.ReadFile(_source, @"sources\install.wim"), IsoContents.ReadFile(_output, @"sources\install.wim"));
    }

    [Fact]
    public async Task Result_boots_on_bios_and_uefi()
    {
        var result = await Build(WriteProfile());
        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);

        Assert.Equal([BiosPlatform, UefiPlatform], IsoContents.BootPlatforms(_output));
    }

    [Fact]
    public async Task Result_is_itself_a_valid_source_image()
    {
        await Build(WriteProfile());

        var inspectSource = await CliRun.RunAsync("inspect", "--iso", _source);
        var inspectResult = await CliRun.RunAsync("inspect", "--iso", _output);

        Assert.Equal(Cli.ExitCodes.Success, inspectResult.ExitCode);
        Assert.Equal(inspectSource.Output, inspectResult.Output);
    }

    [Fact]
    public async Task Leaves_source_untouched_and_cleans_the_workspace()
    {
        var before = Hash(_source);

        var result = await Build(WriteProfile());

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(before, Hash(_source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace));
    }

    [Fact]
    public async Task Overwrites_an_existing_output()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        File.WriteAllText(_output, "old");

        var result = await Build(WriteProfile());

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("autounattend.xml", IsoContents.Files(_output));
        Assert.False(File.Exists(_output + ".partial"));
    }

    [Fact]
    public async Task Dry_run_prints_the_plan_and_builds_nothing()
    {
        var result = await Build(WriteProfile(), "--dry-run");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("Image: Windows 11 24H2 (26100.1742)", result.Output);
        Assert.Contains("1. Extract image", result.Output);
        Assert.Contains("2. Write answer file", result.Output);
        Assert.Contains("3. Pack ISO", result.Output);
        Assert.Contains("Space:", result.Output);
        Assert.False(File.Exists(_output));
        Assert.False(Directory.Exists(_workspace) && Directory.EnumerateFileSystemEntries(_workspace).Any());
    }

    [Fact]
    public async Task Refuses_to_start_without_enough_space()
    {
        var host = new EngineHost { AvailableFreeSpace = _ => 10L * 1024 * 1024 };

        var result = await CliRun.RunAsync(host, "build", "--profile", WriteProfile(), "--workspace", _workspace);

        Assert.Equal(Cli.ExitCodes.NotEnoughSpace, result.ExitCode);
        Assert.Contains("not enough space", result.Error);
        Assert.False(File.Exists(_output));
        Assert.False(Directory.Exists(_workspace));
    }

    [Fact]
    public async Task Refuses_a_workspace_too_deep_for_the_image()
    {
        var deepWorkspace = Path.Combine(_dir.Path, new string('w', 230));

        var result = await CliRun.RunAsync("build", "--profile", WriteProfile(), "--workspace", deepWorkspace);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("workspace path is too long", result.Error);
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task Resolves_relative_paths_against_the_profile_folder()
    {
        var profile = _dir.Write("profiles/relative.json", """
            {
              "formatVersion": 1,
              "source": { "iso": "../fake-windows.iso" },
              "output": { "iso": "../out/result.iso" }
            }
            """);

        var result = await Build(profile);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.True(File.Exists(_output));
    }

    [Theory]
    [InlineData("""{ "formatVersion": 1, "output": { "iso": "out.iso" } }""", "no 'source.iso'")]
    [InlineData("""{ "formatVersion": 1, "source": { "iso": "fake-windows.iso" } }""", "no 'output.iso'")]
    [InlineData("""{ "formatVersion": 1, "source": { "iso": "fake-windows.iso" }, "output": { "iso": "fake-windows.iso" } }""", "must differ")]
    [InlineData("""{ "formatVersion": 1, "source": { "iso": "missing.iso" }, "output": { "iso": "out.iso" } }""", "Image not found")]
    public async Task Rejects_profiles_that_cannot_be_built(string json, string expectedError)
    {
        var result = await Build(_dir.Write("bad.json", json));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    string WriteProfile() => _dir.Write("profile.json", JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        tweaks = new[] { "bypass.tpm", "bypass.secure-boot", "bypass.ram", "bypass.cpu" },
        source = new { iso = _source },
        output = new { iso = _output },
    }));

    Task<CliResult> Build(string profile, params string[] extra) =>
        CliRun.RunAsync(["build", "--profile", profile, "--workspace", _workspace, .. extra]);

    async Task<byte[]> AnswerFileFor(string profile)
    {
        var path = _dir.File("expected-autounattend.xml");
        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--out", path);
        Assert.Equal(Cli.ExitCodes.Success, result.ExitCode);
        return await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
