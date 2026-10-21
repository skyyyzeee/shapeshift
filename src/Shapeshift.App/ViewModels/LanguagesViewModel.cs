using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;
using Shapeshift.Core;

namespace Shapeshift.App.ViewModels;

/// <summary>Добавленный язык интерфейса.</summary>
public sealed record LanguageItem(string Code, string Label);

/// <summary>
/// Языки интерфейса на шаге «Редакции и языки»: что уже есть в образе, какие добавить (языковые пакеты из
/// Windows Update ставит DISM при сборке) и какой сделать языком Windows. Список доступных языков — от движка:
/// для своего ISO он ищет выпуск той же базовой сборки, поэтому нужен интернет.
/// </summary>
public sealed partial class LanguagesViewModel(EngineClient engine) : ObservableObject
{
    string? _isoPath;
    string? _updateId;
    string? _loadedFor;
    IReadOnlyList<string> _available = [];
    LanguageOptions? _pending;

    /// <summary>Языки образа (для UUP — выбранный язык выпуска).</summary>
    public IReadOnlyList<string> Installed { get; private set; } = [];

    [ObservableProperty]
    public partial string InstalledText { get; private set; } = "";

    public ObservableCollection<LanguageItem> Added { get; } = [];

    public ObservableCollection<Choice> AddChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial Choice? ToAdd { get; set; }

    public bool CanAdd => ToAdd is not null;

    public ObservableCollection<Choice> DisplayChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? Display { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>Есть список доступных языков — можно выбирать.</summary>
    public bool CanChoose => !IsLoading && _available.Count > 0;

    public bool HasAdded => Added.Count > 0;

    public static string Label(string code)
    {
        try
        {
            return $"{CultureInfo.GetCultureInfo(code).DisplayName} — {code}";
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Источник сменился: свой ISO или выпуск UUP и языки образа. Добавленные, которых уже нет смысла добавлять, убираются.</summary>
    public void SetSource(string? isoPath, string? updateId, IReadOnlyList<string> installed)
    {
        _isoPath = isoPath;
        _updateId = updateId;
        Installed = installed;
        InstalledText = string.Join(", ", installed.Select(Label));
        foreach (var item in Added.Where(a => installed.Contains(a.Code, StringComparer.OrdinalIgnoreCase)).ToList())
            Added.Remove(item);
        if (_pending is { } pending && installed.Count > 0)
        {
            _pending = null;
            Apply(pending);
        }
        Rebuild();
    }

    /// <summary>Доступные языки для текущего источника (один раз на источник).</summary>
    public async Task LoadAsync()
    {
        var key = _isoPath ?? _updateId;
        if (key is null || key == _loadedFor)
            return;

        IsLoading = true;
        Error = null;
        try
        {
            var result = await engine.LanguagesAsync(_isoPath, _isoPath is null ? _updateId : null, AppSettings.Current.EffectiveWorkspace);
            _available = result.Available;
            Error = result.Error;
            _loadedFor = result.Error is null ? key : null;
        }
        finally
        {
            IsLoading = false;
            Rebuild();
            OnPropertyChanged(nameof(CanChoose));
        }
    }

    public void AddSelected()
    {
        if (ToAdd is not { } choice || Added.Any(a => a.Code == choice.Value))
            return;
        Added.Add(new LanguageItem(choice.Value, choice.Label));
        Rebuild();
    }

    public void Remove(LanguageItem item)
    {
        Added.Remove(item);
        Rebuild();
    }

    /// <summary>Выбор из профиля; если образ ещё не прочитан — применится, когда появятся его языки.</summary>
    public void ApplyProfile(LanguageOptions? languages)
    {
        if (Installed.Count == 0)
        {
            _pending = languages;
            return;
        }
        Apply(languages);
        Rebuild();
    }

    void Apply(LanguageOptions? languages)
    {
        Added.Clear();
        foreach (var code in languages?.Add ?? [])
        {
            if (!Installed.Contains(code, StringComparer.OrdinalIgnoreCase))
                Added.Add(new LanguageItem(code, Label(code)));
        }
        _display = languages?.Display;
    }

    string? _display;

    // Добавить можно доступный язык, которого нет ни в образе, ни среди добавленных; язык Windows — любой из них.
    void Rebuild()
    {
        var display = _display ?? Display?.Value;
        _display = null;

        AddChoices.Clear();
        foreach (var code in _available.Where(c => !Installed.Contains(c, StringComparer.OrdinalIgnoreCase) && Added.All(a => a.Code != c)))
            AddChoices.Add(new Choice(code, Label(code)));
        ToAdd = null;

        DisplayChoices.Clear();
        foreach (var code in Installed.Concat(Added.Select(a => a.Code)))
            DisplayChoices.Add(new Choice(code, Label(code)));
        Display = DisplayChoices.FirstOrDefault(c => string.Equals(c.Value, display, StringComparison.OrdinalIgnoreCase)) ?? DisplayChoices.FirstOrDefault();
        OnPropertyChanged(nameof(HasAdded));
    }

    /// <summary>Для профиля: null — языки как в образе.</summary>
    public LanguagesDraft? ToDraft()
    {
        var display = Display?.Value is { } value && !string.Equals(value, Installed.FirstOrDefault(), StringComparison.OrdinalIgnoreCase) ? value : null;
        return Added.Count == 0 && display is null ? null : new LanguagesDraft(Added.Select(a => a.Code).ToList(), display);
    }

    public IEnumerable<string> Describe()
    {
        if (Added.Count > 0)
            yield return Text.Format("LanguagesSummary_Add", string.Join(", ", Added.Select(a => a.Label)));
        if (ToDraft()?.Display is { } display)
            yield return Text.Format("LanguagesSummary_Display", Label(display));
    }
}
