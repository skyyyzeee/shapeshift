using System.Text;
using System.Xml.Linq;
using Shapeshift.Core;
using Shapeshift.Engine.Programs;
using Shapeshift.Engine.Tweaks;
using static Shapeshift.Engine.AnswerFiles.AnswerFileBuilder;

namespace Shapeshift.Engine.AnswerFiles;

/// <summary>
/// Строит файл ответов по профилю. Чистая функция: никакого ввода-вывода,
/// одинаковый профиль всегда даёт одинаковый документ.
/// </summary>
public static class AnswerFileGenerator
{
    const string ShellSetup = "Microsoft-Windows-Shell-Setup";

    /// <summary>Нулевой ключ: установщик не спрашивает ключ, редакция берётся из InstallFrom. Ничего не активирует.</summary>
    const string NoProductKey = "00000-00000-00000-00000-00000";

    /// <summary>Встроенное приложение, в котором живёт winget.</summary>
    const string WingetPackage = "Microsoft.DesktopAppInstaller";

    /// <param name="profile">Выборы пользователя; <see cref="Problems"/> для него должен быть пуст.</param>
    /// <param name="catalog">Каталог, в котором ищутся твики профиля.</param>
    /// <param name="image">Образ, для которого файл ответов; без него редакцию и язык установщика задать нельзя.</param>
    public static XDocument Generate(Profile profile, TweakCatalog catalog, ImageInfo? image = null)
    {
        // Файл ответов — для образа после сборки: в нём только оставленные редакции, с новыми номерами.
        image = image?.KeepEditions(profile.Editions);
        var file = new AnswerFileBuilder(image?.Architecture ?? profile.Architecture);
        // Твики — первыми: обход ограничений должен выполниться в установщике до проверки требований,
        // а команды первого входа — до выхода из сеанса ради смены пароля.
        var tweakActions = TweakSelection.Resolve(profile.Tweaks, catalog, image?.Release).Tweaks
            .SelectMany(t => t.Actions.Select(a => (Label: t.Id, Action: a)));
        AddActions(file, [.. tweakActions, .. WallpaperActions(profile.Wallpaper)]);
        AddSetupLanguage(file, profile, image);
        AddEdition(file, profile.Setup, image);
        if (profile.Setup.WipeDisk0)
            AddDiskLayout(file);
        AddComputerName(file, profile.Setup);
        AddOobe(file, profile.Setup);
        AddRegional(file, profile.Regional, profile.LanguageChoice.Display);
        // Программы — до выхода из сеанса ради смены пароля (его добавляет AddUser).
        if (profile.Programs.Count > 0)
            file.FirstLogonCommand(InstallScript.Command, "Install programs");
        if (profile.Setup.User is { } user)
            AddUser(file, user);
        return file.ToDocument();
    }

    /// <summary>Почему по профилю нельзя построить файл ответов для этого образа (пусто — можно).</summary>
    public static IEnumerable<string> Problems(Profile profile, TweakCatalog catalog, ImageInfo? image)
    {
        foreach (var problem in TweakSelection.Resolve(profile.Tweaks, catalog, image?.Release).Problems)
            yield return problem;

        var available = image is null ? "" : string.Join(", ", image.Editions.Select(e => e.EditionId));
        if (image is not null)
        {
            foreach (var kept in profile.Editions.Where(id => image.FindEdition(id) is null))
                yield return $"'editions' lists '{kept}', which is not in the image; available: {available}.";
        }

        if (image is not null && DisplayLanguageProblem(profile.LanguageChoice, image.InstalledLanguages) is { } languageProblem)
            yield return languageProblem;

        if (profile.Setup.Edition is not { } edition)
            yield break;

        if (image is null)
            yield return "choosing an edition ('setup.edition') needs the image: pass --iso.";
        else if (image.FindEdition(edition) is null)
            yield return $"edition '{edition}' is not in the image; available: {available}.";
        else if (image.KeepEditions(profile.Editions).FindEdition(edition) is null)
            yield return $"edition '{edition}' is to be installed, but 'editions' removes it from the image.";
    }

