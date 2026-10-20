using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

/// <summary>Выпуск в списке: название, сборка и канал по-человечески.</summary>
public sealed record ReleaseChoice(UupReleaseSummary Release, string Title, string Detail);

/// <summary>Язык выпуска: код и название на языке интерфейса.</summary>
public sealed record LanguageChoice(string Code, string Name, IReadOnlyList<string> Editions);

public enum DownloadState
{
    Idle,
    Downloading,
    Done,
    Failed,
    Cancelled,
}

/// <summary>Редакция выпуска — флажок.</summary>
public sealed partial class EditionPick : ObservableObject
{
    public required string EditionId { get; init; }
    public required string Name { get; init; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

/// <summary>
/// Шаг «Источник → Скачать»: выпуск из Windows Update → язык → редакции → скачивание в кэш.
/// Список и объём приходят от движка (<c>uup releases</c>, <c>uup files</c>), скачивает он же — без повышения,
/// с ходом по каналу. Сборка образа из скачанного — следующая задача.
/// </summary>
public sealed partial class DownloadViewModel(EngineClient engine) : ObservableObject
{
    bool? _loadedAdvanced;

    public ObservableCollection<ReleaseChoice> Releases { get; } = [];

    public ObservableCollection<LanguageChoice> Languages { get; } = [];

    public ObservableCollection<EditionPick> Editions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleases))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool HasReleases => !IsLoading && Releases.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsider), nameof(HasRelease))]
    public partial ReleaseChoice? Release { get; set; }

    public bool HasRelease => Release is not null;

    public bool IsInsider => Release?.Release.Advanced == true;

    [ObservableProperty]
    public partial LanguageChoice? Language { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingLanguages { get; private set; }

    /// <summary>Каналы Insider — только в продвинутом режиме.</summary>
    public async Task LoadAsync(bool advanced, bool force = false)
    {
        if (!force && _loadedAdvanced == advanced && Releases.Count > 0)
            return;

        var previous = Release?.Release.Channel;
        IsLoading = true;
        Error = null;
        try
        {
            var result = await engine.UupReleasesAsync(advanced, AppSettings.Current.EffectiveWorkspace);
            Releases.Clear();
            if (result.Error is { } error)
            {
                Error = Text.Format("UupError", error);
                return;
            }
            foreach (var release in result.Releases)
                Releases.Add(new ReleaseChoice(release, release.Title, Text.Format("UupReleaseDetail", release.Build, ChannelName(release.Channel))));
            _loadedAdvanced = advanced;
            Release = Releases.FirstOrDefault(r => r.Release.Channel == previous) ?? Releases.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasReleases));
        }
    }

    async partial void OnReleaseChanged(ReleaseChoice? value)
    {
        OnPropertyChanged(nameof(CanDownload));
        var previous = Language?.Code ?? CultureInfo.CurrentUICulture.Name;
        Languages.Clear();
        Editions.Clear();
        Language = null;
        if (value is null)
            return;

        IsLoadingLanguages = true;
        try
        {
            var languages = await engine.UupLanguagesAsync(value.Release.UpdateId, AppSettings.Current.EffectiveWorkspace);
            if (Release != value)
                return; // пока читали, выбрали другой выпуск
            if (languages is null)
            {
                Error = Text.Format("UupError", value.Release.UpdateId);
                return;
            }
            foreach (var language in languages.OrderBy(l => LanguageName(l.Code), StringComparer.CurrentCultureIgnoreCase))
                Languages.Add(new LanguageChoice(language.Code, LanguageName(language.Code), language.Editions));
            Language = Languages.FirstOrDefault(l => string.Equals(l.Code, previous, StringComparison.OrdinalIgnoreCase))
                ?? Languages.FirstOrDefault(l => l.Code == "en-US")
                ?? Languages.FirstOrDefault();
        }
        finally
        {
            IsLoadingLanguages = false;
        }
    }

    partial void OnLanguageChanged(LanguageChoice? value)
    {
        var chosen = Editions.Where(e => e.IsOn).Select(e => e.EditionId).ToHashSet();
        Editions.Clear();
        foreach (var edition in value?.Editions ?? [])
        {
            var pick = new EditionPick { EditionId = edition, Name = EditionName(edition), IsOn = chosen.Count == 0 ? edition == "Professional" : chosen.Contains(edition) };
            pick.PropertyChanged += (_, _) => SelectionChanged();
            Editions.Add(pick);
        }
        SelectionChanged();
    }

    // ---- Объём и скачивание ----

    BuildSession? _session;
    CancellationTokenSource? _measuring;
    bool _filesStep;

    [ObservableProperty]
    public partial string? SizeText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsMeasuring { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading), nameof(CanDownload), nameof(IsDone), nameof(HasFailed), nameof(WasCancelled))]
    public partial DownloadState State { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    [ObservableProperty]
    public partial string? FailureText { get; private set; }

    public bool IsDownloading => State == DownloadState.Downloading;

    public bool IsDone => State == DownloadState.Done;

    public bool HasFailed => State == DownloadState.Failed;

    public bool WasCancelled => State == DownloadState.Cancelled;

    public bool CanDownload => State != DownloadState.Downloading && !IsMeasuring && Release is not null && Language is not null && Editions.Any(e => e.IsOn);

    IReadOnlyList<string> ChosenEditions => Editions.Where(e => e.IsOn).Select(e => e.EditionId).ToList();

    void SelectionChanged()
    {
        if (State != DownloadState.Downloading)
            State = DownloadState.Idle;
        OnPropertyChanged(nameof(CanDownload));
        SourceChanged?.Invoke(this, EventArgs.Empty);
        _ = MeasureAsync();
    }

    /// <summary>Изменился выбор выпуска, языка или редакций — источник мастера другой.</summary>
    public event EventHandler? SourceChanged;

    /// <summary>Источник для профиля; null — выбор не закончен (нет редакций).</summary>
    public UupSourceDraft? Draft => Release is { } release && Language is { } language && ChosenEditions.Count > 0
        ? new UupSourceDraft(release.Release.Channel, language.Code, release.Release.UpdateId)
        : null;

    /// <summary>Образ, каким он получится: для шагов «Редакции» и «Установка».</summary>
    public ImageSummary? Image => Draft is null
        ? null
        : new ImageSummary(
            int.TryParse(Release!.Release.Build.Split('.')[0], out var build) && build < 22000 ? "windows10" : "windows11",
            Release.Release.Title,
            "x64",
            Language!.Code,
            Editions.Where(e => e.IsOn).Select((e, i) => new EditionSummary(i + 1, e.EditionId, e.Name)).ToList());

    /// <summary>Выбранные редакции — для профиля (поле editions).</summary>
    public IReadOnlyList<string> SelectedEditions => ChosenEditions;

    /// <summary>Сколько скачать для выбранного: первый раз движок скачивает метаданные выпуска (несколько МБ).</summary>
    async Task MeasureAsync()
    {
        _measuring?.Cancel();
        var measuring = _measuring = new CancellationTokenSource();
        if (Release is not { } release || Language is not { } language || ChosenEditions is not { Count: > 0 } editions)
        {
            SizeText = null;
            return;
        }

        IsMeasuring = true;
        try
        {
            var result = await engine.UupFilesAsync(release.Release.UpdateId, language.Code, editions, AppSettings.Current.EffectiveWorkspace, measuring.Token);
            if (measuring.IsCancellationRequested)
                return;
            SizeText = result.Error is { } error ? Text.Format("UupError", error)
                : result.CachedBytes >= result.Bytes ? Text.Format("UupSizeCached", Text.Size(result.Bytes))
                : Text.Format("UupSize", Text.Size(result.Bytes - result.CachedBytes), Text.Size(result.Bytes));
        }
        catch (OperationCanceledException)
        {
            // Выбор изменился, пока считали.
        }
        finally
        {
            if (measuring == _measuring)
                IsMeasuring = false;
        }
    }

    public async Task DownloadAsync()
    {
        if (!CanDownload || Release is not { } release || Language is not { } language)
            return;

        State = DownloadState.Downloading;
        Progress = 0;
        ProgressText = Text.Get("UupDownloadStarting");
        FailureText = null;
        _filesStep = false;
        try
        {
            _session = await BuildSession.StartDownloadAsync(engine.EnginePath, release.Release.UpdateId, language.Code, ChosenEditions, AppSettings.Current.EffectiveWorkspace, CancellationToken.None);
            var errors = new List<string>();
            var exitCode = -1;
            await foreach (var message in _session.ReadAsync())
            {
                switch (message.Type)
                {
                    case "step":
                        _filesStep = message.Body.GetProperty("kind").GetString() == "DownloadFiles";
                        ProgressText = Text.Get(_filesStep ? "UupDownloadStarting" : "UupDownloadMetadata");
                        break;
                    case "transfer" when _filesStep:
                        ShowTransfer(message.Body);
                        break;
                    case "error":
                        errors.Add(message.Text ?? "");
                        break;
                    case "exit":
                        exitCode = message.Body.GetProperty("code").GetInt32();
                        break;
                }
            }
            FailureText = errors.Count > 0 ? string.Join("\n", errors) : Text.Get("BuildFailedUnknown");
            State = exitCode switch { 0 => DownloadState.Done, 4 => DownloadState.Cancelled, _ => DownloadState.Failed };
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            FailureText = Text.Format("BuildEngineLost", e.Message);
            State = DownloadState.Failed;
        }
        finally
        {
            if (_session is not null)
                await _session.DisposeAsync();
            _session = null;
            _ = MeasureAsync();
        }
    }

    public async Task CancelDownloadAsync()
    {
        if (_session is not null)
        {
            ProgressText = Text.Get("UupDownloadCancelling");
            await _session.CancelAsync();
        }
    }

    void ShowTransfer(System.Text.Json.JsonElement body)
    {
        (var fraction, ProgressText) = DescribeTransfer(body);
        Progress = 100 * fraction;
    }

    /// <summary>Сообщение transfer движка → доля и «2,3 из 9,1 ГБ · 7,5 МБ/с · осталось 18 мин».</summary>
    public static (double Fraction, string Text) DescribeTransfer(System.Text.Json.JsonElement body)
    {
        var bytes = body.GetProperty("bytes").GetInt64();
        var total = body.GetProperty("total").GetInt64();
        var speed = body.GetProperty("bytesPerSecond").GetDouble();
        var parts = new List<string> { Text.Format("UupProgressAmount", Text.Size(bytes), Text.Size(total)) };
        if (speed > 0)
            parts.Add(Text.Format("UupProgressSpeed", Text.Size((long)speed)));
        if (body.GetProperty("secondsLeft") is { ValueKind: System.Text.Json.JsonValueKind.Number } left)
        {
            var time = TimeSpan.FromSeconds(left.GetInt64());
            parts.Add(time.TotalHours >= 1
                ? Text.Format("UupProgressLeftHours", (int)time.TotalHours, time.Minutes)
                : Text.Format("UupProgressLeftMinutes", Math.Max(1, (int)Math.Ceiling(time.TotalMinutes))));
        }
        return (total > 0 ? (double)bytes / total : 0, string.Join("  ·  ", parts));
    }

    static string ChannelName(string channel) => Text.Get($"UupChannel_{channel}") is var name && name != $"UupChannel_{channel}" ? name : channel;

    static string EditionName(string editionId) => Text.Get($"Edition_{editionId}") is var name && name != $"Edition_{editionId}" ? name : editionId;

    static string LanguageName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}
