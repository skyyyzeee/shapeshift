using System.Buffers.Binary;
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
        WriteFile(media, @"sources\install.wim", InstallWim());

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

    byte[] InstallWim()
    {
        var images = Editions.Select((e, i) => $"""
            <IMAGE INDEX="{i + 1}">
              <NAME>{e.Name}</NAME>
              <DISPLAYNAME>{e.Name}</DISPLAYNAME>
              <WINDOWS>
                <ARCH>{Arch}</ARCH>
                <EDITIONID>{e.EditionId}</EDITIONID>
                <INSTALLATIONTYPE>{InstallationType}</INSTALLATIONTYPE>
                <LANGUAGES><LANGUAGE>{Language}</LANGUAGE><DEFAULT>{Language}</DEFAULT></LANGUAGES>
                <VERSION><MAJOR>{Major}</MAJOR><MINOR>0</MINOR><BUILD>{Build}</BUILD><SPBUILD>{SpBuild}</SPBUILD></VERSION>
              </WINDOWS>
            </IMAGE>
            """);
        var xml = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes($"<WIM>{string.Concat(images)}</WIM>"))
            .ToArray();

        const int headerSize = 208;
        var wim = new byte[headerSize + xml.Length];
        "MSWIM\0\0\0"u8.CopyTo(wim);
        BinaryPrimitives.WriteInt32LittleEndian(wim.AsSpan(8), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(wim.AsSpan(44), Editions.Count);
        // Описатель XML-ресурса: размер (7 байт) + флаги, смещение, исходный размер.
        BinaryPrimitives.WriteInt64LittleEndian(wim.AsSpan(72), xml.Length);
        BinaryPrimitives.WriteInt64LittleEndian(wim.AsSpan(80), headerSize);
        BinaryPrimitives.WriteInt64LittleEndian(wim.AsSpan(88), xml.Length);
        xml.CopyTo(wim, headerSize);
        return wim;
    }

    static byte[] Filler(string seed, int length)
    {
        var pattern = Encoding.ASCII.GetBytes(seed + ";");
        return Enumerable.Range(0, length).Select(i => pattern[i % pattern.Length]).ToArray();
    }
}