    /// <summary>Язык интерфейса по умолчанию должен оказаться в образе: уже быть в нём или добавляться.</summary>
    public static string? DisplayLanguageProblem(LanguageOptions languages, IReadOnlyList<string> imageLanguages)
    {
        if (languages.Display is not { } display || imageLanguages.Contains(display, StringComparer.OrdinalIgnoreCase) ||
            languages.Add.Contains(display, StringComparer.OrdinalIgnoreCase))
            return null;
        return $"'languages.display' is {display}, but the image has only {string.Join(", ", imageLanguages.Union(languages.Add, StringComparer.OrdinalIgnoreCase))}; add it to 'languages.add'.";
    }

    /// <summary>Предупреждения о небезопасных выборах профиля — движок пишет их в лог.</summary>
    public static IEnumerable<string> Warnings(Profile profile, ImageInfo? image = null)
    {
        if (profile.Setup.User is { PasswordMode: PasswordMode.Store })
            yield return "the user's password is stored in the answer file; anyone with the image can read it.";
        if (profile.Setup.WipeDisk0)
            yield return "the installation will erase disk 0 without asking.";
        if (profile.Programs.OfType<WingetProgram>().Any() &&
            profile.RemoveApps.Contains(WingetPackage, StringComparer.OrdinalIgnoreCase))
            yield return $"winget programs need App Installer, but 'removeApps' removes {WingetPackage}; they will be skipped.";
        if (NeedsSetupLanguage(profile) && image?.Language is null)
            yield return "the image language is unknown, so the installer will still ask for its language (pass --iso).";
    }

    static bool NeedsSetupLanguage(Profile profile) => profile.Setup.SkipOobe || !profile.Regional.IsEmpty;

    // Первый экран установщика (язык, форматы, раскладка) пропускается, только если задан язык установщика,
    // а он должен быть в образе — поэтому берётся из самого образа.
    static void AddSetupLanguage(AnswerFileBuilder file, Profile profile, ImageInfo? image)
    {
        if (!NeedsSetupLanguage(profile) || image?.Language is not { } language)
            return;

        var regional = profile.Regional;
        var formats = regional.Formats ?? language;
        file.Component(WindowsPe, "Microsoft-Windows-International-Core-WinPE").Add(
            new XElement(Unattend + "SetupUILanguage", new XElement(Unattend + "UILanguage", language)),
            new XElement(Unattend + "InputLocale", regional.Keyboards.Count > 0 ? string.Join(";", regional.Keyboards) : language),
            new XElement(Unattend + "SystemLocale", formats),
            // Язык Windows здесь не задаётся: Setup сверяет его со списком языков носителя (lang.ini), и с языком,
            // добавленным в install.wim, отбрасывает весь компонент — и спрашивает язык. Он — на этапе oobeSystem.
            new XElement(Unattend + "UILanguage", language),
            new XElement(Unattend + "UserLocale", formats));
    }

    // Редакция выбирается номером в образе; ключ — свой ключ пользователя или нулевой (без запроса ключа).
    static void AddEdition(AnswerFileBuilder file, SetupOptions setup, ImageInfo? image)
    {
        var edition = setup.Edition is { } id && image is not null ? image.FindEdition(id) : null;
        if (edition is null && setup.ProductKey is null)
            return;

        var windowsSetup = file.Component(WindowsPe, "Microsoft-Windows-Setup");
        file.Child(windowsSetup, "UserData").Add(new XElement(Unattend + "ProductKey",
            new XElement(Unattend + "Key", setup.ProductKey ?? NoProductKey),
            new XElement(Unattend + "WillShowUI", setup.ProductKey is null ? "Never" : "OnError")));

        if (edition is not null)
        {
            var osImage = file.Child(file.Child(windowsSetup, "ImageInstall"), "OSImage");
            osImage.Add(new XElement(Unattend + "InstallFrom",
                new XElement(Unattend + "MetaData",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(Unattend + "Key", "/IMAGE/INDEX"),
                    new XElement(Unattend + "Value", edition.Index))));
        }
    }

