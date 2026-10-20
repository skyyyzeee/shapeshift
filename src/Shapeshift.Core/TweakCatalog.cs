namespace Shapeshift.Core;

/// <summary>Момент, когда действие твика вступает в силу.</summary>
public enum Stage
{
    /// <summary>В среде установщика (windowsPE), до проверки требований. Реестр — только среды установщика.</summary>
    Installer,

    /// <summary>Во время установки, в будущей системе до первого входа. HKCU — профиль пользователя по умолчанию.</summary>
    Setup,

    /// <summary>
    /// При первом входе пользователя, от его имени — только для первого пользователя. Для того, что Windows
    /// не берёт из профиля по умолчанию или перезаписывает при первом входе (HKCU\Software\Classes, тема).
    /// </summary>
    FirstLogon,
}

public enum RegistryHive
{
    HKLM,
    HKCU,
}

public enum RegistryValueKind
{
    Dword,
    Qword,
    String,
    ExpandString,
}

/// <summary>Элементарная операция, из которых состоит твик.</summary>
public abstract record TweakAction(Stage Stage);

/// <param name="Name">Имя значения; null — значение по умолчанию раздела.</param>
/// <param name="Value">Данные: число для Dword/Qword, текст для строковых видов.</param>
public sealed record RegistryAction(
    Stage Stage, RegistryHive Hive, string Key, string? Name, RegistryValueKind Kind, string Value) : TweakAction(Stage);

public sealed record CommandAction(Stage Stage, string Command) : TweakAction(Stage);

/// <summary>Текст на нескольких языках; ключ — код языка (en, ru).</summary>
public sealed record LocalizedText(IReadOnlyDictionary<string, string> Values)
{
    public string Get(string language) =>
        Values.TryGetValue(language, out var text) ? text : Values.GetValueOrDefault("en") ?? "";
}

/// <summary>К каким выпускам применим твик. Пустые поля — без ограничения.</summary>
public sealed record ReleaseFilter(IReadOnlyList<WindowsProduct> Products, int? MinBuild, int? MaxBuild)
{
    public static ReleaseFilter Any { get; } = new([], null, null);

    public bool Matches(Release release) =>
        (Products.Count == 0 || Products.Contains(release.Product)) &&
        (MinBuild is null || release.Build >= MinBuild) &&
        (MaxBuild is null || release.Build <= MaxBuild);
}

/// <summary>Одно именованное изменение, которое пользователь включает или выключает целиком.</summary>
public sealed record Tweak(
    string Id,
    string Category,
    LocalizedText Name,
    LocalizedText Description,
    ReleaseFilter Releases,
    IReadOnlyList<string> Conflicts,
    bool Advanced,
    IReadOnlyList<TweakAction> Actions);

public sealed record TweakCategory(string Id, LocalizedText Name);

public enum AppSafety
{
    /// <summary>Удаление ничего не ломает; приложение можно вернуть из Microsoft Store.</summary>
    Safe,

    /// <summary>От приложения зависят другие части Windows (магазин, безопасность, кодеки, установщик пакетов).</summary>
    Risky,
}

/// <summary>Разметка встроенного приложения: сам список приложений читается из образа, здесь — что о нём известно.</summary>
/// <param name="Name">Имя пакета без версии, например Microsoft.BingNews.</param>
public sealed record AppMarkup(string Name, LocalizedText Title, AppSafety Safety, bool Recommended);

/// <summary>Популярная программа из winget — для быстрого выбора в окне; любую другую можно указать по id.</summary>
public sealed record ProgramSuggestion(string WingetId, LocalizedText Title);

/// <summary>
/// Полный набор описаний твиков, разметка встроенных приложений и популярные программы.
/// Порядок твиков задаёт порядок их действий.
/// </summary>
public sealed record TweakCatalog(
    IReadOnlyList<TweakCategory> Categories,
    IReadOnlyList<Tweak> Tweaks,
    IReadOnlyList<AppMarkup> Apps,
    IReadOnlyList<ProgramSuggestion> Programs)
{
    public AppMarkup? FindApp(string name) => Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public Tweak? Find(string id) => Tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>Конфликтуют ли твики (отношение симметрично, даже если записано в одном из них).</summary>
    public static bool InConflict(Tweak a, Tweak b) => a.Conflicts.Contains(b.Id) || b.Conflicts.Contains(a.Id);
}
