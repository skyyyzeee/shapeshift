using System.Security.Principal;
using Microsoft.Dism;
using Shapeshift.Engine.Builds;

namespace Shapeshift.Engine.Servicing;

/// <summary>
/// Офлайн-обслуживание через DISM API (управляемая обёртка Microsoft.Dism). Нужны права администратора.
/// Каждое монтирование помечается файлом <see cref="MountMarker"/> в папке сборки — по нему зависшие
/// монтирования Shapeshift отличаются от чужих.
/// </summary>
public static class DismServicing
{
    public const string MountMarker = ".shapeshift-mount";

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Отключает без сохранения зависшие монтирования Shapeshift (после вылета или выключения питания)
    /// и вычищает испорченные точки монтирования. Чужие монтирования не трогает.
    /// </summary>
    /// <returns>Сколько монтирований отключено.</returns>
    public static int CleanupStale(TextWriter log)
    {
        DismApi.Initialize(DismLogLevel.LogErrors);
        try
        {
            var count = 0;
            foreach (var image in DismApi.GetMountedImages())
            {
                var buildDirectory = Path.GetDirectoryName(image.MountPath.TrimEnd('\\'));
                if (buildDirectory is null || !File.Exists(Path.Combine(buildDirectory, MountMarker)))
                    continue;
                log.WriteLine($"Unmounting a stale mount without saving: {image.MountPath}");
                try
                {
                    DismApi.UnmountImage(image.MountPath, commitChanges: false);
                    count++;
                }
                catch (DismException e)
                {
                    log.WriteLine($"warning: could not unmount {image.MountPath}: {e.Message}");
                }
            }
            DismApi.CleanupMountpoints();
            return count;
        }
        finally
        {
            DismApi.Shutdown();
        }
    }

    /// <summary>
    /// Удаляет встроенные приложения из каждого образа WIM: монтирование → удаление → сохранение.
    /// При ошибке или отмене образ отключается без сохранения — install.wim остаётся как был.
    /// </summary>
    /// <exception cref="OperationCanceledException">Отменили; образ отключён без сохранения.</exception>
    /// <exception cref="ServicingException">DISM не смог обслужить образ.</exception>
    public static void RemoveApps(
        string wimPath, IReadOnlyList<int> imageIndices, IReadOnlyList<string> appNames, string mountDirectory,
        IBuildReporter reporter, CancellationToken ct) =>
        Service(wimPath, imageIndices, mountDirectory, reporter, ct, (session, imageIndex, _) =>
        {
            var installed = DismApi.GetProvisionedAppxPackages(session);
            foreach (var name in appNames)
            {
                ct.ThrowIfCancellationRequested();
                var package = installed.FirstOrDefault(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (package is null)
                {
                    reporter.Log($"warning: {name} is not in image {imageIndex}; skipped.");
                    continue;
                }
                DismApi.RemoveProvisionedAppxPackage(session, package.PackageName);
                reporter.Log($"Removed {name}");
            }
        });

    /// <summary>
    /// Встраивает пакеты (.msu, .cab) в каждый образ WIM по порядку. Накопительное обновление с контрольными
    /// точками: DISM сам находит более ранние .msu в той же папке и ставит их первыми.
    /// </summary>
    /// <exception cref="OperationCanceledException">Отменили; образ отключён без сохранения.</exception>
    /// <exception cref="ServicingException">DISM не смог обслужить образ или пакет не подошёл.</exception>
    public static void AddPackages(
        string wimPath, IReadOnlyList<int> imageIndices, IReadOnlyList<string> packages, string mountDirectory,
        IBuildReporter reporter, CancellationToken ct) =>
        Service(wimPath, imageIndices, mountDirectory, reporter, ct, (session, imageIndex, progress) =>
        {
            for (var i = 0; i < packages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var part = i;
                reporter.Log($"Adding {Path.GetFileName(packages[i])} to image {imageIndex}");
                DismApi.AddPackage(session, packages[i], ignoreCheck: false, preventPending: false, p =>
                {
                    if (ct.IsCancellationRequested)
                        p.Cancel = true;
                    else if (p.Total > 0)
                        progress((part + (double)p.Current / p.Total) / packages.Count);
                });
            }
        });

    /// <summary>
    /// Монтирование каждого образа → работа в офлайн-сессии → сохранение. При ошибке или отмене образ
    /// отключается без сохранения. Каждое монтирование помечается <see cref="MountMarker"/>.
    /// </summary>
    /// <param name="work">Сессия, номер образа и доля выполнения работы (0..1).</param>
    static void Service(
        string wimPath, IReadOnlyList<int> imageIndices, string mountDirectory, IBuildReporter reporter, CancellationToken ct,
        Action<DismSession, int, Action<double>> work)
    {
        Directory.CreateDirectory(mountDirectory);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(mountDirectory.TrimEnd('\\'))!, MountMarker), wimPath);

