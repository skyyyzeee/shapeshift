using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Shapeshift.App.Services;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

/// <summary>Шаг мастера, который появится в следующих задачах.</summary>
public sealed partial class StepPlaceholderPage : Page
{
    public StepPlaceholderPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        StepTitle.Text = Text.Get($"StepTitle_{(WizardStep)e.Parameter}");
        base.OnNavigatedTo(e);
    }
}
