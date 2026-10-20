using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Shapeshift.App.Services;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

/// <summary>Параметр навигации: модель настроек и идёт ли сейчас сборка (тогда кэш не трогаем).</summary>
public sealed record SettingsArgs(SettingsViewModel ViewModel, bool IsBuilding);

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    public SettingsViewModel ViewModel { get; private set; } = null!;

    public bool CanClear { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = (SettingsArgs)e.Parameter;
        ViewModel = args.ViewModel;
        CanClear = !args.IsBuilding;
        base.OnNavigatedTo(e);
        await ViewModel.RefreshCacheSizeAsync();
    }

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Exit();
    }

    async void ChangeWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.SetWorkspace(folder.Path);
    }

    void DefaultWorkspace_Click(object sender, RoutedEventArgs e) => ViewModel.SetWorkspace(null);

    async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Text.Get("ClearCacheDialogTitle"),
            Content = new TextBlock { Text = Text.Format("ClearCacheDialogText", ViewModel.WorkspacePath), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Text.Get("ClearCacheDialogConfirm"),
            CloseButtonText = Text.Get("WipeDiskDialogCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        ClearFailedBar.IsOpen = !await ViewModel.ClearCacheAsync();
    }
}
