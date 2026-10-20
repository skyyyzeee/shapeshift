using System.Runtime.CompilerServices;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Встроенные пресеты окна — обычные профили: движок должен принимать их как есть, на любом выпуске.</summary>
public sealed class PresetTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> Presets() => new(
        Directory.GetFiles(PresetFolder(), "*.json").Select(Path.GetFileName).Order()!);

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task Preset_builds_an_answer_file_for_windows_11(string preset)
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);

        var result = await CliRun.RunAsync(
            "answerfile", "--profile", Path.Combine(PresetFolder(), preset), "--iso", iso, "--out", _dir.File("a.xml"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Empty(AnswerFileSchema.Validate(_dir.File("a.xml")));
    }

    [Fact]
    public void There_are_three_presets() => Assert.Equal(["clean.json", "minimal.json", "privacy.json"], Presets().Select(p => p.Data));

    static string PresetFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Shapeshift.App", "presets"));
}
