using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Shapeshift.App.Services;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

/// <summary>
/// Параметр навигации: модель шага, образ для каталога, редакция, чьи приложения показывать (null — первая),
/// и выпуск UUP, если источник — он (приложения — те, что добавит сборка).
/// </summary>
public sealed record CustomizeArgs(CustomizeViewModel ViewModel, string? IsoPath, string? Edition, UupSourceDraft? Uup = null);

public sealed partial class CustomizePage : Page
{
    bool _resolving;

    public CustomizePage() => InitializeComponent();

    public CustomizeViewModel ViewModel { get; private set; } = null!;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = (CustomizeArgs)e.Parameter;
        ViewModel = args.ViewModel;
        base.OnNavigatedTo(e);
        ViewModel.Wallpaper.PropertyChanged -= Wallpaper_PropertyChanged;
        ViewModel.Wallpaper.PropertyChanged += Wallpaper_PropertyChanged;
        ShowWallpaper();
        await ViewModel.LoadAsync(args.IsoPath);
        await ViewModel.LoadAppsAsync(args.IsoPath, args.Edition, args.Uup);
    }

    void AppsRecommended_Click(object sender, RoutedEventArgs e) => ViewModel.SelectRecommendedApps();

    void AppsClear_Click(object sender, RoutedEventArgs e) => ViewModel.ClearApps();

    void AddWinget_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Programs.AddWinget(WingetIdBox.Text))
            WingetIdBox.Text = "";
    }

    void WingetIdBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AddWinget_Click(sender, e);
    }

    async void AddInstaller_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        foreach (var extension in Shapeshift.Core.ProgramRules.Runnable)
            picker.FileTypeFilter.Add(extension);
        if (await picker.PickSingleFileAsync() is { } file)
            ViewModel.Programs.AddFile(file.Path);
    }

    async void AddInstallerFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (await picker.PickSingleFolderAsync() is { } folder)
            ViewModel.Programs.AddFolder(folder.Path);
    }

    async void WallpaperPick_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        foreach (var extension in Shapeshift.Core.WallpaperOptions.Extensions)
            picker.FileTypeFilter.Add(extension);
        if (await picker.PickSingleFileAsync() is { } file)
            ViewModel.Wallpaper.Path = file.Path;
    }

    void WallpaperClear_Click(object sender, RoutedEventArgs e) => ViewModel.Wallpaper.Clear();

    void Wallpaper_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WallpaperViewModel.Path))
            ShowWallpaper();
    }

    // Превью — уменьшенная копия: большие фотографии не держим в памяти целиком.
    void ShowWallpaper() => WallpaperPreview.Source = ViewModel.Wallpaper.Path is { } path && File.Exists(path)
        ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) { DecodePixelWidth = 384 }
        : null;

    void RemoveProgram_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProgramItem item })
            ViewModel.Programs.Remove(item);
    }

    // Включили твик, который конфликтует с уже включёнными, — предлагаем выключить те.
    async void Tweak_Toggled(object sender, RoutedEventArgs e)
    {
        if (_resolving || sender is not ToggleSwitch { Tag: TweakItem tweak, IsOn: true } toggle)
            return;

        var conflicts = ViewModel.ConflictsOf(tweak);
        if (conflicts.Count == 0)
            return;

        _resolving = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Text.Get("TweakConflictTitle"),
                Content = new TextBlock
                {
                    Text = Text.Format("TweakConflictText", tweak.Name, string.Join(", ", conflicts.Select(c => c.Name))),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = Text.Get("TweakConflictReplace"),
                CloseButtonText = Text.Get("TweakConflictKeep"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                foreach (var other in conflicts)
                    other.IsOn = false;
            }
            else
            {
                tweak.IsOn = false;
                toggle.IsOn = false;
            }
        }
        finally
        {
            _resolving = false;
        }
    }
}
