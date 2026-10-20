using Shapeshift.Core;

namespace Shapeshift.Engine.Programs;

/// <summary>Программа в образе: для своего установщика — папка в образе, файлы и что запускать.</summary>
/// <param name="Folder">Папка программы внутри Programs (01, 02…); null — для winget.</param>
/// <param name="Files">Файлы: откуда взять и путь внутри папки программы.</param>
/// <param name="Launch">Что запустить — путь внутри папки программы; null — для winget.</param>
public sealed record PayloadItem(ProgramSpec Program, string? Folder, IReadOnlyList<PayloadFile> Files, string? Launch);

public sealed record PayloadFile(string Source, string RelativePath, long Bytes);

/// <summary>
/// Что сборка положит в образ ради программ профиля. Программы ставятся при первом входе сценарием
/// <see cref="ScriptName"/>: Windows Setup копирует <c>sources\$OEM$\$$</c> в папку Windows (ADR-0008).
/// </summary>
public sealed record ProgramPayload(IReadOnlyList<PayloadItem> Items)
{
    /// <summary>Папка сценария и установщиков на установочном носителе.</summary>
    public const string MediaFolder = @"sources\$OEM$\$$\Setup\Shapeshift";

    /// <summary>Та же папка в установленной Windows.</summary>
    public const string TargetFolder = @"%WINDIR%\Setup\Shapeshift";

    public const string ScriptName = "install-programs.ps1";
    public const string ProgramsFolder = "Programs";

    public static ProgramPayload Empty { get; } = new([]);

    public bool IsEmpty => Items.Count == 0;

    public long Bytes => Items.Sum(i => i.Files.Sum(f => f.Bytes));

    public int FileCount => Items.Sum(i => i.Files.Count);

    /// <summary>Самый длинный путь внутри носителя среди файлов программ (0 — файлов нет).</summary>
    public int LongestMediaPath => Items
        .Where(i => i.Folder is not null)
        .SelectMany(i => i.Files.Select(f => Path.Combine(MediaFolder, ProgramsFolder, i.Folder!, f.RelativePath).Length))
        .DefaultIfEmpty(Path.Combine(MediaFolder, ScriptName).Length)
        .Max();

    /// <summary>Проверяет пути своих установщиков и описывает их файлы. Ошибки — по одной на программу.</summary>
    public static (ProgramPayload Payload, IReadOnlyList<string> Problems) Inspect(IReadOnlyList<ProgramSpec> programs)
    {
        var items = new List<PayloadItem>();
        var problems = new List<string>();
        var number = 0;
        foreach (var program in programs)
        {
            if (program is WingetProgram)
            {
                items.Add(new PayloadItem(program, null, [], null));
                continue;
            }

            var local = (LocalProgram)program;
            var folder = (++number).ToString("00");
            var where = $"program '{local.Name}'";
            if (File.Exists(local.Path))
            {
                if (local.Run is not null)
                    problems.Add($"{where}: 'run' is only for a folder, but '{local.Path}' is a file.");
                else if (!ProgramRules.Runnable.Contains(Path.GetExtension(local.Path).ToLowerInvariant()))
                    problems.Add($"{where}: '{Path.GetFileName(local.Path)}' cannot be run; expected {string.Join(", ", ProgramRules.Runnable)}.");
                else
                {
                    var name = Path.GetFileName(local.Path);
                    items.Add(new PayloadItem(program, folder, [new PayloadFile(local.Path, name, new FileInfo(local.Path).Length)], name));
                }
            }
            else if (Directory.Exists(local.Path))
            {
                if (local.Run is null)
                    problems.Add($"{where}: '{local.Path}' is a folder; say what to run in it with 'run'.");
                else if (!File.Exists(Path.Combine(local.Path, local.Run)))
                    problems.Add($"{where}: '{local.Run}' is not in '{local.Path}'.");
                else
                {
                    var files = Directory.EnumerateFiles(local.Path, "*", SearchOption.AllDirectories)
                        .Select(f => new PayloadFile(f, Path.GetRelativePath(local.Path, f), new FileInfo(f).Length))
                        .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    items.Add(new PayloadItem(program, folder, files, local.Run.Replace('/', '\\')));
                }
            }
            else
            {
                problems.Add($"{where}: '{local.Path}' does not exist.");
            }
        }
        return (new ProgramPayload(items), problems);
    }
}
