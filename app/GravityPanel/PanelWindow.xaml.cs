using System.IO;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using WinRT;
using WinRT.Interop;

namespace GravityPanel;

/// <summary>
/// 面板主窗口：无边框 + 材质背景，从托盘右下角弹出。
///
/// 几条照搬自 AgentLauncher 的实测结论：
///   · 材质必须等窗口真的激活后再装 —— 构造时就设 SystemBackdrop 会退化成纯色；
///   · 中间层(Root)背景必须为 null，否则材质被自己盖住；
///   · 锚点用主显示器工作区右下角，与光标/触点无关（触屏点击不移动鼠标，用光标必漂）。
/// </summary>
public sealed partial class PanelWindow : Window
{
    private const double LogicalW = 420;
    private const double LogicalH = 500;      // 单页三张卡刚好一屏；配网展开时内部滚动
    private const double TrayMarginDip = 8;

    private IntPtr _hwnd = IntPtr.Zero;
    private SystemBackdropConfiguration? _backdropCfg;
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    private bool _dark;
    private bool _backdropApplied;
    private bool _hidePending;
    private int _hideToken;

    // 失焦关闭的三道保险，缺一不可（照 AgentLauncher 的实测结论搬）：
    //   _wasActivated —— 抢前台失败时窗口一显示就会收到 Deactivated，直接关会表现成"闪退"；
    //   _showStamp    —— show 之后系统还会投一次几十毫秒的瞬态失焦，600ms 宽限期内一律忽略；
    //   鼠标钩子      —— 就算从没抢到前台，点窗外也照样能关。
    private bool _wasActivated;
    private DateTime _showStamp = DateTime.MinValue;
    private IntPtr _mouseHook;
    private Native.LowLevelMouseProc? _mouseHookProc;

    public bool IsShown { get; private set; }

    /// <summary>文件选择器一类 WinRT 投影需要宿主窗口句柄</summary>
    internal IntPtr Hwnd => _hwnd;

