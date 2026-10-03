using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace GravityTray;

/// <summary>系统托盘图标 + 菜单 + 面板窗口调度</summary>
public class TrayManager : IDisposable
{
    private readonly WF.NotifyIcon icon;
    private readonly LocalApi api;
    private PanelWindow? window;
    private readonly WF.ToolStripMenuItem autoStartItem;

    public TrayManager(LocalApi api)
    {
        this.api = api;
        icon = new WF.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Gravity 音响面板",
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) TogglePanel();
        };

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("显示/隐藏面板", null, (_, _) => TogglePanel());
        autoStartItem = new WF.ToolStripMenuItem("开机自启") { Checked = AutoStartEnabled };
        autoStartItem.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => { icon.Visible = false; System.Windows.Application.Current.Shutdown(); });
        icon.ContextMenuStrip = menu;

        SyncAutoStartItem();
    }

    public void TogglePanel()
    {
        window ??= new PanelWindow(api);
        if (window.IsVisible) window.Hide();
        else window.ShowAtTray();
    }

    // ---------- 开机自启（HKCU Run） ----------
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ExePath => Environment.ProcessPath ?? "";

    private bool AutoStartEnabled
        => (string?)Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("GravityTray") == ExePath;

    private void ToggleAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (AutoStartEnabled) key.DeleteValue("GravityTray", throwOnMissingValue: false);
        else key.SetValue("GravityTray", $"\"{ExePath}\"");
        SyncAutoStartItem();
    }

    private void SyncAutoStartItem() => autoStartItem.Checked = AutoStartEnabled;

    // ---------- 托盘图标：优先用打进输出的 icon.ico（源图 Assets/icon-src.png 确定性缩放生成），
    //           文件缺失时退回运行时绘制的紫底音箱，保证不会出现"没有图标"----------
    private static Icon MakeIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(path))
            {
                var ico = new Icon(path, 32, 32);   // 显式取 32x32，避免系统挑到 16px 糊掉
                return ico;
            }
        }
        catch { /* 落回手绘 */ }
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(124, 92, 255));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var white = new Pen(Color.White, 2.6f);
            // 音箱：圆角矩形 + 两个喇叭圈
            g.DrawRectangle(white, 11, 7, 10, 18);
            g.DrawEllipse(white, 13.5f, 9, 5, 5);
            g.DrawEllipse(white, 12.5f, 16, 7, 7);
        }
        IntPtr h = bmp.GetHicon();
        return Icon.FromHandle(h);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        window?.Close();
    }
}
