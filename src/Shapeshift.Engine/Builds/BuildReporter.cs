namespace Shapeshift.Engine.Builds;

/// <summary>Куда сборка сообщает о ходе работы: в консоль или по каналу окну.</summary>
public interface IBuildReporter
{
    void StepStarted(int index, int total, BuildStep step);

    /// <summary>Доля выполнения текущего шага, 0..1.</summary>
    void Progress(double fraction);

    void Log(string text);

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

    public void Done(string outputIso) => output.WriteLine($"Done: {outputIso}");
}
