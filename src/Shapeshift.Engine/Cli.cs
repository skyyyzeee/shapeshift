using System.CommandLine;
using System.Globalization;
using Shapeshift.Core;
using Shapeshift.Engine.AnswerFiles;
using Shapeshift.Engine.Builds;
using Shapeshift.Engine.Images;
using Shapeshift.Engine.Pipes;
using Shapeshift.Engine.Programs;
using Shapeshift.Engine.Servicing;
using Shapeshift.Engine.Tweaks;

namespace Shapeshift.Engine;

/// <summary>
/// Точка входа движка. Единственный шов тестирования: тесты вызывают <see cref="RunAsync"/>
/// внутри своего процесса с теми же аргументами, что и командная строка.
/// </summary>
public static partial class Cli
{
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int Failure = 1;
        public const int InvalidInput = 2;
        public const int NotEnoughSpace = 3;
        public const int Cancelled = 4;
        public const int NeedsElevation = 5;
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
            CatalogCommand(context),
            AppsCommand(context),
            UupCommand(context),
            CleanupCommand(context),
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

        var isoOption = new Option<FileInfo>("--iso")
        {
            Description = "Image the answer file is for (default: the profile's 'source.iso'). " +
                "Needed to choose an edition and to skip the installer's language page.",
        };

        var catalogOption = CatalogOption();

        var command = new Command("answerfile", "Generate an answer file from a profile.")
        {
            profileOption,
            outOption,
            isoOption,
            catalogOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var profile = await TryReadProfileAsync(parseResult.GetRequiredValue(profileOption), context, ct);
            if (profile is null)
                return ExitCodes.InvalidInput;

            var catalog = await TryLoadCatalogAsync(parseResult.GetValue(catalogOption), context, ct);
            if (catalog is null)
                return ExitCodes.InvalidInput;

            ImageInfo? image = null;
            if ((parseResult.GetValue(isoOption)?.FullName ?? profile.Source.IsoPath) is { } iso)
            {
                var source = await TryInspectAsync(iso, context);
                if (source is null)
                    return ExitCodes.InvalidInput;
                image = source.Info;
            }

            if (!await CheckAnswerFileAsync(profile, catalog, image, context))
                return ExitCodes.InvalidInput;
            if (profile.Programs.Count > 0)
            {
                await context.Error.WriteLineAsync(
                    "warning: programs are installed by a script that 'build' puts into the image; this answer file alone does not install them.");
            }

            await AnswerFileWriter.WriteAsync(
                AnswerFileGenerator.Generate(profile, catalog, image), parseResult.GetRequiredValue(outOption).FullName, ct);
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

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Print machine-readable JSON (for the app): the image, or { \"error\": { code, message } }.",
        };

        var command = new Command("inspect", "Show the release, architecture and editions of an image.")
        {
            isoOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var iso = parseResult.GetRequiredValue(isoOption).FullName;
            if (!parseResult.GetValue(jsonOption))
            {
                var source = await TryInspectAsync(iso, context);
                if (source is null)
                    return ExitCodes.InvalidInput;

                await WriteImageAsync(source, context.Output);
                return ExitCodes.Success;
            }

            try
            {
                var source = await Task.Run(() => ImageInspector.Inspect(iso), ct);
                await context.Output.WriteLineAsync(EngineJson.Image(source.Info));
                return ExitCodes.Success;
            }
            catch (ImageNotSupportedException e)
            {
                await context.Output.WriteLineAsync(EngineJson.Error(e.Code, e.Message));
                return ExitCodes.InvalidInput;
            }
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

        var catalogOption = CatalogOption();
        var jsonOption = new Option<bool>("--json")
        {
            Description = "With --dry-run: print the plan as JSON (for the app), or { \"error\": { code, message } }.",
        };
        var pipeOption = new Option<string>("--pipe")
        {
            Description = "Report to the app over this named pipe instead of the console (the app is the pipe server).",
        };

        var command = new Command("build", "Build an image from a profile.")
        {
            profileOption,
            workspaceOption,
            dryRunOption,
            catalogOption,
            jsonOption,
            pipeOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var options = new BuildOptions(
                parseResult.GetRequiredValue(profileOption),
                parseResult.GetValue(workspaceOption)?.FullName,
                parseResult.GetValue(catalogOption),
                parseResult.GetValue(dryRunOption));

            if (parseResult.GetValue(pipeOption) is { } pipeName)
                return await BuildOverPipeAsync(options, pipeName, context, ct);

            if (parseResult.GetValue(jsonOption))
            {
                if (!options.DryRun)
                    return await FailAsync(context, ExitCodes.InvalidInput, "--json works only with --dry-run; the app builds with --pipe.");
                return await PlanAsJsonAsync(options, context, ct);
            }

            var (exitCode, _) = await RunBuildAsync(options, context, new ConsoleBuildReporter(context.Output), ct);
            return exitCode;
        });

        return command;
    }

