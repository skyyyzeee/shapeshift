using ManagedWimLib;
using Shapeshift.Engine.Images;

namespace Shapeshift.Engine.Uup;

/// <summary>
/// Установочный носитель из файлов UUP — только wimlib, без DISM и прав администратора (ADR-0010).
/// Образ редакции (<c>professional_ru-ru.esd</c>) содержит три образа: файлы установщика, среду восстановления
/// и саму Windows, данные которой лежат в пакетах выпуска (ESD и CAB). Из них получаются
/// <c>sources\install.wim</c> (по образу на редакцию, со средой восстановления внутри) и <c>sources\boot.wim</c>.
/// </summary>
public static class UupImageBuilder
{
    const int SetupMediaImage = 1;
    const int RecoveryImage = 2;
    const int WindowsImage = 3;

    /// <param name="editionImages">Образы редакций (metadata ESD) в порядке редакций результата.</param>
    /// <param name="packages">Пакеты выпуска: ESD/WIM используются как есть, CAB перепаковываются в WIM.</param>
    /// <param name="media">Папка носителя (создаётся).</param>
    /// <param name="scratch">Временная папка: перепакованные CAB и среда восстановления.</param>
    /// <param name="progress">Доля выполнения 0..1.</param>
    public static void Build(
        IReadOnlyList<string> editionImages, IReadOnlyList<string> packages, string media, string scratch,
        Action<string> log, Action<double> progress, CancellationToken ct)
    {
        WimLib.EnsureInitialized();
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(scratch);
        var sources = Path.Combine(media, "sources");

        // Перепаковка CAB — дольше всего (распаковка тысяч мелких файлов): 60 % хода.
        log("Packing the CAB packages");
        var references = new List<string>();
        var cabs = new List<string>();
        foreach (var package in packages)
        {
            switch (Kind(package))
            {
                case PackageKind.Wim:
                    references.Add(package);
                    break;
                case PackageKind.Cab:
                    cabs.Add(package);
                    break;
            }
        }
        for (var i = 0; i < cabs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            references.Add(CabToWim(cabs[i], Path.Combine(scratch, "cab"), Path.Combine(scratch, "cabwim", $"{i}.wim")));
            progress(0.6 * (i + 1) / cabs.Count);
        }

        var recovery = Path.Combine(scratch, "winre.wim");
        var install = Path.Combine(sources, "install.wim");
        for (var e = 0; e < editionImages.Count; e++)
        {
            ct.ThrowIfCancellationRequested();
            using var edition = Wim.OpenWim(editionImages[e], OpenFlags.None);
            if (e == 0)
            {
                log("Unpacking the setup files");
                edition.ExtractImage(SetupMediaImage, media, ExtractFlags.NoAcls);
                using var winre = Wim.CreateNewWim(CompressionType.LZX);
                edition.ExportImage(RecoveryImage, winre, null!, null!, ExportFlags.Boot);
                winre.Write(recovery, Wim.AllImages, WriteFlags.None, (uint)Wim.DefaultThreads);
            }

            log($"Building Windows image {e + 1} of {editionImages.Count}");
            edition.ReferenceResourceFiles(references, RefFlags.None, OpenFlags.None);
            if (e == 0)
            {
                using var target = Wim.CreateNewWim(CompressionType.LZX);
                edition.ExportImage(WindowsImage, target, null!, null!, ExportFlags.None);
                Write(target, install, ct, p => progress(0.6 + 0.3 * (e + p) / editionImages.Count));
            }
            else
            {
                // Следующие редакции — в тот же WIM: общие данные хранятся один раз.
                using var target = Wim.OpenWim(install, OpenFlags.WriteAccess);
                edition.ExportImage(WindowsImage, target, null!, null!, ExportFlags.None);
                Overwrite(target, ct, p => progress(0.6 + 0.3 * (e + p) / editionImages.Count));
            }
        }

        log("Adding the recovery environment");
        using (var target = Wim.OpenWim(install, OpenFlags.WriteAccess))
        {
            var count = (int)target.GetWimInfo().ImageCount;
            for (var image = 1; image <= count; image++)
                target.UpdateImage(image, UpdateCommand.SetAdd(recovery, @"\Windows\System32\Recovery\Winre.wim", null, AddFlags.None), UpdateFlags.None);
            Overwrite(target, ct, p => progress(0.9 + 0.05 * p));
        }

        log("Building boot.wim");
        BuildBootImage(recovery, media, Path.Combine(sources, "boot.wim"));
        progress(1);
    }

