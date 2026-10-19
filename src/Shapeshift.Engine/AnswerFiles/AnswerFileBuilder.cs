using System.Xml.Linq;
using Shapeshift.Core;

namespace Shapeshift.Engine.AnswerFiles;

/// <summary>
/// Собирает файл ответов из компонентов. Компонент на проход создаётся один раз, при первом обращении;
/// проходы выводятся в порядке, в котором их выполняет установка.
/// </summary>
sealed class AnswerFileBuilder(Architecture architecture)
{
    public static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";
    public static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    public const string WindowsPe = "windowsPE";
    public const string Specialize = "specialize";
    public const string OobeSystem = "oobeSystem";

    static readonly string[] PassOrder =
        [WindowsPe, "offlineServicing", "generalize", Specialize, "auditSystem", "auditUser", OobeSystem];

    readonly Dictionary<(string Pass, string Name), XElement> _components = [];
    readonly List<(string Pass, string Name)> _order = [];

    /// <summary>Компонент прохода; создаётся при первом обращении.</summary>
    public XElement Component(string pass, string name)
    {
        if (_components.TryGetValue((pass, name), out var existing))
            return existing;

        var component = new XElement(Unattend + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", ProcessorArchitecture(architecture)),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"));
        _components[(pass, name)] = component;
        _order.Add((pass, name));
        return component;
    }

    /// <summary>Дочерний элемент компонента; создаётся при первом обращении.</summary>
    public XElement Child(XElement parent, string name)
    {
        var child = parent.Element(Unattend + name);
        if (child is null)
        {
            child = new XElement(Unattend + name);
            parent.Add(child);
        }
        return child;
    }

    /// <summary>Команда, выполняемая во время установки (windowsPE или specialize), в порядке добавления.</summary>
    public void RunSynchronous(string pass, string path)
    {
        var componentName = pass == WindowsPe ? "Microsoft-Windows-Setup" : "Microsoft-Windows-Deployment";
        var list = Child(Component(pass, componentName), "RunSynchronous");
        var order = list.Elements().Count() + 1;
        list.Add(new XElement(Unattend + "RunSynchronousCommand",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Unattend + "Order", order),
            new XElement(Unattend + "Path", path)));
    }

    /// <summary>Команда, выполняемая при первом входе пользователя, в порядке добавления.</summary>
    public void FirstLogonCommand(string commandLine, string description)
    {
        var list = Child(Component(OobeSystem, "Microsoft-Windows-Shell-Setup"), "FirstLogonCommands");
        var order = list.Elements().Count() + 1;
        list.Add(new XElement(Unattend + "SynchronousCommand",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Unattend + "Order", order),
            new XElement(Unattend + "CommandLine", commandLine),
            new XElement(Unattend + "Description", description)));
    }

    public XDocument ToDocument()
    {
        var root = new XElement(Unattend + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", Wcm));
        foreach (var pass in PassOrder)
        {
            var components = _order.Where(k => k.Pass == pass).Select(k => _components[k]).ToList();
            if (components.Count > 0)
                root.Add(new XElement(Unattend + "settings", new XAttribute("pass", pass), components));
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    static string ProcessorArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
    };
}
