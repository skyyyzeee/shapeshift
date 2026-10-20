using System.Text.RegularExpressions;
using ManagedWimLib;

namespace Shapeshift.Engine.Images;

/// <summary>
/// Встроенные приложения редакции — из папки Program Files\WindowsApps внутри WIM, без монтирования и прав
/// администратора. Пакеты ресурсов (split.*) и фреймворки (VCLibs, UI.Xaml и т. п.) — не приложения: их
/// удалять бессмысленно и опасно. Имя приложения — имя пакета без версии; так же его видит DISM
/// (DisplayName), который удаляет приложения при сборке.
/// </summary>
public static partial class ProvisionedApps
{
    const string WindowsApps = "/Program Files/WindowsApps";

    public static IReadOnlyList<string> List(string wimPath, int imageIndex)
    {
        WimLib.EnsureInitialized();
        using var wim = Wim.OpenWim(wimPath, OpenFlags.None);
        if (!wim.DirExists(imageIndex, WindowsApps))
            return [];

        var folders = new List<string>();
        wim.IterateDirTree(imageIndex, WindowsApps, IterateDirTreeFlags.Children, (entry, _) =>
        {
            if (entry.FileName is { } name)
                folders.Add(name);
            return Wim.IterateCallbackSuccess;
        }, null);

        var packages = folders
            .Select(f => PackageFolder().Match(f))
            .Where(m => m.Success && !m.Groups["resource"].Value.StartsWith("split.", StringComparison.Ordinal))
            .GroupBy(m => m.Groups["name"].Value, StringComparer.OrdinalIgnoreCase);

        var apps = new List<string>();
        var scratch = Path.Combine(Path.GetTempPath(), "shapeshift-manifests", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var package in packages)
            {
                // Связка (~) бывает только у приложений; иначе — смотрим, не фреймворк ли это по манифесту.
                if (package.Any(m => m.Groups["resource"].Value == "~") || !IsFramework(wim, imageIndex, package.First().Value, scratch))
                    apps.Add(package.Key);
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return apps.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsFramework(Wim wim, int imageIndex, string folder, string scratch)
    {
        var manifest = $"{WindowsApps}/{folder}/AppxManifest.xml";
        if (!wim.FileExists(imageIndex, manifest))
            return false;
        var target = Path.Combine(scratch, folder);
        Directory.CreateDirectory(target);
        wim.ExtractPath(imageIndex, target, manifest, ExtractFlags.NoAcls | ExtractFlags.NoPreserveDirStructure);
        var text = File.ReadAllText(Path.Combine(target, "AppxManifest.xml"));
        return text.Contains("<Framework>true</Framework>", StringComparison.OrdinalIgnoreCase);
    }

    // Имя_Версия_Архитектура_Ресурс_Издатель: Microsoft.BingNews_4.11.6001.0_neutral_~_8wekyb3d8bbwe
    [GeneratedRegex(@"^(?<name>[^_]+)_(?<version>[0-9.]+)_(?<arch>[^_]+)_(?<resource>[^_]*)_(?<publisher>[^_]+)$")]
    private static partial Regex PackageFolder();
}
