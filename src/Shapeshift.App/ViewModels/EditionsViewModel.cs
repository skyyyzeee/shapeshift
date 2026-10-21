using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

public sealed partial class EditionChoice : ObservableObject
{
    public required string EditionId { get; init; }
    public required string Name { get; init; }

    [ObservableProperty]
    public partial bool IsKept { get; set; } = true;
}

/// <summary>
/// Шаг «Редакции и языки»: какие редакции оставить в образе, какую ставить без вопроса и языки интерфейса.
/// </summary>
public sealed partial class EditionsViewModel(EngineClient engine) : ObservableObject
{
    public LanguagesViewModel Languages { get; } = new(engine);

    const string Ask = "";

    // Выбор из профиля, если образ ещё не прочитан: применится, когда появятся редакции.
    IReadOnlyList<string>? _pendingKeep;
    string? _pendingInstall;

    public ObservableCollection<EditionChoice> Editions { get; } = [];

    public ObservableCollection<Choice> InstallChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? Install { get; set; }

    public bool IsValid => Editions.Count == 0 || Editions.Any(e => e.IsKept);

    public bool HasNoneKept => !IsValid;

    /// <summary>Редакции образа: выбор сохраняется по EditionId, новые — оставлены.</summary>
    public void SetImage(ImageSummary? image)
    {
        var previous = Editions.ToDictionary(e => e.EditionId, e => e.IsKept);
        var install = Install?.Value ?? Ask;
        foreach (var edition in Editions)
            edition.PropertyChanged -= OnEditionChanged;
        Editions.Clear();
        foreach (var edition in image?.Editions ?? [])
        {
            var choice = new EditionChoice
            {
                EditionId = edition.EditionId,
                Name = edition.Name,
                IsKept = _pendingKeep is { } keep
                    ? keep.Contains(edition.EditionId, StringComparer.OrdinalIgnoreCase)
                    : previous.GetValueOrDefault(edition.EditionId, true),
            };
            choice.PropertyChanged += OnEditionChanged;
            Editions.Add(choice);
        }
        if (image is not null && _pendingKeep is not null)
        {
            install = _pendingInstall ?? Ask;
            _pendingKeep = null;
            _pendingInstall = null;
        }
        RebuildInstallChoices(install);
    }

    /// <summary>Выбор из профиля: <paramref name="keep"/> пусто — все редакции.</summary>
    public void ApplyProfile(IReadOnlyList<string> keep, string? install)
    {
        if (Editions.Count == 0)
        {
            _pendingKeep = keep;
            _pendingInstall = install;
            return;
        }
        foreach (var edition in Editions)
            edition.IsKept = keep.Count == 0 || keep.Contains(edition.EditionId, StringComparer.OrdinalIgnoreCase);
        RebuildInstallChoices(install ?? Ask);
    }

    void OnEditionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditionChoice.IsKept))
            return;
        RebuildInstallChoices(Install?.Value ?? Ask);
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(HasNoneKept));
    }

    // Ставить можно только оставленную редакцию.
    void RebuildInstallChoices(string selected)
    {
        InstallChoices.Clear();
        InstallChoices.Add(new Choice(Ask, Text.Get("InstallEditionAsk")));
        foreach (var edition in Editions.Where(e => e.IsKept))
            InstallChoices.Add(new Choice(edition.EditionId, edition.Name));
        Install = InstallChoices.FirstOrDefault(c => string.Equals(c.Value, selected, StringComparison.OrdinalIgnoreCase)) ?? InstallChoices[0];
    }

    /// <summary>Для профиля: какие оставить (null — все) и какую ставить (null — спросить).</summary>
    public (IReadOnlyList<string>? Keep, string? Install) ToDraft()
    {
        var kept = Editions.Where(e => e.IsKept).Select(e => e.EditionId).ToList();
        var keep = kept.Count == Editions.Count ? null : kept;
        var install = Install is { Value: var value } && value != Ask ? value : null;
        return (keep, install);
    }

    /// <summary>Чьи встроенные приложения показывать: ставящуюся редакцию, иначе первую оставленную (null — первую в образе).</summary>
    public string? AppsEdition => ToDraft().Install ?? Editions.FirstOrDefault(e => e.IsKept)?.EditionId;

    public IEnumerable<string> Describe()
    {
        var (keep, install) = ToDraft();
        if (keep is not null)
            yield return Text.Format("EditionsSummary_Keep", string.Join(", ", Editions.Where(e => e.IsKept).Select(e => e.Name)));
        if (install is not null)
            yield return Text.Format("EditionsSummary_Install", Install!.Label);
    }
}
