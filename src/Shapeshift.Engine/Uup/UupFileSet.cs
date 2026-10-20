using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Shapeshift.Engine.Uup;

/// <summary>Файлы для языка и редакций и версия, которую дают образы редакций (без обновлений).</summary>
/// <param name="BaseVersion">Например, 10.0.26100.1 — из CompDB редакции; null — не указана.</param>
public sealed record UupSelection(IReadOnlyList<UupFile> Files, IReadOnlyList<string> Problems, string? BaseVersion);

/// <summary>
/// Какие файлы выпуска нужны для языка и редакций. Источник — описания CompDB из
/// <c>&lt;UpdateId&gt;.AggregatedMetadata.cab</c>: для каждой пары «редакция + язык» — свой список пакетов
/// (<c>DesktopTargetCompDB_professional_ru-ru</c>), для встроенных приложений — <c>DesktopTargetCompDB_App_Neutral</c>.
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

        var appFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            foreach (var dependency in compDb.Descendants(Ns + "Feature").Where(f => (string?)f.Attribute("Group") == "PreinstalledApps"))
                appFeatures.Add((string)dependency.Attribute("FeatureID")!);
        }

        if (appFeatures.Count > 0 && reader.Load("DesktopTargetCompDB_App_Neutral") is { } apps)
            AddApps(apps, appFeatures, language, AddPayload);

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

    // Приложения редакции и всё, без чего они не ставятся (VCLibs, UI.Xaml…). Ресурсы на других языках не нужны.
    static void AddApps(XElement compDb, HashSet<string> wanted, string language, Action<XElement> addPayload)
    {
        var features = compDb.Element(Ns + "Features")?.Elements(Ns + "Feature")
            .GroupBy(f => (string)f.Attribute("FeatureID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        var packages = compDb.Element(Ns + "Packages")?.Elements(Ns + "Package")
            .GroupBy(p => (string)p.Attribute("ID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];

        var queue = new Queue<string>(wanted);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primary = language.Split('-')[0];
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id) || !features.TryGetValue(id, out var feature))
                continue;
            foreach (var dependency in feature.Element(Ns + "Dependencies")?.Elements(Ns + "Feature") ?? [])
                queue.Enqueue((string)dependency.Attribute("FeatureID")!);
            foreach (var reference in feature.Element(Ns + "Packages")?.Elements(Ns + "Package") ?? [])
            {
                var packageId = (string)reference.Attribute("ID")!;
                var split = LanguageSplitPattern().Match(packageId);
                if (split.Success && !string.Equals(split.Groups[1].Value.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (packages.TryGetValue(packageId, out var package))
                    addPayload(package);
            }
        }
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

    [GeneratedRegex(@"\.(esd|wim|cab)$", RegexOptions.IgnoreCase)]
    private static partial Regex MediaPattern();

    [GeneratedRegex(@"-baseless|-express|_Diffs_|_Forward_CompDB_", RegexOptions.IgnoreCase)]
    private static partial Regex SkippedUpdatePattern();

    [GeneratedRegex(@"_split\.language-([a-z0-9-]+)_", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageSplitPattern();

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
