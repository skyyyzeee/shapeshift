using System.Text.Json;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>
/// Тесты на настоящем установочном ISO Windows. Медленные (распаковка и упаковка гигабайт),
/// поэтому запускаются, только если задана переменная SHAPESHIFT_TEST_ISO с путём к образу:
/// <c>SHAPESHIFT_TEST_ISO=D:\win11.iso dotnet test</c>.
/// </summary>
[Trait("Category", "RealImage")]
public sealed class RealImageTests : IDisposable
{
    static readonly string? RealIso = Environment.GetEnvironmentVariable("SHAPESHIFT_TEST_ISO");

    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Inspects_a_real_image()
    {
        var iso = RequireRealIso();

        var result = await CliRun.RunAsync("inspect", "--iso", iso);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Matches(@"Image: Windows 1[01] ", result.Output);
        Assert.Contains("Architecture: x64", result.Output);
    }

    [Fact]
    public async Task Builds_from_a_real_image()
    {
        var iso = RequireRealIso();
        var output = _dir.File("result.iso");
        var profile = _dir.Write("profile.json", JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            tweaks = new[] { "bypass.tpm", "bypass.secure-boot", "bypass.ram", "bypass.cpu" },
            source = new { iso },
            output = new { iso = output },
        }));

        var result = await CliRun.RunAsync(
            "build", "--profile", profile, "--workspace", Path.Combine(_dir.Path, "workspace"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("autounattend.xml", IsoContents.Files(output));
        Assert.Equal([0, 0xEF], IsoContents.BootPlatforms(output));

        var inspectSource = await CliRun.RunAsync("inspect", "--iso", iso);
        var inspectResult = await CliRun.RunAsync("inspect", "--iso", output);
        Assert.Equal(inspectSource.Output, inspectResult.Output);
    }

    [Fact]
    public async Task Keeps_one_edition_of_a_real_image()
    {
        var iso = RequireRealIso();
        var output = _dir.File("one-edition.iso");
        var profile = _dir.Write("profile.json", JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            editions = new[] { "Professional" },
            source = new { iso },
            output = new { iso = output },
        }));

        var result = await CliRun.RunAsync(
            "build", "--profile", profile, "--workspace", Path.Combine(_dir.Path, "workspace"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        var inspect = await CliRun.RunAsync("inspect", "--iso", output);
        Assert.Contains("Professional", inspect.Output);
        Assert.DoesNotContain("Core ", inspect.Output);
    }

    static string RequireRealIso()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(RealIso), "SHAPESHIFT_TEST_ISO is not set.");
        return RealIso!;
    }
}
