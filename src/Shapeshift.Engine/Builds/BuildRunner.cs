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

    static Task RunStepAsync(BuildPlan plan, BuildStep step, IBuildReporter reporter, CancellationToken ct) => step.Kind switch
    {
        BuildStepKind.ExtractImage => Task.Run(() => Extract(plan, reporter, ct), ct),
        BuildStepKind.KeepEditions => Task.Run(() => KeepEditions(plan, reporter, ct), ct),
        BuildStepKind.RemoveApps => Task.Run(() => RemoveApps(plan, reporter, ct), ct),
        BuildStepKind.AddPrograms => Task.Run(() => AddPrograms(plan, reporter, ct), ct),
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

    // Из каждой редакции результата. DISM не монтирует ESD (сплошное сжатие) — тогда сначала WIM.
    static void RemoveApps(BuildPlan plan, IBuildReporter reporter, CancellationToken ct)
    {
        var installImage = Path.Combine(plan.MediaDirectory, plan.Source.InstallImagePath);
        if (installImage.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
        {
            var wim = Path.ChangeExtension(installImage, ".wim");
            reporter.Log("Converting install.esd to install.wim for DISM");
            var count = plan.Source.Info.KeepEditions(plan.Profile.Editions).Editions.Count;
            WimLib.KeepImages(installImage, Enumerable.Range(1, count).ToList(), wim, null, ct, forceWim: true);
            File.Delete(installImage);
            installImage = wim;
        }

        var editions = plan.Source.Info.KeepEditions(plan.Profile.Editions).Editions;
        Servicing.DismServicing.RemoveApps(
            installImage, editions.Select(e => e.Index).ToList(), plan.Profile.RemoveApps, plan.MountDirectory, reporter, ct);
    }

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
