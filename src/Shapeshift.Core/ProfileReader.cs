using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shapeshift.Core;

/// <summary>Профиль не удалось прочитать: битый JSON, неизвестная версия формата или недопустимое значение.</summary>
public sealed class ProfileFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Читает профиль из JSON. Формат версионирован полем <c>formatVersion</c>.</summary>
public static class ProfileReader
{
    public const int CurrentFormatVersion = 1;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Profile Parse(string json)
    {
        // Версию читаем отдельно и первой: профиль из будущей версии должен давать ошибку
        // про версию, а не про незнакомые поля.
        var version = ReadFormatVersion(json);
        if (version != CurrentFormatVersion)
        {
            throw new ProfileFormatException(
                $"Unsupported profile format version {version}; this build understands version {CurrentFormatVersion}.");
        }

        ProfileV1 dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProfileV1>(json, Options)
                ?? throw new ProfileFormatException("Profile is empty.");
        }
        catch (JsonException e)
        {
            throw new ProfileFormatException($"Profile is invalid: {e.Message}", e);
        }

        return new Profile(
            ParseArchitecture(dto.Architecture),
            dto.Bypass is { } b
                ? new BypassOptions(b.Tpm ?? false, b.SecureBoot ?? false, b.Ram ?? false, b.Cpu ?? false)
                : BypassOptions.None);
    }

    static int ReadFormatVersion(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new ProfileFormatException($"Profile is not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProfileFormatException("Profile must be a JSON object.");

            if (!doc.RootElement.TryGetProperty("formatVersion", out var v))
                throw new ProfileFormatException("Profile has no 'formatVersion'.");

            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                throw new ProfileFormatException("'formatVersion' must be an integer.");

            return version;
        }
    }

    static Architecture ParseArchitecture(string? value) => value?.ToLowerInvariant() switch
    {
        null or "x64" => Architecture.X64,
        "arm64" => throw new ProfileFormatException("Architecture 'arm64' is not supported yet; only 'x64' is."),
        _ => throw new ProfileFormatException($"Unknown architecture '{value}'; expected 'x64'."),
    };

    sealed record ProfileV1(int FormatVersion, string? Architecture, BypassV1? Bypass);

    sealed record BypassV1(bool? Tpm, bool? SecureBoot, bool? Ram, bool? Cpu);
}
