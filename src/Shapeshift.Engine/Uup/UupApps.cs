using System.Xml.Linq;

namespace Shapeshift.Engine.Uup;

/// <summary>
/// Встроенное приложение выпуска для офлайн-обслуживания: главный пакет (бандл или пакет), остальные его части
/// (пакет архитектуры, ресурсы — «плоский» бандл ссылается на них по имени), зависимости-фреймворки и лицензия.
/// </summary>
/// <param name="Id">Функция CompDB, например <c>Microsoft.Windows.Photos_8wekyb3d8bbwe</c>.</param>
/// <param name="Name">Имя пакета, как в <c>removeApps</c>: <c>Microsoft.Windows.Photos</c>.</param>
/// <param name="Dependencies">Функции-фреймворки, без которых приложение не ставится.</param>
/// <param name="License">XML лицензии из CompDB; null — без лицензии (фреймворк).</param>
public sealed record UupApp(string Id, string Name, bool IsFramework, UupFile Main, IReadOnlyList<UupFile> Parts, IReadOnlyList<string> Dependencies, string? License)
{
    public IEnumerable<UupFile> Files => Parts.Prepend(Main);
}

/// <summary>
/// Приложения редакции: те, что CompDB редакции называет предустановленными (<c>Group="PreinstalledApps"</c>),
/// из <c>DesktopTargetCompDB_App_Neutral</c> — со всеми частями (ресурсы всех языков — ~50 МБ на весь выпуск:
/// так приложения переводятся и на добавленные языки) и обязательными фреймворками.
/// </summary>
public static class UupApps
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate";

    /// <summary>Приложения каждой редакции (в порядке редакций), фреймворки — тоже элементами списка, раньше зависящих.</summary>
    public static IReadOnlyList<IReadOnlyList<UupApp>> Resolve(UupRelease release, string compDbs, IReadOnlyList<string> editions, string language)
    {
        var catalog = UupFeaturesOnDemand.LoadCompDb(compDbs, "DesktopTargetCompDB_App_Neutral");
        if (catalog is null)
            return editions.Select(_ => (IReadOnlyList<UupApp>)[]).ToList();

        var bySha256 = UupFeaturesOnDemand.ByHash(release);
        var features = catalog.Element(Ns + "Features")?.Elements(Ns + "Feature")
            .GroupBy(f => (string)f.Attribute("FeatureID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        var packages = catalog.Element(Ns + "Packages")?.Elements(Ns + "Package")
            .GroupBy(p => (string)p.Attribute("ID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        var apps = new Dictionary<string, UupApp?>(StringComparer.OrdinalIgnoreCase);

        UupApp? App(string id)
        {
            if (apps.TryGetValue(id, out var known))
                return known;
            apps[id] = null;
            if (!features.TryGetValue(id, out var feature))
                return null;
            var references = feature.Element(Ns + "Packages")?.Elements(Ns + "Package").ToList() ?? [];
            UupFile? FileOf(XElement reference) =>
                packages.TryGetValue((string)reference.Attribute("ID")!, out var package) ? UupFeaturesOnDemand.FileOf(package, bySha256) : null;
            var main = references.FirstOrDefault(r => ((string?)r.Attribute("PackageType"))?.EndsWith("BundlePackage", StringComparison.OrdinalIgnoreCase) == true)
                ?? references.FirstOrDefault(r => ((string?)r.Attribute("PackageType"))?.EndsWith("MainPackage", StringComparison.OrdinalIgnoreCase) == true)
                ?? references.FirstOrDefault();
            if (main is null || FileOf(main) is not { } mainFile)
                return null;
            var parts = references.Where(r => r != main).Select(FileOf).OfType<UupFile>().Distinct().ToList();
            var dependencies = feature.Element(Ns + "Dependencies")?.Elements(Ns + "Feature")
                .Where(d => !string.Equals((string?)d.Attribute("Type"), "Optional", StringComparison.OrdinalIgnoreCase))
                .Select(d => (string)d.Attribute("FeatureID")!).ToList() ?? [];
            var license = feature.Descendants(Ns + "CustomInfo").FirstOrDefault(c => (string?)c.Attribute("Key") == "licensedata")?.Value;
            var isFramework = ((string?)feature.Attribute("Type"))?.EndsWith("Framework", StringComparison.OrdinalIgnoreCase) == true;
            var app = new UupApp(id, id.Split('_')[0], isFramework, mainFile, parts, dependencies, license);
            apps[id] = app;
            return app;
        }

        var result = new List<IReadOnlyList<UupApp>>();
        foreach (var edition in editions)
        {
            var compDb = UupFeaturesOnDemand.LoadCompDb(compDbs, $"DesktopTargetCompDB_{edition}_{language}");
            var wanted = compDb?.Descendants(Ns + "Feature")
                .Where(f => (string?)f.Attribute("Group") == "PreinstalledApps")
                .Select(f => (string)f.Attribute("FeatureID")!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

            // Фреймворки — раньше приложений, которым они нужны.
            var ordered = new List<UupApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string id)
            {
                if (!seen.Add(id) || App(id) is not { } app)
                    return;
                foreach (var dependency in app.Dependencies)
                    Visit(dependency);
                ordered.Add(app);
            }
            foreach (var id in wanted)
                Visit(id);
            result.Add(ordered);
        }
        return result;
    }

    /// <summary>
    /// Без приложений из <c>removeApps</c> (по имени пакета) и без фреймворков, которые были нужны только им.
    /// Фреймворк, который редакция ставит сам по себе, остаётся.
    /// </summary>
    public static IReadOnlyList<UupApp> Without(IReadOnlyList<UupApp> apps, IReadOnlyCollection<string> removed)
    {
        HashSet<string> NeededBy(IEnumerable<UupApp> list)
        {
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Need(UupApp app)
            {
                foreach (var dependency in app.Dependencies)
                {
                    if (needed.Add(dependency) && apps.FirstOrDefault(a => string.Equals(a.Id, dependency, StringComparison.OrdinalIgnoreCase)) is { } framework)
                        Need(framework);
                }
            }
            foreach (var app in list)
                Need(app);
            return needed;
        }

        var kept = apps.Where(a => a.IsFramework || !removed.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var neededBefore = NeededBy(apps.Where(a => !a.IsFramework));
        var neededAfter = NeededBy(kept.Where(a => !a.IsFramework));
        return kept.Where(a => !a.IsFramework || !neededBefore.Contains(a.Id) || neededAfter.Contains(a.Id)).ToList();
    }
}
