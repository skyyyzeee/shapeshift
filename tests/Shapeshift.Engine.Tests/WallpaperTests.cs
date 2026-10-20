using System.Text.Json;
using System.Xml.Linq;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>Обои рабочего стола: картинка в образе, закреплённые политиками или обычные.</summary>
public sealed class WallpaperTests : IDisposable
{
    const string Target = @"C:\Windows\Web\Wallpaper\Shapeshift\wallpaper.png";

    readonly TempDir _dir = new();
    readonly byte[] _picture = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    public WallpaperTests() => File.WriteAllBytes(_dir.File("corp.png"), _picture);

    public void Dispose() => _dir.Dispose();

    string Profile(string wallpaper, string? source = null) => _dir.Write("profile.json", $$"""
        {
          {{(source is null ? "" : $"\"source\": {{ \"iso\": {JsonSerializer.Serialize(source)} }}, \"output\": {{ \"iso\": {JsonSerializer.Serialize(_dir.File("out.iso"))} }},")}}
          "formatVersion": 1,
          "wallpaper": {{wallpaper}}
        }
        """);

    async Task<IReadOnlyList<string>> AnswerFileCommands(string wallpaper)
    {
        Profile(wallpaper);
        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--out", _dir.File("a.xml"));
        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Empty(AnswerFileSchema.Validate(_dir.File("a.xml")));
        // Команды установки — в <Path>, первого входа — в <CommandLine>.
        return XDocument.Load(_dir.File("a.xml")).Descendants().Where(e => e.Name.LocalName is "CommandLine" or "Path").Select(e => e.Value).ToList();
    }

    [Fact]
    public async Task Locked_wallpaper_is_enforced_by_policies_for_everyone()
    {
        var commands = await AnswerFileCommands("""{ "path": "corp.png" }""");

        Assert.Contains($@"reg.exe add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP"" /v DesktopImagePath /t REG_SZ /d ""{Target}"" /f", commands);
        Assert.Contains(@"reg.exe add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP"" /v DesktopImageStatus /t REG_DWORD /d 1 /f", commands);
        // Профиль по умолчанию — все новые пользователи; стиль «заполнить» по умолчанию.
        Assert.Contains($@"reg.exe add ""HKU\DefaultUser\Software\Microsoft\Windows\CurrentVersion\Policies\System"" /v Wallpaper /t REG_SZ /d ""{Target}"" /f", commands);
        Assert.Contains(@"reg.exe add ""HKU\DefaultUser\Software\Microsoft\Windows\CurrentVersion\Policies\System"" /v WallpaperStyle /t REG_SZ /d ""4"" /f", commands);
        Assert.Contains(@"reg.exe add ""HKU\DefaultUser\Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop"" /v NoChangingWallPaper /t REG_DWORD /d 1 /f", commands);
    }

    [Fact]
    public async Task Unlocked_wallpaper_is_just_set_for_the_first_user()
    {
        var commands = await AnswerFileCommands("""{ "path": "corp.png", "locked": false, "style": "tile" }""");

        Assert.DoesNotContain(commands, c => c.Contains("Policies") || c.Contains("PersonalizationCSP"));
        Assert.Contains($@"reg.exe add ""HKCU\Control Panel\Desktop"" /v Wallpaper /t REG_SZ /d ""{Target}"" /f", commands);
        Assert.Contains(@"reg.exe add ""HKCU\Control Panel\Desktop"" /v TileWallpaper /t REG_SZ /d ""1"" /f", commands);
        Assert.Contains("RUNDLL32.EXE user32.dll,UpdatePerUserSystemParameters", commands);
    }

    [Fact]
    public async Task Build_puts_the_picture_where_the_settings_point()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        Profile("""{ "path": "corp.png", "style": "fit" }""", iso);

        var result = await CliRun.RunAsync("build", "--profile", _dir.File("profile.json"), "--workspace", _dir.File("ws"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Equal(_picture, IsoContents.ReadFile(_dir.File("out.iso"), @"sources\$OEM$\$$\Web\Wallpaper\Shapeshift\wallpaper.png"));
        Assert.Contains("WallpaperStyle /t REG_SZ /d \"3\"", System.Text.Encoding.UTF8.GetString(IsoContents.ReadFile(_dir.File("out.iso"), "autounattend.xml")));
    }

    [Fact]
    public async Task Missing_picture_stops_the_build()
    {
        var iso = new FakeWindowsIso().WriteTo(_dir.Path);
        Profile("""{ "path": "missing.jpg" }""", iso);

        var result = await CliRun.RunAsync("build", "--profile", _dir.File("profile.json"), "--workspace", _dir.File("ws"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("missing.jpg' does not exist", result.Error);
    }

    [Theory]
    [InlineData("""{ "path": "corp.gif" }""", "must be a picture")]
    [InlineData("""{ "path": "corp.png", "style": "zoom" }""", "unknown style 'zoom'")]
    [InlineData("""{ }""", "'wallpaper.path' is empty")]
    public async Task Invalid_wallpaper_is_rejected(string wallpaper, string expected)
    {
        Profile(wallpaper);

        var result = await CliRun.RunAsync("answerfile", "--profile", _dir.File("profile.json"), "--out", _dir.File("a.xml"));

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(expected, result.Error);
    }
}
