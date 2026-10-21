using Shapeshift.Engine.AnswerFiles;
using Shapeshift.Engine.Images;
using Shapeshift.Engine.Programs;

namespace Shapeshift.Engine.Builds;

/// <summary>Выполняет план сборки. Исходный образ не меняется; папка сборки удаляется в любом исходе.</summary>
public static class BuildRunner
{
    /// <exception cref="OperationCanceledException">Сборку отменили; результат не создан, папка сборки удалена.</exception>
    public static async Task RunAsync(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                reporter.StepStarted(i, plan.Steps.Count, step);
                await RunStepAsync(plan, step, reporter, ct);
            }

            reporter.Done(plan.OutputIso);
        }
        finally
        {
            TryDelete(plan.BuildDirectory);
        }
    }

    /// <summary>Только шаг скачивания плана (если он есть) — без сборки.</summary>
    public static async Task DownloadAsync(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var index = plan.Steps.ToList().FindIndex(s => s.Kind == BuildStepKind.DownloadFiles);
        if (index < 0)
            return;
        reporter.StepStarted(index, plan.Steps.Count, plan.Steps[index]);
        await RunStepAsync(plan, plan.Steps[index], reporter, ct);
    }

    static Task RunStepAsync(BuildPlan plan, BuildStep step, IBuildReporter reporter, CancellationToken ct) => step.Kind switch
    {
        BuildStepKind.DownloadFiles => plan.Servicing!.Downloader.DownloadAsync(plan.Uup?.DownloadFiles ?? plan.Servicing.Files, plan.Servicing.Links, reporter.Transfer, ct),
        BuildStepKind.AddLanguages => Task.Run(() => AddLanguages(plan, reporter, ct), ct),
        BuildStepKind.EnableDotNet35 => Task.Run(() => EnableDotNet35(plan, reporter, ct), ct),
        BuildStepKind.IntegrateUpdates => Task.Run(() => IntegrateUpdates(plan, reporter, ct), ct),
        BuildStepKind.AssembleImage => Task.Run(() => Assemble(plan, reporter, ct), ct),
        BuildStepKind.ExtractImage => Task.Run(() => Extract(plan, reporter, ct), ct),
        BuildStepKind.KeepEditions => Task.Run(() => KeepEditions(plan, reporter, ct), ct),
        BuildStepKind.RemoveApps => Task.Run(() => RemoveApps(plan, reporter, ct), ct),
        BuildStepKind.AddPrograms => Task.Run(() => AddPrograms(plan, reporter, ct), ct),
        BuildStepKind.AddWallpaper => Task.Run(() => AddWallpaper(plan), ct),
        BuildStepKind.WriteAnswerFile => AnswerFileWriter.WriteAsync(
            AnswerFileGenerator.Generate(plan.Profile, plan.Catalog, plan.Source.Info),
            Path.Combine(plan.MediaDirectory, AnswerFileWriter.FileName),
            ct),
        BuildStepKind.PackIso => Task.Run(() => Pack(plan, reporter, ct), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, null),
    };

    static void Extract(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var total = Math.Max(1, plan.Source.ContentBytes);
        long copied = 0;
        using var iso = IsoImage.Open(plan.Source.IsoPath);
        Directory.CreateDirectory(plan.MediaDirectory);

        foreach (var dir in iso.Directories())
            Directory.CreateDirectory(Path.Combine(plan.MediaDirectory, dir.TrimStart('\\')));

        var buffer = new byte[4 << 20];
        foreach (var file in iso.Files())
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(plan.MediaDirectory, file.TrimStart('\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = iso.OpenFile(file);
            using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, n);
                copied += n;
                reporter.Progress((double)copied / total);
            }
        }
    }

    // Носитель из скачанных файлов UUP (ADR-0010).
    static void Assemble(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var uup = plan.Uup!;
        Uup.UupImageBuilder.Build(
            uup.EditionImages.Select(uup.Downloader.PathOf).ToList(),
            uup.Packages.Select(uup.Downloader.PathOf).ToList(),
            plan.MediaDirectory,
            Path.Combine(plan.BuildDirectory, "u"),
            reporter.Log,
            reporter.Progress,
            ct);
    }

    // Файлы кэша названы по SHA-1, а DISM нужны настоящие имена (.msu/.cab) — жёсткие ссылки в папке сборки,
    // без копирования гигабайт.
    static string Place(BuildPlan plan, string folder, Uup.UupFile file)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, file.Name);
        if (!File.Exists(target))
            FileLinks.LinkOrCopy(plan.Servicing!.Downloader.PathOf(file), target);
        return target;
    }

    // Редакции результата — номера в install.wim после KeepEditions (или все редакции UUP).
    static IReadOnlyList<int> ServicedImages(BuildPlan plan) =>
        plan.Source.Info.KeepEditions(plan.Profile.Editions).Editions.Select(e => e.Index).ToList();

    // DISM не монтирует ESD (сплошное сжатие): перед первым шагом с DISM — переписать в WIM.
    static string MountableInstallImage(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var installImage = Path.Combine(plan.MediaDirectory, plan.Source.InstallImagePath);
        if (!installImage.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
            return installImage;
        var wim = Path.ChangeExtension(installImage, ".wim");
        if (File.Exists(installImage))
        {
            reporter.Log("Converting install.esd to install.wim for DISM");
            WimLib.KeepImages(installImage, ServicedImages(plan), wim, null, ct, forceWim: true);
            File.Delete(installImage);
        }
        return wim;
    }

    // Языки — до обновлений: обновление переводит и добавленные языки. Языковой пакет UUP — ESD, DISM ставит его
    // из развёрнутой папки; возможности языка (CAB) — по порядку зависимостей.
    static void AddLanguages(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var packages = new List<string>();
        foreach (var language in plan.Servicing!.Languages)
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.Combine(plan.BuildDirectory, "lp", language.Language);
            reporter.Log($"Expanding the {language.Language} language pack");
            WimLib.ApplyImage(plan.Servicing.Downloader.PathOf(language.Pack), 1, folder);
            packages.Add(folder);
            packages.AddRange(language.Features.Select(f => Place(plan, Path.Combine(plan.BuildDirectory, "fod"), f)));
        }
        Servicing.DismServicing.AddPackages(MountableInstallImage(plan, reporter, ct), ServicedImages(plan), packages, plan.MountDirectory, reporter, ct);
    }

    // Пакет .NET Framework 3.5 — из sources\sxs своего ISO или из возможностей выпуска UUP. До обновлений:
    // накопительное обновление .NET обновит и его.
    static void EnableDotNet35(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        IReadOnlyList<string> packages = plan.Servicing is { DotNet35.Count: > 0 } servicing
            ? servicing.DotNet35.Select(f => Place(plan, Path.Combine(plan.BuildDirectory, "fod"), f)).ToList()
            : [Path.Combine(plan.MediaDirectory, plan.Source.NetFx3Package!)];
        Servicing.DismServicing.AddPackages(MountableInstallImage(plan, reporter, ct), ServicedImages(plan), packages, plan.MountDirectory, reporter, ct);
    }

    // Обновления выпуска — в каждую редакцию (DISM). Все контрольные точки накопительного — в одной папке.
    static void IntegrateUpdates(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var updates = plan.Servicing!.Updates!;
        var folder = Path.Combine(plan.BuildDirectory, "upd");
        string Place(Uup.UupFile file) => BuildRunner.Place(plan, folder, file);

        var packages = new List<string>();
        foreach (var cumulative in updates.Cumulative)
            Place(cumulative);
        if (updates.Cumulative.Count > 0)
            packages.Add(Place(updates.Cumulative[^1]));
        if (updates.Enablement is { } enablement)
            packages.Add(Place(enablement));
        if (updates.NetFramework is { } netFramework)
            packages.Add(Place(netFramework));

        Servicing.DismServicing.AddPackages(MountableInstallImage(plan, reporter, ct), ServicedImages(plan), packages, plan.MountDirectory, reporter, ct);
    }

    // Только выбранные редакции: экспорт в новый WIM рядом и замена старого. Без DISM и монтирования.
    static void KeepEditions(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var installImage = Path.Combine(plan.MediaDirectory, plan.Source.InstallImagePath);
        var indices = plan.Source.Info.Editions
            .Where(e => plan.Profile.Editions.Contains(e.EditionId, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.Index)
            .ToList();
        reporter.Log($"Keeping {indices.Count} of {plan.Source.Info.Editions.Count} editions: " +
            string.Join(", ", plan.Source.Info.KeepEditions(plan.Profile.Editions).Editions.Select(e => e.EditionId)));

        var exported = installImage + ".new";
        WimLib.KeepImages(installImage, indices, exported, new SynchronousProgress<double>(reporter.Progress), ct);
        File.Move(exported, installImage, overwrite: true);
    }

    // Из каждой редакции результата.
    static void RemoveApps(BuildPlan plan, IBuildReporter reporter, CancellationToken ct) =>
        Servicing.DismServicing.RemoveApps(
            MountableInstallImage(plan, reporter, ct), ServicedImages(plan), plan.Profile.RemoveApps, plan.MountDirectory, reporter, ct);

    // Сценарий установки и установщики — в sources\$OEM$\$$: Windows Setup перенесёт их в папку Windows.
    static void AddPrograms(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var root = Path.Combine(plan.MediaDirectory, ProgramPayload.MediaFolder);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ProgramPayload.ScriptName), InstallScript.Generate(plan.Programs), InstallScript.FileEncoding);

        var total = Math.Max(1, plan.Programs.Bytes);
        long copied = 0;
        var buffer = new byte[4 << 20];
        foreach (var item in plan.Programs.Items.Where(i => i.Folder is not null))
        {
            reporter.Log($"Adding {item.Program.Name}");
            foreach (var file in item.Files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(root, ProgramPayload.ProgramsFolder, item.Folder!, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, n);
                    copied += n;
                    reporter.Progress((double)copied / total);
                }
            }
        }
    }

    // Картинка — туда, откуда её возьмут политики и настройки файла ответов (WallpaperOptions.TargetPath).
    static void AddWallpaper(BuildPlan plan)
    {
        var wallpaper = plan.Profile.Wallpaper!;
        var folder = Path.Combine(plan.MediaDirectory, BuildPlan.WallpaperMediaFolder);
        Directory.CreateDirectory(folder);
        File.Copy(wallpaper.Path, Path.Combine(folder, Path.GetFileName(wallpaper.TargetPath)), overwrite: true);
    }

    static void Pack(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var progress = new SynchronousProgress<double>(reporter.Progress);

        IsoWriter.Write(
            plan.MediaDirectory,
            plan.OutputIso,
            plan.Source.VolumeLabel,
            ImageInspector.BiosBootImage,
            ImageInspector.UefiBootImage,
            progress,
            ct);
    }

    static void TryDelete(string directory)
    {
        // Антивирус и индексатор ненадолго держат свежие файлы — несколько попыток с паузой.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                    return; // не мешаем результату сборки; хвост подчистит `cleanup`
                Thread.Sleep(200 * attempt);
            }
        }
    }

    sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
