using System.Runtime.CompilerServices;

namespace Shapeshift.Engine.Tests.Support;

/// <summary>
/// Снимки результата. Эталон лежит рядом с тестом в Snapshots/&lt;Класс&gt;.&lt;Тест&gt;.verified.&lt;ext&gt;.
/// При расхождении или отсутствии эталона рядом пишется .received.&lt;ext&gt; и тест падает:
/// просмотри его и переименуй в .verified, либо прогони тесты с SHAPESHIFT_ACCEPT_SNAPSHOTS=1.
/// </summary>
public static class Snapshot
{
    public static void Match(
        string actual,
        string extension,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string testName = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(testFile)!, "Snapshots");
        var stem = $"{Path.GetFileNameWithoutExtension(testFile)}.{testName}";
        var verified = Path.Combine(dir, $"{stem}.verified.{extension}");
        var received = Path.Combine(dir, $"{stem}.received.{extension}");

        actual = Normalize(actual);
        Directory.CreateDirectory(dir);

        if (Environment.GetEnvironmentVariable("SHAPESHIFT_ACCEPT_SNAPSHOTS") == "1")
        {
            File.WriteAllText(verified, actual);
            File.Delete(received);
            return;
        }

        if (File.Exists(verified) && Normalize(File.ReadAllText(verified)) == actual)
        {
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, actual);
        Assert.Fail(File.Exists(verified)
            ? $"Snapshot mismatch. Compare {received} with {verified}."
            : $"New snapshot. Review {received} and rename it to .verified.{extension}.");
    }

    static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
}
