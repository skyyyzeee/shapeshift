namespace Shapeshift.Core;

/// <summary>Сохранённый полный набор выборов пользователя, по которому можно повторить сборку.</summary>
public sealed record Profile(Architecture Architecture, BypassOptions Bypass);

/// <summary>Какие проверки требований установщика снимать.</summary>
public sealed record BypassOptions(bool Tpm, bool SecureBoot, bool Ram, bool Cpu)
{
    public static BypassOptions None { get; } = new(false, false, false, false);

    public bool Any => Tpm || SecureBoot || Ram || Cpu;
}
