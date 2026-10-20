namespace Shapeshift.Core;

public enum WindowsProduct
{
    Windows10,
    Windows11,
}

/// <summary>Конкретная версия Windows с номером сборки, например Windows 11 24H2 (26100.1742).</summary>
public sealed record Release(int Build, int Revision)
{
    /// <summary>Самая старая поддерживаемая сборка: Windows 10 2004.</summary>
    public const int MinimumSupportedBuild = 19041;

    const int FirstWindows11Build = 22000;

    public WindowsProduct Product => Build >= FirstWindows11Build ? WindowsProduct.Windows11 : WindowsProduct.Windows10;

    /// <summary>Маркетинговое имя версии (24H2 и т. п.), если сборка известна.</summary>
    public string? Version => Build switch
    {
        19041 => "2004",
        19042 => "20H2",
        19043 => "21H1",
        19044 => "21H2",
        19045 => "22H2",
        22000 => "21H2",
        22621 => "22H2",
        22631 => "23H2",
        26100 => "24H2",
        26200 => "25H2",
        _ => null,
    };

    public bool IsSupported => Build >= MinimumSupportedBuild;

    public override string ToString()
    {
        var product = Product == WindowsProduct.Windows11 ? "Windows 11" : "Windows 10";
        return Version is { } v ? $"{product} {v} ({Build}.{Revision})" : $"{product} ({Build}.{Revision})";
    }
}

/// <summary>Вариант Windows внутри образа.</summary>
public sealed record Edition(int Index, string EditionId, string Name);

/// <summary>Что лежит в образе: выпуск, архитектура, язык по умолчанию, редакции.</summary>
public sealed record ImageInfo(Release Release, Architecture Architecture, string? Language, IReadOnlyList<Edition> Editions)
{
    public Edition? FindEdition(string editionId) =>
        Editions.FirstOrDefault(e => string.Equals(e.EditionId, editionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Образ, в котором остались только эти редакции — в исходном порядке и с новыми номерами (с 1),
    /// как после экспорта. Пустой список — образ как есть; незнакомые редакции пропускаются.
    /// </summary>
    public ImageInfo KeepEditions(IReadOnlyList<string> editionIds)
    {
        if (editionIds.Count == 0)
            return this;
        var kept = Editions
            .Where(e => editionIds.Contains(e.EditionId, StringComparer.OrdinalIgnoreCase))
            .Select((e, i) => e with { Index = i + 1 })
            .ToList();
        return this with { Editions = kept };
    }
}
