using Microsoft.UI.Xaml;

namespace Shapeshift.App;

public partial class App : Application
{
    MainWindow? _window;

    public App() => InitializeComponent();

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
