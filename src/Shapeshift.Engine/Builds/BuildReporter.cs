namespace Shapeshift.Engine.Builds;

/// <summary>Куда сборка сообщает о ходе работы: в консоль или по каналу окну.</summary>
public interface IBuildReporter
{
    void StepStarted(int index, int total, BuildStep step);

    /// <summary>Доля выполнения текущего шага, 0..1.</summary>
    void Progress(double fraction);

    void Log(string text);

    /// <summary>Скачивание: сколько из скольких байт и скорость — для полосы, скорости и оставшегося времени.</summary>
    void Transfer(Uup.TransferProgress progress);

    void Done(string outputIso);
}

/// <summary>Человекочитаемый ход сборки для командной строки.</summary>
public sealed class ConsoleBuildReporter(TextWriter output) : IBuildReporter
{
    int _lastPercent = -1;

    public void StepStarted(int index, int total, BuildStep step)
    {
        _lastPercent = -1;
        output.WriteLine($"[{index + 1}/{total}] {step.Description}...");
    }

    // Каждые 10 %, чтобы не засорять консоль.
    public void Progress(double fraction)
    {
        var percent = (int)(fraction * 100) / 10 * 10;
        if (percent <= _lastPercent)
            return;
        _lastPercent = percent;
        output.WriteLine($"      {percent}%");
    }

    public void Log(string text) => output.WriteLine(text);

    // Каждые 5 %: сколько скачано, скорость, сколько осталось.
    public void Transfer(Uup.TransferProgress progress)
    {
        var percent = progress.Total == 0 ? 100 : (int)(progress.Done * 100 / progress.Total) / 5 * 5;
        if (percent <= _lastPercent)
            return;
        _lastPercent = percent;
        var left = progress.Left is { } time ? $", {Math.Ceiling(time.TotalMinutes)} min left" : "";
        var speed = progress.BytesPerSecond > 0 ? FormattableString.Invariant($", {progress.BytesPerSecond / (1 << 20):0.0} MB/s") : "";
        output.WriteLine($"      {percent}%  {Amount(progress.Done, progress.Total)}{speed}{left}");
    }

    public void Done(string outputIso) => output.WriteLine($"Done: {outputIso}");

    static string Amount(long done, long total) => total >= 1L << 30
        ? FormattableString.Invariant($"{done / (double)(1 << 30):0.00} of {total / (double)(1 << 30):0.00} GB")
        : FormattableString.Invariant($"{done / (double)(1 << 20):0.0} of {total / (double)(1 << 20):0.0} MB");
}
