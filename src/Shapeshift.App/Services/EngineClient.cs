using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Shapeshift.App.Services;

/// <summary>Сведения об образе из <c>inspect --json</c>.</summary>
public sealed record ImageSummary(
    string Product,
    string ReleaseName,
    string Architecture,
    string? Language,
    IReadOnlyList<EditionSummary> Editions,
    IReadOnlyList<string>? Languages = null)
{
    /// <summary>Языки интерфейса в образе (с языком по умолчанию).</summary>
    public IReadOnlyList<string> InstalledLanguages => Languages is { Count: > 0 } languages ? languages : Language is null ? [] : [Language];
}

public sealed record EditionSummary(int Index, string EditionId, string Name);

/// <summary>Образ прочитан — или движок отказал с кодом причины.</summary>
public sealed record InspectResult(ImageSummary? Image, string? ErrorCode, string? ErrorMessage);

/// <summary>Сводка «что будет сделано» из <c>build --dry-run --json</c>.</summary>
public sealed record PlanSummary(
    string OutputIso,
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<SpaceNeed> Space,
    IReadOnlyList<string> Warnings);

public sealed record PlanStep(string Kind, string? Volume, long Bytes);

public sealed record SpaceNeed(string Volume, long RequiredBytes, long AvailableBytes);

public sealed record PlanResult(PlanSummary? Plan, string? ErrorCode, string? ErrorMessage);

/// <summary>Выпуск из Windows Update; <paramref name="Advanced"/> — канал Insider (Beta, Dev, Canary).</summary>
public sealed record UupReleaseSummary(string UpdateId, string Title, string Build, string Channel, bool Advanced);

/// <summary>Выпуски или текст ошибки (нет сети, служба не ответила).</summary>
public sealed record UupReleasesResult(IReadOnlyList<UupReleaseSummary> Releases, string? Error);

public sealed record UupLanguageSummary(string Code, IReadOnlyList<string> Editions);

/// <summary>Языки, которые можно добавить, или текст ошибки (нет сети, неподходящий выпуск).</summary>
public sealed record LanguagesResult(IReadOnlyList<string> Available, string? Error);

/// <summary>Объём скачивания или текст ошибки.</summary>
public sealed record UupFilesResult(long Bytes, long CachedBytes, string? Error);

