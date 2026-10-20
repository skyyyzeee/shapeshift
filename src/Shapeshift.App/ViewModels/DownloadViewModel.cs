using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

/// <summary>Выпуск в списке: название, сборка и канал по-человечески.</summary>
public sealed record ReleaseChoice(UupReleaseSummary Release, string Title, string Detail);

/// <summary>Язык выпуска: код и название на языке интерфейса.</summary>
public sealed record LanguageChoice(string Code, string Name, IReadOnlyList<string> Editions);

/// <summary>Редакция выпуска — флажок.</summary>
public sealed partial class EditionPick : ObservableObject
{
    public required string EditionId { get; init; }
    public required string Name { get; init; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

/// <summary>
/// Шаг «Источник → Скачать»: выпуск из Windows Update → язык → редакции. Список приходит от движка
/// (<c>uup releases</c>), напрямую из Windows Update. Скачивание и сборка образа — следующие задачи.
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
            Editions.Add(new EditionPick { EditionId = edition, Name = EditionName(edition), IsOn = chosen.Count == 0 ? edition == "Professional" : chosen.Contains(edition) });
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
