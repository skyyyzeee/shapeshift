using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Shapeshift.Engine.Images;

/// <summary>
/// Собирает ISO из папки через встроенный в Windows IMAPI2 (ADR-0006): файловая система UDF,
/// загрузочные записи El Torito для BIOS и UEFI, без эмуляции.
/// </summary>
public static class IsoWriter
{
    const int FsiFileSystemUdf = 4;
    const int UdfRevision102 = 0x102;
    const int PlatformX86 = 0;
    const int PlatformEfi = 0xEF;
    const int EmulationNone = 0;

    // IMAPI не любит параллельную работу в одном процессе (сбои при создании объектов),
    // а движку больше одной упаковки за раз и не нужно.
    static readonly Lock Gate = new();

    /// <param name="sourceDirectory">Что положить в образ (содержимое, не сама папка).</param>
    /// <param name="outputPath">Куда записать ISO. Пишется во временный файл рядом и переименовывается в конце.</param>
    /// <param name="biosBootImage">Загрузочный файл BIOS относительно <paramref name="sourceDirectory"/>; null — без записи BIOS.</param>
    /// <param name="uefiBootImage">Загрузочный файл UEFI относительно <paramref name="sourceDirectory"/>; null — без записи UEFI.</param>
    public static void Write(
        string sourceDirectory,
        string outputPath,
        string volumeLabel,
        string? biosBootImage,
        string? uefiBootImage,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // Антивирус проверяет только что записанные файлы (install.wim, boot.wim), и IMAPI получает
        // «файл занят другим процессом». Ошибка IMAPI локализована — повторяем при любой, кроме отмены.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                WriteOnce(sourceDirectory, outputPath, volumeLabel, biosBootImage, uefiBootImage, progress, ct);
                return;
            }
            catch (COMException) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    static void WriteOnce(
        string sourceDirectory,
        string outputPath,
        string volumeLabel,
        string? biosBootImage,
        string? uefiBootImage,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        lock (Gate)
        {
            // IMAPI держит открытыми все файлы дерева, пока живы его COM-объекты, поэтому
            // каждый полученный объект освобождается явно — иначе папку не удалить до сборки мусора.
            var com = new List<object>();
            try
            {
                dynamic image = Track(com, CreateCom("IMAPI2FS.MsftFileSystemImage"));
                image.FileSystemsToCreate = FsiFileSystemUdf;
                image.UDFRevision = UdfRevision102;
                image.FreeMediaBlocks = 0; // без ограничения ёмкости носителя
                image.VolumeName = volumeLabel;

                var boot = new List<object>();
                if (biosBootImage is not null)
                    boot.Add(BootOptions(Path.Combine(sourceDirectory, biosBootImage), PlatformX86, com));
                if (uefiBootImage is not null)
                    boot.Add(BootOptions(Path.Combine(sourceDirectory, uefiBootImage), PlatformEfi, com));
                if (boot.Count > 0)
                    image.BootImageOptionsArray = boot.ToArray();

                dynamic root = Track(com, image.Root);
                root.AddTree(sourceDirectory, false);
                ct.ThrowIfCancellationRequested();

                dynamic result = Track(com, image.CreateResultImage());
                var stream = (IStream)Track(com, result.ImageStream);
                CopyToFile(stream, outputPath, progress, ct);
            }
            finally
            {
                for (var i = com.Count - 1; i >= 0; i--)
                    Marshal.FinalReleaseComObject(com[i]);
            }
        }
    }

    static object Track(List<object> com, object comObject)
    {
        com.Add(comObject);
        return comObject;
    }

    static object BootOptions(string bootImagePath, int platform, List<object> com)
    {
        if (!File.Exists(bootImagePath))
            throw new FileNotFoundException("Boot image not found.", bootImagePath);

        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(
            bootImagePath, StgmRead | StgmShareDenyWrite, 0, false, IntPtr.Zero, out var stream));
        Track(com, stream);

        dynamic options = Track(com, CreateCom("IMAPI2FS.BootOptions"));
        options.PlatformId = platform;
        options.Emulation = EmulationNone;
        options.AssignBootImage(stream);
        return options;
    }

    static void CopyToFile(IStream source, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        source.Stat(out var stat, 1 /* STATFLAG_NONAME */);
        var total = stat.cbSize;

        var temp = outputPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[4 << 20];
                var read = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    long written = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        source.Read(buffer, buffer.Length, read);
                        var n = Marshal.ReadInt32(read);
                        if (n <= 0)
                            break;
                        file.Write(buffer, 0, n);
                        written += n;
                        if (total > 0)
                            progress?.Report((double)written / total);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(read);
                }
            }

            FileRetry.Move(temp, outputPath);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    static object CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new InvalidOperationException($"Cannot create COM object {progId}.");

    const uint StgmRead = 0x0;
    const uint StgmShareDenyWrite = 0x20;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    static extern int SHCreateStreamOnFileEx(
        string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IntPtr pstmTemplate, out IStream ppstm);
}
