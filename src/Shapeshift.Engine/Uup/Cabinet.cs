using System.Diagnostics;

namespace Shapeshift.Engine.Uup;

/// <summary>Распаковка CAB системным <c>expand.exe</c> — он есть в любой Windows и не требует прав администратора.</summary>
public static class Cabinet
{
    /// <summary>Распаковывает все файлы архива в папку (её прежнее содержимое удаляется); возвращает их пути.</summary>
    /// <exception cref="InvalidDataException">Архив повреждён или это не CAB.</exception>
    public static IReadOnlyList<string> Extract(string cabPath, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(cabPath);
        start.ArgumentList.Add("-F:*");
        start.ArgumentList.Add(destination);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var files = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
        if (process.ExitCode != 0 || files.Length == 0)
            throw new InvalidDataException($"cannot unpack {Path.GetFileName(cabPath)}: {(error.Result + output.Result).Trim()}");
        return files;
    }
}
