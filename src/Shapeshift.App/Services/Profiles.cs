using Shapeshift.Core;

namespace Shapeshift.App.Services;

/// <summary>Встроенный пресет: файл профиля рядом с программой и подписи из ресурсов окна.</summary>
public sealed record Preset(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>Профили и пресеты: чтение тем же разборщиком, что у движка (формат один на CLI и окно).</summary>
public static class Profiles
{
    static readonly string PresetFolder = Path.Combine(AppContext.BaseDirectory, "presets");

    public static IReadOnlyList<Preset> Presets { get; } =
        new[] { "clean", "minimal", "privacy" }
            .Where(id => File.Exists(Path.Combine(PresetFolder, id + ".json")))
            .Select(id => new Preset(id, Text.Get($"Preset_{id}"), Text.Get($"Preset_{id}_Description")))
            .ToList();

    public static Profile LoadPreset(Preset preset) => Load(Path.Combine(PresetFolder, preset.Id + ".json"));

    /// <exception cref="ProfileFormatException">Профиль не читается: битый, другой версии формата и т. п.</exception>
    public static Profile Load(string path) =>
        ProfileReader.Parse(File.ReadAllText(path), Path.GetDirectoryName(path));
}
