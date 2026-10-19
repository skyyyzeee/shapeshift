using Shapeshift.Core;

namespace Shapeshift.Engine.Tweaks;

/// <summary>Команды <c>reg.exe</c> для действий реестра из каталога.</summary>
static class RegistryCommand
{
    /// <summary>Куда загружается профиль пользователя по умолчанию на этапе установки.</summary>
    public const string DefaultUserRoot = @"HKU\DefaultUser";

    public const string LoadDefaultUser =
        $"cmd.exe /c reg.exe load \"{DefaultUserRoot}\" \"%SystemDrive%\\Users\\Default\\NTUSER.DAT\"";

    public const string UnloadDefaultUser = $"reg.exe unload \"{DefaultUserRoot}\"";

    /// <summary>Корень раздела для действия: HKCU на этапе установки — профиль по умолчанию (все новые пользователи).</summary>
    public static string Root(RegistryAction action) => (action.Hive, action.Stage) switch
    {
        (RegistryHive.HKCU, Stage.Setup) => DefaultUserRoot,
        (RegistryHive.HKCU, _) => "HKCU",
        _ => "HKLM",
    };

    public static string Add(RegistryAction action)
    {
        var name = action.Name is null ? "/ve" : $"/v {QuoteIfNeeded(action.Name)}";
        var (type, data) = action.Kind switch
        {
            RegistryValueKind.Dword => ("REG_DWORD", action.Value),
            RegistryValueKind.Qword => ("REG_QWORD", action.Value),
            RegistryValueKind.String => ("REG_SZ", $"\"{action.Value}\""),
            RegistryValueKind.ExpandString => ("REG_EXPAND_SZ", $"\"{action.Value}\""),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null),
        };
        return $"reg.exe add \"{Root(action)}\\{action.Key}\" {name} /t {type} /d {data} /f";
    }

    static string QuoteIfNeeded(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
