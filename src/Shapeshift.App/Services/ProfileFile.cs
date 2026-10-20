using System.Text.Json;

namespace Shapeshift.App.Services;

/// <summary>
/// Выборы мастера → профиль для движка (тот же формат, что принимает CLI). Пока в мастере только
/// источник и результат; остальные шаги добавят свои разделы.
/// </summary>
public static class ProfileFile
{
    static readonly string Folder = Path.Combine(Path.GetTempPath(), "Shapeshift");

    public static string Write(string sourceIso, string outputIso)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"profile-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            source = new { iso = sourceIso },
            output = new { iso = outputIso },
        }));
        return path;
    }

    public static void TryDelete(string? path)
    {
        try
        {
            if (path is not null)
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
