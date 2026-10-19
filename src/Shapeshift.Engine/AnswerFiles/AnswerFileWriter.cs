using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Shapeshift.Engine.AnswerFiles;

public static class AnswerFileWriter
{
    /// <summary>Имя, под которым установщик ищет файл ответов в корне носителя.</summary>
    public const string FileName = "autounattend.xml";

    public static async Task WriteAsync(XDocument document, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        await using var stream = File.Create(path);
        await using var writer = XmlWriter.Create(stream, settings);
        await document.SaveAsync(writer, ct);
    }
}
