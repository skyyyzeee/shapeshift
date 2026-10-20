using System.Diagnostics;

namespace Shapeshift.Engine.Images;

/// <summary>
/// ISO, подключённый средствами Windows только для чтения и без буквы диска — чтобы wimlib мог открыть
/// install.wim прямо в образе, без копирования гигабайт. Права администратора не нужны (как двойной щелчок
/// по ISO в Проводнике). Если образ уже был подключён, путь берётся у подключения, а отключать его не будем.
/// </summary>
public sealed class IsoMount : IDisposable
{
    readonly string _isoPath;
    readonly bool _mountedByUs;

    IsoMount(string isoPath, string root, bool mountedByUs)
    {
        _isoPath = isoPath;
        Root = root;
        _mountedByUs = mountedByUs;
    }

    /// <summary>Корень тома вида <c>\\?\Volume{…}\</c>.</summary>
    public string Root { get; }

    public static IsoMount Mount(string isoPath)
    {
        var full = Path.GetFullPath(isoPath);
        var existing = PowerShell($"(Get-DiskImage -ImagePath {Quote(full)} | Where-Object Attached | Get-Volume).Path");
        if (IsVolume(existing))
            return new IsoMount(full, existing, mountedByUs: false);

        var root = PowerShell(
            $"(Mount-DiskImage -ImagePath {Quote(full)} -Access ReadOnly -StorageType ISO -NoDriveLetter -PassThru | Get-Volume).Path");
        if (!IsVolume(root))
            throw new IOException($"Windows could not mount {Path.GetFileName(full)}: {root}");
        return new IsoMount(full, root, mountedByUs: true);
    }

    public void Dispose()
    {
        if (_mountedByUs)
            PowerShell($"Dismount-DiskImage -ImagePath {Quote(_isoPath)} | Out-Null");
    }

    static bool IsVolume(string path) => path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase);

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    // Подключение образов — служба хранилища Windows; её проще всего вызвать штатными командами PowerShell.
    static string PowerShell(string command)
    {
        var start = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output.Trim().Length > 0 ? output : error).Trim();
    }
}
