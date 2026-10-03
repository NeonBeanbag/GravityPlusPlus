using System.Drawing;
using System.IO;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GravityPanel;

/// <summary>
/// 进程级调度：托盘图标(消息循环锚点) + 面板窗口的开合 + 进程外 IPC。
/// 面板窗口懒建：第一次点托盘才创建，之后只淡入淡出、不销毁。
/// </summary>
public sealed partial class App : Application
{
    private TaskbarIcon _tray = null!;
    private HotkeyGate? _hotkey;
    private PanelWindow? _window;
    private DispatcherQueue? _ui;
    private EventWaitHandle? _ipcHandle;

    public static App Self { get; private set; } = null!;

    public App()
    {
        Self = this;
        InitializeComponent();
        UnhandledException += (s, e) =>
        {
            Log.Write($"[FATAL-XAML] {e.Exception}");
            e.Handled = true;   // 单页画坏了不该带走整个托盘
        };
    }

    public void Init()
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        CreateTray();
        StartIpcListener();
        // 全局热键在启动时就注册（不等面板第一次打开），这样别的软件随时能用快捷键呼起面板
        var combo = Prefs.Hotkey ?? HotkeyGate.Default;
        _hotkey = new HotkeyGate(combo, () => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(TogglePanel));
        Log.Write(_hotkey.Registered ? $"热键注册: {combo}" : $"热键没注册上（{combo} 被别的程序占了）—— 托盘和命令行仍然可用");
        Log.Write("Init 完成（托盘图标应已出现）");
    }

    /// <summary>设备状态只写进托盘提示 —— 面板上不再常驻这行字。</summary>
    internal static void SetStatus(string text)
    {
        var tray = Self?._tray;
        if (tray is not null) tray.ToolTipText = "Gravity++ · " + text;
    }

    /// <summary>页眉右上角的音源徽章（AirPlay / 蓝牙 / 待机）；传空串收起。</summary>
    internal static void SetSource(string text) => Self._window?.SetSource(text);

    /// <summary>WinRT 投影（文件选择器等）要宿主窗口句柄；面板没建出来时为 0。</summary>
    internal static IntPtr PanelHwnd => Self._window?.Hwnd ?? IntPtr.Zero;

    // ---------- 面板开合 ----------
    public void TogglePanel()
    {
        if (_window is null)
        {
            _window = new PanelWindow();
            _window.BuildAndPlace();
            _window.ShowPanel();
            return;
        }
        if (_window.IsShown) _window.HidePanel();
        else _window.ShowPanel();
    }

    private void CreateTray()
    {
        _tray = new TaskbarIcon { ToolTipText = "Gravity 音响面板" };
        var ico = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(ico))
            try { _tray.Icon = new Icon(ico); } catch (Exception e) { Log.Write("托盘图标读取失败: " + e); }
        _tray.ForceCreate(true);
        _tray.NoLeftClickDelay = true;         // 左键立即响应，不等双击判定
        _tray.LeftClickCommand = new RelayCommand(TogglePanel);
        _tray.ContextFlyout = BuildTrayMenu();
    }

    private MenuFlyout BuildTrayMenu()
    {
        var menu = new MenuFlyout();
        var toggle = new MenuFlyoutItem { Text = "显示 / 隐藏面板" };
        toggle.Command = new RelayCommand(TogglePanel);

        // 勾选态只有一个来源：注册表。写完立刻回读，菜单不会和系统状态各说各话。
        var autoStart = new ToggleMenuFlyoutItem { Text = "开机自启", IsChecked = StartupRegistrar.Enabled };
        autoStart.Click += (_, _) =>
        {
            StartupRegistrar.Set(!autoStart.IsChecked);
            autoStart.IsChecked = StartupRegistrar.Enabled;
        };

        var exit = new MenuFlyoutItem { Text = "退出" };
        exit.Command = new RelayCommand(ExitApp);

        menu.Items.Add(toggle);
        menu.Items.Add(autoStart);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private void ExitApp()
    {
        try { _ipcHandle?.Dispose(); } catch { }
        try { _window?.Close(); } catch { }
        try { _tray.Dispose(); } catch { }
        Environment.Exit(0);
    }

    // ---------- 进程外 IPC(第二实例只会走到 Set() 就退出) ----------
    private void StartIpcListener()
    {
        try
        {
            _ipcHandle = new EventWaitHandle(false, EventResetMode.AutoReset, Program.IpcTogglePanel, out _);
            var dq = _ui;
            _ = Task.Run(() =>
            {
                while (true)
                {
                    _ipcHandle.WaitOne();
                    if (dq is null) return;
                    dq.TryEnqueue(TogglePanel);
                }
            });
        }
        catch (Exception e) { Log.Write("IPC 监听未启动: " + e); }
    }

    /// <summary>菜单项只吃 ICommand，这里给一个最小实现。</summary>
    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
