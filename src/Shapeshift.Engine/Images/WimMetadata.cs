using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace Shapeshift.Engine.Images;

/// <summary>Одна запись образа из XML-метаданных WIM.</summary>
public sealed record WimImage(
    int Index,
    string Name,
    string? EditionId,
    int? Arch,
    string? InstallationType,
    int? Major,
    int? Build,
    int? SpBuild,
    string? DefaultLanguage);

/// <summary>
/// Читает XML-метаданные из заголовка WIM/ESD без распаковки и без внешних библиотек.
/// Заголовок фиксированный (208 байт), XML хранится несжатым в UTF-16LE и в WIM, и в ESD.
/// </summary>
public static class WimMetadata
{
    static ReadOnlySpan<byte> Magic => "MSWIM\0\0\0"u8;

    const int HeaderSize = 208;
    const int XmlResourceOffset = 72;

    /// <exception cref="InvalidDataException">Не WIM или повреждённые метаданные.</exception>
    public static IReadOnlyList<WimImage> Read(Stream wim)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        wim.Position = 0;
        wim.ReadExactly(header);

        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("Not a WIM file.");

        // Описатель ресурса: 7 байт размера + 1 байт флагов, 8 байт смещения, 8 байт исходного размера.
        var resource = header.Slice(XmlResourceOffset, 24);
        var offset = BinaryPrimitives.ReadInt64LittleEndian(resource[8..16]);
        var size = BinaryPrimitives.ReadInt64LittleEndian(resource[16..24]);

        if (offset <= 0 || size <= 2 || size > 64 * 1024 * 1024 || offset + size > wim.Length)
            throw new InvalidDataException("WIM has no readable XML metadata.");

        var xml = new byte[size];
        wim.Position = offset;
        wim.ReadExactly(xml);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(Encoding.Unicode.GetString(xml).TrimStart('﻿').TrimEnd('\0'));
        }
        catch (System.Xml.XmlException e)
        {
            throw new InvalidDataException("WIM XML metadata is corrupt.", e);
        }

        return doc.Root!.Elements("IMAGE").Select(ParseImage).OrderBy(i => i.Index).ToList();
    }

    static WimImage ParseImage(XElement image)
    {
        var windows = image.Element("WINDOWS");
        var version = windows?.Element("VERSION");
        return new WimImage(
            Index: (int?)image.Attribute("INDEX") ?? 0,
            Name: (string?)image.Element("DISPLAYNAME") ?? (string?)image.Element("NAME") ?? "",
            EditionId: (string?)windows?.Element("EDITIONID"),
            Arch: (int?)windows?.Element("ARCH"),
            InstallationType: (string?)windows?.Element("INSTALLATIONTYPE"),
            Major: (int?)version?.Element("MAJOR"),
            Build: (int?)version?.Element("BUILD"),
            SpBuild: (int?)version?.Element("SPBUILD"),
            DefaultLanguage: (string?)windows?.Element("LANGUAGES")?.Element("DEFAULT"));
    }
}