    sealed record BuildOptions(FileInfo Profile, string? Workspace, FileInfo? Catalog, bool DryRun);

    /// <summary>План для сводки в окне: без вывода текстом, ошибки и предупреждения — внутри JSON.</summary>
    static async Task<int> PlanAsJsonAsync(BuildOptions options, Context context, CancellationToken ct)
    {
        var errors = new StringWriter();
        var quiet = context with { Output = TextWriter.Null, Error = errors };
        var (exitCode, plan) = await RunBuildAsync(options, quiet, new ConsoleBuildReporter(TextWriter.Null), ct);

        var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var warnings = lines.Where(l => l.StartsWith("warning: ")).Select(l => l["warning: ".Length..]).ToList();
        var problems = lines.Where(l => !l.StartsWith("warning: ")).Select(l => l.StartsWith("error: ") ? l["error: ".Length..] : l).ToList();

        await context.Output.WriteLineAsync(exitCode == ExitCodes.Success && plan is not null
            ? EngineJson.Plan(plan, warnings)
            : EngineJson.Error(ExitCodeName(exitCode), string.Join("\n", problems)));
        return exitCode;
    }

    /// <summary>Сборка для окна: всё — сообщениями в канал, отмена — командой окна или обрывом канала.</summary>
    static Task<int> BuildOverPipeAsync(BuildOptions options, string pipeName, Context context, CancellationToken ct) =>
        OverPipeAsync(pipeName, context, ct, async (piped, reporter, token) => (await RunBuildAsync(options, piped, reporter, token)).ExitCode);