    // Язык интерфейса — тоже здесь: на этапе oobeSystem он становится языком системы и пользователей.
    static void AddRegional(AnswerFileBuilder file, RegionalOptions regional, string? displayLanguage)
    {
        if (regional.Keyboards.Count > 0 || regional.Formats is not null || displayLanguage is not null)
        {
            var international = file.Component(OobeSystem, "Microsoft-Windows-International-Core");
            if (regional.Keyboards.Count > 0)
                international.Add(new XElement(Unattend + "InputLocale", string.Join(";", regional.Keyboards)));
            if (regional.Formats is { } formats)
                international.Add(new XElement(Unattend + "SystemLocale", formats));
            if (displayLanguage is not null)
                international.Add(new XElement(Unattend + "UILanguage", displayLanguage));
            if (regional.Formats is { } userFormats)
                international.Add(new XElement(Unattend + "UserLocale", userFormats));
        }

        if (regional.TimeZone is { } timeZone)
            file.Component(OobeSystem, ShellSetup).Add(new XElement(Unattend + "TimeZone", timeZone));
    }

    // Действия по этапам, в порядке твиков каталога (обои — после них). HKCU на этапе установки пишется
    // в профиль пользователя по умолчанию — одним блоком между его загрузкой и выгрузкой.
    static void AddActions(AnswerFileBuilder file, IReadOnlyList<(string Label, TweakAction Action)> actions)
    {
        foreach (var (_, action) in actions.Where(x => x.Action.Stage == Stage.Installer))
            file.RunSynchronous(WindowsPe, Render(action));

        var setup = actions.Where(x => x.Action.Stage == Stage.Setup).Select(x => x.Action).ToList();
        bool IsDefaultUser(TweakAction a) => a is RegistryAction { Hive: RegistryHive.HKCU };
        foreach (var action in setup.Where(a => !IsDefaultUser(a)))
            file.RunSynchronous(Specialize, Render(action));
        if (setup.Any(IsDefaultUser))
        {
            file.RunSynchronous(Specialize, RegistryCommand.LoadDefaultUser);
            foreach (var action in setup.Where(IsDefaultUser))
                file.RunSynchronous(Specialize, Render(action));
            file.RunSynchronous(Specialize, RegistryCommand.UnloadDefaultUser);
        }

        foreach (var (label, action) in actions.Where(x => x.Action.Stage == Stage.FirstLogon))
            file.FirstLogonCommand(Render(action), label);
    }

    /// <summary>
    /// Обои из своей картинки (файл кладёт сборка, см. <see cref="WallpaperOptions.TargetPath"/>).
    /// Закреплённые — политиками: «Обои рабочего стола» и запрет смены в профиле по умолчанию (все новые
    /// пользователи) и PersonalizationCSP для компьютера. Незакреплённые — обычные настройки первого пользователя
    /// при первом входе: раньше их перезаписывает тема Windows.
    /// </summary>
    static IEnumerable<(string Label, TweakAction Action)> WallpaperActions(WallpaperOptions? wallpaper)
    {
        if (wallpaper is null)
            yield break;
        const string label = "wallpaper";
        var path = wallpaper.TargetPath;
        if (wallpaper.Locked)
        {
            const string csp = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";
            yield return (label, new RegistryAction(Stage.Setup, RegistryHive.HKLM, csp, "DesktopImagePath", RegistryValueKind.String, path));
            yield return (label, new RegistryAction(Stage.Setup, RegistryHive.HKLM, csp, "DesktopImageUrl", RegistryValueKind.String, path));
            yield return (label, new RegistryAction(Stage.Setup, RegistryHive.HKLM, csp, "DesktopImageStatus", RegistryValueKind.Dword, "1"));
            // Стиль в политике: 0 по центру, 1 замостить, 2 растянуть, 3 вписать, 4 заполнить, 5 расширить.
            var policyStyle = wallpaper.Style switch
            {
                WallpaperStyle.Center => "0",
                WallpaperStyle.Tile => "1",
                WallpaperStyle.Stretch => "2",
                WallpaperStyle.Fit => "3",
                WallpaperStyle.Span => "5",
                _ => "4",
            };
            const string system = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
            yield return (label, new RegistryAction(Stage.Setup, RegistryHive.HKCU, system, "Wallpaper", RegistryValueKind.String, path));
            yield return (label, new RegistryAction(Stage.Setup, RegistryHive.HKCU, system, "WallpaperStyle", RegistryValueKind.String, policyStyle));
            yield return (label, new RegistryAction(Stage.Setup, RegistryHive.HKCU,
                @"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop", "NoChangingWallPaper", RegistryValueKind.Dword, "1"));
            yield break;
        }

        // Обычные настройки: 10 заполнить, 6 вписать, 2 растянуть, 0 по центру (и замостить — с TileWallpaper), 22 расширить.
        var style = wallpaper.Style switch
        {
            WallpaperStyle.Fit => "6",
            WallpaperStyle.Stretch => "2",
            WallpaperStyle.Center or WallpaperStyle.Tile => "0",
            WallpaperStyle.Span => "22",
            _ => "10",
        };
        const string desktop = @"Control Panel\Desktop";
        yield return (label, new RegistryAction(Stage.FirstLogon, RegistryHive.HKCU, desktop, "Wallpaper", RegistryValueKind.String, path));
        yield return (label, new RegistryAction(Stage.FirstLogon, RegistryHive.HKCU, desktop, "WallpaperStyle", RegistryValueKind.String, style));
        yield return (label, new RegistryAction(Stage.FirstLogon, RegistryHive.HKCU, desktop, "TileWallpaper", RegistryValueKind.String,
            wallpaper.Style == WallpaperStyle.Tile ? "1" : "0"));
        yield return (label, new CommandAction(Stage.FirstLogon, "RUNDLL32.EXE user32.dll,UpdatePerUserSystemParameters"));
    }

