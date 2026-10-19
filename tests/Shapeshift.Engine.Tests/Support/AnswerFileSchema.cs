using System.Xml;
using System.Xml.Schema;

namespace Shapeshift.Engine.Tests.Support;

/// <summary>Проверка файла ответов по схеме из папки Schemas.</summary>
public static class AnswerFileSchema
{
    static readonly Lazy<XmlSchemaSet> Schemas = new(Load);

    public static IReadOnlyList<string> Validate(string path)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings
                | XmlSchemaValidationFlags.ProcessIdentityConstraints,
        };
        settings.ValidationEventHandler += (_, e) =>
            errors.Add($"{e.Severity} ({e.Exception.LineNumber}:{e.Exception.LinePosition}): {e.Message}");

        using var reader = XmlReader.Create(path, settings);
        while (reader.Read()) { }
        return errors;
    }

    static XmlSchemaSet Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Schemas");
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.Add("urn:schemas-microsoft-com:unattend", Path.Combine(dir, "unattend.xsd"));
        set.Compile();
        return set;
    }
}