        DismApi.Initialize(DismLogLevel.LogErrors);
        try
        {
            for (var i = 0; i < imageIndices.Count; i++)
            {
                // На образ: монтирование — 15 %, работа — 70 %, сохранение — 15 %.
                var part = i;
                void Report(double from, double share, double fraction) =>
                    reporter.Progress((part + from + share * Math.Clamp(fraction, 0, 1)) / imageIndices.Count);

                var commit = false;
                try
                {
                    reporter.Log($"Mounting image {imageIndices[i]} ({i + 1} of {imageIndices.Count})");
                    DismApi.MountImage(wimPath, mountDirectory, imageIndices[i], readOnly: false, p =>
                    {
                        // Монтирование долгое — отмена прерывает его; отключение ниже не прерывается никогда.
                        if (ct.IsCancellationRequested)
                            p.Cancel = true;
                        else if (p.Total > 0)
                            Report(0, 0.15, (double)p.Current / p.Total);
                    });
                    ct.ThrowIfCancellationRequested();
                    using (var session = OpenSession(mountDirectory))
                        work(session, imageIndices[i], fraction => Report(0.15, 0.7, fraction));
                    commit = true;
                }
                finally
                {
                    Unmount(mountDirectory, commit && !ct.IsCancellationRequested, reporter,
                        p => { if (p.Total > 0) Report(0.85, 0.15, (double)p.Current / p.Total); });
                }
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is not OperationCanceledException)
        {
            throw new OperationCanceledException("Cancelled while servicing the image.", e, ct);
        }
        catch (DismException e)
        {
            throw new ServicingException($"DISM failed: {e.Message} (0x{e.HResult:X8}).", e);
        }
        finally
        {
            DismApi.Shutdown();
        }
    }

    static DismSession OpenSession(string mountDirectory)
    {
        try
        {
            return DismApi.OpenOfflineSession(mountDirectory);
        }
        catch (DismException e) when (e.HResult == E_INVALIDARG)
        {
            // Так бывает, когда Windows не может загрузить кусты реестра образа (reg load тоже падает).
            throw new ServicingException(
                $"DISM could not open the mounted image (0x{e.HResult:X8}): this Windows cannot load the image's registry. " +
                "Check that 'reg load' works on this computer, or build on another one.", e);
        }
    }

    // Отключает, если смонтировано (монтирование могло и не начаться). Ошибку отключения при неудачной сборке
    // не поднимаем поверх исходной — хвост подчистит следующий запуск (CleanupStale).
    static void Unmount(string mountDirectory, bool commit, IBuildReporter reporter, DismProgressCallback progress)
    {
        var mounted = DismApi.GetMountedImages()
            .Any(m => string.Equals(m.MountPath.TrimEnd('\\'), mountDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!mounted)
            return;

        reporter.Log(commit ? "Saving the image" : "Unmounting without saving");
        try
        {
            DismApi.UnmountImage(mountDirectory, commit, progress);
        }
        catch (DismException) when (!commit)
        {
            reporter.Log($"warning: could not unmount {mountDirectory}; it will be cleaned up on the next build.");
        }
    }

    const int E_INVALIDARG = unchecked((int)0x80070057);

    /// <summary>
    /// DISM этой системы старше выпуска образа — обслуживание может завершиться ошибкой.
    /// null — всё в порядке.
    /// </summary>
    public static string? HostTooOldWarning(int imageBuild)
    {
        var host = Environment.OSVersion.Version.Build;
        return host >= imageBuild
            ? null
            : $"this Windows (build {host}) is older than the image (build {imageBuild}); its DISM may fail to service the image. " +
              "If it does, install the DISM from the Windows ADK for the image's release.";
    }
}
