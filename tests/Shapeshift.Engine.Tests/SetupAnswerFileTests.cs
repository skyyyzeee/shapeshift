using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Раздел профиля <c>setup</c>: пользователь, имя компьютера, OOBE, разметка диска.</summary>
public sealed class SetupAnswerFileTests : IDisposable
{
    static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";

    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task Fully_unattended_setup() => SnapshotOf("""
        {
          "formatVersion": 1,
          "bypass": { "tpm": true, "secureBoot": true, "ram": true, "cpu": true },
          "setup": {
            "user": { "name": "Alex", "password": { "mode": "store", "value": "s3cret!" }, "autoLogon": true },
            "computerName": "ALEX-PC",
            "skipOobe": true,
            "bypassOnlineAccount": true,
            "wipeDisk0": true
          }
        }
        """);

    [Fact]
    public Task User_who_sets_the_password_at_first_sign_in() => SnapshotOf("""
        {
          "formatVersion": 1,
          "setup": { "user": { "name": "Мария Иванова" }, "skipOobe": true }
        }
        """);

    [Fact]
    public async Task Password_to_change_at_first_sign_in_is_not_in_the_answer_file()
    {
        var (doc, error) = await Generate("""
            { "formatVersion": 1, "setup": { "user": { "name": "Alex" } } }
            """);

        var account = doc.Descendants(U + "LocalAccount").Single();
        Assert.Equal("", account.Element(U + "Password")!.Element(U + "Value")!.Value);
        Assert.Empty(doc.Descendants(U + "AutoLogon"));

        var commands = doc.Descendants(U + "CommandLine").Select(c => c.Value).ToList();
        Assert.Equal(["cmd.exe /c net user \"Alex\" /logonpasswordchg:yes", "shutdown.exe /l"], commands);
        Assert.Equal("", error);
    }

    [Fact]
    public async Task Stored_password_is_encoded_and_warned_about()
    {
        var (doc, error) = await Generate("""
            { "formatVersion": 1, "setup": { "user": { "name": "Alex", "password": { "mode": "store", "value": "s3cret!" }, "autoLogon": true } } }
            """);

        Assert.DoesNotContain("s3cret!", doc.ToString());
        foreach (var password in doc.Descendants(U + "Password"))
        {
            Assert.Equal("false", password.Element(U + "PlainText")!.Value);
            var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(password.Element(U + "Value")!.Value));
            Assert.Equal("s3cret!Password", decoded);
        }
        Assert.Equal(2, doc.Descendants(U + "Password").Count()); // учётная запись и автовход
        Assert.Contains("warning: the user's password is stored in the answer file", error);
        Assert.Empty(doc.Descendants(U + "FirstLogonCommands"));
    }

    [Fact]
    public async Task Disk_is_left_alone_unless_wiping_is_asked_for()
    {
        var (untouched, quiet) = await Generate("""
            { "formatVersion": 1, "setup": { "skipOobe": true } }
            """);
        var (wiped, warned) = await Generate("""
            { "formatVersion": 1, "setup": { "wipeDisk0": true } }
            """);

        Assert.Empty(untouched.Descendants(U + "DiskConfiguration"));
        Assert.Equal("", quiet);
        Assert.Equal("true", wiped.Descendants(U + "WillWipeDisk").Single().Value);
        Assert.Equal("4", wiped.Descendants(U + "InstallTo").Single().Element(U + "PartitionID")!.Value);
        Assert.Contains("warning: the installation will erase disk 0", warned);
    }

    [Theory]
    [InlineData("""{ "skipOobe": true }""", "HideEULAPage", "BypassNRO")]
    [InlineData("""{ "bypassOnlineAccount": true }""", "BypassNRO", "HideEULAPage")]
    [InlineData("""{ "computerName": "OFFICE-7" }""", "ComputerName", "OOBE")]
    [InlineData("""{ "user": { "name": "Alex" } }""", "HideLocalAccountScreen", "HideEULAPage")]
    public async Task Each_option_is_independent(string setup, string present, string absent)
    {
        var (doc, _) = await Generate($$"""{ "formatVersion": 1, "setup": {{setup}} }""");

        var xml = doc.ToString();
        Assert.Contains(present, xml);
        Assert.DoesNotContain(absent, xml);
    }

    [Theory]
    [InlineData("""{ "user": { "name": "" } }""", "user name is empty")]
    [InlineData("""{ "user": { "name": "averyveryverylongusername" } }""", "longer than 20")]
    [InlineData("""{ "user": { "name": "a%b" } }""", "forbidden character")]
    [InlineData("""{ "user": { "name": "a\"b" } }""", "forbidden character")]
    [InlineData("""{ "user": { "name": " alex" } }""", "start or end with a space")]
    [InlineData("""{ "user": { "name": "..." } }""", "only of dots")]
    [InlineData("""{ "user": { "name": "administrator" } }""", "built-in account")]
    [InlineData("""{ "user": { "name": "Alex", "password": { "mode": "secret" } } }""", "unknown mode 'secret'")]
    [InlineData("""{ "user": { "name": "Alex", "password": { "value": "x" } } }""", "only used with mode 'store'")]
    [InlineData("""{ "user": { "name": "Alex", "password": { "mode": "store" } } }""", "password is empty")]
    [InlineData("""{ "user": { "name": "Alex", "autoLogon": true } }""", "autoLogon' needs password mode 'store'")]
    [InlineData("""{ "computerName": "ABCDEFGHIJKLMNOP" }""", "longer than 15")]
    [InlineData("""{ "computerName": "МОЙ-ПК" }""", "only Latin letters")]
    [InlineData("""{ "computerName": "12345" }""", "only of digits")]
    [InlineData("""{ "wipeDisk": true }""", "wipeDisk")]
    public async Task Invalid_setup_is_rejected(string setup, string expectedError)
    {
        var profile = _dir.Write("profile.json", $$"""{ "formatVersion": 1, "setup": {{setup}} }""");
        var answerFile = _dir.File("autounattend.xml");

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--out", answerFile);

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
        Assert.False(File.Exists(answerFile));
    }

    async Task SnapshotOf(string profileJson, [CallerMemberName] string testName = "")
    {
        var (doc, _) = await Generate(profileJson);
        Snapshot.Match(await File.ReadAllTextAsync(_dir.File("autounattend.xml"), TestContext.Current.CancellationToken), "xml", testName: testName);
        Assert.NotNull(doc);
    }

    async Task<(XDocument Doc, string Error)> Generate(string profileJson)
    {
        var profile = _dir.Write("profile.json", profileJson);
        var answerFile = _dir.File("autounattend.xml");

        var result = await CliRun.RunAsync("answerfile", "--profile", profile, "--out", answerFile);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Empty(AnswerFileSchema.Validate(answerFile));
        return (XDocument.Load(answerFile), result.Error);
    }
}
