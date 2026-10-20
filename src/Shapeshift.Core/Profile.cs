namespace Shapeshift.Core;

/// <summary>Сохранённый полный набор выборов пользователя, по которому можно повторить сборку.</summary>
/// <param name="Tweaks">Идентификаторы включённых твиков каталога.</param>
/// <param name="Editions">Какие редакции оставить в образе (EditionId); пусто — все.</param>
/// <param name="RemoveApps">Встроенные приложения, которые удалить из образа (имя пакета без версии).</param>
/// <param name="Programs">Программы, которые поставятся при первом входе, в порядке установки.</param>
public sealed record Profile(
    Architecture Architecture,
    IReadOnlyList<string> Tweaks,
    IReadOnlyList<string> Editions,
    IReadOnlyList<string> RemoveApps,
    IReadOnlyList<ProgramSpec> Programs,
    SetupOptions Setup,
    RegionalOptions Regional,
    SourceOptions Source,
    OutputOptions Output);

/// <summary>Откуда берётся исходный образ. Пока — только свой ISO.</summary>
public sealed record SourceOptions(string? IsoPath)
{
    public static SourceOptions None { get; } = new((string?)null);
}

/// <summary>Во что превращается сборка. Пока — только файл ISO.</summary>
public sealed record OutputOptions(string? IsoPath)
{
    public static OutputOptions None { get; } = new((string?)null);
}
