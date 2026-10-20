using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;
using Shapeshift.Core;

namespace Shapeshift.App.ViewModels;

/// <summary>Популярная программа из каталога — флажок.</summary>
public sealed partial class ProgramSuggestionItem : ObservableObject
{
    public required string WingetId { get; init; }
    public required string Title { get; init; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

/// <summary>Программа, добавленная вручную: пакет winget по id, свой установщик или папка с установщиком.</summary>
public sealed partial class ProgramItem : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>Id winget или путь — подпись под названием.</summary>
    public required string Detail { get; init; }

    public string? WingetId { get; init; }
    public string? Path { get; init; }

    /// <summary>Что запустить в папке; варианты — <see cref="RunChoices"/>.</summary>
    [ObservableProperty]
    public partial string? Run { get; set; }

    public IReadOnlyList<string> RunChoices { get; init; } = [];

    [ObservableProperty]
    public partial string? Args { get; set; }

    public bool IsLocal => Path is not null;

    public bool IsFolder => RunChoices.Count > 0;

    /// <summary>Подсказка в поле параметров — по типу установщика.</summary>
    public string ArgsHint => System.IO.Path.GetExtension(Run ?? Path ?? "").ToLowerInvariant() switch
    {
        ".msi" => Text.Get("ProgramArgsMsi"),
        ".cmd" or ".bat" or ".ps1" => Text.Get("ProgramArgsScript"),
        _ => Text.Get("ProgramArgsExe"),
    };

    partial void OnRunChanged(string? value) => OnPropertyChanged(nameof(ArgsHint));
}

/// <summary>
/// Программы, которые поставятся при первом входе: популярные из каталога (winget) и добавленные вручную.
/// Порядок установки — сначала отмеченные популярные, потом добавленные, в порядке добавления.
/// </summary>
public sealed partial class ProgramsViewModel : ObservableObject
{

    public ObservableCollection<ProgramSuggestionItem> Suggestions { get; } = [];

    public ObservableCollection<ProgramItem> Added { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddError))]
    public partial string? AddError { get; private set; }

    public bool HasAddError => AddError is not null;

    /// <summary>Популярные из каталога; выбор из профиля, пришедший раньше каталога, применяется здесь.</summary>
    public void FillSuggestions(JsonElement catalog)
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var chosen = Suggestions.Where(s => s.IsOn).Select(s => s.WingetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Suggestions.Clear();
        if (!catalog.TryGetProperty("programs", out var programs))
            return;

        foreach (var program in programs.EnumerateArray())
        {
            var id = program.GetProperty("winget").GetString()!;
            var title = program.GetProperty("title");
            Suggestions.Add(new ProgramSuggestionItem
            {
                WingetId = id,
                Title = (title.TryGetProperty(language, out var t) ? t.GetString() : null)
                    ?? (title.TryGetProperty("en", out var en) ? en.GetString() : null) ?? id,
                IsOn = chosen.Contains(id),
            });
        }
        // Пакеты из профиля, которые оказались популярными, — флажками.
        foreach (var item in Added.Where(a => a.WingetId is not null).ToList())
        {
            if (Suggestions.FirstOrDefault(s => string.Equals(s.WingetId, item.WingetId, StringComparison.OrdinalIgnoreCase)) is { } suggestion)
            {
                suggestion.IsOn = true;
                Added.Remove(item);
            }
        }
    }

    /// <summary>Добавляет пакет winget по id; false — id не похож на идентификатор winget.</summary>
    public bool AddWinget(string id)
    {
        id = id.Trim();
        AddError = null;
        if (!ProgramRules.IsWingetId(id))
        {
            AddError = Text.Format("ProgramBadWingetId", id);
            return false;
        }
        if (Suggestions.FirstOrDefault(s => string.Equals(s.WingetId, id, StringComparison.OrdinalIgnoreCase)) is { } suggestion)
        {
            suggestion.IsOn = true;
            return true;
        }
        if (!Added.Any(a => string.Equals(a.WingetId, id, StringComparison.OrdinalIgnoreCase)))
            Added.Add(Winget(id, id));
        return true;
    }

    public void AddFile(string path)
    {
        AddError = null;
        if (Added.Any(a => a.Path is not null && a.RunChoices.Count == 0 && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        Added.Add(new ProgramItem
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Detail = path,
            Path = path,
        });
    }

    /// <summary>Папка с установщиком (Office Deployment Tool, 1С): запускаемые файлы — на выбор, setup.exe по умолчанию.</summary>
    public void AddFolder(string path)
    {
        AddError = null;
        var choices = RunnableFiles(path);
        if (choices.Count == 0)
        {
            AddError = Text.Format("ProgramNoInstallerInFolder", path);
            return;
        }
        var run = choices.FirstOrDefault(c => string.Equals(c, "setup.exe", StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        Added.Add(new ProgramItem
        {
            Name = System.IO.Path.GetFileName(path.TrimEnd('\\')),
            Detail = path,
            Path = path,
            RunChoices = choices,
            Run = run,
        });
    }

    public void Remove(ProgramItem item) => Added.Remove(item);

    /// <summary>Выбор из профиля. Пакеты winget, пока каталог не загружен, — в добавленных, на своём месте.</summary>
    public void ApplyProfile(IReadOnlyList<ProgramSpec> programs)
    {
        Added.Clear();
        foreach (var suggestion in Suggestions)
            suggestion.IsOn = false;

        foreach (var program in programs)
        {
            switch (program)
            {
                case WingetProgram winget:
                    if (Suggestions.FirstOrDefault(s => string.Equals(s.WingetId, winget.Id, StringComparison.OrdinalIgnoreCase)) is { } s)
                        s.IsOn = true;
                    else
                        Added.Add(Winget(winget.Id, winget.Name));
                    break;
                case LocalProgram local:
                    Added.Add(new ProgramItem
                    {
                        Name = local.Name,
                        Detail = local.Path,
                        Path = local.Path,
                        RunChoices = local.Run is null ? [] : [.. RunnableFiles(local.Path).Union([local.Run], StringComparer.OrdinalIgnoreCase)],
                        Run = local.Run,
                        Args = local.Args,
                    });
                    break;
            }
        }
    }

    public IReadOnlyList<ProgramDraft> ToDraft() =>
    [
        .. Suggestions.Where(s => s.IsOn).Select(s => new ProgramDraft(s.WingetId, null, null, null, null)),

        .. Added.Select(a => a.WingetId is { } id
            ? new ProgramDraft(id, null, null, null, a.Name == id ? null : a.Name)
            : new ProgramDraft(null, a.Path, a.IsFolder ? a.Run : null, string.IsNullOrWhiteSpace(a.Args) ? null : a.Args.Trim(), a.Name)),
    ];

    public IEnumerable<string> SelectedNames =>
        Suggestions.Where(s => s.IsOn).Select(s => s.Title).Concat(Added.Select(a => a.Name));

    static ProgramItem Winget(string id, string name) => new() { Name = name, Detail = $"winget: {id}", WingetId = id };

    // Запускаемые файлы папки — в ней самой и на уровень глубже: дистрибутивы кладут setup.exe не глубже.
    static IReadOnlyList<string> RunnableFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 })
                .Where(f => ProgramRules.Runnable.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => System.IO.Path.GetRelativePath(folder, f))
                .OrderBy(f => f.Count(c => c == '\\'))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
