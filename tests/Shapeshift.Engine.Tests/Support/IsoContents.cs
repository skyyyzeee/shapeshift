using System.Buffers.Binary;
using System.Text;
using DiscUtils.Udf;

namespace Shapeshift.Engine.Tests.Support;

/// <summary>Независимое от движка чтение результата: файлы через DiscUtils, загрузочные записи — разбором El Torito.</summary>
public static class IsoContents
{
    const int Sector = 2048;

    public static byte[] ReadFile(string iso, string path)
    {
        using var stream = File.OpenRead(iso);
        using var udf = new UdfReader(stream);
        using var file = udf.OpenFile(path, FileMode.Open, FileAccess.Read);
        using var buffer = new MemoryStream();
        file.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static IReadOnlyList<string> Files(string iso)
    {
        using var stream = File.OpenRead(iso);
        using var udf = new UdfReader(stream);
        return udf.GetFiles("", "*.*", SearchOption.AllDirectories)
            .Select(f => f.TrimStart('\\'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Платформы загрузочных записей El Torito (0 — BIOS x86, 0xEF — UEFI), только загрузочные записи.</summary>
    public static IReadOnlyList<int> BootPlatforms(string iso)
    {
        using var stream = File.OpenRead(iso);
        var catalogSector = FindBootCatalog(stream)
            ?? throw new InvalidDataException("No El Torito boot record.");

        var catalog = new byte[Sector];
        stream.Position = (long)catalogSector * Sector;
        stream.ReadExactly(catalog);

        var platforms = new List<int>();
        // Запись проверки (0x01) задаёт платформу записи по умолчанию, что идёт сразу за ней.
        if (catalog[0] != 0x01)
            throw new InvalidDataException("Bad boot catalog validation entry.");
        if (catalog[32] == 0x88)
            platforms.Add(catalog[1]);

        // Дальше — заголовки секций (0x90, последний — 0x91) с записями.
        for (var offset = 64; offset + 32 <= Sector;)
        {
            var header = catalog[offset];
            if (header is not (0x90 or 0x91))
                break;
            var platform = catalog[offset + 1];
            var count = BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(offset + 2));
            offset += 32;
            for (var i = 0; i < count && offset + 32 <= Sector; i++, offset += 32)
            {
                if (catalog[offset] == 0x88)
                    platforms.Add(platform);
            }
            if (header == 0x91)
                break;
        }

        return platforms;
    }

    static uint? FindBootCatalog(Stream stream)
    {
        var descriptor = new byte[Sector];
        for (var sector = 16; sector < 64; sector++)
        {
            stream.Position = (long)sector * Sector;
            stream.ReadExactly(descriptor);
            var id = Encoding.ASCII.GetString(descriptor, 1, 5);
            if (id == "CD001" && descriptor[0] == 0 &&
                Encoding.ASCII.GetString(descriptor, 7, 23) == "EL TORITO SPECIFICATION")
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(0x47));
            }
            if (id == "CD001" && descriptor[0] == 0xFF)
                break; // конец набора дескрипторов
        }
        return null;
    }
}
