using System.Globalization;
using Shapeshift.Core;
using Shapeshift.Engine.Images;

namespace Shapeshift.Engine.Uup;

/// <summary>
/// Источник сборки — выпуск UUP: что скачать и из чего собрать носитель. Вместо распаковки ISO сборка
/// скачивает файлы (шаг DownloadFiles) и собирает носитель wimlib (шаг AssembleImage).
/// </summary>
public sealed class UupBuildSource(
    UupRelease release, string language, IReadOnlyList<string> editions, IReadOnlyList<UupFile> files,
    UupDownloader downloader, UupLinks links, string? baseVersion)
{
    public UupRelease Release => release;

    public string Language => language;

    public UupDownloader Downloader => downloader;

    public UupLinks Links => links;

    /// <summary>Образы редакций в порядке редакций результата.</summary>
    public IReadOnlyList<UupFile> EditionImages { get; } =
        editions.Select(e => UupFileSet.EditionImage(files, e, language) ?? throw new InvalidOperationException($"no image for {e}")).ToList();

    /// <summary>Что скачать для носителя: образы редакций и пакеты (приложения и обновления — при обслуживании).</summary>
    public IReadOnlyList<UupFile> MediaFiles { get; } = UupFileSet.MediaFiles(files);

    public IReadOnlyList<UupFile> Packages => MediaFiles.Except(EditionImages).ToList();

    public long BytesToDownload => MediaFiles.Where(f => !downloader.IsCached(f)).Sum(f => f.Size);

    /// <summary>
    /// Образ, каким он получится (для плана и файла ответов): версия — исходная сборка выпуска (обновления не встроены),
    /// редакции — с номерами по порядку.
    /// </summary>
    public SourceImage Image()
    {
        var version = (baseVersion ?? "10.0." + release.Build).Split('.');
        var build = int.Parse(version.ElementAtOrDefault(2) ?? "0", CultureInfo.InvariantCulture);
        var revision = int.Parse(version.ElementAtOrDefault(3) ?? "0", CultureInfo.InvariantCulture);
        var releaseInfo = new Release(build, revision);
        var product = releaseInfo.Product == WindowsProduct.Windows11 ? "Windows 11" : "Windows 10";
        var info = new ImageInfo(releaseInfo, Architecture.X64, language,
            editions.Select((e, i) => new Edition(i + 1, e, $"{product} {EditionName(e)}")).ToList());

        // Сжатый LZMS образ распаковывается в LZX примерно в полтора раза больше.
        var mediaBytes = (long)(MediaFiles.Sum(f => f.Size) * 1.5);
        var label = $"{(build >= 22000 ? "WIN11" : "WIN10")}_{build}_{language.ToUpperInvariant().Replace('-', '_')}";
        return new SourceImage("", label, info, @"sources\install.wim", mediaBytes, FileCount: 1500,
            InstallImageBytes: mediaBytes, LongestPathLength: 120);
    }

    static string EditionName(string editionId) => editionId switch
    {
        "Core" => "Home",
        "CoreN" => "Home N",
        "CoreSingleLanguage" => "Home Single Language",
        "CoreCountrySpecific" => "Home China",
        "Professional" => "Pro",
        "ProfessionalN" => "Pro N",
        _ => editionId,
    };
}
