namespace Shapeshift.Core;

/// <summary>Сохранённый полный набор выборов пользователя, по которому можно повторить сборку.</summary>
public sealed record Profile(
    Architecture Architecture,
    BypassOptions Bypass,
    SetupOptions Setup,
    SourceOptions Source,
    OutputOptions Output);

/// <summary>Какие проверки требований установщика снимать.</summary>
public sealed record BypassOptions(bool Tpm, bool SecureBoot, bool Ram, bool Cpu)
{
    public static BypassOptions None { get; } = new(false, false, false, false);

    public bool Any => Tpm || SecureBoot || Ram || Cpu;
}

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
