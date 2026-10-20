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

        // `Shapeshift.exe образ.iso` или `Shapeshift.exe профиль.json` (и «Открыть с помощью») — сразу открыть.
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(File.Exists))
        {
            if (arg.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                _window.OpenIso(arg);
            else if (arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                _window.OpenProfile(arg);
        }
    }
}
