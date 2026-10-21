using System.CommandLine;
using Shapeshift.Engine.Uup;

namespace Shapeshift.Engine;

// Языки интерфейса: какие есть в образе и какие можно добавить (языковые пакеты выпуска Windows Update).
public static partial class Cli
{
    static Command LanguagesCommand(Context context)
    {
        var isoOption = new Option<FileInfo>("--iso") { Description = "Your own Windows ISO: languages it has and which can be added (needs the internet)." };
        var updateOption = new Option<string>("--update") { Description = "Or a release found by 'uup releases': its language packs." };
        var workspaceOption = new Option<DirectoryInfo>("--workspace") { Description = "Workspace; release metadata is cached in its cache\\uup folder." };
        var jsonOption = new Option<bool>("--json") { Description = "Print JSON for the app, or { \"error\": { code, message } }." };
        var command = new Command("languages", "Display languages of an image and the language packs that can be added to it.")
        {
            isoOption, updateOption, workspaceOption, jsonOption,
        };
        command.SetAction(async (parseResult, ct) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var quiet = json ? context with { Output = TextWriter.Null, Error = new StringWriter() } : context;
            var workspace = parseResult.GetValue(workspaceOption)?.FullName ?? context.Host.DefaultWorkspace;
            var (exitCode, installed, available, release) = await ListLanguagesAsync(
                parseResult.GetValue(isoOption)?.FullName, parseResult.GetValue(updateOption), workspace, quiet, ct);

            if (json)
            {
                await context.Output.WriteLineAsync(exitCode == ExitCodes.Success
                    ? EngineJson.Languages(installed, available, release)
                    : EngineJson.Error(ExitCodeName(exitCode), Problems(quiet.Error)));
                return exitCode;
            }
            if (exitCode != ExitCodes.Success)
                return exitCode;
            if (installed.Count > 0)
                await context.Output.WriteLineAsync($"In the image: {string.Join(", ", installed)}");
            await context.Output.WriteLineAsync($"Can be added (from {release}): {string.Join(", ", available.Except(installed, StringComparer.OrdinalIgnoreCase))}");
            return ExitCodes.Success;
        });
        return command;
    }

    static async Task<(int ExitCode, IReadOnlyList<string> Installed, IReadOnlyList<string> Available, string? Release)> ListLanguagesAsync(
        string? iso, string? updateId, string workspace, Context context, CancellationToken ct)
    {
        if ((iso is null) == (updateId is null))
            return (await FailAsync(context, ExitCodes.InvalidInput, "pass either --iso or --update."), [], [], null);

        using var http = context.Host.CreateHttpClient();
        IReadOnlyList<string> installed = [];
        UupRelease? release;
        if (iso is not null)
        {
            var source = await TryInspectAsync(iso, context);
            if (source is null)
                return (ExitCodes.InvalidInput, [], [], null);
            installed = source.Info.InstalledLanguages;
            if (ServicingChannel(source.Info.Release) is not { } target || UupChannels.Find(target.Channel) is not { } channel)
            {
                return (await FailAsync(context, ExitCodes.InvalidInput,
                    $"adding languages to your own ISO works for Windows 11 24H2 and 25H2 and Windows 10 2004–22H2; this image is {source.Info.Release}."), installed, [], null);
            }
            try
            {
                var (found, problem) = await UupChannels.FindOneAsync(new WindowsUpdateClient(http), channel, ct);
                if (found is null)
                    return (await FailAsync(context, ExitCodes.Failure, $"language packs come from Windows Update: {problem}. Check the internet connection."), installed, [], null);
                release = found;
                UupReleaseCache.Save(workspace, release);
            }
            catch (WindowsUpdateException e)
            {
                return (await FailAsync(context, ExitCodes.Failure, $"language packs come from Windows Update: {e.Message} Check the internet connection."), installed, [], null);
            }
        }
        else if (UupReleaseCache.Load(workspace, updateId!) is not { } cached)
        {
            return (await FailAsync(context, ExitCodes.InvalidInput, $"release {updateId} is not known; run 'uup releases' first."), [], [], null);
        }
        else
        {
            release = cached;
        }

        try
        {
            var meta = await FetchMetadataAsync(release, workspace, context, http, null, ct);
            if (meta is null)
                return (await FailAsync(context, ExitCodes.Failure, "the release has no metadata file; Windows Update sent an unusual release."), installed, [], null);
            return (ExitCodes.Success, installed, UupLanguagePacks.Available(meta.Value.CompDbs), release.Build);
        }
        catch (Exception e) when (e is UupDownloadException or WindowsUpdateException or InvalidDataException)
        {
            return (await FailAsync(context, ExitCodes.Failure, e.Message), installed, [], null);
        }
    }
}
