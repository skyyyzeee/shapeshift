namespace Shapeshift.Core;

/// <summary>Сохранённый полный набор выборов пользователя, по которому можно повторить сборку.</summary>
/// <param name="Tweaks">Идентификаторы включённых твиков каталога.</param>
/// <param name="Editions">Какие редакции оставить в образе (EditionId); пусто — все.</param>
/// <param name="RemoveApps">Встроенные приложения, которые удалить из образа (имя пакета без версии).</param>
/// <param name="Programs">Программы, которые поставятся при первом входе, в порядке установки.</param>
/// <param name="LatestUpdate">Встроить последнее накопительное обновление; null — по умолчанию (для UUP — да, для своего ISO — нет).</param>
public sealed record Profile(
    Architecture Architecture,
    IReadOnlyList<string> Tweaks,
    IReadOnlyList<string> Editions,
    IReadOnlyList<string> RemoveApps,
    IReadOnlyList<ProgramSpec> Programs,
    SetupOptions Setup,
    RegionalOptions Regional,
    SourceOptions Source,
    OutputOptions Output,
    bool? LatestUpdate = null);

/// <summary>Откуда берётся исходный образ: свой ISO или выпуск из Windows Update (UUP).</summary>
public sealed record SourceOptions(string? IsoPath, UupSourceOptions? Uup = null)
{
    public static SourceOptions None { get; } = new((string?)null);
}

/// <summary>
/// Выпуск из Windows Update. Редакции — поле <c>editions</c> профиля.
/// </summary>
/// <param name="Channel">Канал (windows11, windows10, release-preview…): при повторной сборке берётся его самый новый выпуск.</param>
/// <param name="Language">Язык Windows, например ru-RU.</param>
/// <param name="UpdateId">Конкретный выпуск; null — самый новый в канале на момент сборки.</param>
public sealed record UupSourceOptions(string Channel, string Language, string? UpdateId);

/// <summary>Во что превращается сборка. Пока — только файл ISO.</summary>
public sealed record OutputOptions(string? IsoPath)
{
    public static OutputOptions None { get; } = new((string?)null);
}
