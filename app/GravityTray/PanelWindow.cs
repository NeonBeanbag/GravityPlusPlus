using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;

namespace GravityTray;

/// <summary>托盘弹出的控制面板窗口（无边框，右下角定位，失焦自动收起）</summary>
public class PanelWindow : Window
{
    private const double PanelWidth = 460, PanelHeight = 720;
    private readonly WebView2 webView = new();
    private readonly LocalApi api;
    private bool initialized;

    public PanelWindow(LocalApi api)
    {
        this.api = api;
        Title = "Gravity 音响面板";
        Width = PanelWidth;
        Height = PanelHeight + 32;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x18));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x3B));
        BorderThickness = new Thickness(1);

        var root = new DockPanel();

        var header = new Border
        {
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x26)),
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = "Gravity 音响 · 控制面板",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA3)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(title, 0);
        headerGrid.Children.Add(title);
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 32,
            Height = 32,
            FontSize = 12,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA3)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        closeBtn.Click += (_, _) => Hide();
        Grid.SetColumn(closeBtn, 1);
        headerGrid.Children.Add(closeBtn);
        header.Child = headerGrid;
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        root.Children.Add(webView);
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Deactivated += (_, _) => { if (IsVisible) Hide(); };
        Loaded += InitWebViewAsync;
    }

    private async void InitWebViewAsync(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GravityTray", "WebView2");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.Source = new Uri(api.BaseUrl);
        }
        catch (Exception ex)
        {
            webView.Dispose();
            var err = new TextBlock
            {
                Text = "WebView2 初始化失败（需要 Edge WebView2 Runtime）：\n" + ex.Message,
                Foreground = Brushes.OrangeRed,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
            };
            ((DockPanel)Content).Children.Add(err);
        }
    }

    /// <summary>在托盘附近（屏幕右下角）弹出</summary>
    public void ShowAtTray()
    {
        var wa = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Max(wa.Left, wa.Right - Width - 12);
        Top = Math.Max(wa.Top, wa.Bottom - Height - 12);
        Show();
        Activate();
        // 首次显示时 WebView2 子控件可能还没拿到焦点，主动聚焦
        Dispatcher.BeginInvoke(() => webView.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭窗口不退出程序，收进托盘
        e.Cancel = true;
        Hide();
    }
}
