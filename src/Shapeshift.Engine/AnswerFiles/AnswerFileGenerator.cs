using System.Text;
using System.Xml.Linq;
using Shapeshift.Core;
using static Shapeshift.Engine.AnswerFiles.AnswerFileBuilder;

namespace Shapeshift.Engine.AnswerFiles;

/// <summary>
/// Строит файл ответов по профилю. Чистая функция: никакого ввода-вывода,
/// одинаковый профиль всегда даёт одинаковый документ.
/// </summary>
public static class AnswerFileGenerator
{
    const string ShellSetup = "Microsoft-Windows-Shell-Setup";

    public static XDocument Generate(Profile profile)
    {
        var file = new AnswerFileBuilder(profile.Architecture);
        AddBypass(file, profile.Bypass);
        if (profile.Setup.WipeDisk0)
            AddDiskLayout(file);
        AddComputerName(file, profile.Setup);
        AddOobe(file, profile.Setup);
        if (profile.Setup.User is { } user)
            AddUser(file, user);
        return file.ToDocument();
    }

    /// <summary>Предупреждения о небезопасных выборах профиля — движок пишет их в лог.</summary>
    public static IEnumerable<string> Warnings(Profile profile)
    {
        if (profile.Setup.User is { PasswordMode: PasswordMode.Store })
            yield return "the user's password is stored in the answer file; anyone with the image can read it.";
        if (profile.Setup.WipeDisk0)
            yield return "the installation will erase disk 0 without asking.";
    }

    // Установщик Windows 11 пропускает проверку требований, если в среде установки
    // заданы эти значения LabConfig. Команды выполняются в windowsPE до проверки.
    static void AddBypass(AnswerFileBuilder file, BypassOptions bypass)
    {
        if (bypass.Tpm) file.RunSynchronous(WindowsPe, LabConfig("BypassTPMCheck"));
        if (bypass.SecureBoot) file.RunSynchronous(WindowsPe, LabConfig("BypassSecureBootCheck"));
        if (bypass.Ram) file.RunSynchronous(WindowsPe, LabConfig("BypassRAMCheck"));
        if (bypass.Cpu) file.RunSynchronous(WindowsPe, LabConfig("BypassCPUCheck"));
    }

    static string LabConfig(string valueName) =>
        $"reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v {valueName} /t REG_DWORD /d 1 /f";

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

        setup.Add(new XElement(Unattend + "ImageInstall",
            new XElement(Unattend + "OSImage",
                new XElement(Unattend + "InstallTo",
                    new XElement(Unattend + "DiskID", 0),
                    new XElement(Unattend + "PartitionID", 4)),
                new XElement(Unattend + "WillShowUI", "OnError"))));
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
