namespace Shapeshift.Engine.Uup;

/// <summary>
/// Что сборка ставит в Windows из выпуска Windows Update при офлайн-обслуживании (DISM): языки интерфейса
/// (шаг AddLanguages), затем обновления (шаг IntegrateUpdates) — в таком порядке, чтобы обновление перевело
/// и добавленные языки. Источник образа — этот же выпуск или свой ISO той же базовой сборки.
/// </summary>
public sealed class UupServicing(
    UupRelease release, UupDownloader downloader, UupLinks links, IReadOnlyList<UupLanguagePack> languages, UupUpdates? updates)
{
    public UupRelease Release => release;

    public UupDownloader Downloader => downloader;

    public UupLinks Links => links;

    public IReadOnlyList<UupLanguagePack> Languages => languages;

    /// <summary>Что встроить в Windows; null — образ остаётся своей сборкой.</summary>
    public UupUpdates? Updates => updates is { IsEmpty: false } ? updates : null;

    public bool IsEmpty => Languages.Count == 0 && Updates is null;

    /// <summary>Что скачать для обслуживания.</summary>
    public IReadOnlyList<UupFile> Files => [.. Languages.SelectMany(l => l.Files), .. Updates?.All ?? []];

    public long BytesToDownload => Files.Where(f => !downloader.IsCached(f)).Sum(f => f.Size);
}
