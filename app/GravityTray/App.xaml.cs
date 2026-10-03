using System.Threading;
using System.Windows;
using WpfApp = System.Windows.Application;

namespace GravityTray;

public partial class App : WpfApp
{
    private Mutex? singleInstance;
    private TrayManager? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        singleInstance = new Mutex(true, "GravityTray_SingleInstance", out bool isNew);
        if (!isNew) { Shutdown(); return; }

        var api = new LocalApi();
        api.Start();
        tray = new TrayManager(api);
        if (e.Args.Contains("--show")) tray.TogglePanel();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        base.OnExit(e);
    }
}
