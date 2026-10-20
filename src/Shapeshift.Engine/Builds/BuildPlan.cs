using Shapeshift.Core;
using Shapeshift.Engine.Images;

namespace Shapeshift.Engine.Builds;

public enum BuildStepKind
{
    ExtractImage,
    KeepEditions,
    WriteAnswerFile,
    PackIso,
}

/// <summary>Шаг сборки; <see cref="Bytes"/> — сколько места он займёт на <see cref="Volume"/>.</summary>
public sealed record BuildStep(BuildStepKind Kind, string Description, string? Volume = null, long Bytes = 0);

/// <summary>Сколько места нужно на томе и сколько там есть.</summary>
public sealed record SpaceRequirement(string Volume, long RequiredBytes, long AvailableBytes)
{
    public bool IsSatisfied => RequiredBytes <= AvailableBytes;
}

/// <summary>Что и в каком порядке сделает сборка. Строится без побочных эффектов.</summary>
public sealed record BuildPlan(
    Profile Profile,
    TweakCatalog Catalog,
    SourceImage Source,
    string OutputIso,
    string BuildDirectory,
    IReadOnlyList<BuildStep> Steps,
    IReadOnlyList<SpaceRequirement> Space)
{
    /// <summary>IMAPI работает только с путями короче MAX_PATH, поэтому распакованное дерево должно в него влезать.</summary>
    public const int MaxPath = 259;

    // Имена коротко: каждый символ здесь отнимается у самого длинного пути внутри образа.
    public string MediaDirectory => Path.Combine(BuildDirectory, "m");

    public bool HasEnoughSpace => Space.All(s => s.IsSatisfied);

    /// <summary>Длина самого длинного пути после распаковки.</summary>
    public int LongestExtractedPath => MediaDirectory.Length + 1 + Source.LongestPathLength;

    public bool FitsMaxPath => LongestExtractedPath <= MaxPath;
}

/// <summary>Профиль + исходный образ → план сборки.</summary>
public static class BuildPlanner
{
    // Запас на служебные структуры файловых систем и непредвиденное.
    const long Headroom = 512L * 1024 * 1024;
    const long PerFileSlack = 4096;

    public static BuildPlan Plan(Profile profile, TweakCatalog catalog, SourceImage source, string outputIso, string workspace, EngineHost host)
    {
        var buildDirectory = Path.Combine(Path.GetFullPath(workspace), Guid.NewGuid().ToString("N")[..8]);

        var mediaBytes = source.ContentBytes + source.FileCount * PerFileSlack;
        var isoBytes = mediaBytes; // UDF добавляет немного; покрывается запасом

        var workspaceVolume = EngineHost.VolumeOf(buildDirectory);
        var outputVolume = EngineHost.VolumeOf(outputIso);

        var steps = new List<BuildStep> { new(BuildStepKind.ExtractImage, "Extract image", workspaceVolume, mediaBytes) };

        // Новый install.wim пишется рядом со старым — на время шага нужно ещё столько же места.
        if (profile.Editions.Count > 0 && source.Info.KeepEditions(profile.Editions).Editions.Count < source.Info.Editions.Count)
            steps.Add(new(BuildStepKind.KeepEditions, "Keep selected editions", workspaceVolume, source.InstallImageBytes));

        steps.Add(new(BuildStepKind.WriteAnswerFile, "Write answer file"));
        steps.Add(new(BuildStepKind.PackIso, "Pack ISO", outputVolume, isoBytes));

        var space = steps
            .Where(s => s.Volume is not null)
            .GroupBy(s => s.Volume!)
            .Select(g => new SpaceRequirement(g.Key, g.Sum(s => s.Bytes) + Headroom, host.AvailableFreeSpace(g.Key)))
            .ToList();

        return new BuildPlan(profile, catalog, source, Path.GetFullPath(outputIso), buildDirectory, steps, space);
    }
}
