using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

public enum BuildState
{
    Ready,
    Building,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Шаг «Результат»: куда сохранить ISO, сводка «что будет сделано», сборка и её ход.</summary>
public sealed partial class OutputViewModel(
    EngineClient engine, SourceViewModel source, EditionsViewModel editions, SetupViewModel setup, CustomizeViewModel customize)
    : ObservableObject
{
    BuildSession? _session;
    CancellationTokenSource? _planning;

    [ObservableProperty]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild), nameof(HasPlan))]
    public partial PlanSummary? Plan { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild), nameof(HasPlanError))]
    public partial string? PlanError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild))]
    public partial bool IsPlanning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild), nameof(IsBuilding), nameof(IsFinished), nameof(ShowSummary), nameof(ShowProgress),
        nameof(Succeeded), nameof(Failed), nameof(WasCancelled))]
    public partial BuildState State { get; private set; }

    [ObservableProperty]
    public partial string StepText { get; private set; } = "";

    /// <summary>Общий прогресс сборки, 0..100.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string FailureText { get; private set; } = "";

    /// <summary>Подробности шага: при скачивании — объём, скорость, оставшееся время.</summary>
    [ObservableProperty]
    public partial string StepDetail { get; private set; } = "";

    public ObservableCollection<string> Log { get; } = [];

    public ObservableCollection<string> SummaryLines { get; } = [];

    /// <summary>Выборы шага «Установка» — чтобы перед сборкой было видно, что попадёт в образ.</summary>
    public ObservableCollection<string> Choices { get; } = [];

    IEnumerable<string> DescribeTweaks()
    {
        var names = customize.SelectedNames.ToList();
        if (names.Count > 0)
            yield return Text.Format("CustomizeSummary_Tweaks", names.Count, string.Join(", ", names));
        var apps = customize.SelectedAppTitles.ToList();
        if (apps.Count > 0)
            yield return Text.Format("CustomizeSummary_Apps", apps.Count, string.Join(", ", apps));
        var programs = customize.Programs.SelectedNames.ToList();
        if (programs.Count > 0)
            yield return Text.Format("CustomizeSummary_Programs", programs.Count, string.Join(", ", programs));
        if (customize.Wallpaper.Summary is { } wallpaper)
            yield return wallpaper;
    }

    ProfileDraft Draft(string outputIso)
    {
        var (tweaks, setupDraft, regional) = setup.ToDraft();
        var (keep, install) = editions.ToDraft();
        // Для UUP редакции — всегда явно: без них движок скачал бы только Pro.
        if (source.Uup is not null)
            keep ??= editions.Editions.Select(e => e.EditionId).ToList();
        return new ProfileDraft(source.Uup is null ? source.IsoPath : null, outputIso, [.. tweaks, .. customize.SelectedTweaks], keep,
            setupDraft with { Edition = install }, regional, customize.SelectedApps, customize.Programs.ToDraft(), source.Uup,
            customize.Wallpaper.ToDraft(), editions.Languages.ToDraft(), editions.DotNet35);
    }

    bool HasSource => source.HasSource;

    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasPlan => Plan is not null;

    public bool HasPlanError => PlanError is not null;

    public bool CanBuild => Plan is not null && PlanError is null && !IsPlanning && State != BuildState.Building;

    public bool IsBuilding => State == BuildState.Building;

    public bool IsFinished => State is BuildState.Succeeded or BuildState.Failed or BuildState.Cancelled;

    public bool ShowSummary => State == BuildState.Ready;

    public bool ShowProgress => State != BuildState.Ready;

    public bool Succeeded => State == BuildState.Succeeded;

    public bool Failed => State == BuildState.Failed;

    public bool WasCancelled => State == BuildState.Cancelled;

    /// <summary>Путь по умолчанию: «Документы\&lt;исходный образ&gt;-shapeshift.iso» (для UUP — «Windows-ru-RU-shapeshift.iso»).</summary>
    public void SuggestOutputPath()
    {
        if (OutputPath is not null || !HasSource)
            return;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var name = source.Uup is { } uup
            ? $"{(source.Image?.Product == "windows10" ? "Windows10" : "Windows11")}-{uup.Language}"
            : Path.GetFileNameWithoutExtension(source.IsoPath);
        OutputPath = Path.Combine(documents, name + "-shapeshift.iso");
    }

    /// <summary>Все выборы мастера — в файл профиля (тот же формат, что принимает CLI).</summary>
    public async Task SaveProfileAsync(string path)
    {
        if (!HasSource || OutputPath is null)
            return;
        await File.WriteAllTextAsync(path, ProfileFile.ToJson(Draft(OutputPath)));
    }

    /// <summary>Только файл ответов — для обычного ISO. null — успех, иначе текст ошибки.</summary>
    public async Task<string?> ExportAnswerFileAsync(string path)
    {
        if (!HasSource || OutputPath is null)
            return null;
        var profile = ProfileFile.Write(Draft(OutputPath));
        try
        {
            return await engine.AnswerFileAsync(profile, path);
        }
        finally
        {
            ProfileFile.TryDelete(profile);
        }
    }

    /// <summary>Пробный прогон движка без повышения — для сводки и проверки места.</summary>
    public async Task RefreshPlanAsync()
    {
        if (!HasSource || OutputPath is null || State == BuildState.Building)
            return;

        _planning?.Cancel();
        var planning = _planning = new CancellationTokenSource();
        IsPlanning = true;
        Plan = null;
        PlanError = null;
        var profile = ProfileFile.Write(Draft(OutputPath));
        try
        {
            var result = await engine.PlanAsync(profile, AppSettings.Current.EffectiveWorkspace, planning.Token);
            if (planning.IsCancellationRequested)
                return;

            Plan = result.Plan;
            PlanError = result.Plan is null ? Text.Format("PlanError", result.ErrorMessage ?? result.ErrorCode ?? "") : null;
            FillSummary(result.Plan);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ProfileFile.TryDelete(profile);
            if (planning == _planning)
                IsPlanning = false;
        }
    }

    void FillSummary(PlanSummary? plan)
    {
        SummaryLines.Clear();
        Warnings.Clear();
        Choices.Clear();
        foreach (var choice in editions.Describe().Concat(editions.Languages.Describe()).Concat(setup.Describe()).Concat(DescribeTweaks()))
            Choices.Add(choice);
        if (plan is null)
            return;

        foreach (var step in plan.Steps)
        {
            var name = Text.Get($"StepKind_{step.Kind}");
            SummaryLines.Add(step.Volume is null ? name : Text.Format("SummaryStepOnVolume", name, Size(step.Bytes), step.Volume));
        }
        foreach (var space in plan.Space)
            SummaryLines.Add(Text.Format("SummarySpace", space.Volume, Size(space.RequiredBytes), Size(space.AvailableBytes)));
        foreach (var warning in plan.Warnings)
            Warnings.Add(warning);
    }

    /// <summary>Сборка: движок с повышением (один запрос UAC), ход — по каналу.</summary>
    public async Task BuildAsync()
    {
        if (!CanBuild || !HasSource || OutputPath is null)
            return;

        Log.Clear();
        Progress = 0;
        StepText = Text.Get("BuildStarting");
        FailureText = "";
        State = BuildState.Building;

        var profile = ProfileFile.Write(Draft(OutputPath));
        try
        {
            _session = await BuildSession.StartBuildAsync(engine.EnginePath, profile, AppSettings.Current.EffectiveWorkspace, CancellationToken.None);
            if (_session is null)
            {
                Finish(BuildState.Failed, Text.Get("BuildUacDenied"));
                return;
            }

            var errors = new List<string>();
            var exitCode = -1;
            await foreach (var message in _session.ReadAsync())
            {
                switch (message.Type)
                {
                    case "step":
                        var index = message.Body.GetProperty("index").GetInt32();
                        var total = message.Body.GetProperty("total").GetInt32();
                        var kind = message.Body.GetProperty("kind").GetString();
                        StepText = Text.Format("BuildStep", index + 1, total, Text.Get($"StepKind_{kind}"));
                        StepDetail = "";
                        Progress = 100.0 * index / total;
                        _stepIndex = index;
                        _stepTotal = total;
                        Log.Add($"[{index + 1}/{total}] {message.Body.GetProperty("description").GetString()}");
                        break;
                    case "progress":
                        var fraction = message.Body.GetProperty("fraction").GetDouble();
                        Progress = 100.0 * (_stepIndex + fraction) / Math.Max(1, _stepTotal);
                        break;
                    case "transfer":
                        var (done, detail) = DownloadViewModel.DescribeTransfer(message.Body);
                        Progress = 100.0 * (_stepIndex + done) / Math.Max(1, _stepTotal);
                        StepDetail = detail;
                        break;
                    case "log":
                        Log.Add(message.Text ?? "");
                        break;
                    case "warning":
                        Log.Add("warning: " + message.Text);
                        break;
                    case "error":
                        errors.Add(message.Text ?? "");
                        Log.Add("error: " + message.Text);
                        break;
                    case "done":
                        Log.Add("Done: " + message.Body.GetProperty("outputIso").GetString());
                        break;
                    case "exit":
                        exitCode = message.Body.GetProperty("code").GetInt32();
                        break;
                }
            }

            switch (exitCode)
            {
                case 0:
                    Progress = 100;
                    Finish(BuildState.Succeeded, "");
                    break;
                case 4:
                    Finish(BuildState.Cancelled, "");
                    break;
                default:
                    Finish(BuildState.Failed, errors.Count > 0 ? string.Join("\n", errors) : Text.Get("BuildFailedUnknown"));
                    break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException)
        {
            Finish(BuildState.Failed, Text.Format("BuildEngineLost", e.Message));
        }
        finally
        {
            if (_session is not null)
                await _session.DisposeAsync();
            _session = null;
            ProfileFile.TryDelete(profile);
        }
    }

    int _stepIndex;
    int _stepTotal = 1;

    public async Task CancelAsync()
    {
        if (_session is not null)
        {
            StepText = Text.Get("BuildCancelling");
            await _session.CancelAsync();
        }
    }

    /// <summary>Вернуться к сводке после завершённой сборки.</summary>
    public void Reset()
    {
        if (State != BuildState.Building)
            State = BuildState.Ready;
    }

    void Finish(BuildState state, string failure)
    {
        FailureText = failure;
        State = state;
    }

    static string Size(long bytes) => Text.Size(bytes);
}
