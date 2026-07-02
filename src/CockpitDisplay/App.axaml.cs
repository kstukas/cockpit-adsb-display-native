using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CockpitDisplay.Services;
using CockpitDisplay.ViewModels;

namespace CockpitDisplay;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var tiles   = new MbTilesService();
        var stratux = new StratuxService();
        var prefs   = new PrefsService();
        var mainVm  = new MainViewModel(tiles, stratux, prefs);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow { DataContext = mainVm };
            desktop.ShutdownRequested += (_, _) => { stratux.Dispose(); tiles.Dispose(); };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            // DRM mode — no window manager, just a single fullscreen view
            single.MainView = new Views.CockpitView { DataContext = mainVm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
