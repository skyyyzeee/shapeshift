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

    public static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = new { code, message } }, Options);
}
