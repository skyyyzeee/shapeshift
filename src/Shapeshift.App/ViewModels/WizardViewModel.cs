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
        Setup = new SetupViewModel();
        Customize = new CustomizeViewModel(engine);
        Output = new OutputViewModel(engine, Source, Setup, Customize);
        Output.PropertyChanged += OnOutputChanged;
        Source.PropertyChanged += OnSourceChanged;
        Setup.PropertyChanged += OnSetupChanged;
    }

    public SourceViewModel Source { get; }

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
            Setup.IsWindows11 = Source.Image?.Product != "windows10";
        if (e.PropertyName == nameof(SourceViewModel.HasImage))
            StepsChanged();
    }
}
