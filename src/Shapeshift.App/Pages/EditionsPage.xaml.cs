using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

public sealed partial class EditionsPage : Page
{
    public EditionsPage() => InitializeComponent();

    public EditionsViewModel ViewModel { get; private set; } = null!;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (EditionsViewModel)e.Parameter;
        base.OnNavigatedTo(e);
        await ViewModel.Languages.LoadAsync();
    }

    async void LanguagesRetry_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await ViewModel.Languages.LoadAsync();

    void LanguageAdd_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ViewModel.Languages.AddSelected();

    void LanguageRemove_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: LanguageItem item })
            ViewModel.Languages.Remove(item);
    }
}
