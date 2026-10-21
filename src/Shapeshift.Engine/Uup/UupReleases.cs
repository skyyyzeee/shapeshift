using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shapeshift.Engine.Uup;

/// <summary>Откуда брать выпуск: какие «устройства» спрашивают Windows Update.</summary>
/// <param name="Id">Имя канала для CLI и окна.</param>
/// <param name="Queries">Запросы канала; берётся самая новая из предложенных сборок. Несколько — потому что
/// Windows Update выдаёт новую версию не всем устройствам сразу и на один и тот же запрос отвечает по-разному.</param>
/// <param name="Advanced">Только в продвинутом режиме: сборки Insider (Beta, Dev, Canary), твики могут не работать.</param>
public sealed record UupChannel(string Id, IReadOnlyList<UpdateQuery> Queries, bool Advanced);

/// <summary>Язык выпуска (например, ru-RU) и редакции (EditionID), которые для него есть готовыми.</summary>
public sealed record UupLanguage(string Code, IReadOnlyList<string> Editions);

/// <summary>Выпуск, найденный в канале: сохраняется в кэше, чтобы потом выбрать язык и редакции и скачать файлы.</summary>
public sealed record UupRelease(string Channel, UpdateQuery Query, string UpdateId, int Revision, string Title, string Build, IReadOnlyList<UupFile> Files)
{
    /// <summary>Есть ли такой выпуск в канале продвинутого режима.</summary>
    public bool Advanced => UupChannels.Find(Channel)?.Advanced ?? false;

    /// <summary>Языки и редакции — по образам редакций в файлах выпуска: <c>professional_ru-ru.esd</c>.</summary>
    public IReadOnlyList<UupLanguage> Languages() => UupChannels.Languages(Files);
}

public static partial class UupChannels
{
    /// <summary>
    /// Каналы в порядке показа. Последняя Windows 11 — так, как её предложили бы устройству с 23H2;
    /// 24H2 — исходная сборка этой версии (обновление накатывается при сборке образа);
    /// Windows 10 — устройству с 1909, только сборки Windows 10.
    /// </summary>
    public static IReadOnlyList<UupChannel> All { get; } =
    [
        new("windows11", [new("RETAIL", "10.0.22631.1", "ni_release", false), new("RETAIL", "10.0.26100.1", "ge_release", false)], false),
        new("windows11-24h2", [new("RETAIL", "10.0.26100.1", "ge_release", true)], false),
        new("windows10", [new("RETAIL", "10.0.18363.1", "19h1_release", false, MaxMajor: 19999)], false),
        new("release-preview", [new("RP", "10.0.26200.1", "ge_release", false)], false),
        new("beta", [new("WIS", "10.0.26200.1", "ge_release", false)], true),
        new("dev", [new("WIF", "10.0.26200.1", "rs_prerelease", false)], true),
        new("canary", [new("CANARY", "10.0.26200.1", "rs_prerelease", false)], true),
    ];

    public static UupChannel? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Спрашивает все каналы (параллельно); ошибка одного канала не мешает остальным.</summary>
    public static async Task<(IReadOnlyList<UupRelease> Releases, IReadOnlyList<(string Channel, string Problem)> Problems)> FindAsync(
        WindowsUpdateClient client, bool advanced, CancellationToken ct)
    {
        var channels = All.Where(c => advanced || !c.Advanced).ToList();
        await client.ConnectAsync(ct);
        var results = await Task.WhenAll(channels.Select(channel => AskAsync(client, channel, ct)));

        var releases = new List<UupRelease>();
        foreach (var (channel, query, found, _) in results)
        {
            // Один и тот же выпуск в двух каналах (например, Release Preview совпал с обычным) — показываем один раз.
            if (found is null || releases.Any(r => r.UpdateId == found.UpdateId))
                continue;
            releases.Add(new UupRelease(channel.Id, query, found.UpdateId, found.Revision, ReleaseTitle(found, channel.Advanced), found.Build, found.Files));
        }
        var problems = results.Where(r => r.Problem is not null).Select(r => (r.Channel.Id, r.Problem!)).ToList();
        return (releases, problems);
    }

    /// <summary>Самый новый выпуск одного канала; null — канал ничего не предложил (причина — в Problem).</summary>
    public static async Task<(UupRelease? Release, string? Problem)> FindOneAsync(WindowsUpdateClient client, UupChannel channel, CancellationToken ct)
    {
        var (_, query, found, problem) = await AskAsync(client, channel, ct);
        return found is null
            ? (null, problem)
            : (new UupRelease(channel.Id, query, found.UpdateId, found.Revision, ReleaseTitle(found, channel.Advanced), found.Build, found.Files), null);
    }