    /// <summary>Работа для окна: всё — сообщениями в канал, отмена — командой окна или обрывом канала.</summary>
    static async Task<int> OverPipeAsync(
        string pipeName, Context context, CancellationToken ct, Func<Context, IBuildReporter, CancellationToken, Task<int>> work)
    {
        await using var pipe = await EnginePipe.ConnectAsync(pipeName, ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopListening = new CancellationTokenSource();
        var listening = pipe.ListenAsync(cancel.Cancel, stopListening.Token);

        var piped = context with { Output = new PipeTextWriter(pipe, isErrorStream: false), Error = new PipeTextWriter(pipe, isErrorStream: true) };
        var exitCode = await work(piped, new PipeBuildReporter(pipe), cancel.Token);

        await piped.Output.FlushAsync(CancellationToken.None);
        await piped.Error.FlushAsync(CancellationToken.None);
        if (exitCode == ExitCodes.Cancelled)
            pipe.Send("cancelled");
        pipe.Send("exit", new { code = exitCode });

        await stopListening.CancelAsync();
        await listening;
        return exitCode;
    }

    static string ExitCodeName(int exitCode) => exitCode switch
    {
        ExitCodes.InvalidInput => "invalid-input",
        ExitCodes.NotEnoughSpace => "not-enough-space",
        ExitCodes.Cancelled => "cancelled",
        ExitCodes.NeedsElevation => "needs-elevation",
        _ => "failure",
    };

    /// <summary>Проверки, план и (если не пробный прогон) сама сборка. План — null, если до него не дошло.</summary>
    static async Task<(int ExitCode, BuildPlan? Plan)> RunBuildAsync(
        BuildOptions options, Context context, IBuildReporter reporter, CancellationToken ct)
    {
        // Отмена может прийти на любом шаге — и пока читаются профиль и образ, и во время сборки.
        try
        {
            return await RunBuildCoreAsync(options, context, reporter, ct);
        }
        catch (OperationCanceledException)
        {
            await context.Error.WriteLineAsync("error: the build was cancelled; nothing was written.");
            return (ExitCodes.Cancelled, null);
        }
        catch (ServicingException e)
        {
            return (await FailAsync(context, ExitCodes.Failure, e.Message), null);
        }
    }

    static async Task<(int ExitCode, BuildPlan? Plan)> RunBuildCoreAsync(
        BuildOptions options, Context context, IBuildReporter reporter, CancellationToken ct)
    {
        {
            var profile = await TryReadProfileAsync(options.Profile, context, ct);
            if (profile is null)
                return (ExitCodes.InvalidInput, null);

            var catalog = await TryLoadCatalogAsync(options.Catalog, context, ct);
            if (catalog is null)
                return (ExitCodes.InvalidInput, null);

            if (profile.Source.IsoPath is not { } sourceIso)
                return (await FailAsync(context, ExitCodes.InvalidInput, "profile has no 'source.iso'."), null);
            if (profile.Output.IsoPath is not { } outputIso)
                return (await FailAsync(context, ExitCodes.InvalidInput, "profile has no 'output.iso'."), null);
            if (string.Equals(Path.GetFullPath(sourceIso), Path.GetFullPath(outputIso), StringComparison.OrdinalIgnoreCase))
                return (await FailAsync(context, ExitCodes.InvalidInput, "'output.iso' must differ from 'source.iso'."), null);

            var source = await TryInspectAsync(sourceIso, context);
            if (source is null)
                return (ExitCodes.InvalidInput, null);

            var (programs, programProblems) = ProgramPayload.Inspect(profile.Programs);
            foreach (var problem in programProblems)
                await context.Error.WriteLineAsync($"error: {problem}");
            if (programProblems.Count > 0)
                return (ExitCodes.InvalidInput, null);

            var workspace = options.Workspace ?? context.Host.DefaultWorkspace;
            var plan = BuildPlanner.Plan(profile, catalog, source, programs, outputIso, workspace, context.Host);
            await WritePlanAsync(plan, context.Output);
            if (!await CheckAnswerFileAsync(profile, catalog, source.Info, context))
                return (ExitCodes.InvalidInput, plan);

            if (!plan.HasEnoughSpace)
            {
                foreach (var s in plan.Space.Where(s => !s.IsSatisfied))
                {
                    await context.Error.WriteLineAsync(
                        $"error: not enough space on {s.Volume}: needs {Size(s.RequiredBytes)}, free {Size(s.AvailableBytes)}.");
                }
                return (ExitCodes.NotEnoughSpace, plan);
            }

            if (!plan.FitsMaxPath)
            {
                var budget = BuildPlan.MaxPath - (plan.LongestExtractedPath - Path.GetFullPath(workspace).Length);
                return (await FailAsync(context, ExitCodes.InvalidInput,
                    $"workspace path is too long: the deepest file of the image would need {plan.LongestExtractedPath} characters, " +
                    $"the limit is {BuildPlan.MaxPath}. Choose a workspace path of at most {budget} characters."), plan);
            }

            if (plan.NeedsElevation)
            {
                if (DismServicing.HostTooOldWarning(source.Info.Release.Build) is { } tooOld)
                    await context.Error.WriteLineAsync($"warning: {tooOld}");
                if (!options.DryRun && !DismServicing.IsElevated)
                {
                    return (await FailAsync(context, ExitCodes.NeedsElevation,
                        "removing built-in apps uses DISM and needs administrator rights; run the engine as administrator."), plan);
                }
            }

            if (options.DryRun)
                return (ExitCodes.Success, plan);

            ct.ThrowIfCancellationRequested();
            // Зависшие монтирования прошлых сборок (вылет, выключение питания) мешают новому — отключаем.
            if (plan.NeedsElevation)
                await Task.Run(() => DismServicing.CleanupStale(context.Output), ct);
            await BuildRunner.RunAsync(plan, reporter, ct);
            return (ExitCodes.Success, plan);
        }
    }

    static Command CatalogCommand(Context context)
    {
        var fileArgument = new Argument<FileInfo>("file")
        {
            Description = "Catalog JSON file (default: the catalog shipped with the engine).",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var validate = new Command("validate", "Check a tweak catalog.") { fileArgument };
        validate.SetAction(async (parseResult, ct) =>
        {
            var catalog = await TryLoadCatalogAsync(parseResult.GetValue(fileArgument), context, ct);
            if (catalog is null)
                return ExitCodes.InvalidInput;

            await context.Output.WriteLineAsync(
                $"ok: {catalog.Tweaks.Count} tweaks in {catalog.Categories.Count} categories.");
            return ExitCodes.Success;
        });

        var catalogOption = CatalogOption();
        var isoOption = new Option<FileInfo>("--iso")
        {
            Description = "Mark which tweaks apply to this image.",
        };
        var list = new Command("list", "Print the catalog as JSON for the app.") { catalogOption, isoOption };
        list.SetAction(async (parseResult, ct) =>
        {
            var catalog = await TryLoadCatalogAsync(parseResult.GetValue(catalogOption), context, ct);
            if (catalog is null)
                return ExitCodes.InvalidInput;

            ImageInfo? image = null;
            if (parseResult.GetValue(isoOption) is { } iso)
            {
                var source = await TryInspectAsync(iso.FullName, context);
                if (source is null)
                    return ExitCodes.InvalidInput;
                image = source.Info;
            }

            await context.Output.WriteLineAsync(EngineJson.Catalog(catalog, image?.Release));
            return ExitCodes.Success;
        });

        return new Command("catalog", "Work with the tweak catalog.") { validate, list };
    }

    static Command AppsCommand(Context context)
    {
        var isoOption = new Option<FileInfo>("--iso") { Description = "Windows installation ISO.", Required = true };
        var editionOption = new Option<string>("--edition") { Description = "Edition to list (EditionId; default: the first one)." };
        var jsonOption = new Option<bool>("--json") { Description = "Print JSON for the app." };
        var catalogOption = CatalogOption();

        var list = new Command("list", "List the built-in apps of an edition, without administrator rights.")
        {
            isoOption, editionOption, jsonOption, catalogOption,
        };
        list.SetAction(async (parseResult, ct) =>
        {
            var catalog = await TryLoadCatalogAsync(parseResult.GetValue(catalogOption), context, ct);
            var source = await TryInspectAsync(parseResult.GetRequiredValue(isoOption).FullName, context);
            if (catalog is null || source is null)
                return ExitCodes.InvalidInput;

            var editionId = parseResult.GetValue(editionOption);
            var edition = editionId is null ? source.Info.Editions[0] : source.Info.FindEdition(editionId);
            if (edition is null)
                return await FailAsync(context, ExitCodes.InvalidInput, $"edition '{editionId}' is not in the image.");

            IReadOnlyList<string> apps;
            try
            {
                apps = await Task.Run(() =>
                {
                    using var mount = IsoMount.Mount(source.IsoPath);
                    return ProvisionedApps.List(Path.Combine(mount.Root, source.InstallImagePath), edition.Index);
                }, ct);
            }
            catch (IOException e)
            {
                return await FailAsync(context, ExitCodes.Failure, e.Message);
            }

            if (parseResult.GetValue(jsonOption))
            {
                await context.Output.WriteLineAsync(EngineJson.Apps(edition, apps, catalog));
                return ExitCodes.Success;
            }

            await context.Output.WriteLineAsync($"Built-in apps of {edition.Name} ({edition.EditionId}):");
            foreach (var app in apps)
            {
                var markup = catalog.FindApp(app);
                var safety = markup is null ? "unknown" : markup.Safety == AppSafety.Safe ? "safe" : "risky";
                await context.Output.WriteLineAsync($"  {app,-50} {safety}{(markup?.Recommended == true ? ", recommended" : "")}");
            }
            return ExitCodes.Success;
        });

        return new Command("apps", "Built-in (provisioned) apps of an image.") { list };
    }

    static Command UupCommand(Context context)
    {
        var workspaceOption = new Option<DirectoryInfo>("--workspace")
        {
            Description = "Workspace; found releases are cached in its cache\\uup folder.",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Print machine-readable JSON (for the app), or { \"error\": { code, message } }.",
        };
        var advancedOption = new Option<bool>("--advanced")
        {
            Description = "Also ask the Insider channels Beta, Dev and Canary (tweaks may not work on them).",
        };

        var releases = new Command("releases", "Ask Windows Update for the current Windows releases, channel by channel.")
        {
            workspaceOption,
            advancedOption,
            jsonOption,
        };
        releases.SetAction(async (parseResult, ct) =>
        {
            var workspace = parseResult.GetValue(workspaceOption)?.FullName ?? context.Host.DefaultWorkspace;
            var json = parseResult.GetValue(jsonOption);
            using var http = context.Host.CreateHttpClient();
            IReadOnlyList<Uup.UupRelease> found;
            IReadOnlyList<(string Channel, string Problem)> problems;
            try
            {
                (found, problems) = await Uup.UupChannels.FindAsync(new Uup.WindowsUpdateClient(http), parseResult.GetValue(advancedOption), ct);
            }
            catch (Uup.WindowsUpdateException e)
            {
                return await FailUupAsync(context, json, e.Message);
            }
            if (found.Count == 0)
                return await FailUupAsync(context, json, string.Join("; ", problems.Select(p => $"{p.Channel}: {p.Problem}")));

            foreach (var release in found)
                Uup.UupReleaseCache.Save(workspace, release);

            if (json)
            {
                await context.Output.WriteLineAsync(EngineJson.UupReleases(found, problems));
                return ExitCodes.Success;
            }
            foreach (var release in found)
            {
                var insider = release.Advanced ? "  (Insider: tweaks may not work)" : "";
                await context.Output.WriteLineAsync($"{release.Channel,-16} {release.Build,-12} {release.Title}{insider}");
                await context.Output.WriteLineAsync($"{"",-16} {release.UpdateId}");
            }
            foreach (var (channel, problem) in problems)
                await context.Error.WriteLineAsync($"warning: {channel}: {problem}");
            return ExitCodes.Success;
        });

        var updateOption = new Option<string>("--update")
        {
            Description = "Update id of a release found by 'uup releases'.",
            Required = true,
        };
        var release = new Command("release", "Languages and editions of a release found by 'uup releases'.")
        {
            updateOption,
            workspaceOption,
            jsonOption,
        };
        release.SetAction(async (parseResult, ct) =>
        {
            var workspace = parseResult.GetValue(workspaceOption)?.FullName ?? context.Host.DefaultWorkspace;
            var json = parseResult.GetValue(jsonOption);
            var updateId = parseResult.GetRequiredValue(updateOption);
            if (Uup.UupReleaseCache.Load(workspace, updateId) is not { } found)
            {
                var message = $"release {updateId} is not known; run 'uup releases' first.";
                if (json)
                {
                    await context.Output.WriteLineAsync(EngineJson.Error("unknown-release", message));
                    return ExitCodes.InvalidInput;
                }
                return await FailAsync(context, ExitCodes.InvalidInput, message);
            }

            if (json)
            {
                await context.Output.WriteLineAsync(EngineJson.UupRelease(found));
                return ExitCodes.Success;
            }
            await context.Output.WriteLineAsync($"{found.Title} ({found.Build}), channel {found.Channel}");
            foreach (var language in found.Languages())
            {
                var editions = string.Join(", ", language.Editions);
                await context.Output.WriteLineAsync($"  {language.Code,-11} {editions}");
            }
            return ExitCodes.Success;
        });

        return new Command("uup", "Windows releases straight from Windows Update (UUP).")
        {
            releases,
            release,
            UupFilesCommand(context),
            UupDownloadCommand(context),
        };
    }

    static async Task<int> FailUupAsync(Context context, bool json, string message)
    {
        message = $"{message} Check the internet connection, or use your own ISO.";
        if (!json)
            return await FailAsync(context, ExitCodes.Failure, message);
        await context.Output.WriteLineAsync(EngineJson.Error("network", message));
        return ExitCodes.Failure;
    }

    static Command CleanupCommand(Context context)
    {
        var command = new Command("cleanup", "Unmount images left mounted by an interrupted Shapeshift build (needs administrator rights).");
        command.SetAction(async (_, ct) =>
        {
            if (!DismServicing.IsElevated)
                return await FailAsync(context, ExitCodes.NeedsElevation, "cleanup uses DISM and needs administrator rights.");
            var count = await Task.Run(() => DismServicing.CleanupStale(context.Output), ct);
            await context.Output.WriteLineAsync($"ok: {count} stale mount(s) removed.");
            return ExitCodes.Success;
        });
        return command;
    }

    static Option<FileInfo> ProfileOption() => new("--profile")
    {
        Description = "Profile JSON file.",
        Required = true,
    };

    static Option<FileInfo> CatalogOption() => new("--catalog")
    {
        Description = "Tweak catalog JSON file (default: the catalog shipped with the engine).",
    };

    /// <summary>Читает и проверяет каталог; ошибки — в поток ошибок, null — каталогом пользоваться нельзя.</summary>
    static async Task<TweakCatalog?> TryLoadCatalogAsync(FileInfo? file, Context context, CancellationToken ct)
    {
        var path = file?.FullName ?? context.Host.DefaultCatalog;
        if (!File.Exists(path))
        {
            await context.Error.WriteLineAsync($"error: catalog not found: {path}");
            return null;
        }

        TweakCatalog catalog;
        try
        {
            catalog = CatalogReader.Parse(await File.ReadAllTextAsync(path, ct));
        }
        catch (CatalogFormatException e)
        {
            await context.Error.WriteLineAsync($"error: {Path.GetFileName(path)}: {e.Message}");
            return null;
        }

        var problems = CatalogValidator.Validate(catalog);
        foreach (var problem in problems)
            await context.Error.WriteLineAsync($"error: {Path.GetFileName(path)}: {problem}");
        return problems.Count == 0 ? catalog : null;
    }

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

    /// <summary>Пишет ошибки и предупреждения файла ответов; false — строить нельзя.</summary>
    static async Task<bool> CheckAnswerFileAsync(Profile profile, TweakCatalog catalog, ImageInfo? image, Context context)
    {
        var problems = AnswerFileGenerator.Problems(profile, catalog, image).ToList();
        foreach (var problem in problems)
            await context.Error.WriteLineAsync($"error: {problem}");
        if (problems.Count > 0)
            return false;

        foreach (var warning in AnswerFileGenerator.Warnings(profile, image))
            await context.Error.WriteLineAsync($"warning: {warning}");
        return true;
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
