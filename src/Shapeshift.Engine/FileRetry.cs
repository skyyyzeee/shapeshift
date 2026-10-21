namespace Shapeshift.Engine;

/// <summary>
/// Замена только что записанного файла. Антивирус и индексатор ненадолго открывают свежие файлы (WIM, ISO, скачанное),
/// и замена получает «файл занят» или «доступ запрещён» — несколько попыток с паузой.
/// </summary>
public static class FileRetry
{
    public static void Move(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }
}