namespace Shapeshift.Core;

/// <summary>Как будет устроена установка: пользователь, имя компьютера, экраны OOBE, разметка диска.</summary>
/// <param name="Edition">Какую редакцию ставить (EditionId: Professional, Core…); null — установщик спросит.</param>
/// <param name="ProductKey">Свой ключ продукта пользователя; null — без ключа.</param>
public sealed record SetupOptions(
    UserAccount? User,
    string? ComputerName,
    bool SkipOobe,
    bool BypassOnlineAccount,
    bool WipeDisk0,
    string? Edition = null,
    string? ProductKey = null)
{
    public static SetupOptions None { get; } = new(null, null, false, false, false);
}

/// <summary>Локальный пользователь, которого создаёт установка. Всегда администратор.</summary>
public sealed record UserAccount(string Name, PasswordMode PasswordMode, string? Password, bool AutoLogon);

public enum PasswordMode
{
    /// <summary>Пароль пустой и должен быть задан при первом входе; в файл ответов ничего не попадает.</summary>
    ChangeAtFirstLogon,

    /// <summary>Пароль хранится в файле ответов — его может прочитать любой, у кого есть образ.</summary>
    Store,
}
