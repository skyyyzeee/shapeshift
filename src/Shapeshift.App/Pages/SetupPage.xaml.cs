using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Shapeshift.App.Services;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

public sealed partial class SetupPage : Page
{
    public SetupPage()
    {
        InitializeComponent();
        PasswordModeButtons.ItemsSource = new[] { Text.Get("PasswordMode_Change"), Text.Get("PasswordMode_Store") };
    }

    public SetupViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (SetupViewModel)e.Parameter;
        base.OnNavigatedTo(e);
    }

    void AddKeyboard_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        var query = sender.Text.Trim();
        sender.ItemsSource = query.Length == 0
            ? null
            : SetupViewModel.CultureChoices.Where(c => c.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(12).ToList();
    }

    void AddKeyboard_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var choice = args.ChosenSuggestion as Choice
            ?? SetupViewModel.CultureChoices.FirstOrDefault(c => c.Label.Contains(args.QueryText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (choice is null)
            return;
        ViewModel.AddKeyboard(choice);
        sender.Text = "";
        sender.ItemsSource = null;
    }

    void MakeDefaultKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Choice keyboard })
            ViewModel.MakeDefaultKeyboard(keyboard);
    }

    void RemoveKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Choice keyboard })
            ViewModel.Keyboards.Remove(keyboard);
    }

    // Стирание диска включается только через явное подтверждение.
    async void WipeDisk_Toggled(object sender, RoutedEventArgs e)
    {
        if (!WipeDiskSwitch.IsOn)
        {
            ViewModel.WipeDisk0 = false;
            return;
        }
        if (ViewModel.WipeDisk0)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Text.Get("WipeDiskDialogTitle"),
            Content = new TextBlock { Text = Text.Get("WipeDiskDialogText"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Text.Get("WipeDiskDialogConfirm"),
            CloseButtonText = Text.Get("WipeDiskDialogCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        ViewModel.WipeDisk0 = confirmed;
        WipeDiskSwitch.IsOn = confirmed;
    }
}
