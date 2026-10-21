using Shapeshift.Core;
using Shapeshift.Engine.Images;
using Shapeshift.Engine.Programs;

namespace Shapeshift.Engine.Builds;

public enum BuildStepKind
{
    DownloadMetadata,
    DownloadFiles,
    AssembleImage,
    ExtractImage,
    KeepEditions,
    AddLanguages,
    EnableDotNet35,
    IntegrateUpdates,
    RemoveApps,
    AddPrograms,
    AddWallpaper,
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
    ProgramPayload Programs,
    string OutputIso,
    string BuildDirectory,
    IReadOnlyList<BuildStep> Steps,
    IReadOnlyList<SpaceRequirement> Space,
    Uup.UupBuildSource? Uup = null,
    Uup.UupServicing? Servicing = null)
{
    /// <summary>IMAPI работает только с путями короче MAX_PATH, поэтому распакованное дерево должно в него влезать.</summary>
    public const int MaxPath = 259;

    // Имена коротко: каждый символ здесь отнимается у самого длинного пути внутри образа.
    public string MediaDirectory => Path.Combine(BuildDirectory, "m");

    public bool HasEnoughSpace => Space.All(s => s.IsSatisfied);

    /// <summary>Длина самого длинного пути после распаковки.</summary>
    public int LongestExtractedPath => MediaDirectory.Length + 1 + Math.Max(Source.LongestPathLength, Programs.LongestMediaPath);

    public bool FitsMaxPath => LongestExtractedPath <= MaxPath;

    /// <summary>Есть шаги через DISM — сборке нужны права администратора.</summary>
    public bool NeedsElevation => Steps.Any(s => s.Kind is BuildStepKind.RemoveApps or BuildStepKind.AddLanguages or BuildStepKind.EnableDotNet35 or BuildStepKind.IntegrateUpdates);

    /// <summary>Картинка обоев на носителе: Windows Setup перенесёт <c>$OEM$\$$</c> в папку Windows.</summary>
    public const string WallpaperMediaFolder = @"sources\$OEM$\$$\Web\Wallpaper\Shapeshift";

    /// <summary>Сюда DISM монтирует образ; путь короткий — из-за того же предела длины пути.</summary>
    public string MountDirectory => Path.Combine(BuildDirectory, "mnt");
}

/// <summary>Профиль + исходный образ → план сборки.</summary>
public static class BuildPlanner
{
    // Запас на служебные структуры файловых систем и непредвиденное.
    const long Headroom = 512L * 1024 * 1024;
    const long PerFileSlack = 4096;

