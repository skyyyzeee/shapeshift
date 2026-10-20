using Shapeshift.Engine.AnswerFiles;
using Shapeshift.Engine.Images;

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
