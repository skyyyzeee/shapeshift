using System.Runtime.CompilerServices;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

public sealed class AnswerFileCommandTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task All_bypasses() => SnapshotOf("""
        {
          "formatVersion": 1,
          "architecture": "x64",
          "bypass": { "tpm": true, "secureBoot": true, "ram": true, "cpu": true }
        }
        """);

    [Fact]
    public Task Bypasses_come_in_canonical_order() => SnapshotOf("""
        {
          "formatVersion": 1,
          "bypass": { "ram": true, "tpm": true }
        }
        """);

    [Fact]
    public Task No_bypasses() => SnapshotOf("""
        { "formatVersion": 1 }
        """);

    [Fact]
    public async Task Disabled_bypass_is_not_in_the_answer_file()
    {
        var xml = await GenerateValid("""
            {
              "formatVersion": 1,
              "bypass": { "tpm": true, "secureBoot": false }
            }
            """);

        Assert.Contains("BypassTPMCheck", xml);
        Assert.DoesNotContain("BypassSecureBootCheck", xml);
        Assert.DoesNotContain("BypassRAMCheck", xml);
        Assert.DoesNotContain("BypassCPUCheck", xml);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{}", "no 'formatVersion'")]
    [InlineData("""{ "formatVersion": "1" }""", "must be an integer")]
    [InlineData("""{ "formatVersion": 2 }""", "Unsupported profile format version 2")]
    [InlineData("""{ "formatVersion": 1, "architecture": "arm64" }""", "'arm64' is not supported")]
    [InlineData("""{ "formatVersion": 1, "architecture": "x86" }""", "Unknown architecture 'x86'")]
    [InlineData("""{ "formatVersion": 1, "bypas": {} }""", "bypas")]
    [InlineData("""{ "formatVersion": 1, "bypass": { "tpm": "yes" } }""", "Profile is invalid")]
    public async Task Invalid_profile_is_rejected(string json, string expectedError)
    {
        var profile = _dir.Write("profile.json", json);
        var answerFile = _dir.File("autounattend.xml");

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--out", answerFile);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
        Assert.False(File.Exists(answerFile));
    }

    [Fact]
    public async Task Missing_profile_file_is_rejected()
    {
        var result = await CliRun.RunAsync(
            "answerfile", "--profile", _dir.File("nope.json"), "--out", _dir.File("autounattend.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("profile not found", result.Error);
    }

    [Fact]
    public async Task Missing_required_option_fails()
    {
        var result = await CliRun.RunAsync("answerfile", "--out", _dir.File("autounattend.xml"));

        Assert.NotEqual(Cli.ExitCodes.Success, result.ExitCode);
        Assert.Contains("--profile", result.Error);
    }

    async Task SnapshotOf(string profileJson, [CallerMemberName] string testName = "") =>
        Snapshot.Match(await GenerateValid(profileJson), "xml", testName: testName);

    async Task<string> GenerateValid(string profileJson)
    {
        var profile = _dir.Write("profile.json", profileJson);
        var answerFile = _dir.File("out/autounattend.xml");

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--out", answerFile);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Empty(AnswerFileSchema.Validate(answerFile));
        return await File.ReadAllTextAsync(answerFile, TestContext.Current.CancellationToken);
    }
}