    static string Render(TweakAction action) => action switch
    {
        RegistryAction registry => RegistryCommand.Add(registry),
        CommandAction command => command.Command,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    // Раскладка диска 0 по образцу Microsoft для UEFI/GPT: WinRE, EFI, MSR, Windows на всё остальное.
    static void AddDiskLayout(AnswerFileBuilder file)
    {
        var setup = file.Component(WindowsPe, "Microsoft-Windows-Setup");

        XElement Create(int order, string type, int? sizeMb) => new(Unattend + "CreatePartition",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Unattend + "Order", order),
            new XElement(Unattend + "Type", type),
            sizeMb is { } size
                ? new XElement(Unattend + "Size", size)
                : new XElement(Unattend + "Extend", "true"));

        XElement Modify(int order, params object[] settings) => new(Unattend + "ModifyPartition",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Unattend + "Order", order),
            new XElement(Unattend + "PartitionID", order),
            settings);

        setup.Add(new XElement(Unattend + "DiskConfiguration",
            new XElement(Unattend + "Disk",
                new XAttribute(Wcm + "action", "add"),
                new XElement(Unattend + "DiskID", 0),
                new XElement(Unattend + "WillWipeDisk", "true"),
                new XElement(Unattend + "CreatePartitions",
                    Create(1, "Primary", 1024),
                    Create(2, "EFI", 300),
                    Create(3, "MSR", 16),
                    Create(4, "Primary", null)),
                new XElement(Unattend + "ModifyPartitions",
                    Modify(1,
                        new XElement(Unattend + "Label", "WinRE"),
                        new XElement(Unattend + "Format", "NTFS"),
                        new XElement(Unattend + "TypeID", "de94bba4-06d1-4d40-a16a-bfd50179d6ac")),
                    Modify(2,
                        new XElement(Unattend + "Label", "System"),
                        new XElement(Unattend + "Format", "FAT32")),
                    Modify(3),
                    Modify(4,
                        new XElement(Unattend + "Label", "Windows"),
                        new XElement(Unattend + "Letter", "C"),
                        new XElement(Unattend + "Format", "NTFS")))),
            new XElement(Unattend + "WillShowUI", "OnError")));