    // Как на официальном носителе: образ 1 — Windows PE, образ 2 — установщик (загружается он).
    static void BuildBootImage(string recovery, string media, string boot)
    {
        using (var source = Wim.OpenWim(recovery, OpenFlags.None))
        using (var target = Wim.CreateNewWim(CompressionType.LZX))
        {
            source.ExportImage(1, target, "Microsoft Windows PE (amd64)", "Microsoft Windows PE (amd64)", ExportFlags.None);
            target.Write(boot, Wim.AllImages, WriteFlags.None, (uint)Wim.DefaultThreads);
        }
        // Тот же образ второй раз в один WIM за одну запись wimlib не экспортирует — отдельной записью.
        using (var source = Wim.OpenWim(recovery, OpenFlags.None))
        using (var target = Wim.OpenWim(boot, OpenFlags.WriteAccess))
        {
            source.ExportImage(1, target, "Microsoft Windows Setup (amd64)", "Microsoft Windows Setup (amd64)", ExportFlags.Boot);
            target.Overwrite(WriteFlags.None, (uint)Wim.DefaultThreads);
        }

        var sources = Path.Combine(media, "sources");
        using var wim = Wim.OpenWim(boot, OpenFlags.WriteAccess);
        wim.SetImageProperty(1, "FLAGS", "9");
        // Без winpeshl.ini среда запускает установщик (setup.exe в корне образа), а не восстановление.
        wim.UpdateImage(1, UpdateCommand.SetDelete(@"\Windows\System32\winpeshl.ini", DeleteFlags.Force), UpdateFlags.None);

        var commands = new List<UpdateCommand>
        {
            UpdateCommand.SetDelete(@"\Windows\System32\winpeshl.ini", DeleteFlags.Force),
            UpdateCommand.SetAdd(Path.Combine(media, "setup.exe"), @"\setup.exe", null, AddFlags.None),
        };
        foreach (var file in Directory.EnumerateFiles(sources))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("install.wim", StringComparison.OrdinalIgnoreCase) || name.Equals("boot.wim", StringComparison.OrdinalIgnoreCase))
                continue;
            commands.Add(UpdateCommand.SetAdd(file, $@"\sources\{name}", null, AddFlags.None));
        }
        // Ресурсы установщика: inf и папки языков (ru-ru и т. п.).
        foreach (var dir in Directory.EnumerateDirectories(sources).Where(d => Path.GetFileName(d) is var n && (n == "inf" || n.Contains('-'))))
            commands.Add(UpdateCommand.SetAdd(dir, $@"\sources\{Path.GetFileName(dir)}", null, AddFlags.None));
        if (new[] { "background_cli.png", "background_cli.bmp" }.Select(n => Path.Combine(sources, n)).FirstOrDefault(File.Exists) is { } background)
            commands.Add(UpdateCommand.SetAdd(background, @"\sources\background" + Path.GetExtension(background), null, AddFlags.None));

        wim.UpdateImage(2, commands, UpdateFlags.None);
        wim.SetImageProperty(2, "FLAGS", "2");
        wim.SetWimInfo(new WimInfo { BootIndex = 2 }, ChangeFlags.BootIndex);
        wim.Overwrite(WriteFlags.None, (uint)Wim.DefaultThreads);
    }

    // Пакет CBS в CAB → WIM с теми же файлами: данные адресуются по SHA-1, и образ Windows находит их по ссылкам.
    static string CabToWim(string cab, string unpacked, string wim)
    {
        Cabinet.Extract(cab, unpacked);
        Directory.CreateDirectory(Path.GetDirectoryName(wim)!);
        using var target = Wim.CreateNewWim(CompressionType.XPRESS);
        target.AddImage(unpacked, "package", null!, AddFlags.NoAcls);
        target.Write(wim, Wim.AllImages, WriteFlags.None, (uint)Wim.DefaultThreads);
        return wim;
    }

    static void Write(Wim wim, string path, CancellationToken ct, Action<double> progress)
    {
        Track(wim, ct, progress);
        try
        {
            wim.Write(path, Wim.AllImages, WriteFlags.None, (uint)Wim.DefaultThreads);
        }
        catch (WimLibException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    static void Overwrite(Wim wim, CancellationToken ct, Action<double> progress)
    {
        Track(wim, ct, progress);
        try
        {
            wim.Overwrite(WriteFlags.None, (uint)Wim.DefaultThreads);
        }
        catch (WimLibException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    static void Track(Wim wim, CancellationToken ct, Action<double> progress) =>
        wim.RegisterCallback((message, info, _) =>
        {
            if (ct.IsCancellationRequested)
                return CallbackStatus.Abort;
            if (message == ProgressMsg.WriteStreams && info is WriteStreamsProgress write && write.TotalBytes > 0)
                progress((double)write.CompletedBytes / write.TotalBytes);
            return CallbackStatus.Continue;
        });

    enum PackageKind
    {
        Other,
        Wim,
        Cab,
    }

    // По сигнатуре: файлы в кэше лежат без расширений (имя — SHA-1).
    static PackageKind Kind(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8];
        var read = file.Read(magic);
        if (read >= 8 && magic.SequenceEqual("MSWIM\0\0\0"u8))
            return PackageKind.Wim;
        if (read >= 4 && magic[..4].SequenceEqual("MSCF"u8))
            return PackageKind.Cab;
        return PackageKind.Other;
    }
}
