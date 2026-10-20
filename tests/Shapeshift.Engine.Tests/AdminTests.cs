using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Dism;
using Shapeshift.Engine.Servicing;
using Shapeshift.Engine.Tests.Support;

namespace Shapeshift.Engine.Tests;

/// <summary>
/// Офлайн-обслуживание через DISM: нужны права администратора, для удаления приложений — ещё и настоящий ISO
/// (SHAPESHIFT_TEST_ISO). Запуск из PowerShell от администратора:
/// <c>dotnet test -- --filter-trait "Category=Admin"</c>.
/// </summary>
[Trait("Category", "Admin")]
public sealed class AdminTests : IDisposable
{
    static readonly string? RealIso = Environment.GetEnvironmentVariable("SHAPESHIFT_TEST_ISO");

    // Короткий путь: DISM и IMAPI упираются в предел длины пути.
    readonly string _root = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "sst", Guid.NewGuid().ToString("N")[..6]);

    public AdminTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Removes_apps_from_a_real_image()
    {
        var iso = RequireRealIsoAndElevation();
        var output = Path.Combine(_root, "apps.iso");
        var profile = WriteProfile(iso, output, new[] { "Microsoft.BingNews", "Clipchamp.Clipchamp", "Microsoft.GetHelp", "Contoso.NotThere" });

        var result = await CliRun.RunAsync("build", "--profile", profile, "--workspace", Path.Combine(_root, "ws"));

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error + result.Output);
        Assert.Contains("Removed Microsoft.BingNews", result.Output);
        Assert.Contains("warning: Contoso.NotThere is not in image 1; skipped.", result.Output);

        var apps = await CliRun.RunAsync("apps", "list", "--iso", output, "--json");
        Assert.True(apps.ExitCode == Cli.ExitCodes.Success, apps.Error);
        using var json = JsonDocument.Parse(apps.Output);
        var names = json.RootElement.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("Microsoft.BingNews", names);
        Assert.DoesNotContain("Clipchamp.Clipchamp", names);
        Assert.DoesNotContain("Microsoft.GetHelp", names);
        Assert.Contains("Microsoft.Paint", names);
        Assert.Empty(OurMounts());
    }

    // Отмена после удаления первого приложения — изменения в образе есть, но не сохраняются.
    [Fact]
    public Task Cancel_while_mounted_unmounts_without_saving() => CancelOnLogAsync("Removed ");

    // Отмена посреди монтирования — DISM прерывает его, смонтированным ничего не остаётся.
    [Fact]
    public Task Cancel_while_mounting_leaves_nothing_mounted() => CancelOnLogAsync("Mounting image ");

    async Task CancelOnLogAsync(string logPrefix)
    {
        var iso = RequireRealIsoAndElevation();
        var output = Path.Combine(_root, "cancel.iso");
        var profile = WriteProfile(iso, output, new[] { "Microsoft.BingNews", "Microsoft.GetHelp" });
        var ct = TestContext.Current.CancellationToken;
        var name = "sst-" + Guid.NewGuid().ToString("N");

        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var build = CliRun.RunAsync("build", "--profile", profile, "--workspace", Path.Combine(_root, "ws"), "--pipe", name);
        await server.WaitForConnectionAsync(ct);

        var log = new StringBuilder();
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            log.AppendLine(line);
            using var doc = JsonDocument.Parse(line);
            var message = doc.RootElement;
            if (message.GetProperty("type").GetString() == "log" &&
                message.GetProperty("text").GetString()!.StartsWith(logPrefix, StringComparison.Ordinal))
            {
                await server.WriteAsync(Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}\n"), ct);
                await server.FlushAsync(ct);
            }
            if (message.GetProperty("type").GetString() == "exit")
                break;
        }

        var result = await build;
        Assert.True(result.ExitCode == Cli.ExitCodes.Cancelled, log.ToString());
        Assert.False(File.Exists(output));
        Assert.Empty(OurMounts());
    }

    [Fact]
    public async Task Cleanup_unmounts_a_stale_shapeshift_mount()
    {
        Assert.SkipUnless(DismServicing.IsElevated, "Needs administrator rights.");
        var wim = Path.Combine(_root, "install.wim");
        new FakeWindowsIso().WriteInstallWim(wim, Path.Combine(_root, "content"));
        var build = Path.Combine(_root, "b");
        var mount = Path.Combine(build, "mnt");
        Directory.CreateDirectory(mount);
        File.WriteAllText(Path.Combine(build, DismServicing.MountMarker), wim);

        // Как после вылета: смонтировано, процесс «умер».
        DismApi.Initialize(DismLogLevel.LogErrors);
        try { DismApi.MountImage(wim, mount, 1, readOnly: false); }
        finally { DismApi.Shutdown(); }
        Assert.Single(OurMounts());

        var result = await CliRun.RunAsync("cleanup");

        Assert.True(result.ExitCode == Cli.ExitCodes.Success, result.Error);
        Assert.Contains("ok: 1 stale mount(s) removed.", result.Output);
        Assert.Empty(OurMounts());
    }

    List<string> OurMounts()
    {
        DismApi.Initialize(DismLogLevel.LogErrors);
        try
        {
            return DismApi.GetMountedImages()
                .Where(m => m.MountPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.MountPath)
                .ToList();
        }
        finally
        {
            DismApi.Shutdown();
        }
    }

    string WriteProfile(string iso, string output, string[] removeApps)
    {
        var path = Path.Combine(_root, "profile.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            editions = new[] { "Professional" },
            removeApps,
            source = new { iso },
            output = new { iso = output },
        }));
        return path;
    }

    static string RequireRealIsoAndElevation()
    {
        Assert.SkipUnless(DismServicing.IsElevated, "Needs administrator rights.");
        Assert.SkipWhen(string.IsNullOrEmpty(RealIso), "SHAPESHIFT_TEST_ISO is not set.");
        return RealIso!;
    }
}