        file.Child(file.Child(setup, "ImageInstall"), "OSImage").Add(
            new XElement(Unattend + "InstallTo",
                new XElement(Unattend + "DiskID", 0),
                new XElement(Unattend + "PartitionID", 4)),
            new XElement(Unattend + "WillShowUI", "OnError"));
    }

    static void AddComputerName(AnswerFileBuilder file, SetupOptions setup)
    {
        if (setup.ComputerName is { } name)
            file.Component(Specialize, ShellSetup).Add(new XElement(Unattend + "ComputerName", name));
    }

    static void AddOobe(AnswerFileBuilder file, SetupOptions setup)
    {
        // Без сетевого подключения Windows 11 предлагает продолжить без аккаунта Microsoft только с этим флагом.
        if (setup.BypassOnlineAccount)
        {
            file.RunSynchronous(Specialize,
                "reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE\" /v BypassNRO /t REG_DWORD /d 1 /f");
        }

        var settings = new List<XElement>();
        if (setup.SkipOobe)
        {
            // Лицензию показывает и установщик (windowsPE), и OOBE — принимаем в обоих местах.
            file.Child(file.Component(WindowsPe, "Microsoft-Windows-Setup"), "UserData")
                .Add(new XElement(Unattend + "AcceptEula", "true"));
            settings.Add(new XElement(Unattend + "HideEULAPage", "true"));
            settings.Add(new XElement(Unattend + "HideOEMRegistrationScreen", "true"));
            // 3 — не отправлять дополнительные данные: экран параметров конфиденциальности не показывается.
            settings.Add(new XElement(Unattend + "ProtectYourPC", 3));
        }
        if (setup.BypassOnlineAccount)
        {
            settings.Add(new XElement(Unattend + "HideOnlineAccountScreens", "true"));
            settings.Add(new XElement(Unattend + "HideWirelessSetupInOOBE", "true"));
        }
        if (setup.User is not null)
            settings.Add(new XElement(Unattend + "HideLocalAccountScreen", "true"));

        if (settings.Count > 0)
            file.Component(OobeSystem, ShellSetup).Add(new XElement(Unattend + "OOBE", settings));
    }

    static void AddUser(AnswerFileBuilder file, UserAccount user)
    {
        var shell = file.Component(OobeSystem, ShellSetup);
        var password = user.PasswordMode == PasswordMode.Store ? user.Password! : "";

        shell.Add(new XElement(Unattend + "UserAccounts",
            new XElement(Unattend + "LocalAccounts",
                new XElement(Unattend + "LocalAccount",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(Unattend + "Name", user.Name),
                    new XElement(Unattend + "DisplayName", user.Name),
                    new XElement(Unattend + "Group", "Administrators"),
                    PasswordElement(password)))));

        if (user.AutoLogon)
        {
            shell.Add(new XElement(Unattend + "AutoLogon",
                new XElement(Unattend + "Enabled", "true"),
                // Счётчик уменьшается при каждом входе; такое значение делает автовход фактически постоянным.
                new XElement(Unattend + "LogonCount", 9999999),
                new XElement(Unattend + "Username", user.Name),
                PasswordElement(password)));
        }

        if (user.PasswordMode == PasswordMode.ChangeAtFirstLogon)
        {
            // Учётная запись создаётся с пустым паролем. При первом входе помечаем, что пароль нужно сменить,
            // и сразу выходим из сеанса: экран входа попросит задать пароль. Имя проверено NameRules,
            // в нём нет % и кавычек.
            file.FirstLogonCommand($"cmd.exe /c net user \"{user.Name}\" /logonpasswordchg:yes", "Require a new password");
            file.FirstLogonCommand("shutdown.exe /l", "Sign out to set the password");
        }
    }

    // Пустой пароль хранится как есть, непустой — в стандартной для файлов ответов форме
    // base64(UTF-16LE(пароль + "Password")). Это скрывает пароль от случайного взгляда, но не шифрует его.
    static XElement PasswordElement(string password) => password.Length == 0
        ? new XElement(Unattend + "Password",
            new XElement(Unattend + "Value", ""),
            new XElement(Unattend + "PlainText", "true"))
        : new XElement(Unattend + "Password",
            new XElement(Unattend + "Value", Convert.ToBase64String(Encoding.Unicode.GetBytes(password + "Password"))),
            new XElement(Unattend + "PlainText", "false"));
}
