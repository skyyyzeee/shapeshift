using System.Text.Encodings.Web;
using System.Text.Json;
using Shapeshift.Core;

namespace Shapeshift.Engine;

/// <summary>
/// Машиночитаемый вывод движка для окна. Формат — контракт между процессами:
/// поля только добавляются, существующие не меняют смысла.
/// </summary>
public static class EngineJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Image(ImageInfo info) => JsonSerializer.Serialize(new
    {
        release = new
        {
            product = info.Release.Product == WindowsProduct.Windows11 ? "windows11" : "windows10",
            version = info.Release.Version,
            build = info.Release.Build,
            revision = info.Release.Revision,
            name = info.Release.ToString(),
        },
        architecture = info.Architecture.ToString().ToLowerInvariant(),
        language = info.Language,
        editions = info.Editions.Select(e => new { index = e.Index, editionId = e.EditionId, name = e.Name }),
    }, Options);

    /// <summary>План сборки для сводки «что будет сделано» (<c>build --dry-run --json</c>).</summary>
    public static string Plan(Builds.BuildPlan plan, IReadOnlyList<string> warnings) => JsonSerializer.Serialize(new
    {
        image = JsonSerializer.Deserialize<JsonElement>(Image(plan.Source.Info)),
        outputIso = plan.OutputIso,
        steps = plan.Steps.Select(s => new { kind = s.Kind.ToString(), description = s.Description, volume = s.Volume, bytes = s.Bytes }),
        space = plan.Space.Select(s => new { volume = s.Volume, requiredBytes = s.RequiredBytes, availableBytes = s.AvailableBytes }),
        tweaks = plan.Profile.Tweaks,
        warnings,
    }, Options);

    /// <summary>
    /// Каталог для экрана «Настройка» (<c>catalog list --json</c>): тексты на всех языках каталога,
    /// фильтр выпусков и — если известен образ — применимость к нему.
    /// </summary>
    public static string Catalog(TweakCatalog catalog, Release? release) => JsonSerializer.Serialize(new
    {
        categories = catalog.Categories.Select(c => new { id = c.Id, name = c.Name.Values }),
        tweaks = catalog.Tweaks.Select(t => new
        {
            id = t.Id,
            category = t.Category,
            name = t.Name.Values,
            description = t.Description.Values,
            advanced = t.Advanced,
            conflicts = t.Conflicts,
            releases = new
            {
                products = t.Releases.Products.Select(p => p == WindowsProduct.Windows11 ? "windows11" : "windows10"),
                minBuild = t.Releases.MinBuild,
                maxBuild = t.Releases.MaxBuild,
            },
            applicable = release is null ? (bool?)null : t.Releases.Matches(release),
        }),
        programs = catalog.Programs.Select(p => new { winget = p.WingetId, title = p.Title.Values }),
    }, Options);

    /// <summary>Встроенные приложения редакции с разметкой каталога (<c>apps list --json</c>).</summary>
    public static string Apps(Edition edition, IReadOnlyList<string> apps, TweakCatalog catalog) => JsonSerializer.Serialize(new
    {
        edition = new { index = edition.Index, editionId = edition.EditionId, name = edition.Name },
        apps = apps.Select(name =>
        {
            var markup = catalog.FindApp(name);
            return new
            {
                name,
                title = markup?.Title.Values,
                safety = markup is null ? "unknown" : markup.Safety == AppSafety.Safe ? "safe" : "risky",
                recommended = markup?.Recommended ?? false,
            };
        }),
    }, Options);

    /// <summary>Выпуски из Windows Update по каналам (<c>uup releases --json</c>); problems — каналы, которые не ответили.</summary>
    public static string UupReleases(IReadOnlyList<Uup.UupRelease> releases, IReadOnlyList<(string Channel, string Problem)> problems) =>
        JsonSerializer.Serialize(new
        {
            releases = releases.Select(r => new
            {
                updateId = r.UpdateId,
                title = r.Title,
                build = r.Build,
                channel = r.Channel,
                advanced = r.Advanced,
            }),
            problems = problems.Select(p => new { channel = p.Channel, message = p.Problem }),
        }, Options);

    /// <summary>Языки и редакции выпуска (<c>uup release --json</c>). Редакции — те, что есть в выпуске готовыми.</summary>
    public static string UupRelease(Uup.UupRelease release) => JsonSerializer.Serialize(new
    {
        updateId = release.UpdateId,
        title = release.Title,
        build = release.Build,
        channel = release.Channel,
        advanced = release.Advanced,
        languages = release.Languages().Select(l => new
        {
            code = l.Code,
            editions = l.Editions,
        }),
    }, Options);

    public static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = new { code, message } }, Options);
}
