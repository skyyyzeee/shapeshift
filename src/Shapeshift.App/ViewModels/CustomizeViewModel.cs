using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

/// <summary>Один твик каталога на экране.</summary>
public sealed partial class TweakItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool Advanced { get; init; }
    public required IReadOnlyList<string> Conflicts { get; init; }

    /// <summary>Применим к выбранному образу (или образ неизвестен).</summary>
    public required bool IsApplicable { get; init; }

    /// <summary>Почему не применим — для подписи под твиком.</summary>
    public string? Reason { get; init; }

    public bool HasReason => Reason is not null;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}

public sealed class TweakCategoryItem
{
    public required string Name { get; init; }
    public ObservableCollection<TweakItem> Tweaks { get; } = [];
}

/// <summary>
/// Шаг «Настройка»: экран строится из каталога, который отдаёт движок, — новый твик в каталоге
/// появляется здесь без пересборки окна. Категория «Обход ограничений» живёт на шаге «Установка».
/// </summary>
public sealed partial class CustomizeViewModel(EngineClient engine) : ObservableObject
{
    const string SetupCategory = "bypass";

    string? _loadedFor;
    readonly HashSet<string> _remembered = [];

    public ObservableCollection<TweakCategoryItem> Categories { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool AdvancedMode { get; set; }

    partial void OnAdvancedModeChanged(bool value) => ApplyVisibility();

    IEnumerable<TweakItem> All => Categories.SelectMany(c => c.Tweaks);

    /// <summary>Выбранные применимые твики — для профиля.</summary>
    public IReadOnlyList<string> SelectedTweaks => All.Where(t => t.IsOn && t.IsApplicable && (AdvancedMode || !t.Advanced)).Select(t => t.Id).ToList();

    public IEnumerable<string> SelectedNames => All.Where(t => t.IsOn && t.IsApplicable && (AdvancedMode || !t.Advanced)).Select(t => t.Name);

    /// <summary>Твики из профиля, которые не подошли к образу или которых нет в каталоге.</summary>
    public ObservableCollection<string> Dropped { get; } = [];

    [ObservableProperty]
    public partial bool HasDropped { get; private set; }

    /// <summary>В профиле есть твики продвинутого режима — мастер его включит.</summary>
    public event EventHandler? AdvancedTweaksSelected;

    /// <summary>Выбор из профиля: применится при следующей загрузке каталога (в нём же видно, что не подошло).</summary>
    public void ApplySelection(IEnumerable<string> ids)
    {
        _remembered.Clear();
        foreach (var id in ids.Where(id => !id.StartsWith(SetupCategory + ".", StringComparison.Ordinal)))
            _remembered.Add(id);
        Categories.Clear();
        _loadedFor = null;
        _reportDropped = true;
    }

    bool _reportDropped;

    /// <summary>Загружает каталог для образа; выбор сохраняется при смене образа.</summary>
    public async Task LoadAsync(string? isoPath)
    {
        if (isoPath == _loadedFor && Categories.Count > 0)
            return;

        foreach (var tweak in All.Where(t => t.IsOn))
            _remembered.Add(tweak.Id);

        IsLoading = true;
        Error = null;
        try
        {
            using var catalog = await engine.CatalogAsync(isoPath);
            if (catalog is null)
            {
                Error = Text.Get("CatalogError");
                return;
            }
            Fill(catalog.RootElement);
            _loadedFor = isoPath;
        }
        finally
        {
            IsLoading = false;
        }
    }

    void Fill(JsonElement root)
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        string Localized(JsonElement texts) =>
            texts.TryGetProperty(language, out var t) ? t.GetString() ?? "" :
            texts.TryGetProperty("en", out var en) ? en.GetString() ?? "" : "";

        Categories.Clear();
        foreach (var category in root.GetProperty("categories").EnumerateArray())
        {
            var id = category.GetProperty("id").GetString();
            if (id == SetupCategory)
                continue;

            var item = new TweakCategoryItem { Name = Localized(category.GetProperty("name")) };
            foreach (var tweak in root.GetProperty("tweaks").EnumerateArray().Where(t => t.GetProperty("category").GetString() == id))
            {
                var applicable = tweak.GetProperty("applicable");
                var isApplicable = applicable.ValueKind != JsonValueKind.False;
                var tweakId = tweak.GetProperty("id").GetString()!;
                item.Tweaks.Add(new TweakItem
                {
                    Id = tweakId,
                    Name = Localized(tweak.GetProperty("name")),
                    Description = Localized(tweak.GetProperty("description")),
                    Advanced = tweak.GetProperty("advanced").GetBoolean(),
                    Conflicts = tweak.GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()!).ToList(),
                    IsApplicable = isApplicable,
                    Reason = isApplicable ? null : DescribeReleases(tweak.GetProperty("releases")),
                    IsOn = isApplicable && _remembered.Contains(tweakId),
                });
            }
            Categories.Add(item);
        }
        ApplyVisibility();

        // Только после загрузки профиля: что из него не подошло к этому образу.
        if (_reportDropped)
        {
            _reportDropped = false;
            Dropped.Clear();
            var byId = All.ToDictionary(t => t.Id);
            foreach (var id in _remembered)
            {
                if (!byId.TryGetValue(id, out var tweak))
                    Dropped.Add(id);
                else if (!tweak.IsApplicable)
                    Dropped.Add($"{tweak.Name} — {tweak.Reason}");
            }
            HasDropped = Dropped.Count > 0;
            if (All.Any(t => t.IsOn && t.Advanced))
                AdvancedTweaksSelected?.Invoke(this, EventArgs.Empty);
        }
    }

    void ApplyVisibility()
    {
        foreach (var tweak in All)
            tweak.IsVisible = AdvancedMode || !tweak.Advanced;
    }

    /// <summary>Включённые твики, с которыми конфликтует этот (отношение симметрично).</summary>
    public IReadOnlyList<TweakItem> ConflictsOf(TweakItem tweak) =>
        All.Where(other => other.IsOn && other != tweak && (tweak.Conflicts.Contains(other.Id) || other.Conflicts.Contains(tweak.Id)))
            .ToList();

    static string DescribeReleases(JsonElement releases)
    {
        var products = releases.GetProperty("products").EnumerateArray().Select(p => p.GetString()).ToList();
        var parts = new List<string>();
        if (products.Count == 1)
            parts.Add(Text.Get(products[0] == "windows11" ? "TweakOnlyWindows11" : "TweakOnlyWindows10"));
        if (releases.GetProperty("minBuild") is { ValueKind: JsonValueKind.Number } min)
            parts.Add(Text.Format("TweakMinBuild", min.GetInt32()));
        if (releases.GetProperty("maxBuild") is { ValueKind: JsonValueKind.Number } max)
            parts.Add(Text.Format("TweakMaxBuild", max.GetInt32()));
        return Text.Format("TweakNotApplicable", string.Join(", ", parts));
    }
}
