namespace Shapeshift.Core;

/// <summary>Языки интерфейса Windows: какие добавить в образ и какой сделать языком системы.</summary>
/// <param name="Add">Языки, которых нет в образе (например, en-US): языковые пакеты и возможности ставятся при сборке.</param>
/// <param name="Display">Язык интерфейса после установки; null — язык образа. Должен быть в образе или в <paramref name="Add"/>.</param>
public sealed record LanguageOptions(IReadOnlyList<string> Add, string? Display)
{
    public static LanguageOptions None { get; } = new([], null);

    public bool IsEmpty => Add.Count == 0 && Display is null;
}
