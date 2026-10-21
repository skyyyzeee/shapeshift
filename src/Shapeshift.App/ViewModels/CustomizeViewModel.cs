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

/// <summary>Встроенное приложение образа; отмеченное удаляется при сборке.</summary>
public sealed partial class AppItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Title { get; init; }

    /// <summary>safe | risky | unknown (нет в каталоге).</summary>
    public required string Safety { get; init; }

    public required bool Recommended { get; init; }

    /// <summary>Предупреждение под названием — для risky и unknown.</summary>
    public string? Note { get; init; }

    public bool HasNote => Note is not null;

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

    /// <summary>Программы для установки при первом входе; популярные приходят вместе с каталогом.</summary>
    public ProgramsViewModel Programs { get; } = new();

    public WallpaperViewModel Wallpaper { get; } = new();

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

    /// <summary>В профиле есть твики или приложения продвинутого режима — мастер его включит.</summary>
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

        foreach (var tweak in All)
        {
            if (tweak.IsOn)
                _remembered.Add(tweak.Id);
            else
                _remembered.Remove(tweak.Id);
        }

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
        Programs.FillSuggestions(root);

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
        foreach (var app in Apps)
            app.IsVisible = AdvancedMode || app.Safety == "safe";
        // Скрытые элементы сетки всё равно занимают ячейки — на экране только видимые.
        ShownApps.Clear();
        foreach (var app in Apps.Where(a => a.IsVisible))
            ShownApps.Add(app);
    }

    string? _appsLoadedFor;
    readonly HashSet<string> _rememberedApps = new(StringComparer.OrdinalIgnoreCase);
    bool _reportDroppedApps;

    public ObservableCollection<AppItem> Apps { get; } = [];

    /// <summary>Приложения, видимые в текущем режиме, — для экрана.</summary>
    public ObservableCollection<AppItem> ShownApps { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingApps { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsError))]
    public partial string? AppsError { get; private set; }

    public bool HasAppsError => AppsError is not null;

    IEnumerable<AppItem> SelectedAppItems => Apps.Where(a => a.IsOn && a.IsVisible);

    /// <summary>Приложения к удалению — для профиля.</summary>
    public IReadOnlyList<string> SelectedApps => SelectedAppItems.Select(a => a.Name).ToList();

    public IEnumerable<string> SelectedAppTitles => SelectedAppItems.Select(a => a.Title);

    /// <summary>Приложения из профиля: отметятся при следующей загрузке списка.</summary>
    public void ApplyAppSelection(IEnumerable<string> names)
    {
        _rememberedApps.Clear();
        _rememberedApps.UnionWith(names);
        Apps.Clear();
        ShownApps.Clear();
        _appsLoadedFor = null;
        _reportDroppedApps = true;
    }

    /// <summary>Список приложений редакции образа (чтение ISO — несколько секунд); выбор сохраняется.</summary>
    public async Task LoadAppsAsync(string? isoPath, string? edition, UupSourceDraft? uup = null)
    {
        var key = $"{isoPath ?? uup?.UpdateId}|{uup?.Language}|{edition}";
        if (isoPath is null && uup?.UpdateId is null)
        {
            // Источник ещё не выбран.
            Apps.Clear();
            ShownApps.Clear();
            AppsError = Text.Get("AppsNoIso");
            _appsLoadedFor = null;
            return;
        }
        if (key == _appsLoadedFor && Apps.Count > 0)
            return;

        foreach (var app in Apps)
        {
            if (app.IsOn)
                _rememberedApps.Add(app.Name);
            else
                _rememberedApps.Remove(app.Name);
        }

        IsLoadingApps = true;
        AppsError = null;
        try
        {
            using var list = await engine.AppsAsync(isoPath, edition, uup);
            if (list is null)
            {
                Apps.Clear();
                ShownApps.Clear();
                AppsError = Text.Get("AppsError");
                return;
            }
            FillApps(list.RootElement);
            _appsLoadedFor = key;
        }
        finally
        {
            IsLoadingApps = false;
        }
    }

    void FillApps(JsonElement root)
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        string? Localized(JsonElement texts) =>
            texts.ValueKind != JsonValueKind.Object ? null :
            texts.TryGetProperty(language, out var t) ? t.GetString() :
            texts.TryGetProperty("en", out var en) ? en.GetString() : null;

        var items = root.GetProperty("apps").EnumerateArray().Select(app =>
        {
            var name = app.GetProperty("name").GetString()!;
            var safety = app.GetProperty("safety").GetString()!;
            return new AppItem
            {
                Name = name,
                Title = Localized(app.GetProperty("title")) ?? name,
                Safety = safety,
                Recommended = app.GetProperty("recommended").GetBoolean(),
                Note = safety switch { "risky" => Text.Get("AppRisky"), "unknown" => Text.Get("AppUnknown"), _ => null },
                IsOn = _rememberedApps.Contains(name),
            };
        });

        Apps.Clear();
        // Безопасные — сначала, рекомендованные среди них — первыми.
        foreach (var item in items.OrderBy(a => a.Safety switch { "safe" => 0, "risky" => 1, _ => 2 })
                     .ThenByDescending(a => a.Recommended)
                     .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase))
            Apps.Add(item);
        ApplyVisibility();

        if (_reportDroppedApps)
        {
            _reportDroppedApps = false;
            var present = Apps.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _rememberedApps.Where(n => !present.Contains(n)))
                Dropped.Add(Text.Format("AppNotInImage", name));
            HasDropped = Dropped.Count > 0;
            if (Apps.Any(a => a.IsOn && a.Safety != "safe"))
                AdvancedTweaksSelected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Отметить рекомендованные к удалению (остальные снять).</summary>
    public void SelectRecommendedApps()
    {
        foreach (var app in Apps)
            app.IsOn = app.Recommended;
    }

    public void ClearApps()
    {
        foreach (var app in Apps)
            app.IsOn = false;
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
