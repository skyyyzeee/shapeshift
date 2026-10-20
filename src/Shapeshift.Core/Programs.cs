using System.Text.RegularExpressions;

namespace Shapeshift.Core;

/// <summary>Программа, которая ставится сама при первом входе в установленную Windows.</summary>
/// <param name="Name">Название для журнала установки и окна.</param>
public abstract record ProgramSpec(string Name);

/// <summary>Пакет winget — ставится из интернета; без сети пропускается.</summary>
/// <param name="Id">Идентификатор пакета winget, например Telegram.TelegramDesktop.</param>
public sealed record WingetProgram(string Id, string Name) : ProgramSpec(Name);

/// <summary>Свой установщик: файл или папка копируются в образ, ставятся без интернета.</summary>
/// <param name="Path">Полный путь к файлу установщика (.exe, .msi, .cmd, .bat, .ps1) или к папке.</param>
/// <param name="Run">Для папки: что в ней запустить (путь внутри папки). Для файла — null.</param>
/// <param name="Args">Параметры тихой установки; для .msi к ним добавляется <c>/qn /norestart</c>.</param>
public sealed record LocalProgram(string Name, string Path, string? Run, string? Args) : ProgramSpec(Name);

public static partial class ProgramRules
{
    public const int MaxNameLength = 64;
    public const int MaxArgsLength = 1024;

    /// <summary>Расширения, которые сценарий установки умеет запускать.</summary>
    public static readonly string[] Runnable = [".exe", ".msi", ".cmd", ".bat", ".ps1"];

    /// <summary>Идентификатор пакета winget: Publisher.Name, допускаются цифры, «+», «_», «-».</summary>
    public static bool IsWingetId(string id) => WingetIdPattern().IsMatch(id);

    /// <summary>Строка без управляющих символов (переводов строк, табуляции) — для названий и параметров.</summary>
    public static bool IsSingleLine(string text) => !text.Any(char.IsControl);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+_-]*(\.[A-Za-z0-9+_-]+)+$")]
    private static partial Regex WingetIdPattern();
}