    static async Task<(UupChannel Channel, UpdateQuery Query, FoundRelease? Found, string? Problem)> AskAsync(
        WindowsUpdateClient client, UupChannel channel, CancellationToken ct)
    {
        var offers = await Task.WhenAll(channel.Queries.Select(async query =>
        {
            try
            {
                return (Query: query, Found: await client.FindReleaseAsync(query, ct), Problem: (string?)null);
            }
            catch (WindowsUpdateException e)
            {
                return (Query: query, Found: (FoundRelease?)null, Problem: e.Message);
            }
        }));
        var best = offers.Where(o => o.Found is not null).OrderByDescending(o => WindowsUpdateClient.BuildKey(o.Found!.Build)).FirstOrDefault();
        var problem = best.Found is not null ? null : offers.Select(o => o.Problem).FirstOrDefault(p => p is not null) ?? "Windows Update offered nothing";
        return (channel, best.Found is not null ? best.Query : channel.Queries[0], best.Found, problem);
    }

    // Версии по сборкам: Windows Update называет выпуски непоследовательно (24H2 приходит как «Cumulative Update
    // … Insider Preview»), поэтому для обычных каналов название строится по номеру сборки.
    static readonly Dictionary<int, string> Versions = new()
    {
        [19045] = "Windows 10, version 22H2",
        [22631] = "Windows 11, version 23H2",
        [26100] = "Windows 11, version 24H2",
        [26200] = "Windows 11, version 25H2",
    };

    static string ReleaseTitle(FoundRelease found, bool insider)
    {
        if (insider)
            return found.Title;
        var major = WindowsUpdateClient.BuildKey(found.Build).Major;
        if (Versions.TryGetValue(major, out var name))
            return name;
        return VersionTitlePattern().IsMatch(found.Title) ? found.Title : $"Windows {(major >= 22000 ? 11 : 10)} (build {found.Build})";
    }

    [GeneratedRegex(@"^Windows 1[01], version \w+$")]
    private static partial Regex VersionTitlePattern();

    internal static IReadOnlyList<UupLanguage> Languages(IReadOnlyList<UupFile> files) => files
        .Select(f => (File: f, Match: EditionImagePattern().Match(f.Name)))
        .Where(x => x.Match.Success)
        .GroupBy(x => LanguageCode(x.Match.Groups["lang"].Value))
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new UupLanguage(g.Key, g
            .Select(x => EditionId(x.Match.Groups["edition"].Value))
            .Distinct()
            .OrderBy(EditionOrder)
            .ThenBy(e => e, StringComparer.Ordinal)
            .ToList()))
        .ToList();

    // Имена в файлах — строчными; в образе и профиле — как в EditionID Windows.
    static readonly Dictionary<string, string> KnownEditions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = "Core",
        ["coren"] = "CoreN",
        ["coresinglelanguage"] = "CoreSingleLanguage",
        ["corecountryspecific"] = "CoreCountrySpecific",
        ["professional"] = "Professional",
        ["professionaln"] = "ProfessionalN",
        ["education"] = "Education",
        ["educationn"] = "EducationN",
        ["enterprise"] = "Enterprise",
        ["enterprisen"] = "EnterpriseN",
        ["professionalworkstation"] = "ProfessionalWorkstation",
        ["professionaleducation"] = "ProfessionalEducation",
        ["cloudedition"] = "CloudEdition",
        ["cloudeditionn"] = "CloudEditionN",
    };

    static string EditionId(string file) =>
        KnownEditions.TryGetValue(file, out var known) ? known : char.ToUpperInvariant(file[0]) + file[1..];

    static int EditionOrder(string editionId) => editionId switch
    {
        "Core" => 0,
        "Professional" => 1,
        _ => 2,
    };

    static string LanguageCode(string file)
    {
        try
        {
            return CultureInfo.GetCultureInfo(file).Name;
        }
        catch (CultureNotFoundException)
        {
            return file;
        }
    }

    [GeneratedRegex(@"^(?<edition>[a-z]+)_(?<lang>[a-z]{2,3}(-[a-z]+)+)\.esd$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionImagePattern();
}

/// <summary>Найденные выпуски — в кэше рабочей папки: <c>cache\uup\&lt;UpdateId&gt;.json</c>.</summary>
public static class UupReleaseCache
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Folder(string workspace) => Path.Combine(workspace, "cache", "uup");

    public static void Save(string workspace, UupRelease release)
    {
        Directory.CreateDirectory(Folder(workspace));
        File.WriteAllText(Path.Combine(Folder(workspace), release.UpdateId + ".json"), JsonSerializer.Serialize(release, Json));
    }

    /// <summary>Самый новый выпуск канала из кэша (найденный раньше); null — таких нет.</summary>
    public static UupRelease? Newest(string workspace, string channel)
    {
        if (!Directory.Exists(Folder(workspace)))
            return null;
        static Version VersionOf(string build) => Version.TryParse(build, out var v) ? v : new Version(0, 0);
        return Directory.EnumerateFiles(Folder(workspace), "*.json")
            .Select(f => Load(workspace, Path.GetFileNameWithoutExtension(f)))
            .OfType<UupRelease>()
            .Where(r => string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase))
            .MaxBy(r => VersionOf(r.Build));
    }

    /// <summary>null — такого выпуска в кэше нет (не искали или кэш очищен).</summary>
    public static UupRelease? Load(string workspace, string updateId)
    {
        if (!Guid.TryParse(updateId, out _))
            return null;
        var path = Path.Combine(Folder(workspace), updateId + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<UupRelease>(File.ReadAllText(path), Json) : null;
    }
}
