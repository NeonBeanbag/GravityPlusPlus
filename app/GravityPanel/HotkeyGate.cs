using System.Runtime.InteropServices;

namespace GravityPanel;

/// <summary>全局热键门：别的软件/键盘用一条快捷键呼起面板（命令行 `GravityPanel.exe --toggle-panel` 是另一条入口）。
/// 用一个 HWND_MESSAGE 的隐藏窗口接 WM_HOTKEY —— 不去子类化 WinUI 的窗口，免得搞坏它的输入路由。</summary>
internal sealed class HotkeyGate : IDisposable
{
    public const string Default = "Ctrl+Alt+G";
    private const int HotkeyId = 0x4750;      // 'GP'

    public bool Registered { get; private set; }
    private IntPtr _hwnd;
    private readonly Native.WndProc _proc;     // 委托必须被字段持有，否则会被 GC 掉

    public HotkeyGate(string combo, Action onFire)
    {
        _proc = (h, msg, w, l) =>
        {
            if (msg == HotkeyConsts.WM_HOTKEY && w.ToInt32() == HotkeyId)
            {
                Log.Write($"热键命中 id=0x{w.ToInt64():X}");
                onFire();
                return IntPtr.Zero;
            }
            return Native.DefWindowProcW(h, msg, w, l);
        };

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandleW(IntPtr.Zero),
            lpszClassName = "GravityPanelHotkeyGate",
        };
        Native.RegisterClassExW(ref wc);      // 同进程重复注册会失败，忽略即可
        _hwnd = Native.CreateWindowExW(0, wc.lpszClassName, null, 0, 0, 0, 0, 0,
            HotkeyConsts.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        var (mods, vk) = Parse(combo);
        if (vk == 0) return;
        Registered = Native.RegisterHotKey(_hwnd, HotkeyId, mods, vk);
    }

    /// <summary>"Ctrl+Alt+G" / "Win+Shift+F8" 这类写法 → (修饰键, 虚拟键码)。认不出来返回 (0,0)，调用方降级。</summary>
    public static (uint mods, uint vk) Parse(string combo)
    {
        uint mods = 0, vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyConsts.MOD_CONTROL; break;
                case "alt": mods |= HotkeyConsts.MOD_ALT; break;
                case "shift": mods |= HotkeyConsts.MOD_SHIFT; break;
                case "win" or "cmd": mods |= HotkeyConsts.MOD_WIN; break;
                default: vk = VkOf(raw); break;
            }
        }
        return vk == 0 ? (0, 0) : (mods, vk);
    }

    private static uint VkOf(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (key.StartsWith("f", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key[1..], out var n) && n is >= 1 and <= 24) return (uint)(0x6F + n);
        return key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            _ => 0,
        };
    }

    public void Dispose()
    {
        if (Registered && _hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, HotkeyId);
        Registered = false;
        if (_hwnd != IntPtr.Zero) { Native.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }
}
