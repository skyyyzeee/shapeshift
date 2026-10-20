using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

public sealed partial class EditionsPage : Page
{
    public EditionsPage() => InitializeComponent();

    public EditionsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (EditionsViewModel)e.Parameter;
        base.OnNavigatedTo(e);
    }
}
