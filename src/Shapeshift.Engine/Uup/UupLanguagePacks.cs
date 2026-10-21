using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Shapeshift.Engine.Uup;

/// <summary>
/// Язык интерфейса для офлайн-обслуживания: языковой пакет (ESD — DISM ставит его из развёрнутой папки) и
/// возможности языка (базовые, рукописный ввод, OCR, речь — CAB), в порядке установки: зависимости первыми.
/// </summary>
public sealed record UupLanguagePack(string Language, UupFile Pack, IReadOnlyList<UupFile> Features)
{
    public IEnumerable<UupFile> Files => Features.Prepend(Pack);
}

/// <summary>Языковые пакеты выпуска и версия, под которую они собраны (например, 10.0.26100.1).</summary>
public sealed record UupLanguageSelection(IReadOnlyList<UupLanguagePack> Packs, IReadOnlyList<string> Problems, string? BaseVersion);

/// <summary>
/// Языки из метаданных выпуска: <c>DesktopTargetCompDB_&lt;язык&gt;</c> — языковой пакет,
/// <c>DesktopTargetCompDB_FOD_Neutral</c> — возможности <c>Language.*~&lt;язык&gt;~</c> и их зависимости (шрифты).
/// Файлы находятся по SHA-256, как и пакеты редакций.
/// </summary>
public static partial class UupLanguagePacks
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate";

    /// <summary>Языки, для которых в выпуске есть языковой пакет (имена — как в Windows: en-US).</summary>
    public static IReadOnlyList<string> Available(string compDbs) =>
        Directory.Exists(compDbs)
            ? Directory.EnumerateFiles(compDbs, "*.xml.cab")
                .Select(f => LanguageCompDbPattern().Match(Path.GetFileName(f)))
                .Where(m => m.Success)
                .Select(m => Canonical(m.Groups[1].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public static UupLanguageSelection Resolve(UupRelease release, string compDbs, IReadOnlyList<string> languages)
    {
        var problems = new List<string>();
        var packs = new List<UupLanguagePack>();
        if (languages.Count == 0)
            return new UupLanguageSelection(packs, problems, null);

        var bySha256 = UupFeaturesOnDemand.ByHash(release);
        var fod = UupFeaturesOnDemand.Load(release, compDbs);

        string? baseVersion = null;
        foreach (var language in languages)
        {
            var compDb = UupFeaturesOnDemand.LoadCompDb(compDbs, "DesktopTargetCompDB_" + language);
            var packElement = compDb?.Element(Ns + "Packages")?.Elements(Ns + "Package").FirstOrDefault();
            if (packElement is null)
            {
                problems.Add($"there is no language pack for {language} in this release.");
                continue;
            }
            baseVersion ??= (string?)compDb!.Attribute("OSVersion");
            if (UupFeaturesOnDemand.FileOf(packElement, bySha256) is not { } pack)
            {
                problems.Add($"metadata lists the {language} language pack, which is not in the release.");
                continue;
            }

            // Возможности языка — в порядке зависимостей (шрифты и базовые — раньше рукописного ввода и речи).
            var marker = $"~{language}~";
            var features = fod.FilesFor(fod.FeatureIds
                .Where(id => id.StartsWith("Language.", StringComparison.OrdinalIgnoreCase) && id.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase));
            if (features.Count == 0)
                problems.Add($"there are no language features (handwriting, OCR, speech) for {language} in this release.");
            packs.Add(new UupLanguagePack(language, pack, features));
        }
        return new UupLanguageSelection(packs, problems, baseVersion);
    }

    // de-de → de-DE, sr-latn-rs → sr-Latn-RS — как называет языки Windows.
    static string Canonical(string name)
    {
        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(name, predefinedOnly: true).Name;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return name;
        }
    }

    // DesktopTargetCompDB_ru-RU.xml.cab, но не _professional_ru-ru, _App_Neutral, _FOD_ru-RU.
    [GeneratedRegex(@"^DesktopTargetCompDB_([a-z]{2,3}(?:-[a-z0-9]+)+)\.xml\.cab$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageCompDbPattern();
}
