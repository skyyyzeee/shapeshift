using System.CommandLine;
using Shapeshift.Engine.Builds;
using Shapeshift.Engine.Pipes;
using Shapeshift.Engine.Uup;

namespace Shapeshift.Engine;

// Скачивание выпуска UUP: какие файлы нужны (uup files) и само скачивание в кэш (uup download).
public static partial class Cli
{
    sealed record UupSelection(string UpdateId, string Language, IReadOnlyList<string> Editions, string Workspace);

    sealed class UupOptions
    {
        public Option<string> Update { get; } = new("--update") { Description = "Update id of a release found by 'uup releases'.", Required = true };
        public Option<string> Language { get; } = new("--lang") { Description = "Windows language, like ru-RU.", Required = true };
        public Option<string[]> Editions { get; } = new("--edition") { Description = "Edition (EditionID, like Professional); repeat for several.", Required = true, AllowMultipleArgumentsPerToken = true };
        public Option<DirectoryInfo> Workspace { get; } = new("--workspace") { Description = "Workspace; files are cached in its cache\\uup folder." };

        public void AddTo(Command command)
        {
            command.Options.Add(Update);
            command.Options.Add(Language);
            command.Options.Add(Editions);
            command.Options.Add(Workspace);
        }

        public UupSelection Read(ParseResult parseResult, Context context) => new(
            parseResult.GetRequiredValue(Update),
            parseResult.GetRequiredValue(Language),
            parseResult.GetRequiredValue(Editions),
            parseResult.GetValue(Workspace)?.FullName ?? context.Host.DefaultWorkspace);
    }

