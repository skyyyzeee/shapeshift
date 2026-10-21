using ManagedWimLib;
using System.Text;
using Shapeshift.Engine.Images;

namespace Shapeshift.Engine.Tests.Support;

/// <summary>
/// Маленький ISO со структурой установочного образа Windows: загрузочные файлы-заглушки,
/// <c>sources\boot.wim</c> и <c>sources\install.wim</c>, в котором только заголовок WIM и XML-метаданные.
/// Этого достаточно движку, чтобы проверить образ и пересобрать его.
/// </summary>
public sealed record FakeWindowsIso
{
    public int Arch { get; init; } = 9;
    public int Major { get; init; } = 10;
    public int Build { get; init; } = 26100;
    public int SpBuild { get; init; } = 1742;
    public string InstallationType { get; init; } = "Client";
    public string Language { get; init; } = "en-US";
    public IReadOnlyList<(string EditionId, string Name)> Editions { get; init; } =
        [("Core", "Windows 11 Home"), ("Professional", "Windows 11 Pro")];

    /// <summary>Файлы, которых не будет в образе (пути как в образе).</summary>
    public IReadOnlyList<string> Omit { get; init; } = [];

    /// <summary>Папки в Program Files\WindowsApps каждой редакции: имя папки пакета и фреймворк ли это (по манифесту).</summary>
    public IReadOnlyList<(string Folder, bool Framework)> WindowsApps { get; init; } = [];

    /// <summary>Положить источник .NET Framework 3.5 в sources\sxs — как в официальных ISO.</summary>
    public bool WithNetFx3 { get; init; }

    public const string NetFx3Package = @"sources\sxs\microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~amd64~~.cab";

    public const string VolumeLabel = "FAKE_WIN11";

    /// <summary>Файлы образа и их содержимое (кроме install.wim).</summary>
    public static IReadOnlyDictionary<string, byte[]> PlainFiles { get; } = new Dictionary<string, byte[]>
    {
        [@"setup.exe"] = Filler("setup", 4096),
        [@"boot\etfsboot.com"] = Filler("bios-boot", 4096),
        [@"efi\microsoft\boot\efisys.bin"] = Filler("uefi-boot", 16384),
        [@"sources\boot.wim"] = Filler("boot.wim", 8192),
        [@"sources\lang.ini"] = Encoding.ASCII.GetBytes("[Available UI Languages]\r\nen-us = 3\r\n"),
    };

    public string WriteTo(string directory)
    {
        var media = Path.Combine(directory, "fake-media");
        foreach (var (path, content) in PlainFiles)
            WriteFile(media, path, content);
        if (WithNetFx3)
            WriteFile(media, NetFx3Package, Filler("netfx3", 2048));
        if (!Omit.Contains(@"sources\install.wim", StringComparer.OrdinalIgnoreCase))
            WriteInstallWim(Path.Combine(media, "sources", "install.wim"), directory);

        var iso = Path.Combine(directory, "fake-windows.iso");
        IsoWriter.Write(media, iso, VolumeLabel, BootImage(ImageInspector.BiosBootImage), BootImage(ImageInspector.UefiBootImage));
        Directory.Delete(media, recursive: true);
        return iso;
    }

    string? BootImage(string path) => Omit.Contains(path, StringComparer.OrdinalIgnoreCase) ? null : path;

    void WriteFile(string media, string path, byte[] content)
    {
        if (Omit.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;
        var full = Path.Combine(media, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    // Настоящий WIM (wimlib): по образу на редакцию из крошечной папки, свойства Windows — в XML,
    // как у установочного образа. Содержимое у каждой редакции своё, чтобы их можно было различить.
    public void WriteInstallWim(string path, string scratch)
    {
        WimLib.EnsureInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wim = Wim.CreateNewWim(CompressionType.LZX);
        for (var i = 0; i < Editions.Count; i++)
        {
            var (editionId, name) = Editions[i];
            var content = Path.Combine(scratch, $"edition-{i}");
            Directory.CreateDirectory(Path.Combine(content, "Windows"));
            File.WriteAllText(Path.Combine(content, "Windows", "edition.txt"), editionId);
            foreach (var (folder, framework) in WindowsApps)
            {
                var package = Path.Combine(content, "Program Files", "WindowsApps", folder);
                Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, "AppxManifest.xml"),
                    $"<Package><Properties><Framework>{(framework ? "true" : "false")}</Framework></Properties></Package>");
            }

            wim.AddImage(content, name, null!, AddFlags.None);
            var index = i + 1;
            void Set(string property, object value) => wim.SetImageProperty(index, property, value.ToString()!);
            Set("DISPLAYNAME", name);
            Set("WINDOWS/ARCH", Arch);
            Set("WINDOWS/EDITIONID", editionId);
            Set("WINDOWS/INSTALLATIONTYPE", InstallationType);
            Set("WINDOWS/LANGUAGES/LANGUAGE", Language);
            Set("WINDOWS/LANGUAGES/DEFAULT", Language);
            Set("WINDOWS/VERSION/MAJOR", Major);
            Set("WINDOWS/VERSION/MINOR", 0);
            Set("WINDOWS/VERSION/BUILD", Build);
            Set("WINDOWS/VERSION/SPBUILD", SpBuild);
        }
        wim.Write(path, Wim.AllImages, WriteFlags.None, (uint)Wim.DefaultThreads);
    }

    /// <summary>EditionId образов WIM по порядку — чтобы проверить, что осталось в результате.</summary>
    public static IReadOnlyList<string> EditionIds(string wimPath)
    {
        WimLib.EnsureInitialized();
        using var wim = Wim.OpenWim(wimPath, OpenFlags.None);
        var count = (int)wim.GetWimInfo().ImageCount;
        return Enumerable.Range(1, count).Select(i => wim.GetImageProperty(i, "WINDOWS/EDITIONID") ?? "").ToList();
    }

    static byte[] Filler(string seed, int length)
    {
        var pattern = Encoding.ASCII.GetBytes(seed + ";");
        return Enumerable.Range(0, length).Select(i => pattern[i % pattern.Length]).ToArray();
    }
}
