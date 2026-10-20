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
/// Дальше первого шага не пройти, пока не выбран подходящий образ.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    public WizardViewModel(EngineClient engine)
    {
        Source = new SourceViewModel(engine);
        Source.PropertyChanged += OnSourceChanged;
    }

    public SourceViewModel Source { get; }

    public static IReadOnlyList<WizardStep> Steps { get; } = Enum.GetValues<WizardStep>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoNext), nameof(IsLastStep))]
    public partial WizardStep Current { get; set; }

    /// <summary>Раскрывает дополнительные выборы на тех же шагах (шаги появятся в следующих задачах).</summary>
    [ObservableProperty]
    public partial bool AdvancedMode { get; set; }

    public bool CanGoBack => Current != WizardStep.Source;

    public bool CanGoNext => !IsLastStep && CanEnter(Current + 1);

    public bool IsLastStep => Current == Steps[^1];

    public bool CanEnter(WizardStep step) => step == WizardStep.Source || Source.HasImage;

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

    void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SourceViewModel.HasImage))
            return;
        OnPropertyChanged(nameof(CanGoNext));
        StepsAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
