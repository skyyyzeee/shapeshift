using ManagedWimLib;

namespace Shapeshift.Engine.Images;

/// <summary>
/// wimlib (LGPL) — отдельной динамически загружаемой библиотекой <c>libwim-15.dll</c> (ADR-0006):
/// работа с WIM без DISM и без прав администратора.
/// </summary>
public static class WimLib
{
    static readonly Lock Gate = new();
    static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "libwim-15.dll"),
                Path.Combine(AppContext.BaseDirectory, "libwim-15.dll"),
            ];
            var library = candidates.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("libwim-15.dll is missing next to the engine.", candidates[0]);
            Wim.GlobalInit(library);
            _initialized = true;
        }
    }

    /// <summary>
    /// Новый WIM только с выбранными образами (номера с 1), в их исходном порядке. Сжатие — как у исходного,
    /// поэтому данные копируются без пересжатия. ESD остаётся ESD (сплошное сжатие LZMS).
    /// </summary>
    /// <exception cref="OperationCanceledException">Отменили; недописанный файл удалён.</exception>
    /// <param name="forceWim">Писать обычный WIM (LZX, не сплошной) даже из ESD — например, для DISM. Это пересжатие, медленно.</param>
    public static void KeepImages(
        string wimPath, IReadOnlyList<int> indices, string outputPath, IProgress<double>? progress, CancellationToken ct,
        bool forceWim = false)
    {
        EnsureInitialized();
        using var source = Wim.OpenWim(wimPath, OpenFlags.None);
        var solid = !forceWim && Path.GetExtension(wimPath).Equals(".esd", StringComparison.OrdinalIgnoreCase);
        var compression = forceWim ? CompressionType.LZX : source.GetWimInfo().CompressionType;

        using var target = Wim.CreateNewWim(compression);
        if (solid)
            target.SetOutputPackCompressionType(CompressionType.LZMS);
        // NULL имени и описания для wimlib — «взять из исходного образа» (обёртка помечает их как ненулевые).
        foreach (var index in indices)
            source.ExportImage(index, target, null!, null!, ExportFlags.None);

        target.RegisterCallback((message, info, _) =>
        {
            if (ct.IsCancellationRequested)
                return CallbackStatus.Abort;
            if (message == ProgressMsg.WriteStreams && info is WriteStreamsProgress write && write.TotalBytes > 0)
                progress?.Report((double)write.CompletedBytes / write.TotalBytes);
            return CallbackStatus.Continue;
        });

        try
        {
            target.Write(outputPath, Wim.AllImages, solid ? WriteFlags.Solid : WriteFlags.None, (uint)Wim.DefaultThreads);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            File.Delete(outputPath);
            throw new OperationCanceledException(ct);
        }
    }
}
