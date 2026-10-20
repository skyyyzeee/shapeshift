using System.Globalization;
using Microsoft.UI.Xaml;
using Shapeshift.App.Services;

namespace Shapeshift.App;

public partial class App : Application
{
    MainWindow? _window;

    public App()
    {
        ApplyLanguage(AppSettings.Current.Language);
        InitializeComponent();
    }

    /// <summary>Язык из настроек — до загрузки любых строк; null — как в Windows.</summary>
    static void ApplyLanguage(string? language)
    {
        if (language is null)
            return;
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo(language);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // `Shapeshift.exe образ.iso` (и «Открыть с помощью») — сразу открыть образ.
        var iso = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (iso is not null)
            _window.OpenIso(iso);
    }
}
