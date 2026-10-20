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
    EngineClient engine, SourceViewModel source, SetupViewModel setup, CustomizeViewModel customize) : ObservableObject
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

    public ObservableCollection<string> Log { get; } = [];

    public ObservableCollection<string> SummaryLines { get; } = [];

    /// <summary>Выборы шага «Установка» — чтобы перед сборкой было видно, что попадёт в образ.</summary>
    public ObservableCollection<string> Choices { get; } = [];

    IEnumerable<string> DescribeTweaks()
    {
        var names = customize.SelectedNames.ToList();
        if (names.Count > 0)
            yield return Text.Format("CustomizeSummary_Tweaks", names.Count, string.Join(", ", names));
    }

    ProfileDraft Draft(string sourceIso, string outputIso)
    {
        var (tweaks, setupDraft, regional) = setup.ToDraft();
        return new ProfileDraft(sourceIso, outputIso, [.. tweaks, .. customize.SelectedTweaks], setupDraft, regional);
    }

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

    /// <summary>Путь по умолчанию: «Документы\&lt;исходный образ&gt;-shapeshift.iso».</summary>
    public void SuggestOutputPath()
    {
        if (OutputPath is not null || source.IsoPath is null)
            return;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        OutputPath = Path.Combine(documents, Path.GetFileNameWithoutExtension(source.IsoPath) + "-shapeshift.iso");
    }

    /// <summary>Пробный прогон движка без повышения — для сводки и проверки места.</summary>
    public async Task RefreshPlanAsync()
    {
        if (source.IsoPath is null || OutputPath is null || State == BuildState.Building)
            return;

        _planning?.Cancel();
        var planning = _planning = new CancellationTokenSource();
        IsPlanning = true;
        Plan = null;
        PlanError = null;
        var profile = ProfileFile.Write(Draft(source.IsoPath, OutputPath));
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
        foreach (var choice in setup.Describe().Concat(DescribeTweaks()))
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
        if (!CanBuild || source.IsoPath is null || OutputPath is null)
            return;

        Log.Clear();
        Progress = 0;
        StepText = Text.Get("BuildStarting");
        FailureText = "";
        State = BuildState.Building;

        var profile = ProfileFile.Write(Draft(source.IsoPath, OutputPath));
        try
        {
            _session = await BuildSession.StartAsync(engine.EnginePath, profile, AppSettings.Current.EffectiveWorkspace, CancellationToken.None);
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
                        Progress = 100.0 * index / total;
                        _stepIndex = index;
                        _stepTotal = total;
                        Log.Add($"[{index + 1}/{total}] {message.Body.GetProperty("description").GetString()}");
                        break;
                    case "progress":
                        var fraction = message.Body.GetProperty("fraction").GetDouble();
                        Progress = 100.0 * (_stepIndex + fraction) / Math.Max(1, _stepTotal);
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

    static string Size(long bytes) => bytes >= 1L << 30
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} {1}", bytes / (double)(1L << 30), Text.Get("UnitGB"))
        : string.Format(CultureInfo.CurrentCulture, "{0:0.0} {1}", bytes / (double)(1L << 20), Text.Get("UnitMB"));
}
