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
    IReadOnlyList<EditionSummary> Editions);

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
                    dto.Editions.Select(e => new EditionSummary(e.Index, e.EditionId, e.Name)).ToList()),
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

    sealed record ImageDto(ReleaseDto Release, string Architecture, string? Language, List<EditionDto> Editions);

    sealed record ReleaseDto(string Name, string Product);

    sealed record EditionDto(int Index, string EditionId, string Name);
}
