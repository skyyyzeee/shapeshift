using System.Text.Json;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>
/// .NET Framework 3.5 включается при сборке офлайн: из sources\sxs своего ISO (без интернета) или из возможностей
/// выпуска UUP. Сама установка пакета DISM — в стенде (нужны права администратора и настоящий образ).
/// </summary>
public sealed class DotNet35Tests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    string Workspace => _dir.File("ws");

    string Output => _dir.File("out.iso");

    void IsoProfile(string iso) => _dir.Write("profile.json", $$"""
        {
          "formatVersion": 1,
          "dotNet35": true,
          "source": { "iso": {{JsonSerializer.Serialize(iso)}} },
          "output": { "iso": {{JsonSerializer.Serialize(Output)}} }
        }
        """);

    Task<CliResult> DryRun(FakeWindowsUpdate wu, params string[] extra) =>
        CliRun.RunAsync(wu.Host(Workspace), ["build", "--profile", _dir.File("profile.json"), "--workspace", Workspace, "--dry-run", .. extra]);

    [Fact]
    public async Task Own_iso_enables_it_from_its_own_sxs_folder_without_the_internet()
    {
        IsoProfile(new FakeWindowsIso { WithNetFx3 = true }.WriteTo(_dir.Path));
        var wu = new FakeWindowsUpdate { Offline = true };

        var result = await DryRun(wu);

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Assert.Contains("Enable .NET Framework 3.5", result.Output);
        Assert.DoesNotContain("Download files", result.Output);
        Assert.Empty(wu.Requests);
    }

    [Fact]
    public async Task Own_iso_without_the_package_is_rejected_before_building()
    {
        IsoProfile(new FakeWindowsIso().WriteTo(_dir.Path));

        var result = await DryRun(new FakeWindowsUpdate { Offline = true });

        Assert.Equal(Cli.ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("the image has no .NET Framework 3.5 package", result.Error);
    }

    [Fact]
    public async Task Enabling_it_needs_administrator_rights()
    {
        Assert.SkipWhen(Servicing.DismServicing.IsElevated, "Runs only without administrator rights.");
        IsoProfile(new FakeWindowsIso { WithNetFx3 = true }.WriteTo(_dir.Path));

        var result = await CliRun.RunAsync("build", "--profile", _dir.File("profile.json"), "--workspace", Workspace);

        Assert.Equal(Cli.ExitCodes.NeedsElevation, result.ExitCode);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task Uup_image_gets_the_package_from_the_release_before_the_update()
    {
        var release = new FakeUupRelease(_dir.Path);
        _dir.Write("profile.json", $$"""
            {
              "formatVersion": 1,
              "dotNet35": true,
              "editions": ["Professional"],
              "source": { "uup": { "channel": "windows11", "language": "ru-RU" } },
              "output": { "iso": {{JsonSerializer.Serialize(Output)}} }
            }
            """);
        var wu = new FakeWindowsUpdate { Release = release };

        var result = await DryRun(wu);
        var plan = await DryRun(wu, "--json");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        var steps = result.Output.Split('\n').Select(l => l.Trim()).ToList();
        Assert.True(steps.FindIndex(l => l.Contains("Enable .NET Framework 3.5")) < steps.FindIndex(l => l.Contains("Integrate updates")));
        using var json = JsonDocument.Parse(plan.Output);
        Assert.Contains(json.RootElement.GetProperty("steps").EnumerateArray(), s => s.GetProperty("kind").GetString() == "EnableDotNet35");
    }

    [Fact]
    public async Task Uup_download_includes_the_package()
    {
        var release = new FakeUupRelease(_dir.Path);
        var wu = new FakeWindowsUpdate { Release = release };
        async Task<long> DownloadBytes(bool dotnet35)
        {
            _dir.Write("profile.json", $$"""
                {
                  "formatVersion": 1, "latestUpdate": false, "dotNet35": {{(dotnet35 ? "true" : "false")}}, "editions": ["Professional"],
                  "source": { "uup": { "channel": "windows11", "language": "ru-RU" } },
                  "output": { "iso": {{JsonSerializer.Serialize(Output)}} }
                }
                """);
            var plan = await DryRun(wu, "--json");
            Assert.True(plan.ExitCode == Cli.ExitCodes.Success, plan.Output + plan.Error);
            using var json = JsonDocument.Parse(plan.Output);
            return json.RootElement.GetProperty("steps").EnumerateArray()
                .First(s => s.GetProperty("kind").GetString() == "DownloadFiles").GetProperty("bytes").GetInt64();
        }

        Assert.Equal(await DownloadBytes(false) + release.Files[FakeUupRelease.NetFx3].Length, await DownloadBytes(true));
    }
}
