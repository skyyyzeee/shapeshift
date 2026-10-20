using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Shapeshift.App.Services;
using Shapeshift.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Shapeshift.App.Pages;

public sealed partial class SourcePage : Page
{
    public SourcePage() => InitializeComponent();

    public SourceViewModel ViewModel { get; private set; } = null!;

    public WizardViewModel Wizard { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Wizard = (WizardViewModel)e.Parameter;
        ViewModel = Wizard.Source;
        base.OnNavigatedTo(e);
        FillPresets();
    }

    async void ChooseIso_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".iso");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await ViewModel.LoadAsync(file.Path);
    }

    // Перетаскивание работает, потому что окно запущено без повышения (ADR-0003).
    void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanChoose || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
        DropZone.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    void Page_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

    async void Page_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        var iso = items.OfType<StorageFile>()
            .FirstOrDefault(f => string.Equals(f.FileType, ".iso", StringComparison.OrdinalIgnoreCase));
        if (iso is not null)
            await ViewModel.LoadAsync(iso.Path);
    }

    void ResetDropZone() => DropZone.ClearValue(Border.BorderBrushProperty);

    bool _presetsFilled;

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _presetsFilled = false;
        base.OnNavigatedFrom(e);
    }

    void FillPresets()
    {
        if (_presetsFilled)
            return;
        PresetBox.ItemsSource = Profiles.Presets;
        _presetsFilled = true;
    }

    void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is not Preset preset)
            return;
        try
        {
            Wizard.ApplyProfile(Profiles.LoadPreset(preset), isPreset: true);
            PresetDescription.Text = preset.Description;
            ProfileMessage.IsOpen = false;
        }
        catch (Exception ex) when (ex is IOException or Shapeshift.Core.ProfileFormatException)
        {
            ShowProfileMessage(InfoBarSeverity.Error, Text.Get("ProfileLoadFailed"), ex.Message);
        }
    }

    async void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        try
        {
            Wizard.ApplyProfile(Profiles.Load(file.Path), isPreset: false);
            PresetBox.SelectedItem = null;
            PresetDescription.Text = "";
            ShowProfileMessage(InfoBarSeverity.Success, Text.Get("ProfileLoaded"), Path.GetFileName(file.Path));
        }
        catch (Exception ex) when (ex is IOException or Shapeshift.Core.ProfileFormatException)
        {
            ShowProfileMessage(InfoBarSeverity.Error, Text.Get("ProfileLoadFailed"), ex.Message);
        }
    }

    void ShowProfileMessage(InfoBarSeverity severity, string title, string message)
    {
        ProfileMessage.Severity = severity;
        ProfileMessage.Title = title;
        ProfileMessage.Message = message;
        ProfileMessage.IsOpen = true;
    }
}