    static Command UupFilesCommand(Context context)
    {
        var options = new UupOptions();
        var jsonOption = new Option<bool>("--json") { Description = "Print machine-readable JSON (for the app), or { \"error\": { code, message } }." };
        var listOption = new Option<bool>("--list") { Description = "Also list the files, one per line." };
        var command = new Command("files", "Which files a language and editions of a release need, and how much is left to download.");
        options.AddTo(command);
        command.Options.Add(jsonOption);
        command.Options.Add(listOption);
        command.SetAction(async (parseResult, ct) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var quiet = json ? context with { Output = TextWriter.Null, Error = new StringWriter() } : context;
            using var http = context.Host.CreateHttpClient();
            var (exitCode, plan) = await PlanUupAsync(options.Read(parseResult, context), quiet, http, new ConsoleBuildReporter(quiet.Output), ct);
            if (json)
            {
                await context.Output.WriteLineAsync(plan is not null
                    ? EngineJson.UupFiles(plan.Files, plan.Files.Where(plan.Downloader.IsCached).Sum(f => f.Size))
                    : EngineJson.Error(ExitCodeName(exitCode), Problems(quiet.Error)));
                return exitCode;
            }
            if (plan is null)
                return exitCode;
            var cached = plan.Files.Where(plan.Downloader.IsCached).Sum(f => f.Size);
            if (parseResult.GetValue(listOption))
            {
                foreach (var file in plan.Files)
                    await context.Output.WriteLineAsync($"  {file.Name}");
            }
            await context.Output.WriteLineAsync($"{plan.Files.Count} files, {Size(plan.Files.Sum(f => f.Size))}; {Size(cached)} already in the cache.");
            return ExitCodes.Success;
        });
        return command;
    }

    static Command UupDownloadCommand(Context context)
    {
        var options = new UupOptions();
        var pipeOption = new Option<string>("--pipe") { Description = "Report to the app over this named pipe (the app is the pipe server)." };
        var command = new Command("download", "Download the files of a release for a language and editions into the cache (resumes where it stopped).");
        options.AddTo(command);
        command.Options.Add(pipeOption);
        command.SetAction(async (parseResult, ct) =>
        {
            var selection = options.Read(parseResult, context);
            if (parseResult.GetValue(pipeOption) is { } pipeName)
                return await OverPipeAsync(pipeName, context, ct, (piped, reporter, token) => DownloadUupAsync(selection, piped, reporter, token));
            return await DownloadUupAsync(selection, context, new ConsoleBuildReporter(context.Output), ct);
        });
        return command;
    }

    static async Task<int> DownloadUupAsync(UupSelection selection, Context context, IBuildReporter reporter, CancellationToken ct)
    {
        using var http = context.Host.CreateHttpClient();
        try
        {
            var (exitCode, plan) = await PlanUupAsync(selection, context, http, reporter, ct);
            if (plan is null)
                return exitCode;

            reporter.StepStarted(1, 2, new BuildStep(BuildStepKind.DownloadFiles, "Download files"));
            await plan.Downloader.DownloadAsync(plan.Files, plan.Urls, reporter.Transfer, ct);
            await context.Output.WriteLineAsync($"ok: {plan.Files.Count} files are in {plan.Downloader.Folder}.");
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            await context.Error.WriteLineAsync("error: the download was cancelled; it resumes where it stopped next time.");
            return ExitCodes.Cancelled;
        }
        catch (Exception e) when (e is UupDownloadException or WindowsUpdateException)
        {
            return await FailAsync(context, ExitCodes.Failure, e.Message);
        }
    }

    sealed record UupPlan(UupRelease Release, IReadOnlyList<UupFile> Files, UupDownloader Downloader, UupLinks Urls);

    /// <summary>
    /// Выпуск из кэша → проверка языка и редакций → метаданные (скачиваются, если их ещё нет) → список файлов.
    /// План — null, если дальше идти нельзя; причина уже в потоке ошибок.
    /// </summary>
    static async Task<(int ExitCode, UupPlan? Plan)> PlanUupAsync(
        UupSelection selection, Context context, HttpClient http, IBuildReporter reporter, CancellationToken ct)
    {
        if (UupReleaseCache.Load(selection.Workspace, selection.UpdateId) is not { } release)
            return (await FailAsync(context, ExitCodes.InvalidInput, $"release {selection.UpdateId} is not known; run 'uup releases' first."), null);

        var languages = release.Languages();
        var language = languages.FirstOrDefault(l => string.Equals(l.Code, selection.Language, StringComparison.OrdinalIgnoreCase));
        if (language is null)
        {
            return (await FailAsync(context, ExitCodes.InvalidInput,
                $"language '{selection.Language}' is not in this release; available: {string.Join(", ", languages.Select(l => l.Code))}."), null);
        }
        var editions = new List<string>();
        foreach (var wanted in selection.Editions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (language.Editions.FirstOrDefault(e => string.Equals(e, wanted, StringComparison.OrdinalIgnoreCase)) is not { } edition)
            {
                return (await FailAsync(context, ExitCodes.InvalidInput,
                    $"edition '{wanted}' is not available in {language.Code}; available: {string.Join(", ", language.Editions)}."), null);
            }
            editions.Add(edition);
        }

        var client = new WindowsUpdateClient(http);
        var urls = new UupLinks(ct => client.GetFileUrlsAsync(release.UpdateId, release.Revision, release.Query, ct));
        var downloader = new UupDownloader(http, selection.Workspace)
        {
            FreeSpace = context.Host.AvailableFreeSpace,
            StallTimeout = context.Host.DownloadStallTimeout,
        };
        if (UupFileSet.MetadataFile(release) is not { } metadata)
            return (await FailAsync(context, ExitCodes.Failure, "the release has no metadata file; Windows Update sent an unusual release."), null);

        try
        {
            reporter.StepStarted(0, 2, new BuildStep(BuildStepKind.DownloadMetadata, "Download metadata"));
            await downloader.DownloadAsync([metadata], urls, reporter.Transfer, ct);
            var compDbs = Path.Combine(selection.Workspace, "cache", "uup", "meta", release.UpdateId);
            if (!Directory.Exists(compDbs) || !Directory.EnumerateFiles(compDbs, "*.xml.cab").Any())
                Cabinet.Extract(downloader.PathOf(metadata), compDbs);

            var (files, problems) = UupFileSet.Resolve(release, compDbs, language.Code, editions);
            foreach (var problem in problems)
                await context.Error.WriteLineAsync($"error: {problem}");
            return problems.Count > 0 ? (ExitCodes.Failure, null) : (ExitCodes.Success, new UupPlan(release, files, downloader, urls));
        }
        catch (Exception e) when (e is UupDownloadException or WindowsUpdateException or InvalidDataException)
        {
            return (await FailAsync(context, ExitCodes.Failure, e.Message), null);
        }
    }

    static string Problems(TextWriter errors) => string.Join("\n", errors.ToString()!
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => !l.StartsWith("warning: ", StringComparison.Ordinal))
        .Select(l => l.StartsWith("error: ", StringComparison.Ordinal) ? l["error: ".Length..] : l));
}
