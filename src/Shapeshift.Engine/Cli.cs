using System.CommandLine;
using System.Text;
using System.Xml;
using Shapeshift.Core;
using Shapeshift.Engine.AnswerFiles;

namespace Shapeshift.Engine;

/// <summary>
/// Точка входа движка. Единственный шов тестирования: тесты вызывают <see cref="RunAsync"/>
/// внутри своего процесса с теми же аргументами, что и командная строка.
/// </summary>
public static class Cli
{
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int Failure = 1;
        public const int InvalidInput = 2;
    }

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var root = new RootCommand("Shapeshift engine: builds customized Windows installation images.")
        {
            AnswerFileCommand(error),
        };

        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct);
    }

    static Command AnswerFileCommand(TextWriter error)
    {
        var profileOption = new Option<FileInfo>("--profile")
        {
            Description = "Profile JSON file.",
            Required = true,
        };
        var outOption = new Option<FileInfo>("--out")
        {
            Description = "Where to write the answer file.",
            Required = true,
        };

        var command = new Command("answerfile", "Generate an answer file from a profile.")
        {
            profileOption,
            outOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var profile = await TryReadProfileAsync(parseResult.GetRequiredValue(profileOption), error, ct);
            if (profile is null)
                return ExitCodes.InvalidInput;

            var document = AnswerFileGenerator.Generate(profile);
            await WriteXmlAsync(document, parseResult.GetRequiredValue(outOption), ct);
            return ExitCodes.Success;
        });

        return command;
    }

    static async Task<Profile?> TryReadProfileAsync(FileInfo file, TextWriter error, CancellationToken ct)
    {
        if (!file.Exists)
        {
            await error.WriteLineAsync($"error: profile not found: {file.FullName}");
            return null;
        }

        try
        {
            return ProfileReader.Parse(await File.ReadAllTextAsync(file.FullName, ct));
        }
        catch (ProfileFormatException e)
        {
            await error.WriteLineAsync($"error: {file.Name}: {e.Message}");
            return null;
        }
    }

    static async Task WriteXmlAsync(System.Xml.Linq.XDocument document, FileInfo file, CancellationToken ct)
    {
        file.Directory?.Create();
        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        await using var stream = File.Create(file.FullName);
        await using var writer = XmlWriter.Create(stream, settings);
        await document.SaveAsync(writer, ct);
    }
}
