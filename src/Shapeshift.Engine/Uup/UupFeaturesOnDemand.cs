using System.Xml.Linq;

namespace Shapeshift.Engine.Uup;

/// <summary>
/// Возможности по требованию (FOD) выпуска из <c>DesktopTargetCompDB_FOD_Neutral</c>: функция (например,
/// <c>Language.Basic~de-de~1.0</c> или <c>NetFX3~</c>) → её пакеты CAB и пакеты всех её зависимостей.
/// Файлы находятся по SHA-256, как и пакеты редакций.
/// </summary>
public sealed class UupFeaturesOnDemand
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate";

    readonly Dictionary<string, XElement> _features;
    readonly Dictionary<string, XElement> _packages;
    readonly Dictionary<string, UupFile> _bySha256;

    UupFeaturesOnDemand(XElement? compDb, UupRelease release)
    {
        _features = compDb?.Element(Ns + "Features")?.Elements(Ns + "Feature")
            .GroupBy(f => (string)f.Attribute("FeatureID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        _packages = compDb?.Element(Ns + "Packages")?.Elements(Ns + "Package")
            .GroupBy(p => (string)p.Attribute("ID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        _bySha256 = ByHash(release);
    }

    public static UupFeaturesOnDemand Load(UupRelease release, string compDbs) => new(LoadCompDb(compDbs, "DesktopTargetCompDB_FOD_Neutral"), release);

    /// <summary>Все функции выпуска.</summary>
    public IEnumerable<string> FeatureIds => _features.Keys;

    /// <summary>
    /// Файлы функций в порядке установки: зависимости раньше зависящих, без повторов. Неизвестные функции и
    /// пакеты, которых нет среди файлов выпуска, пропускаются (их отсутствие видно по пустому результату).
    /// </summary>
    public IReadOnlyList<UupFile> FilesFor(IEnumerable<string> featureIds)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string id)
        {
            if (!seen.Add(id) || !_features.TryGetValue(id, out var feature))
                return;
            foreach (var dependency in feature.Element(Ns + "Dependencies")?.Elements(Ns + "Feature") ?? [])
                Visit((string)dependency.Attribute("FeatureID")!);
            ordered.Add(id);
        }
        foreach (var id in featureIds)
            Visit(id);

        var files = new List<UupFile>();
        foreach (var id in ordered)
        {
            foreach (var reference in _features[id].Element(Ns + "Packages")?.Elements(Ns + "Package") ?? [])
            {
                if (_packages.TryGetValue((string)reference.Attribute("ID")!, out var package) && FileOf(package, _bySha256) is { } file &&
                    !files.Contains(file))
                    files.Add(file);
            }
        }
        return files;
    }

    internal static Dictionary<string, UupFile> ByHash(UupRelease release) => release.Files.Where(f => f.Sha256 is not null)
        .GroupBy(f => f.Sha256!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    internal static UupFile? FileOf(XElement package, Dictionary<string, UupFile> bySha256)
    {
        var hash = (string?)package.Descendants(Ns + "PayloadItem").FirstOrDefault()?.Attribute("PayloadHash");
        return hash is not null && bySha256.TryGetValue(Convert.ToHexStringLower(Convert.FromBase64String(hash)), out var file) ? file : null;
    }

    internal static XElement? LoadCompDb(string folder, string name)
    {
        var cab = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.xml.cab").FirstOrDefault(f => string.Equals(Path.GetFileName(f), name + ".xml.cab", StringComparison.OrdinalIgnoreCase))
            : null;
        if (cab is null)
            return null;
        var xml = Cabinet.Extract(cab, Path.Combine(folder, "_x", Path.GetFileNameWithoutExtension(cab))).Single();
        return XElement.Load(xml);
    }
}
