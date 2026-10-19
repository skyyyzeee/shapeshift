using System.CommandLine;
using System.Globalization;
using Shapeshift.Core;
using Shapeshift.Engine.AnswerFiles;
using Shapeshift.Engine.Builds;
using Shapeshift.Engine.Images;

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
        public const int NotEnoughSpace = 3;
    }

    public static Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, EngineHost? host = null, CancellationToken ct = default)
    {
        var context = new Context(output, error, host ?? EngineHost.Default);
        var root = new RootCommand("Shapeshift engine: builds customized Windows installation images.")
        {
            AnswerFileCommand(context),
            InspectCommand(context),
            BuildCommand(context),
        };

        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct);
    }

    sealed record Context(TextWriter Output, TextWriter Error, EngineHost Host);

    static Command AnswerFileCommand(Context context)
    {
        var profileOption = ProfileOption();
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
            var profile = await TryReadProfileAsync(parseResult.GetRequiredValue(profileOption), context, ct);
            if (profile is null)
                return ExitCodes.InvalidInput;

            await AnswerFileWriter.WriteAsync(
                AnswerFileGenerator.Generate(profile), parseResult.GetRequiredValue(outOption).FullName, ct);
            return ExitCodes.Success;
        });

        return command;
    }

    static Command InspectCommand(Context context)
    {
        var isoOption = new Option<FileInfo>("--iso")
        {
            Description = "Windows installation ISO.",
            Required = true,
        };

        var command = new Command("inspect", "Show the release, architecture and editions of an image.")
        {
            isoOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = await TryInspectAsync(parseResult.GetRequiredValue(isoOption).FullName, context);
            if (source is null)
                return ExitCodes.InvalidInput;

            await WriteImageAsync(source, context.Output);
            return ExitCodes.Success;
        });

        return command;
    }

    static Command BuildCommand(Context context)
    {
        var profileOption = ProfileOption();
        var workspaceOption = new Option<DirectoryInfo>("--workspace")
        {
            Description = "Folder for intermediate build data.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the plan and the space it needs without building anything.",
        };

        var command = new Command("build", "Build an image from a profile.")
        {
            profileOption,
            workspaceOption,
            dryRunOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var profile = await TryReadProfileAsync(parseResult.GetRequiredValue(profileOption), context, ct);
            if (profile is null)
                return ExitCodes.InvalidInput;

            if (profile.Source.IsoPath is not { } sourceIso)
                return await FailAsync(context, ExitCodes.InvalidInput, "profile has no 'source.iso'.");
            if (profile.Output.IsoPath is not { } outputIso)
                return await FailAsync(context, ExitCodes.InvalidInput, "profile has no 'output.iso'.");
            if (string.Equals(Path.GetFullPath(sourceIso), Path.GetFullPath(outputIso), StringComparison.OrdinalIgnoreCase))
                return await FailAsync(context, ExitCodes.InvalidInput, "'output.iso' must differ from 'source.iso'.");

            var source = await TryInspectAsync(sourceIso, context);
            if (source is null)
                return ExitCodes.InvalidInput;

            var workspace = parseResult.GetValue(workspaceOption)?.FullName ?? context.Host.DefaultWorkspace;
            var plan = BuildPlanner.Plan(profile, source, outputIso, workspace, context.Host);
            await WritePlanAsync(plan, context.Output);

            if (!plan.HasEnoughSpace)
            {
                foreach (var s in plan.Space.Where(s => !s.IsSatisfied))
                {
                    await context.Error.WriteLineAsync(
                        $"error: not enough space on {s.Volume}: needs {Size(s.RequiredBytes)}, free {Size(s.AvailableBytes)}.");
                }
                return ExitCodes.NotEnoughSpace;
            }

            if (!plan.FitsMaxPath)
            {
                var budget = BuildPlan.MaxPath - (plan.LongestExtractedPath - Path.GetFullPath(workspace).Length);
                return await FailAsync(context, ExitCodes.InvalidInput,
                    $"workspace path is too long: the deepest file of the image would need {plan.LongestExtractedPath} characters, " +
                    $"the limit is {BuildPlan.MaxPath}. Choose a workspace path of at most {budget} characters.");
            }

            if (parseResult.GetValue(dryRunOption))
                return ExitCodes.Success;

            await BuildRunner.RunAsync(plan, context.Output, ct);
            return ExitCodes.Success;
        });

        return command;
    }

    static Option<FileInfo> ProfileOption() => new("--profile")
    {
        Description = "Profile JSON file.",
        Required = true,
    };

    static async Task<Profile?> TryReadProfileAsync(FileInfo file, Context context, CancellationToken ct)
    {
        if (!file.Exists)
        {
            await context.Error.WriteLineAsync($"error: profile not found: {file.FullName}");
            return null;
        }

        try
        {
            return ProfileReader.Parse(await File.ReadAllTextAsync(file.FullName, ct), file.DirectoryName);
        }
        catch (ProfileFormatException e)
        {
            await context.Error.WriteLineAsync($"error: {file.Name}: {e.Message}");
            return null;
        }
    }

    static async Task<SourceImage?> TryInspectAsync(string isoPath, Context context)
    {
        try
        {
            return await Task.Run(() => ImageInspector.Inspect(isoPath));
        }
        catch (ImageNotSupportedException e)
        {
            await context.Error.WriteLineAsync($"error: {e.Message}");
            return null;
        }
    }

    static async Task<int> FailAsync(Context context, int exitCode, string message)
    {
        await context.Error.WriteLineAsync($"error: {message}");
        return exitCode;
    }

    static async Task WriteImageAsync(SourceImage source, TextWriter output)
    {
        await output.WriteLineAsync($"Image: {source.Info.Release}");
        await output.WriteLineAsync($"Architecture: {source.Info.Architecture.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync("Editions:");
        var idWidth = source.Info.Editions.Max(e => e.EditionId.Length);
        foreach (var edition in source.Info.Editions)
            await output.WriteLineAsync($"  {edition.Index,2}  {edition.EditionId.PadRight(idWidth)}  {edition.Name}");
    }

    static async Task WritePlanAsync(BuildPlan plan, TextWriter output)
    {
        await WriteImageAsync(plan.Source, output);
        await output.WriteLineAsync("Plan:");
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var space = step.Volume is null ? "" : $"  {Size(step.Bytes)} on {step.Volume}";
            await output.WriteLineAsync($"  {i + 1}. {step.Description,-18}{space}".TrimEnd());
        }
        await output.WriteLineAsync("Space:");
        foreach (var s in plan.Space)
            await output.WriteLineAsync($"  {s.Volume}  needs {Size(s.RequiredBytes)}, free {Size(s.AvailableBytes)}");
    }

    static string Size(long bytes) => bytes >= 1L << 30
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", bytes / (double)(1L << 30))
        : string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / (double)(1L << 20));
}
