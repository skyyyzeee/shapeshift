using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shapeshift.App.Pages;
using Shapeshift.App.Services;
using Shapeshift.App.ViewModels;
using Windows.Graphics;

namespace Shapeshift.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        ViewModel = new WizardViewModel(new EngineClient());
        InitializeComponent();

        Title = "Shapeshift";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SizeAndCenter(1120, 760);

        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.StepsAvailabilityChanged += (_, _) => UpdateStepAvailability();
        UpdateStepAvailability();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public WizardViewModel ViewModel { get; }

    public void OpenProfile(string path)
    {
        try
        {
            ViewModel.ApplyProfile(Profiles.Load(path), isPreset: false);
        }
        catch (Exception e) when (e is IOException or Shapeshift.Core.ProfileFormatException)
        {
            // Окно ещё может быть не готово к диалогу — показываем, когда загрузится содержимое.
            ContentFrame.Loaded += async (_, _) => await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = Text.Get("ProfileLoadFailed"),
                Content = e.Message,
                CloseButtonText = "OK",
            }.ShowAsync();
        }
    }

    public void OpenIso(string path)
    {
        ViewModel.Current = WizardStep.Source;
        _ = ViewModel.Source.LoadAsync(path);
    }

    readonly SettingsViewModel _settings = new();

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Настройки — не шаг мастера: кнопки «Назад/Далее» на них не нужны.
        if (args.IsSettingsSelected)
        {
            WizardButtons.Visibility = Visibility.Collapsed;
            ContentFrame.Navigate(typeof(SettingsPage), new SettingsArgs(_settings, ViewModel.Output.IsBuilding));
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<WizardStep>(tag, out var step))
        {
            WizardButtons.Visibility = Visibility.Visible;
            ViewModel.Current = step;
            Navigate(step);
        }
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WizardViewModel.Current))
            return;
        var item = Nav.MenuItems[(int)ViewModel.Current];
        if (!ReferenceEquals(Nav.SelectedItem, item))
            Nav.SelectedItem = item;
    }

    void Navigate(WizardStep step)
    {
        switch (step)
        {
            case WizardStep.Source:
                ContentFrame.Navigate(typeof(SourcePage), ViewModel);
                break;
            case WizardStep.EditionsAndLanguages:
                ContentFrame.Navigate(typeof(EditionsPage), ViewModel.Editions);
                break;
            case WizardStep.Setup:
                ContentFrame.Navigate(typeof(SetupPage), ViewModel.Setup);
                break;
            case WizardStep.Customize:
                ContentFrame.Navigate(typeof(CustomizePage), new CustomizeArgs(ViewModel.Customize, ViewModel.Source.Uup is null ? ViewModel.Source.IsoPath : null, ViewModel.Editions.AppsEdition, ViewModel.Source.Uup));
                break;
            case WizardStep.Output:
                ContentFrame.Navigate(typeof(OutputPage), ViewModel.Output);
                break;
            default:
                ContentFrame.Navigate(typeof(StepPlaceholderPage), step);
                break;
        }
    }

    // Шаги после «Источника» недоступны, пока не выбран подходящий образ.
    void UpdateStepAvailability()
    {
        foreach (var (item, step) in Nav.MenuItems.Cast<NavigationViewItem>().Zip(WizardViewModel.Steps))
            item.IsEnabled = ViewModel.CanEnter(step);
    }

    void Back_Click(object sender, RoutedEventArgs e) => ViewModel.GoBack();

    void Next_Click(object sender, RoutedEventArgs e) => ViewModel.GoNext();

    void SizeAndCenter(int width, int height)
    {
        var scale = Content.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(width * scale), (int)(height * scale));
        AppWindow.Resize(size);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(820 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}
