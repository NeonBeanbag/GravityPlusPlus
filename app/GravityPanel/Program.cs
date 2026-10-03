using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace GravityPanel;

/// <summary>
/// 入口点。XAML 只用来描述页面，进程级的规矩(DPI、单实例、异常钩子)必须自己掌握，
/// 所以屏蔽了 XAML 生成的 Main，在这里手写。
/// </summary>
public static class Program
{
    internal const string IpcTogglePanel = "Local\\GravityPanel.Ipc.TogglePanel";

    private static string? GetIpcEvent(string[] args)
        => args.Any(a => a.Equals("--toggle-panel", StringComparison.OrdinalIgnoreCase)) ? IpcTogglePanel : null;

    [STAThread]
    public static void Main()
    {
        var argv = Environment.GetCommandLineArgs();

        // 无头探针：不建窗口、不抢前台，跑完就退
        int pi = Array.FindIndex(argv, a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));
        if (pi >= 0 && pi + 1 < argv.Length)
        {
            int oi = Array.FindIndex(argv, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
            try { Probe.Run(argv[pi + 1], oi >= 0 && oi + 1 < argv.Length ? argv[oi + 1] : null); }
            catch (Exception e) { Log.Write("[PROBE] " + e); }
            return;
        }

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4，必须早于任何窗口/XAML
        try { Native.SetProcessDpiAwarenessContext(-4); } catch { }

        Log.Write("=== Main 进入 ===");

        // 第二实例带 --toggle-panel 时不该弹"已在运行"，所以先做 IPC 通知再判互斥。
        string? ipc = GetIpcEvent(argv);
        if (ipc != null)
        {
            if (EventWaitHandle.TryOpenExisting(ipc, out var evt))
            {
                evt.Set();
                evt.Dispose();
                Log.Write($"[IPC] 已通知已有实例: {ipc}");
                return;
            }
            Log.Write($"[IPC] 无已运行实例({ipc})，转正常启动");
        }

        // 三个钩子缺一不可：Application.Start 的 try/catch 只看得到 Main 里的同步调用，
        // UI 线程上布局/渲染期抛出的 XAML 异常得靠 UnhandledException(在 App 里注册)。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"[FATAL-AppDomain] IsTerminating={e.IsTerminating}\n{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"[FATAL-Task] {e.Exception}");
            e.SetObserved();
        };

        bool createdNew;
        using var single = new Mutex(true, "GravityPanel_SingleInstance", out createdNew);
        if (!createdNew)
        {
            Native.MessageBoxW(IntPtr.Zero, "Gravity 面板已在运行（看托盘图标）。", "已在运行", 0x40);
            return;
        }

        try
        {
            Native.XamlCheckProcessRequirements();
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var app = new App();
                app.Init();
            });
        }
        catch (Exception ex)
        {
            Log.Write($"[FATAL] {ex}");
            Native.MessageBoxW(IntPtr.Zero, $"启动失败:\n\n{ex?.GetType().Name}: {ex?.Message}\n\n(详见 Gravity++.log)", "Gravity++", 0x10);
        }
    }
}