    /// <summary>页眉右侧的音源徽章：空字符串就是收起。音响在 AirPlay 会话里不回曲目，所以这里只报"听谁"。</summary>
    internal void SetSource(string text)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                SourceBadgeText.Text = text;
                SourceBadge.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            });
        }
        catch { }
    }

    /// <summary>窗口高度跟着内容走：收起时矮，展开配网时自己长高（始终贴右下角，向上生长）。</summary>
    private double _logicalH = LogicalH;

    private void HookAutoHeight()
    {
        Home.SizeChanged += (_, _) => FitHeight();
    }

    private void FitHeight()
    {
        if (_hwnd == IntPtr.Zero || Home.ActualHeight <= 0) return;
        double want = 16 + Header.ActualHeight + 8 + Home.ActualHeight + 16;
        var mon = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, Consts.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (Native.GetMonitorInfo(mon, ref mi))
        {
            double cap = (mi.rcWork.Bottom - mi.rcWork.Top) / Scale - 2 * TrayMarginDip;
            if (want > cap) want = cap;
        }
        if (Math.Abs(want - _logicalH) < 2) return;
        _logicalH = want;          // 收起状态也先记下，下次 show 一上来就是对的尺寸
        if (IsShown) Place();      // ★ 收起时不能碰 Place()，那会把面板掀出来
    }


    public PanelWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _dark = IsDarkTheme();
        LogoSet();
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            UninstallOutsideClickHook();
        };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    // ---------- 建窗与定位 ----------

    /// <summary>去边框 + 圆角 + 常驻置顶，并量好尺寸位置（此时还不显示）。</summary>
    public void BuildAndPlace()
    {
        long style = Native.GetWindowLongPtr(_hwnd, Consts.GWL_STYLE).ToInt64();
        style &= ~Consts.WS_CAPTION;
        style &= ~Consts.WS_THICKFRAME;
        Native.SetWindowLongPtr(_hwnd, Consts.GWL_STYLE, new IntPtr(style));

        long ex = Native.GetWindowLongPtr(_hwnd, Consts.GWL_EXSTYLE).ToInt64();
        ex |= Consts.WS_EX_TOOLWINDOW;   // 不进任务栏，也不进 Alt+Tab
        Native.SetWindowLongPtr(_hwnd, Consts.GWL_EXSTYLE, new IntPtr(ex));

        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Consts.SWP_FRAMECHANGED | Consts.SWP_NOMOVE | Consts.SWP_NOSIZE);

        int corner = Consts.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(_hwnd, Consts.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int noShadow = 2;
        Native.DwmSetWindowAttribute(_hwnd, Consts.DWMWA_NCRENDERING_POLICY, ref noShadow, sizeof(int));

        Place();
        HookAutoHeight();
    }

    private double Scale
    {
        get
        {
            uint dpi = Native.GetDpiForWindow(_hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
    }

    /// <summary>贴主显示器工作区右下角；尺寸每次重算，缩放/换屏后不会跑到屏幕外。
    /// ★ show 与否由调用方决定：这里带 SWP_SHOWWINDOW 会把"收起状态"的面板直接掀出来（空白一块）。</summary>
    private void Place(bool show = false)
    {
        double s = Scale;
        int w = (int)Math.Round(LogicalW * s);
        int h = (int)Math.Round(_logicalH * s);

        var mon = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, Consts.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;

        int m = (int)Math.Round(TrayMarginDip * s);
        int x = mi.rcWork.Right - w - m;
        int y = mi.rcWork.Bottom - h - m;
        if (x < mi.rcWork.Left) x = mi.rcWork.Left;
        if (y < mi.rcWork.Top) y = mi.rcWork.Top;

        uint flags = Consts.SWP_NOACTIVATE | (show ? Consts.SWP_SHOWWINDOW : 0u);
        Native.SetWindowPos(_hwnd, Consts.HWND_TOPMOST, x, y, w, h, flags);
        Log.Write($"定位: {w}x{h} @ ({x},{y}) scale={s:F2}");
    }

    // ---------- 开合 ----------

    public void ShowPanel()
    {
        _hideToken++;          // 取消进行中的淡出
        _hidePending = false;
        Home.UpdateLayout();     // 先量一次（窗口还没显示，量完才是真实内容高度）
        FitHeight();           // 免得弹出来之后肉眼看见它连跳两下
        Place(show: true);
        IsShown = true;
        Native.ShowWindow(_hwnd, Consts.SW_SHOWNA);

        // 抢前台：非前台进程直接 SetForegroundWindow 会被系统拒绝，先借前台线程的输入队列
        uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        uint mine = Native.GetCurrentThreadId();
        if (fgThread != mine) Native.AttachThreadInput(fgThread, mine, true);
        Activate();
        Native.SetForegroundWindow(_hwnd);
        Native.SetFocus(_hwnd);
        if (fgThread != mine) Native.AttachThreadInput(fgThread, mine, false);

        PlayEnterAnimation();
        Log.Write($"ShowPanel: 前台={Native.GetForegroundWindow() == _hwnd}");
    }

    public void HidePanel()
    {
        if (!IsShown) return;
        IsShown = false;
        _hidePending = true;
        int token = ++_hideToken;
        PlayExitAnimation(() =>
        {
            // 淡出可能被"立刻又显示"取消：token 变了就别再把窗口藏起来
            if (token != _hideToken || !_hidePending) return;
            Native.ShowWindow(_hwnd, Consts.SW_HIDE);
            UninstallOutsideClickHook();
            _wasActivated = false;   // 下次 show 重新确认激活
            _hidePending = false;
        });
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        Log.Write($"OnActivated: state={e.WindowActivationState}, 之前 _wasActivated={_wasActivated}");
        bool inputActive = e.WindowActivationState != WindowActivationState.Deactivated;
        if (_backdropCfg is not null) _backdropCfg.IsInputActive = inputActive;

        if (inputActive)
        {
            _wasActivated = true;
            if (!_backdropApplied) ApplyBackdrop();
            return;
        }

        if (!_wasActivated) { Log.Write("失焦但从未真激活 → 不关（抢前台失败的典型表现）"); return; }
        if ((DateTime.Now - _showStamp).TotalMilliseconds < 600) { Log.Write("失焦在 show 宽限期内 → 忽略瞬态失焦"); return; }
        if (ForegroundIsOurs()) { Log.Write("前台是我们自己的弹层（下拉/对话框）→ 不关"); return; }
        Log.Write("失焦 → 关闭面板");
        HidePanel();
    }

    /// <summary>前台窗口是不是本进程的弹层。ComboBox 下拉、ContentDialog 都是独立顶层窗口，
    /// 它们抢走前台不算"用户点了别处"。</summary>
    private bool ForegroundIsOurs()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return true;
        // 托盘图标窗口 / IME 辅助窗口也属于本进程但是不可见的，它们"当前台"不代表用户在用我们的弹层
        if (!Native.IsWindowVisible(fg)) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ---------- 点窗外关闭（被动钩子，只观察不拦截）----------

    private void InstallOutsideClickHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc = MouseHookProc;      // 委托必须被字段持有，否则会被 GC 掉
        _mouseHook = Native.SetWindowsHookExW(Consts.WH_MOUSE_LL, _mouseHookProc,
            Native.GetModuleHandleW(IntPtr.Zero), 0);
        Log.Write($"鼠标钩子安装: {(_mouseHook != IntPtr.Zero ? "OK" : "FAIL")}");
    }

    private void UninstallOutsideClickHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        try { Native.UnhookWindowsHookEx(_mouseHook); } catch { }
        _mouseHook = IntPtr.Zero;
        _mouseHookProc = null;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsShown)
        {
            int msg = wParam.ToInt32();
            if (msg is Consts.WM_LBUTTONDOWN or Consts.WM_RBUTTONDOWN or Consts.WM_MBUTTONDOWN)
            {
                var ms = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!PointInOurWindows(ms.pt.X, ms.pt.Y))
                {
                    Log.Write($"窗外点击 ({ms.pt.X},{ms.pt.Y}) → 关闭面板");
                    HidePanel();
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>命中面板本体或本进程任何一个可见弹层都算"内"，否则下拉列表一展开就被自己关掉。</summary>
    private static bool PointInOurWindows(int x, int y)
    {
        uint mine = (uint)Environment.ProcessId;
        bool hit = false;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid != mine || !Native.IsWindowVisible(h)) return true;
            if (Native.GetWindowRect(h, out RECT r) && x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
            {
                hit = true;
                return false;         // 命中即停
            }
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    // ---------- 背景材质 ----------

    private void ApplyBackdrop()
    {
        try
        {
            DispatcherQueue?.EnsureSystemDispatcherQueue();
            DisposeBackdrop();
            _dark = IsDarkTheme();
            _backdropCfg = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
            Root.Background = null;   // 中间层不透明 = 材质白装

            if (MicaController.IsSupported())
            {
                _mica = new MicaController { Kind = MicaKind.Base };
                _mica.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                _mica.SetSystemBackdropConfiguration(_backdropCfg);
            }
            else if (DesktopAcrylicController.IsSupported())
            {
                _acrylic = new DesktopAcrylicController
                {
                    Kind = DesktopAcrylicKind.Base,
                    // 四个属性要全给，缺一个就等于把主题默认值禁掉，面板会发白
                    TintColor = _dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243),
                    TintOpacity = _dark ? 0.68f : 0.60f,
                    LuminosityOpacity = _dark ? 0.90f : 0.92f,
                    FallbackColor = _dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243),
                };
                _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                _acrylic.SetSystemBackdropConfiguration(_backdropCfg);
            }
            else
            {
                Root.Background = new SolidColorBrush(_dark
                    ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                    : Windows.UI.Color.FromArgb(255, 243, 243, 243));
            }
            _backdropApplied = true;
        }
        catch (Exception e)
        {
            Log.Write("材质背景失败，退回纯色: " + e);
            Root.Background = new SolidColorBrush(_dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        }
    }

    private void DisposeBackdrop()
    {
        try { _mica?.Dispose(); } catch { }
        try { _acrylic?.Dispose(); } catch { }
        _mica = null;
        _acrylic = null;
        try { SystemBackdrop = null; } catch { }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            _backdropApplied = false;
            ApplyBackdrop();
        });
    }

    internal static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { }
        return false;
    }

    // ---------- 进出场动画 ----------

    private void PlayEnterAnimation()
    {
        // 注意：_wasActivated 不在这里清零 —— 窗口在构造期就可能收到过一次 CodeActivated，
        // 若在此处重置，之后真正的失焦会被"从未真激活"这道保险挡掉，Alt+Tab 走就关不掉。
        _showStamp = DateTime.Now;
        InstallOutsideClickHook();
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(Root);
            var comp = visual.Compositor;
            visual.Opacity = 0f;
            visual.Scale = new System.Numerics.Vector3(0.98f, 0.98f, 1f);

            var op = comp.CreateScalarKeyFrameAnimation();
            op.Duration = TimeSpan.FromMilliseconds(160);
            op.InsertKeyFrame(0f, 0f);
            op.InsertKeyFrame(1f, 1f);
            visual.StartAnimation("Opacity", op);

            var sc = comp.CreateVector3KeyFrameAnimation();
            sc.Duration = TimeSpan.FromMilliseconds(200);
            sc.InsertKeyFrame(0f, new System.Numerics.Vector3(0.98f, 0.98f, 1f));
            sc.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f),
                comp.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0f, 0f), new System.Numerics.Vector2(0.5f, 1f)));
            visual.StartAnimation("Scale", sc);
        }
        catch (Exception e) { Log.Write("入场动画异常: " + e); }
    }

    private void PlayExitAnimation(Action after)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(Root);
            var comp = visual.Compositor;
            var op = comp.CreateScalarKeyFrameAnimation();
            op.Duration = TimeSpan.FromMilliseconds(120);
            op.InsertKeyFrame(1f, 0f);
            visual.StartAnimation("Opacity", op);
            _ = Task.Delay(140).ContinueWith(_ => after());
        }
        catch
        {
            after();
        }
    }

    // ---------- 页眉图标 ----------

    private void LogoSet()
    {
        try
        {
            var png = Path.Combine(AppContext.BaseDirectory, "icon.png");
            if (File.Exists(png)) Logo.Source = new BitmapImage(new Uri(png));
        }
        catch (Exception e) { Log.Write("页眉图标加载失败: " + e); }
    }
}
