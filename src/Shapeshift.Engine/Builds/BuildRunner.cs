using Shapeshift.Engine.AnswerFiles;
using Shapeshift.Engine.Images;

namespace Shapeshift.Engine.Builds;

/// <summary>Выполняет план сборки. Исходный образ не меняется; папка сборки удаляется в любом исходе.</summary>
public static class BuildRunner
{
    public static async Task RunAsync(BuildPlan plan, TextWriter log, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                await log.WriteLineAsync($"[{i + 1}/{plan.Steps.Count}] {step.Description}...");
                await RunStepAsync(plan, step, log, ct);
            }

            await log.WriteLineAsync($"Done: {plan.OutputIso}");
        }
        finally
        {
            TryDelete(plan.BuildDirectory);
        }
    }

    static Task RunStepAsync(BuildPlan plan, BuildStep step, TextWriter log, CancellationToken ct) => step.Kind switch
    {
        BuildStepKind.ExtractImage => Task.Run(() => Extract(plan, ct), ct),
        BuildStepKind.WriteAnswerFile => AnswerFileWriter.WriteAsync(
            AnswerFileGenerator.Generate(plan.Profile with { Architecture = plan.Source.Info.Architecture }),
            Path.Combine(plan.MediaDirectory, AnswerFileWriter.FileName),
            ct),
        BuildStepKind.PackIso => Task.Run(() => Pack(plan, log, ct), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, null),
    };

    static void Extract(BuildPlan plan, CancellationToken ct)
    {
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
            }
        }
    }

    static void Pack(BuildPlan plan, TextWriter log, CancellationToken ct)
    {
        var lastReported = -1;
        var progress = new SynchronousProgress<double>(fraction =>
        {
            var percent = (int)(fraction * 100) / 10 * 10;
            if (percent > lastReported)
            {
                lastReported = percent;
                log.WriteLine($"      {percent}%");
            }
        });

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