    /// <param name="uup">Источник — выпуск UUP: вместо распаковки ISO — скачивание и сборка носителя.</param>
    /// <param name="servicing">Языки и обновления из Windows Update (для UUP — <c>uup.Servicing</c>).</param>
    public static BuildPlan Plan(
        Profile profile, TweakCatalog catalog, SourceImage source, ProgramPayload programs, string outputIso, string workspace, EngineHost host,
        Uup.UupBuildSource? uup = null, Uup.UupServicing? servicing = null)
    {
        var buildDirectory = Path.Combine(Path.GetFullPath(workspace), Guid.NewGuid().ToString("N")[..8]);

        var mediaBytes = source.ContentBytes + source.FileCount * PerFileSlack;
        var programBytes = programs.Bytes + programs.FileCount * PerFileSlack;
        var isoBytes = mediaBytes + programBytes; // UDF добавляет немного; покрывается запасом

        var workspaceVolume = EngineHost.VolumeOf(buildDirectory);
        var outputVolume = EngineHost.VolumeOf(outputIso);

        var steps = new List<BuildStep>();
        if (uup is not null)
        {
            // Кэш скачанного — в рабочей папке; носитель собирается из него, перепакованные CAB — во временной папке.
            steps.Add(new(BuildStepKind.DownloadFiles, "Download files", workspaceVolume, uup.BytesToDownload));
            steps.Add(new(BuildStepKind.AssembleImage, "Assemble image", workspaceVolume, mediaBytes + (1L << 30)));
        }
        else
        {
            // Языки и обновления для своего ISO качаются до распаковки: без сети сборка остановится сразу.
            if (servicing is { IsEmpty: false })
                steps.Add(new(BuildStepKind.DownloadFiles, "Download files", workspaceVolume, servicing.BytesToDownload));
            steps.Add(new(BuildStepKind.ExtractImage, "Extract image", workspaceVolume, mediaBytes));

            // Новый install.wim пишется рядом со старым — на время шага нужно ещё столько же места.
            if (profile.Editions.Count > 0 && source.Info.KeepEditions(profile.Editions).Editions.Count < source.Info.Editions.Count)
                steps.Add(new(BuildStepKind.KeepEditions, "Keep selected editions", workspaceVolume, source.InstallImageBytes));
        }

        // DISM не монтирует ESD: перед первым шагом с DISM install.esd переписывается в WIM (столько же места ещё раз).
        var editionCount = source.Info.KeepEditions(profile.Editions).Editions.Count;
        var esdToWim = uup is null && source.InstallImagePath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) ? source.InstallImageBytes * 2 : 0;
        var dismSteps = new List<BuildStep>();
        // Языковой пакет разворачивается из ESD (в 10 раз больше), а в каждой редакции WIM прирастает примерно на его размер.
        if (servicing is { Languages.Count: > 0 } withLanguages)
        {
            var languageBytes = withLanguages.Languages.SelectMany(l => l.Files).Sum(f => f.Size);
            dismSteps.Add(new(BuildStepKind.AddLanguages, "Add languages", workspaceVolume, languageBytes * 10 + languageBytes * 2 * editionCount));
        }
        // .NET Framework 3.5 — около 250 МБ в каждой редакции (общие файлы WIM хранит один раз).
        if (profile.DotNet35)
            dismSteps.Add(new(BuildStepKind.EnableDotNet35, "Enable .NET Framework 3.5", workspaceVolume, (250L << 20) * editionCount));
        // DISM дописывает в WIM изменённые файлы — до размера обновлений на каждую редакцию, плюс распаковка обновления.
        if (servicing?.Updates is { } updates)
        {
            var updateBytes = updates.All.Sum(f => f.Size);
            dismSteps.Add(new(BuildStepKind.IntegrateUpdates, "Integrate updates", workspaceVolume, updateBytes * (editionCount + 1)));
        }
        // DISM монтирует образ без копирования, а при сохранении дописывает в WIM только изменения — с запасом 1 ГБ.
        if (uup is null && profile.RemoveApps.Count > 0)
            dismSteps.Add(new(BuildStepKind.RemoveApps, "Remove built-in apps", workspaceVolume, 1L << 30));
        if (dismSteps.Count > 0)
            dismSteps[0] = dismSteps[0] with { Bytes = dismSteps[0].Bytes + esdToWim };
        steps.AddRange(dismSteps);

        // Установщики своих программ копируются в образ; winget ставит из интернета при первом входе.
        if (!programs.IsEmpty)
            steps.Add(new(BuildStepKind.AddPrograms, "Add programs", workspaceVolume, programBytes));

        if (profile.Wallpaper is { } wallpaper)
            steps.Add(new(BuildStepKind.AddWallpaper, "Add wallpaper", workspaceVolume, File.Exists(wallpaper.Path) ? new FileInfo(wallpaper.Path).Length : 0));

        steps.Add(new(BuildStepKind.WriteAnswerFile, "Write answer file"));
        steps.Add(new(BuildStepKind.PackIso, "Pack ISO", outputVolume, isoBytes));

        var space = steps
            .Where(s => s.Volume is not null)
            .GroupBy(s => s.Volume!)
            .Select(g => new SpaceRequirement(g.Key, g.Sum(s => s.Bytes) + Headroom, host.AvailableFreeSpace(g.Key)))
            .ToList();

        return new BuildPlan(profile, catalog, source, programs, Path.GetFullPath(outputIso), buildDirectory, steps, space, uup, servicing);
    }
}
