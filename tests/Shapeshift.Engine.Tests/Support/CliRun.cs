namespace Shapeshift.Engine.Tests.Support;

/// <summary>Результат вызова движка внутри тестового процесса.</summary>
public sealed record CliResult(int ExitCode, string Output, string Error);

public static class CliRun
{
    public static async Task<CliResult> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await Cli.RunAsync(args, output, error, TestContext.Current.CancellationToken);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }
}

/// <summary>Временная папка теста; удаляется вместе с содержимым.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "shapeshift-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, string content)
    {
        var path = File(name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
