using System.Xml.Linq;
using Shapeshift.Core;

namespace Shapeshift.Engine.AnswerFiles;

/// <summary>
/// Строит файл ответов по профилю. Чистая функция: никакого ввода-вывода,
/// одинаковый профиль всегда даёт одинаковый документ.
/// </summary>
public static class AnswerFileGenerator
{
    static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";
    static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    const string LabConfigKey = @"HKLM\SYSTEM\Setup\LabConfig";

    public static XDocument Generate(Profile profile)
    {
        var root = new XElement(Unattend + "unattend",
            new XAttribute(XNamespace.Xmlns + "wcm", Wcm));

        var windowsPe = WindowsPePass(profile);
        if (windowsPe is not null)
            root.Add(windowsPe);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    static XElement? WindowsPePass(Profile profile)
    {
        var commands = BypassCommands(profile.Bypass).ToList();
        if (commands.Count == 0)
            return null;

        return new XElement(Unattend + "settings",
            new XAttribute("pass", "windowsPE"),
            Component(profile.Architecture, "Microsoft-Windows-Setup",
                new XElement(Unattend + "RunSynchronous",
                    commands.Select((path, i) => new XElement(Unattend + "RunSynchronousCommand",
                        new XAttribute(Wcm + "action", "add"),
                        new XElement(Unattend + "Order", i + 1),
                        new XElement(Unattend + "Path", path))))));
    }

    // Установщик Windows 11 пропускает проверку требований, если в среде установки
    // заданы эти значения LabConfig. Команды выполняются в windowsPE до проверки.
    static IEnumerable<string> BypassCommands(BypassOptions bypass)
    {
        if (bypass.Tpm) yield return LabConfig("BypassTPMCheck");
        if (bypass.SecureBoot) yield return LabConfig("BypassSecureBootCheck");
        if (bypass.Ram) yield return LabConfig("BypassRAMCheck");
        if (bypass.Cpu) yield return LabConfig("BypassCPUCheck");
    }

    static string LabConfig(string valueName) =>
        $"reg.exe add \"{LabConfigKey}\" /v {valueName} /t REG_DWORD /d 1 /f";

    static XElement Component(Architecture architecture, string name, params object[] content) =>
        new(Unattend + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", ProcessorArchitecture(architecture)),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            content);

    static string ProcessorArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
    };
}