/// <summary>
/// Окно не работает с образами само: всё делает движок — отдельный процесс (ADR-0003).
/// Чтение образа не требует прав администратора, поэтому движок здесь запускается без повышения.
/// </summary>
public sealed class EngineClient
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string EnginePath { get; } = Path.Combine(AppContext.BaseDirectory, "Shapeshift.Engine.exe");

    public async Task<InspectResult> InspectAsync(string isoPath, CancellationToken ct = default)
    {
        var (exitCode, output, error) = await RunAsync(["inspect", "--iso", isoPath, "--json"], ct);

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("error", out var problem))
            {
                return new InspectResult(null,
                    problem.GetProperty("code").GetString(),
                    problem.GetProperty("message").GetString());
            }

            var dto = doc.RootElement.Deserialize<ImageDto>(Json)!;
            return new InspectResult(
                new ImageSummary(
                    dto.Release.Product,
                    dto.Release.Name,
                    dto.Architecture,
                    dto.Language,
                    dto.Editions.Select(e => new EditionSummary(e.Index, e.EditionId, e.Name)).ToList(),
                    dto.Languages),
                null, null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or NullReferenceException)
        {
            return new InspectResult(null, "engine-failed", $"exit {exitCode}: {error.Trim()}");
        }
    }

    /// <summary>Пробный прогон без повышения: план, место и предупреждения — ничего не собирается.</summary>
    public async Task<PlanResult> PlanAsync(string profilePath, string workspace, CancellationToken ct = default)
    {
        var (exitCode, output, error) = await RunAsync(
            ["build", "--profile", profilePath, "--workspace", workspace, "--dry-run", "--json"], ct);

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var problem))
                return new PlanResult(null, problem.GetProperty("code").GetString(), problem.GetProperty("message").GetString());

            return new PlanResult(
                new PlanSummary(
                    root.GetProperty("outputIso").GetString()!,
                    root.GetProperty("steps").EnumerateArray().Select(s => new PlanStep(
                        s.GetProperty("kind").GetString()!,
                        s.GetProperty("volume").GetString(),
                        s.GetProperty("bytes").GetInt64())).ToList(),
                    root.GetProperty("space").EnumerateArray().Select(s => new SpaceNeed(
                        s.GetProperty("volume").GetString()!,
                        s.GetProperty("requiredBytes").GetInt64(),
                        s.GetProperty("availableBytes").GetInt64())).ToList(),
                    root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList()),
                null, null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new PlanResult(null, "engine-failed", $"exit {exitCode}: {error.Trim()}");
        }
    }

    /// <summary>Только файл ответов — тот же, что положит сборка. null — успех, иначе текст ошибки движка.</summary>
    public async Task<string?> AnswerFileAsync(string profilePath, string outPath, CancellationToken ct = default)
    {
        var (exitCode, _, error) = await RunAsync(["answerfile", "--profile", profilePath, "--out", outPath], ct);
        if (exitCode == 0)
            return null;
        var problems = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("warning: ", StringComparison.Ordinal))
            .Select(l => l.StartsWith("error: ", StringComparison.Ordinal) ? l["error: ".Length..] : l);
        return string.Join("\n", problems);
    }

    /// <summary>Каталог твиков с применимостью к образу. null — каталог прочитать не удалось.</summary>
    public async Task<JsonDocument?> CatalogAsync(string? isoPath, CancellationToken ct = default)
    {
        string[] args = isoPath is null ? ["catalog", "list"] : ["catalog", "list", "--iso", isoPath];
        var (exitCode, output, _) = await RunAsync(args, ct);
        if (exitCode != 0)
            return null;
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Встроенные приложения редакции (null — первой) с разметкой каталога, из <c>apps list --json</c>.
    /// null — список прочитать не удалось.
    /// </summary>
    public async Task<JsonDocument?> AppsAsync(string isoPath, string? edition, CancellationToken ct = default)
    {
        string[] args = edition is null
            ? ["apps", "list", "--iso", isoPath, "--json"]
            : ["apps", "list", "--iso", isoPath, "--edition", edition, "--json"];
        var (exitCode, output, _) = await RunAsync(args, ct);
        if (exitCode != 0)
            return null;
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Выпуски Windows из Windows Update по каналам (<c>uup releases --json</c>).</summary>
    public async Task<UupReleasesResult> UupReleasesAsync(bool advanced, string workspace, CancellationToken ct = default)
    {
        string[] args = ["uup", "releases", "--json", "--workspace", workspace, .. advanced ? new[] { "--advanced" } : []];
        var (exitCode, output, error) = await RunAsync(args, ct);
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var problem))
                return new UupReleasesResult([], problem.GetProperty("message").GetString());
            return new UupReleasesResult(
                root.GetProperty("releases").EnumerateArray().Select(r => new UupReleaseSummary(
                    r.GetProperty("updateId").GetString()!,
                    r.GetProperty("title").GetString()!,
                    r.GetProperty("build").GetString()!,
                    r.GetProperty("channel").GetString()!,
                    r.GetProperty("advanced").GetBoolean())).ToList(),
                null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UupReleasesResult([], $"exit {exitCode}: {error.Trim()}");
        }
    }

    /// <summary>Языки и готовые редакции выпуска (<c>uup release --json</c>); null — не удалось.</summary>
    public async Task<IReadOnlyList<UupLanguageSummary>?> UupLanguagesAsync(string updateId, string workspace, CancellationToken ct = default)
    {
        var (exitCode, output, _) = await RunAsync(["uup", "release", "--update", updateId, "--json", "--workspace", workspace], ct);
        if (exitCode != 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.GetProperty("languages").EnumerateArray().Select(l => new UupLanguageSummary(
                l.GetProperty("code").GetString()!,
                l.GetProperty("editions").EnumerateArray().Select(e => e.GetString()!).ToList())).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Языки, которые можно добавить (<c>languages --json</c>): для своего ISO — из выпуска той же базовой сборки
    /// (нужен интернет), для UUP — из выбранного выпуска.
    /// </summary>
    public async Task<LanguagesResult> LanguagesAsync(string? isoPath, string? updateId, string workspace, CancellationToken ct = default)
    {
        string[] source = isoPath is not null ? ["--iso", isoPath] : ["--update", updateId!];
        var (exitCode, output, error) = await RunAsync(["languages", .. source, "--workspace", workspace, "--json"], ct);
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var problem))
                return new LanguagesResult([], problem.GetProperty("message").GetString());
            return new LanguagesResult(root.GetProperty("available").EnumerateArray().Select(l => l.GetString()!).ToList(), null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new LanguagesResult([], $"exit {exitCode}: {error.Trim()}");
        }
    }

    /// <summary>Сколько скачать для языка и редакций и сколько уже в кэше (<c>uup files --json</c>).</summary>
    public async Task<UupFilesResult> UupFilesAsync(
        string updateId, string language, IReadOnlyList<string> editions, string workspace, bool withUpdates, CancellationToken ct = default)
    {
        string[] args = ["uup", "files", "--update", updateId, "--lang", language, .. editions.SelectMany(e => new[] { "--edition", e }), "--workspace", workspace, "--media-only", .. withUpdates ? new[] { "--with-updates" } : [], "--json"];
        var (exitCode, output, error) = await RunAsync(args, ct);
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var problem))
                return new UupFilesResult(0, 0, problem.GetProperty("message").GetString());
            return new UupFilesResult(root.GetProperty("bytes").GetInt64(), root.GetProperty("cachedBytes").GetInt64(), null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UupFilesResult(0, 0, $"exit {exitCode}: {error.Trim()}");
        }
    }

    async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, CancellationToken ct)
    {
        if (!File.Exists(EnginePath))
            return (-1, "", $"engine not found: {EnginePath}");

        var start = new ProcessStartInfo(EnginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await output, await error);
    }

    sealed record ImageDto(ReleaseDto Release, string Architecture, string? Language, List<EditionDto> Editions, List<string>? Languages);

    sealed record ReleaseDto(string Name, string Product);

    sealed record EditionDto(int Index, string EditionId, string Name);
}
