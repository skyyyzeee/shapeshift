using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Shapeshift.Engine.Uup;

/// <summary>
/// Обновления выпуска для образа Windows, в порядке встраивания: накопительные (последнее — после контрольных точек),
/// пакет включения версии (например, 25H2 поверх 24H2), обновление .NET. Обновления среды восстановления и
/// установщика (SafeOS, SetupDU) сюда не входят.
/// </summary>
/// <param name="Cumulative">Накопительные .msu/.cab по возрастанию номера KB; встраивается последний, DISM сам ставит контрольные точки из той же папки.</param>
public sealed record UupUpdates(IReadOnlyList<UupFile> Cumulative, UupFile? Enablement, UupFile? NetFramework)
{
    public bool IsEmpty => Cumulative.Count == 0 && Enablement is null && NetFramework is null;

    public IEnumerable<UupFile> All => Cumulative.Concat(new[] { Enablement, NetFramework }.OfType<UupFile>());
}

/// <summary>Файлы для языка и редакций и версия, которую дают образы редакций (без обновлений).</summary>
/// <param name="BaseVersion">Например, 10.0.26100.1 — из CompDB редакции; null — не указана.</param>
public sealed record UupSelection(IReadOnlyList<UupFile> Files, IReadOnlyList<string> Problems, string? BaseVersion);

