using System.Text.RegularExpressions;
using Shapeshift.Core;

namespace Shapeshift.Engine.Tweaks;

/// <summary>Смысловые проверки каталога: ссылки, переводы, применимость и то, во что превратятся действия.</summary>
public static partial class CatalogValidator
{
    public static readonly string[] RequiredLanguages = ["en", "ru"];

    /// <summary>Ограничения файла ответов на длину команды.</summary>
    public const int MaxRunSynchronousLength = 259;
    public const int MaxFirstLogonLength = 1024;

    public static IReadOnlyList<string> Validate(TweakCatalog catalog)
    {
        var problems = new List<string>();

        foreach (var id in Duplicates(catalog.Categories.Select(c => c.Id)))
            problems.Add($"category '{id}' is defined more than once.");
        foreach (var id in Duplicates(catalog.Tweaks.Select(t => t.Id)))
            problems.Add($"tweak '{id}' is defined more than once.");

        foreach (var category in catalog.Categories)
            CheckText(problems, $"category '{category.Id}'", "name", category.Name);

        foreach (var name in Duplicates(catalog.Apps.Select(a => a.Name.ToLowerInvariant())))
            problems.Add($"app '{name}' is described more than once.");
        foreach (var app in catalog.Apps)
        {
            if (!AppNamePattern().IsMatch(app.Name))
                problems.Add($"app '{app.Name}': name must be a package name without version, like Microsoft.BingNews or MSTeams.");
            CheckText(problems, $"app '{app.Name}'", "title", app.Title);
            if (app.Recommended && app.Safety == AppSafety.Risky)
                problems.Add($"app '{app.Name}': a risky app cannot be recommended for removal.");
        }

        foreach (var id in Duplicates(catalog.Programs.Select(p => p.WingetId.ToLowerInvariant())))
            problems.Add($"program '{id}' is listed more than once.");
        foreach (var program in catalog.Programs)
        {
            if (!ProgramRules.IsWingetId(program.WingetId))
                problems.Add($"program '{program.WingetId}': not a winget package id like Telegram.TelegramDesktop.");
            CheckText(problems, $"program '{program.WingetId}'", "title", program.Title);
        }

        var categories = catalog.Categories.Select(c => c.Id).ToHashSet();
        var ids = catalog.Tweaks.Select(t => t.Id).ToHashSet();
        foreach (var tweak in catalog.Tweaks)
            CheckTweak(problems, tweak, categories, ids);

        return problems;
    }

    static void CheckTweak(List<string> problems, Tweak tweak, HashSet<string> categories, HashSet<string> ids)
    {
        var where = $"tweak '{tweak.Id}'";

        if (!TweakIdPattern().IsMatch(tweak.Id))
            problems.Add($"{where}: id must look like 'category.name' (lowercase letters, digits, hyphens).");
        if (!categories.Contains(tweak.Category))
            problems.Add($"{where}: unknown category '{tweak.Category}'.");

        CheckText(problems, where, "name", tweak.Name);
        CheckText(problems, where, "description", tweak.Description);

        foreach (var conflict in tweak.Conflicts)
        {
            if (conflict == tweak.Id)
                problems.Add($"{where}: conflicts with itself.");
            else if (!ids.Contains(conflict))
                problems.Add($"{where}: conflicts with unknown tweak '{conflict}'.");
        }

        if (tweak.Releases is { MinBuild: { } min, MaxBuild: { } max } && min > max)
            problems.Add($"{where}: minBuild {min} is greater than maxBuild {max}.");

        if (tweak.Actions.Count == 0)
            problems.Add($"{where}: has no actions.");

        foreach (var action in tweak.Actions)
            CheckAction(problems, where, action);
    }

    static void CheckAction(List<string> problems, string where, TweakAction action)
    {
        switch (action)
        {
            case RegistryAction registry:
                if (registry.Stage == Stage.Installer && registry.Hive != RegistryHive.HKLM)
                    problems.Add($"{where}: the installer stage has no user registry; use HKLM.");
                if (registry.Key.StartsWith('\\') || registry.Key.EndsWith('\\') || registry.Key.Contains(@"\\"))
                    problems.Add($"{where}: key '{registry.Key}' has a stray backslash.");
                CheckText(problems, where, "key", registry.Key);
                if (registry.Name is { } name)
                    CheckText(problems, where, "value name", name);
                if (registry.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString)
                {
                    CheckText(problems, where, "value", registry.Value);
                    if (registry.Value.EndsWith('\\'))
                        problems.Add($"{where}: a string value cannot end with a backslash (it would escape the closing quote).");
                }
                CheckLength(problems, where, registry.Stage, RegistryCommand.Add(registry));
                break;

            case CommandAction command:
                CheckLength(problems, where, command.Stage, command.Command);
                break;
        }
    }

    // Кавычки ломают командную строку, а % раскрывается как переменная окружения.
    static void CheckText(List<string> problems, string where, string what, string text)
    {
        if (text.Contains('"') || text.Contains('%'))
            problems.Add($"{where}: {what} contains \" or %.");
    }

    static void CheckText(List<string> problems, string where, string what, LocalizedText text)
    {
        foreach (var language in RequiredLanguages)
        {
            if (!text.Values.TryGetValue(language, out var value) || string.IsNullOrWhiteSpace(value))
                problems.Add($"{where}: {what} has no '{language}' translation.");
        }
    }

    static void CheckLength(List<string> problems, string where, Stage stage, string command)
    {
        var limit = stage == Stage.FirstLogon ? MaxFirstLogonLength : MaxRunSynchronousLength;
        if (command.Length > limit)
            problems.Add($"{where}: command is {command.Length} characters, the limit for this stage is {limit}.");
    }

    static IEnumerable<string> Duplicates(IEnumerable<string> ids) =>
        ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9]+(-[a-z0-9]+)*)+$")]
    private static partial Regex TweakIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]+([.-][A-Za-z0-9]+)*$")]
    private static partial Regex AppNamePattern();
}
