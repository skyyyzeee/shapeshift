using System.Text.RegularExpressions;

namespace Shapeshift.Core;

/// <summary>
/// Правила для имён, которые попадают в будущую систему. Общие для профиля и окна (подсказки у полей).
/// Возвращают текст ошибки или null, если имя подходит.
/// </summary>
public static partial class NameRules
{
    public const int MaxUserNameLength = 20;
    public const int MaxComputerNameLength = 15;
    public const int MaxPasswordLength = 127;

    // Запрещены Windows для локальных учётных записей; % — ещё и потому, что имя попадает в команду cmd.
    const string ForbiddenUserNameChars = "\"/\\[]:;|=,+*?<>@%";

    static readonly string[] ReservedUserNames =
        ["Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount", "defaultuser0", "Администратор", "Гость"];

    public static string? CheckUserName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "user name is empty";
        if (name.Length > MaxUserNameLength)
            return $"user name is longer than {MaxUserNameLength} characters";
        if (name.Any(c => char.IsControl(c) || ForbiddenUserNameChars.Contains(c)))
            return $"user name contains a forbidden character (one of {ForbiddenUserNameChars})";
        if (name.Trim('.', ' ').Length == 0 || name != name.Trim())
            return "user name cannot start or end with a space or consist only of dots";
        if (ReservedUserNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return $"'{name}' is a built-in account name";
        return null;
    }

    public static string? CheckComputerName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "computer name is empty";
        if (name.Length > MaxComputerNameLength)
            return $"computer name is longer than {MaxComputerNameLength} characters";
        if (!ComputerNamePattern().IsMatch(name))
            return "computer name may contain only Latin letters, digits and hyphens";
        if (name.All(char.IsAsciiDigit))
            return "computer name cannot consist only of digits";
        return null;
    }

    public static string? CheckPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "password is empty";
        if (password.Length > MaxPasswordLength)
            return $"password is longer than {MaxPasswordLength} characters";
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex ComputerNamePattern();
}
