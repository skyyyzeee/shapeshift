using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Shapeshift.App.Services;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

/// <summary>Параметр навигации: модель шага, образ для каталога и редакция, чьи приложения показывать (null — первая).</summary>
public sealed record CustomizeArgs(CustomizeViewModel ViewModel, string? IsoPath, string? Edition);

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
        await ViewModel.LoadAsync(args.IsoPath);
        await ViewModel.LoadAppsAsync(args.IsoPath, args.Edition);
    }

    void AppsRecommended_Click(object sender, RoutedEventArgs e) => ViewModel.SelectRecommendedApps();

    void AppsClear_Click(object sender, RoutedEventArgs e) => ViewModel.ClearApps();

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
