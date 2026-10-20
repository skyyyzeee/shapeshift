using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

public sealed class InspectCommandTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Shows_release_architecture_and_editions()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("inspect", "--iso", iso);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Snapshot.Match(result.Output, "txt");
    }

    [Fact]
    public async Task Json_output_is_the_contract_for_the_app()
    {
        var iso = new FakeWindowsIso { Language = "ru-RU" }.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("inspect", "--iso", iso, "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Snapshot.Match(result.Output, "json");
    }

    [Theory]
    [InlineData("server", "server")]
    [InlineData("arm64", "arm64")]
    [InlineData("old", "too-old")]
    [InlineData("no-install", "not-windows-installer")]
    public async Task Json_error_carries_a_code(string variant, string expectedCode)
    {
        var fake = variant switch
        {
            "server" => new FakeWindowsIso { InstallationType = "Server" },
            "arm64" => new FakeWindowsIso { Arch = 12 },
            "old" => new FakeWindowsIso { Build = 17763 },
            _ => new FakeWindowsIso { Omit = [@"sources\install.wim"] },
        };
        var iso = fake.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("inspect", "--iso", iso, "--json");

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Json_error_for_a_missing_file()
    {
        var result = await CliRun.RunAsync("inspect", "--iso", _dir.File("nope.iso"), "--json");

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("\"code\":\"not-found\"", result.Output);
    }

    [Fact]
    public async Task Windows_10_ltsc_is_supported()
    {
        var iso = new FakeWindowsIso
        {
            Build = 19044,
            SpBuild = 5737,
            Editions = [("EnterpriseS", "Windows 10 Enterprise LTSC")],
        }.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("inspect", "--iso", iso);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("Image: Windows 10 21H2 (19044.5737)", result.Output);
    }

    public static TheoryData<string, FakeWindowsIso, string> Unsupported => new()
    {
        { "no install image", new FakeWindowsIso { Omit = [@"sources\install.wim"] }, @"missing sources\install.wim" },
        { "no setup image", new FakeWindowsIso { Omit = [@"sources\boot.wim"] }, @"missing sources\boot.wim" },
        { "no uefi boot", new FakeWindowsIso { Omit = [@"efi\microsoft\boot\efisys.bin"] }, "efisys.bin" },
        { "server", new FakeWindowsIso { InstallationType = "Server" }, "Windows Server images are not supported" },
        { "arm64", new FakeWindowsIso { Arch = 12 }, "ARM64 images are not supported" },
        { "x86", new FakeWindowsIso { Arch = 0 }, "(x86) images are not supported" },
        { "too old", new FakeWindowsIso { Build = 17763 }, "oldest supported build is 19041" },
        { "windows 8.1", new FakeWindowsIso { Major = 6, Build = 9600 }, "Only Windows 10 and 11" },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task Rejects_unsupported_images(string _, FakeWindowsIso fake, string expectedError)
    {
        var iso = fake.WriteTo(_dir.Path);

        var result = await CliRun.RunAsync("inspect", "--iso", iso);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public async Task Rejects_a_file_that_is_not_a_disc_image()
    {
        var notIso = _dir.Write("notes.iso", new string('x', 100_000));

        var result = await CliRun.RunAsync("inspect", "--iso", notIso);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("not a disc image", result.Error);
    }

    [Fact]
    public async Task Rejects_a_missing_file()
    {
        var result = await CliRun.RunAsync("inspect", "--iso", _dir.File("nope.iso"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("Image not found", result.Error);
    }
}