/// <summary>
/// Какие файлы выпуска нужны для языка и редакций. Источник — описания CompDB из
/// <c>&lt;UpdateId&gt;.AggregatedMetadata.cab</c>: для каждой пары «редакция + язык» — свой список пакетов
/// (<c>DesktopTargetCompDB_professional_ru-ru</c>), для встроенных приложений — <see cref="UupApps"/>.
/// Файл пакета находится по SHA-256 из CompDB. Обновления (накопительное, пакет включения версии) — по именам.
/// </summary>
public static partial class UupFileSet
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate";

    /// <summary>Файл метаданных выпуска (его скачивают первым).</summary>
    public static UupFile? MetadataFile(UupRelease release) =>
        release.Files.FirstOrDefault(f => f.Name.EndsWith(".AggregatedMetadata.cab", StringComparison.OrdinalIgnoreCase));

    /// <param name="compDbs">Папка с распакованным AggregatedMetadata.cab (вложенные *.xml.cab).</param>
    public static UupSelection Resolve(
        UupRelease release, string compDbs, string language, IReadOnlyList<string> editions)
    {
        var problems = new List<string>();
        var bySha256 = release.Files.Where(f => f.Sha256 is not null)
            .GroupBy(f => f.Sha256!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<string, UupFile>(StringComparer.OrdinalIgnoreCase);
        var reader = new CompDbReader(compDbs);

        void AddPayload(XElement package)
        {
            foreach (var item in package.Descendants(Ns + "PayloadItem"))
            {
                var hash = (string?)item.Attribute("PayloadHash");
                if (hash is null)
                    continue;
                if (bySha256.TryGetValue(Convert.ToHexStringLower(Convert.FromBase64String(hash)), out var file))
                    chosen.TryAdd(file.Name, file);
                else
                    problems.Add($"metadata lists {(string?)item.Attribute("Path")}, which is not in the release.");
            }
        }

        string? baseVersion = null;
        foreach (var edition in editions)
        {
            var compDb = reader.Load($"DesktopTargetCompDB_{edition}_{language}");
            if (compDb is null)
            {
                problems.Add($"edition {edition} is not available in {language} in this release.");
                continue;
            }
            baseVersion ??= (string?)compDb.Attribute("OSVersion");
            foreach (var package in compDb.Descendants(Ns + "Packages").Elements(Ns + "Package"))
                AddPayload(package);
        }

        foreach (var app in UupApps.Resolve(release, compDbs, editions.Where(e => reader.Load($"DesktopTargetCompDB_{e}_{language}") is not null).ToList(), language).SelectMany(a => a))
        {
            foreach (var file in app.Files)
                chosen.TryAdd(file.Name, file);
        }

        foreach (var update in Updates(release.Files))
            chosen.TryAdd(update.Name, update);

        return new UupSelection(chosen.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(), problems.Distinct().ToList(), baseVersion);
    }

    /// <summary>Образ редакции (<c>professional_ru-ru.esd</c>) среди файлов выпуска.</summary>
    public static UupFile? EditionImage(IReadOnlyList<UupFile> files, string edition, string language) =>
        files.FirstOrDefault(f => string.Equals(f.Name, $"{edition}_{language}.esd", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Что нужно для носителя (wimlib): образы редакций и пакеты с данными Windows (ESD, WIM, CAB).
    /// Приложения и обновления ставятся отдельно — при офлайн-обслуживании.
    /// </summary>
    public static IReadOnlyList<UupFile> MediaFiles(IReadOnlyList<UupFile> files)
    {
        var updates = Updates(files).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Where(f => !updates.Contains(f.Name) && MediaPattern().IsMatch(f.Name)).ToList();
    }

    // Обновления выпуска: накопительные (.msu или .cab), пакет включения версии, обновления установщика и среды
    // восстановления. Пары «.cab + .msu» — одно и то же: берётся .cab. Разностные части (-baseless, -express, .psf) не нужны.
    internal static IEnumerable<UupFile> Updates(IReadOnlyList<UupFile> files)
    {
        var candidates = files.Where(f => UpdatePattern().IsMatch(f.Name) && !SkippedUpdatePattern().IsMatch(f.Name)).ToList();
        var cabs = candidates.Where(f => f.Name.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(f => !(f.Name.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) && cabs.Contains(Path.GetFileNameWithoutExtension(f.Name))));
    }

    [GeneratedRegex(@"^(Windows1[01]\.0-KB.*\.(cab|msu|wim)|.*AggregatedMetadata.*\.cab|DesktopDeployment.*\.cab)$", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatePattern();

    /// <summary>
    /// Какие обновления встраивать в Windows: номера KB — из имён CompDB в метаданных (<c>outer.AggregatedMetadata_KB…</c> —
    /// накопительные, <c>EKBUpdateCompDB_KB…</c> — пакет включения, <c>SafeOSDUCompDB</c>/<c>SetupDUCompDB</c> — не для Windows).
    /// </summary>
    public static UupUpdates ClassifyUpdates(IReadOnlyList<UupFile> files, string compDbs)
    {
        var names = Directory.Exists(compDbs) ? Directory.EnumerateFiles(compDbs).Select(Path.GetFileName).ToList() : [];
        IEnumerable<string> Kbs(Regex pattern) => names.Select(n => pattern.Match(n!)).Where(m => m.Success).Select(m => m.Groups[1].Value);
        var notForWindows = Kbs(SafeOsOrSetupPattern()).ToHashSet();
        var enablementKb = Kbs(EnablementPattern()).FirstOrDefault();
        var cumulativeKbs = Kbs(CumulativePattern()).ToList();

        var updates = Updates(files).Where(f => KbOf(f.Name) is { } kb && !notForWindows.Contains(kb)).ToList();
        UupFile? Package(string kb) =>
            updates.Where(f => KbOf(f.Name) == kb).OrderBy(f => f.Name.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();

        // Без списка накопительных в метаданных (Windows 10, старые выпуски) — все .msu, кроме пакета включения.
        if (cumulativeKbs.Count == 0)
        {
            cumulativeKbs = updates.Where(f => f.Name.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) && KbOf(f.Name) != enablementKb)
                .Select(f => KbOf(f.Name)!).ToList();
        }
        var cumulative = cumulativeKbs.Distinct().OrderBy(kb => long.Parse(kb)).Select(Package).OfType<UupFile>().ToList();
        var enablement = enablementKb is null ? null
            : updates.FirstOrDefault(f => KbOf(f.Name) == enablementKb && f.Name.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) ?? Package(enablementKb);
        var netFramework = updates.FirstOrDefault(f => NetFrameworkPattern().IsMatch(f.Name));
        return new UupUpdates(cumulative, enablement, netFramework);
    }

    static string? KbOf(string name) => KbPattern().Match(name) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"-KB(\d+)-", RegexOptions.IgnoreCase)]
    private static partial Regex KbPattern();

    [GeneratedRegex(@"^outer\.AggregatedMetadata_KB(\d+)\.cab$", RegexOptions.IgnoreCase)]
    private static partial Regex CumulativePattern();

    [GeneratedRegex(@"^EKBUpdateCompDB_KB(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnablementPattern();

    [GeneratedRegex(@"^(?:SafeOSDUCompDB|SetupDUCompDB)_KB(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SafeOsOrSetupPattern();

    [GeneratedRegex(@"-NDP\d+\.(cab|msu)$", RegexOptions.IgnoreCase)]
    private static partial Regex NetFrameworkPattern();

    [GeneratedRegex(@"\.(esd|wim|cab)$", RegexOptions.IgnoreCase)]
    private static partial Regex MediaPattern();

    [GeneratedRegex(@"-baseless|-express|_Diffs_|_Forward_CompDB_", RegexOptions.IgnoreCase)]
    private static partial Regex SkippedUpdatePattern();

    /// <summary>Описания CompDB: каждое — отдельный CAB с одним XML внутри.</summary>
    sealed class CompDbReader(string folder)
    {
        public XElement? Load(string name)
        {
            var cab = Directory.EnumerateFiles(folder, "*.xml.cab")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name + ".xml.cab", StringComparison.OrdinalIgnoreCase));
            if (cab is null)
                return null;
            var xml = Cabinet.Extract(cab, Path.Combine(folder, "_x", Path.GetFileNameWithoutExtension(cab))).Single();
            return XElement.Load(xml);
        }
    }
}
