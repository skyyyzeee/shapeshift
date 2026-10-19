using System.Globalization;
using System.Text.RegularExpressions;

namespace Shapeshift.Core;

/// <summary>Форматы, раскладки и часовой пояс будущей системы. Не язык интерфейса.</summary>
/// <param name="Formats">Культура форматов даты и чисел, например ru-RU.</param>
/// <param name="Keyboards">Раскладки; первая — по умолчанию. Имя культуры (en-US) или пара «язык:раскладка» (0419:00000409).</param>
/// <param name="TimeZone">Идентификатор часового пояса Windows, например Russian Standard Time.</param>
public sealed record RegionalOptions(string? Formats, IReadOnlyList<string> Keyboards, string? TimeZone)
{
    public static RegionalOptions None { get; } = new(null, [], null);

    public bool IsEmpty => Formats is null && Keyboards.Count == 0 && TimeZone is null;
}

public static partial class RegionalRules
{
    public static string? CheckCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "culture name is empty";
        try
        {
            var culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return culture.IsNeutralCulture ? $"'{name}' has no region; use a name like 'ru-RU'" : null;
        }
        catch (CultureNotFoundException)
        {
            return $"'{name}' is not a known culture";
        }
    }

    public static string? CheckKeyboard(string? keyboard) =>
        keyboard is not null && KeyboardPairPattern().IsMatch(keyboard) ? null : CheckCulture(keyboard);

    public static string? CheckTimeZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out _)
            ? null
            : $"'{id}' is not a Windows time zone id (for example 'Russian Standard Time')";

    [GeneratedRegex("^[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}$")]
    private static partial Regex KeyboardPairPattern();
}
