using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shapeshift.Core;

/// <summary>Каталог не удалось прочитать: битый JSON, незнакомые поля или недопустимые значения.</summary>
public sealed class CatalogFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Читает каталог твиков из JSON. Структуру проверяет строго (незнакомые поля — ошибка);
/// смысловые проверки — ссылки, переводы, длины команд — делает валидатор движка.
/// </summary>
public static class CatalogReader
{
    public const int CurrentFormatVersion = 1;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TweakCatalog Parse(string json)
    {
        CatalogV1 dto;
        try
        {
            dto = JsonSerializer.Deserialize<CatalogV1>(json, Options)
                ?? throw new CatalogFormatException("Catalog is empty.");
        }
        catch (JsonException e)
        {
            throw new CatalogFormatException($"Catalog is invalid: {e.Message}", e);
        }

        if (dto.FormatVersion != CurrentFormatVersion)
        {
            throw new CatalogFormatException(
                $"Unsupported catalog format version {dto.FormatVersion}; this build understands version {CurrentFormatVersion}.");
        }

        return new TweakCatalog(
            (dto.Categories ?? []).Select(c => new TweakCategory(Require(c.Id, "category id"), Text(c.Name))).ToList(),
            (dto.Tweaks ?? []).Select(ToTweak).ToList(),
            (dto.Apps ?? []).Select(ToApp).ToList());
    }

    static AppMarkup ToApp(AppV1 a)
    {
        var name = Require(a.Name, "app name");
        var safety = a.Safety switch
        {
            "safe" => AppSafety.Safe,
            "risky" => AppSafety.Risky,
            var other => throw new CatalogFormatException($"app '{name}': unknown safety '{other}'; expected 'safe' or 'risky'."),
        };
        return new AppMarkup(name, Text(a.Title), safety, a.Recommended ?? false);
    }

    static Tweak ToTweak(TweakV1 t)
    {
        var id = Require(t.Id, "tweak id");
        return new Tweak(
            id,
            Require(t.Category, $"category of '{id}'"),
            Text(t.Name),
            Text(t.Description),
            t.Releases is { } r
                ? new ReleaseFilter((r.Products ?? []).Select(p => ParseProduct(p, id)).ToList(), r.MinBuild, r.MaxBuild)
                : ReleaseFilter.Any,
            t.Conflicts ?? [],
            t.Advanced ?? false,
            (t.Actions ?? []).Select(a => ToAction(a, id)).ToList());
    }

    static TweakAction ToAction(ActionV1 a, string tweakId)
    {
        var stage = a.Stage switch
        {
            "installer" => Stage.Installer,
            "setup" => Stage.Setup,
            "firstLogon" => Stage.FirstLogon,
            var other => throw new CatalogFormatException(
                $"'{tweakId}': unknown stage '{other}'; expected 'installer', 'setup' or 'firstLogon'."),
        };

        switch (a.Type)
        {
            case "registry":
                if (a.Command is not null)
                    throw new CatalogFormatException($"'{tweakId}': a registry action has no 'command'.");
                var hive = a.Hive switch
                {
                    "HKLM" => RegistryHive.HKLM,
                    "HKCU" => RegistryHive.HKCU,
                    var other => throw new CatalogFormatException($"'{tweakId}': unknown hive '{other}'; expected 'HKLM' or 'HKCU'."),
                };
                var kind = a.ValueType switch
                {
                    "dword" => RegistryValueKind.Dword,
                    "qword" => RegistryValueKind.Qword,
                    "string" => RegistryValueKind.String,
                    "expandString" => RegistryValueKind.ExpandString,
                    var other => throw new CatalogFormatException(
                        $"'{tweakId}': unknown valueType '{other}'; expected 'dword', 'qword', 'string' or 'expandString'."),
                };
                return new RegistryAction(stage, hive, Require(a.Key, $"key in '{tweakId}'"), a.Name, kind, Value(a.Value, kind, tweakId));

            case "command":
                if (a.Hive is not null || a.Key is not null || a.Name is not null || a.ValueType is not null || a.Value is not null)
                    throw new CatalogFormatException($"'{tweakId}': a command action has only 'stage' and 'command'.");
                return new CommandAction(stage, Require(a.Command, $"command in '{tweakId}'"));

            default:
                throw new CatalogFormatException($"'{tweakId}': unknown action type '{a.Type}'; expected 'registry' or 'command'.");
        }
    }

    static string Value(JsonElement? value, RegistryValueKind kind, string tweakId)
    {
        if (value is not { } v)
            throw new CatalogFormatException($"'{tweakId}': registry action has no 'value'.");

        return kind switch
        {
            RegistryValueKind.Dword when v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var d) => d.ToString(),
            RegistryValueKind.Qword when v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var q) => q.ToString(),
            RegistryValueKind.String or RegistryValueKind.ExpandString when v.ValueKind == JsonValueKind.String => v.GetString()!,
            _ => throw new CatalogFormatException($"'{tweakId}': value {v.GetRawText()} does not fit valueType '{kind}'."),
        };
    }

    static WindowsProduct ParseProduct(string product, string tweakId) => product switch
    {
        "windows10" => WindowsProduct.Windows10,
        "windows11" => WindowsProduct.Windows11,
        _ => throw new CatalogFormatException($"'{tweakId}': unknown product '{product}'; expected 'windows10' or 'windows11'."),
    };

    static LocalizedText Text(Dictionary<string, string>? values) => new(values ?? []);

    static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new CatalogFormatException($"Missing {what}.") : value;

    sealed record CatalogV1(int FormatVersion, List<CategoryV1>? Categories, List<TweakV1>? Tweaks, List<AppV1>? Apps);

    sealed record AppV1(string? Name, Dictionary<string, string>? Title, string? Safety, bool? Recommended);

    sealed record CategoryV1(string? Id, Dictionary<string, string>? Name);

    sealed record TweakV1(
        string? Id,
        string? Category,
        Dictionary<string, string>? Name,
        Dictionary<string, string>? Description,
        ReleasesV1? Releases,
        List<string>? Conflicts,
        bool? Advanced,
        List<ActionV1>? Actions);

    sealed record ReleasesV1(List<string>? Products, int? MinBuild, int? MaxBuild);

    sealed record ActionV1(
        string? Type,
        string? Stage,
        string? Hive,
        string? Key,
        string? Name,
        string? ValueType,
        JsonElement? Value,
        string? Command);
}
