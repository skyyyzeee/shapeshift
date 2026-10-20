using System.Text.Json;

namespace Shapeshift.App.Services;

/// <summary>
/// Настройки программы — между запусками, в %LOCALAPPDATA%\Shapeshift\settings.json.
/// Пустое значение — «по умолчанию».
/// </summary>
/// <param name="Language">"ru-RU", "en-US" или null — как в Windows.</param>
/// <param name="Workspace">Рабочая папка или null — на диске с наибольшим свободным местом.</param>
public sealed record AppSettings(string? Language, string? Workspace)
{
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shapeshift");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? Default;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Испорченный файл настроек — не повод не запускаться.
        }
        return Default;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static AppSettings Default { get; } = new(null, null);

    public static AppSettings Current { get; set; } = Load();

    /// <summary>Рабочая папка с учётом умолчания. Вычисляется заново — в файл не пишется.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveWorkspace => Workspace ?? WorkspaceFolder.Default();
}

/// <summary>Рабочая папка движка: где по умолчанию, сколько занимает, как очистить.</summary>
public static class WorkspaceFolder
{
    /// <summary>
    /// На диске с наибольшим свободным местом. Путь короткий: IMAPI не умеет пути длиннее 259 символов,
    /// и каждый символ здесь отнимается у самого глубокого файла образа.
    /// </summary>
    public static string Default()
    {
        var best = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace)
            .FirstOrDefault();

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (best is null || string.Equals(best.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shapeshift", "Workspace");
        return Path.Combine(best.RootDirectory.FullName, "Shapeshift", "Workspace");
    }

    /// <summary>Сколько занимает: после удачной сборки папка пуста, всё остальное — кэш и остатки прерванных сборок.</summary>
    public static long Size(string path)
    {
        if (!Directory.Exists(path))
            return 0;
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Удаляет содержимое папки (саму папку оставляет). false — что-то занято или нет прав.</summary>
    public static bool Clear(string path)
    {
        if (!Directory.Exists(path))
            return true;
        var ok = true;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo dir)
                    dir.Delete(recursive: true);
                else
                    entry.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }
}
