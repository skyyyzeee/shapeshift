namespace Shapeshift.Engine.Uup;

/// <summary>
/// Что сборка ставит в Windows из выпуска Windows Update при офлайн-обслуживании (DISM): языки интерфейса
/// (шаг AddLanguages), .NET Framework 3.5 (EnableDotNet35), затем обновления (IntegrateUpdates) — в таком порядке,
/// чтобы обновление перевело добавленные языки и обновило .NET. Источник образа — этот же выпуск или свой ISO
/// той же базовой сборки.
/// </summary>
/// <param name="dotNet35">Пакет .NET Framework 3.5 из выпуска (для образа из UUP: в нём нет sources\sxs).</param>
public sealed class UupServicing(
    UupRelease release, UupDownloader downloader, UupLinks links, IReadOnlyList<UupLanguagePack> languages, UupUpdates? updates,
    IReadOnlyList<UupFile>? dotNet35 = null)
{
    public IReadOnlyList<UupFile> DotNet35 => dotNet35 ?? [];

    public UupRelease Release => release;

    public UupDownloader Downloader => downloader;

    public UupLinks Links => links;

    public IReadOnlyList<UupLanguagePack> Languages => languages;

    /// <summary>Что встроить в Windows; null — образ остаётся своей сборкой.</summary>
    public UupUpdates? Updates => updates is { IsEmpty: false } ? updates : null;

    public bool IsEmpty => Languages.Count == 0 && Updates is null && DotNet35.Count == 0;

    /// <summary>Что скачать для обслуживания.</summary>
    public IReadOnlyList<UupFile> Files => [.. Languages.SelectMany(l => l.Files), .. DotNet35, .. Updates?.All ?? []];

    public long BytesToDownload => Files.Where(f => !downloader.IsCached(f)).Sum(f => f.Size);
}
