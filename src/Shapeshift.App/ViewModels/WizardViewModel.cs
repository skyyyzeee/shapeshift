using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

public enum WizardStep
{
    Source,
    EditionsAndLanguages,
    Setup,
    Customize,
    Output,
}

/// <summary>
/// Мастер: Источник → Редакции и языки → Установка → Настройка → Результат.
/// Дальше первого шага не пройти, пока не выбран подходящий образ; дальше «Установки» — пока в ней ошибки.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    public WizardViewModel(EngineClient engine)
    {
        Source = new SourceViewModel(engine);
        Editions = new EditionsViewModel();
        Setup = new SetupViewModel();
        Customize = new CustomizeViewModel(engine);
        Output = new OutputViewModel(engine, Source, Editions, Setup, Customize);
        Editions.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EditionsViewModel.IsValid)) StepsChanged(); };
        Output.PropertyChanged += OnOutputChanged;
        Source.PropertyChanged += OnSourceChanged;
        Setup.PropertyChanged += OnSetupChanged;
        Customize.AdvancedTweaksSelected += (_, _) => AdvancedMode = true;
    }

    public SourceViewModel Source { get; }

    public EditionsViewModel Editions { get; }

    public SetupViewModel Setup { get; }

    public CustomizeViewModel Customize { get; }

    public OutputViewModel Output { get; }

    public static IReadOnlyList<WizardStep> Steps { get; } = Enum.GetValues<WizardStep>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoNext), nameof(IsLastStep))]
    public partial WizardStep Current { get; set; }

    /// <summary>Раскрывает дополнительные выборы на тех же шагах.</summary>
    [ObservableProperty]
    public partial bool AdvancedMode { get; set; }

    public bool CanGoBack => Current != WizardStep.Source && !Output.IsBuilding;

    public bool CanGoNext => !IsLastStep && CanEnter(Current + 1);

    public bool IsLastStep => Current == Steps[^1];

    /// <summary>Пока идёт сборка, уйти с шага «Результат» нельзя.</summary>
    public bool CanEnter(WizardStep step)
    {
        if (Output.IsBuilding)
            return step == Current;
        if (step == WizardStep.Source)
            return true;
        if (!Source.HasImage)
            return false;
        if (step > WizardStep.EditionsAndLanguages && !Editions.IsValid)
            return false;
        return step <= WizardStep.Setup || Setup.IsValid;
    }

    public void GoBack()
    {
        if (CanGoBack)
            Current--;
    }

    public void GoNext()
    {
        if (CanGoNext)
            Current++;
    }

    public event EventHandler? StepsAvailabilityChanged;

    /// <summary>
    /// Профиль → выборы мастера. Пресет (<paramref name="isPreset"/>) меняет только то, что в нём задано;
    /// профиль из файла — всё, включая исходный образ и путь результата.
    /// </summary>
    public void ApplyProfile(Shapeshift.Core.Profile profile, bool isPreset)
    {
        Setup.ApplyProfile(profile, onlyWhatIsSet: isPreset);
        Customize.ApplySelection(profile.Tweaks);
        Editions.ApplyProfile(profile.Editions, profile.Setup.Edition);
        if (profile.Setup.WipeDisk0)
            AdvancedMode = true;
        if (profile.Output.IsoPath is { } output)
            Output.OutputPath = output;
        if (profile.Source.IsoPath is { } source && source != Source.IsoPath)
        {
            Current = WizardStep.Source;
            _ = Source.LoadAsync(source);
        }
    }

    partial void OnAdvancedModeChanged(bool value)
    {
        Setup.AdvancedMode = value;
        Customize.AdvancedMode = value;
    }

    void StepsChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        StepsAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnOutputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputViewModel.IsBuilding))
            StepsChanged();
    }

    void OnSetupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SetupViewModel.IsValid))
            StepsChanged();
    }

    void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourceViewModel.Image))
            Editions.SetImage(Source.Image);
        if (e.PropertyName == nameof(SourceViewModel.Image))
            Setup.IsWindows11 = Source.Image?.Product != "windows10";
        if (e.PropertyName == nameof(SourceViewModel.HasImage))
            StepsChanged();
    }
}
