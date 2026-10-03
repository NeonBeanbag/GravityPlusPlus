using GravityTray;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GravityPanel.Pages;

/// <summary>面板只有这一页：正在播放 / 音量与音效 / 设备连接（含折叠的配网）。</summary>
public sealed partial class HomePage : UserControl
{
    // Segoe Fluent Icons 的播放/暂停码位。源码里一律写转义，不放私用区字符。
    private const string GlyphPlay = "\uE768";
    private const string GlyphPause = "\uE769";

    /// <summary>关掉 Intel 网卡的「数据包合并」。开着它一轮 3 个小包会被合成一次空口发送，长度通道整个报废。</summary>
    private const string CoalFix =
        @"Set-NetAdapterAdvancedProperty -Name WLAN -RegistryKeyword '*PacketCoalescing' -RegistryValue 0 -NoRestart";

    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer volCommit = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string ip = "";
    private bool suppressEq;
    private bool suppressVol;
    private byte[]? lastCover;
    private string? localIp;
    private string currentSsid = "";
    private string targetSsid = "";
    private Core.EnvInfo? last;
    private int watchTick;
    private bool wasRunning;
    private bool busy;

    public HomePage()
    {
        InitializeComponent();
        EqBox.ItemsSource = Core.EqNames;
        // ★ Slider 的事件只能在代码里挂：写成 XAML 属性会让 XamlCompiler.exe 静默退出码 1，
        //   一条诊断都不给（见本仓库踩坑记录）。
        Vol.ValueChanged += OnVolChanged;
        ConnToggle.Checked += (_, _) => ConnBody.Visibility = Visibility.Visible;
        ConnToggle.Unchecked += (_, _) => ConnBody.Visibility = Visibility.Collapsed;
        ProvToggle.Checked += (_, _) => ProvBody.Visibility = Visibility.Visible;
        ProvToggle.Unchecked += (_, _) => ProvBody.Visibility = Visibility.Collapsed;
        // XAML 里若带 IsChecked，Checked 事件在 InitializeComponent 期间就发完了（那时还没挂处理函数），
        // 所以这里按当前状态同步一次可见性。
        ConnBody.Visibility = ConnToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProvBody.Visibility = ProvToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EnvFly.Opening += (_, _) => { _ = RefreshEnvAsync(); };
        volCommit.Tick += async (_, _) => { volCommit.Stop(); await CommitVolumeAsync(); };
        poll.Tick += async (_, _) => await RefreshAsync();
        timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) =>
        {
            ip = Prefs.SpeakerIp ?? "";
            IpBox.Text = ip;
            if (ip != "") _ = ConnectAsync();
            if (Core.Cooee.Current.Running)
            {
                ConnToggle.IsChecked = true; ProvToggle.IsChecked = true;
                timer.Start(); SetRunningUi(true);
            }
        };
        Unloaded += (_, _) => { poll.Stop(); volCommit.Stop(); timer.Stop(); };
    }

    // ---------- 正在播放 ----------

    private async Task RefreshAsync()
    {
        ip = Prefs.SpeakerIp ?? "";
        if (ip == "")
        {
            Collapse();
            App.SetStatus("未连接");
            return;
        }
        try
        {
            var s = await Core.Speaker.ReadAsync(ip);
            PlayCard.Visibility = VolCard.Visibility = Visibility.Visible;
            App.SetStatus($"已连接 {ip}");
            App.SetSource(SourceName(s.InputSource));
            PlayGlyph.Glyph = s.Now.Status == "STARTED" ? GlyphPause : GlyphPlay;

            if (s.Now.EqMode >= 0 && s.Now.EqMode < Core.EqNames.Length)
            {
                suppressEq = true;
                EqBox.SelectedIndex = s.Now.EqMode;
                suppressEq = false;
            }
            if (s.Volume is int v)
            {
                suppressVol = true;      // 回填不能当成"用户在调音量"，否则会把值又发回音响
                Vol.Value = v;
                suppressVol = false;
                VolNum.Text = v.ToString();
            }
            await ShowNowAsync(s);
        }
        catch (Exception ex)
        {
            poll.Stop();
            Collapse();
            App.SetStatus("未连接");
            Log.Write("刷新失败: " + ex);
        }
    }

    private void Collapse()
    {
        PlayCard.Visibility = VolCard.Visibility = Visibility.Collapsed;
        App.SetSource("");
    }

    /// <summary>音响在 AirPlay 会话里不回曲目 —— playList 永远是它自带的演示曲（Gravity / Meizu），
    /// 所以歌名/封面优先取本机 SMTC（和音响屏幕上显示的是同一份来源）。</summary>
    private async Task ShowNowAsync(SpeakerApi.Snapshot s)
    {
        var m = await Smtc.ReadAsync();
        if (m is not null && !string.IsNullOrEmpty(m.Title))
        {
            NowLine.Text = Join(" · ", m.Title, m.Artist);
            if (m.Art is { Length: > 0 } bytes) await SetCoverAsync(bytes);
            return;
        }
        NowLine.Text = string.IsNullOrEmpty(s.Now.Title) ? "（无播放）" : Join(" · ", s.Now.Title, s.Now.Artist);
        if (!string.IsNullOrEmpty(s.Now.CoverUrl))
        {
            var bytes = await Core.Speaker.CoverAsync(s.Now.CoverUrl);
            if (bytes is not null) await SetCoverAsync(bytes);
        }
    }

    private async Task SetCoverAsync(byte[] bytes)
    {
        if (lastCover?.SequenceEqual(bytes) == true) return;
        lastCover = bytes;
        try { await Art.SetAsync(Cover, bytes); }
        catch (Exception ex) { Log.Write("封面解码失败: " + ex); }
    }

    private static string Join(string sep, params string[] parts)
        => string.Join(sep, parts.Where(t => !string.IsNullOrEmpty(t)));

    private static string SourceName(string raw) => raw.ToLowerInvariant() switch
    {
        "airplay" => "AirPlay",
        "bluetooth" or "blue tooth" => "蓝牙",
        "" or "none" => "待机",
        _ => raw,
    };

    // ---------- 播控 ----------

    private async void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (ip == "") return;
        if (PlayGlyph.Glyph == GlyphPause) await Core.Speaker.PauseAsync(ip);
        else await Core.Speaker.PlayAsync(ip);
        await RefreshAsync();
    }

    private void OnPrev(object sender, RoutedEventArgs e) { if (ip != "") _ = Core.Speaker.PrevAsync(ip); }
    private void OnNext(object sender, RoutedEventArgs e) { if (ip != "") _ = Core.Speaker.NextAsync(ip); }
    private void OnRefresh(object sender, RoutedEventArgs e) { _ = RefreshAsync(); }

    // ---------- 音量 / 音效 ----------

    private void OnVolChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        VolNum.Text = ((int)e.NewValue).ToString();
        if (suppressVol || ip == "") return;
        volCommit.Stop();          // 拖动中不断续期，手停了 600ms 才真正下发
        volCommit.Start();
    }

    /// <summary>超过 25 先确认 —— 音响不会替你限，音量 100 很难听。</summary>
    private async Task CommitVolumeAsync()
    {
        if (ip == "") return;
        var v = (int)Vol.Value;
        if (v > 25)
        {
            var dlg = new ContentDialog
            {
                Title = "音量偏大",
                Content = $"音量 {v} 可能过大，确认调整？",
                PrimaryButtonText = "调整",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) { await RefreshAsync(); return; }
        }
        await Core.Speaker.SetVolumeAsync(ip, v);
    }

    private async void OnEqChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressEq || ip == "" || EqBox.SelectedIndex < 0) return;
        await Core.Speaker.SetEqAsync(ip, EqBox.SelectedIndex);
    }

    // ---------- 设备连接 ----------

    private void OnConnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private void OnIpKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        ip = IpBox.Text.Trim();
        if (!SpeakerApi.LooksLikeIp(ip)) { Say("IP 不对", on: false); return; }
        Say("连接中…", on: false);
        if (!await Core.Speaker.PingAsync(ip))
        {
            Say("连不上（检查 IP / 同网段 / 音响已开机）", on: false);
            return;
        }
        Prefs.SpeakerIp = ip;
        Say($"已连接 {ip}", on: true);
        await RefreshAsync();
        poll.Start();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        BtnScan.IsEnabled = false;
        ScanList.Items.Clear();
        Say("扫描中…", on: null);
        var found = await Core.Speaker.ScanAsync();
        foreach (var f in found)
        {
            var b = new Button { Content = f };
            b.Click += (_, _) => { IpBox.Text = f; _ = ConnectAsync(); };
            ScanList.Items.Add(b);
        }
        if (found.Count == 0) Say("未发现设备", on: false);
        BtnScan.IsEnabled = true;
    }

    private void Say(string text, bool? on)
    {
        ConnState.Text = text;
        if (on is null) return;
        try
        {
            Dot.Fill = (Brush)Application.Current.Resources[
                on.Value ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        }
        catch { /* 取不到主题键就留默认色 */ }
        App.SetStatus(text);
    }

    // ---------- 自检 ----------

    private async Task RefreshEnvAsync()
    {
        var e = await Core.ReadEnvAsync();
        localIp = e.LocalIp;
        currentSsid = e.CurSsid;
        EnvSsid.Text = string.IsNullOrEmpty(e.CurSsid) ? "没连上 Wi-Fi" : e.CurSsid;
        EnvIp.Text = e.LocalIp ?? "取不到（WLAN 没连？）";

        EnvCoal.Text = e.Coal;
        Paint(EnvCoal, e.CoalOk);
        BtnFixCoal.Visibility = e.CoalOk ? Visibility.Collapsed : Visibility.Visible;

        EnvFw.Text = e.FwBlocked ? "被 Block 规则拦截" : e.FwAllow ? $"已放行 {Core.StreamPort}" : "没有入站规则";
        Paint(EnvFw, e.FwOk);
        BtnFixFw.Visibility = e.FwOk ? Visibility.Collapsed : Visibility.Visible;

        SsidBox.ItemsSource = e.Nets.Select(t => t.ssid).Distinct().ToList();
        if (string.IsNullOrEmpty(SsidBox.Text) && !string.IsNullOrEmpty(e.CurSsid)) SsidBox.Text = e.CurSsid;
        last = e;
    }

    private static void Paint(TextBlock tb, bool ok)
    {
        try
        {
            tb.Foreground = (Brush)Application.Current.Resources[
                ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        }
        catch { /* 取不到主题键就沿用默认色 */ }
    }

    private async void OnFixCoalescing(object sender, RoutedEventArgs e)
    {
        var fired = EnvCheck.Elevate(CoalFix);
        Say(fired ? "已请求管理员权限" : "UAC 没通过", on: false);
        if (fired) await DelayedRefresh(6000);
    }

    private async void OnFixFirewall(object sender, RoutedEventArgs e)
    {
        var fired = EnvCheck.FixFirewall(Core.StreamPort, Environment.ProcessPath ?? "");
        Say(fired ? "已请求管理员权限" : "UAC 没通过", on: false);
        if (fired) await DelayedRefresh(6000);
    }

    private async Task DelayedRefresh(int ms)
    {
        await Task.Delay(ms);
        await RefreshEnvAsync();
    }

    private void OnEnvRefresh(object sender, RoutedEventArgs e) => _ = RefreshEnvAsync();

    // ---------- 配网 ----------

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var ssid = (SsidBox.Text ?? "").Trim();
        var pwd = PassBox.Password;
        if (ssid == "" || pwd == "") { Say(InfoBarSeverity.Warning, "SSID 和密码都要填。"); return; }
        if (busy) return;
        busy = true;
        ProvToggle.IsChecked = true;
        BtnStart.IsEnabled = false;
        PvBar.Visibility = Visibility.Visible;
        PvResult.IsOpen = false;
        var started = false;
        try
        {
            PvState.Text = "正在体检：当前 Wi-Fi、网卡合并、本机 WLAN 地址…";
            await RefreshEnvAsync();

            if (string.IsNullOrEmpty(currentSsid))
            {
                Say(InfoBarSeverity.Error, "电脑现在没连任何 Wi-Fi —— 先在任务栏连上「" + ssid + "」的 2.4G，再点开始。");
                return;
            }
            // cooee 是"长度通道"：音响只从**它要加入的那个 AP** 的空中帧里量长度。电脑连在 A 却发 B 的账号，
            // 包根本不会从 B 的 AP 出去，音响一个字都收不到 —— 表现就是"发了几百轮、界面一切正常、音响毫无反应"。
            if (ssid != currentSsid)
            {
                Say(InfoBarSeverity.Error,
                    $"电脑现在连的是「{currentSsid}」，你要配的是「{ssid}」。先把电脑连到「{ssid}」（注意选 2.4G 那个），再回来点开始。");
                Log.Write($"配网被拦：目标={ssid} 但电脑在={currentSsid}");
                return;
            }

            if (last is { CoalOk: false })
            {
                PvState.Text = "网卡「数据包合并」开着 —— 正在关掉它（会弹一次 UAC，点「是」）…";
                if (!EnvCheck.Elevate(CoalFix))
                {
                    Say(InfoBarSeverity.Error, "关掉「数据包合并」需要管理员权限，而 UAC 没通过 —— 这一步不能省，否则长度通道必废。");
                    return;
                }
                // 改这个属性会让网卡瞬断再自连，所以要等：合并关了 + Wi-Fi 回来了 + WLAN 地址又拿到了
                for (var i = 0; i < 12; i++)
                {
                    await Task.Delay(2000);
                    await RefreshEnvAsync();
                    PvState.Text = $"等网卡改完并自己重连…（第 {i + 1} 次复查）";
                    if (last is { CoalOk: true } && !string.IsNullOrEmpty(last.LocalIp) && !string.IsNullOrEmpty(last.CurSsid)) break;
                }
                if (last is { CoalOk: false })
                {
                    Say(InfoBarSeverity.Error, "「数据包合并」还是开着 —— 改完没生效（或被驱动回滚了），必要时去设备管理器手动改。");
                    return;
                }
                if (currentSsid != ssid)
                {
                    Say(InfoBarSeverity.Error,
                        $"网卡重启后电脑连到了「{currentSsid}」，不是目标「{ssid}」—— 手动连回「{ssid}」再点开始。");
                    return;
                }
            }

            // 实测（2026-10-02）：它在配网模式下只听"自己已保存网络"所在的那个信道 ——
            // 同一台电脑发 MyWiFi-A(信道1) 16 秒就解到，发 MyWiFi-B(信道6) 197 秒 `commend_ssid` 仍为空。
            var curCh = last?.ChannelOf(currentSsid) ?? 0;
            var tgtCh = last?.ChannelOf(ssid) ?? 0;
            if (curCh > 0 && tgtCh > 0 && curCh != tgtCh)
                Say(InfoBarSeverity.Warning,
                    $"注意：「{currentSsid}」在信道 {curCh}，目标「{ssid}」在信道 {tgtCh} —— 换网大概率收不到（救砖不受影响）。" +
                    "解法：把目标 AP 的信道改成同一个，或先用 adb 清掉它存的旧网络。");

            // 不绑地址的话，多网卡（Tailscale/以太网）机器上组播可能从别的口出去 —— 音响一个字都收不到。
            var lip = localIp ?? CooeeProvisioner.LocalWlanIPv4();
            localIp = lip;
            if (string.IsNullOrEmpty(lip))
            {
                Say(InfoBarSeverity.Error, "取不到本机 WLAN IPv4 —— 确认电脑连的是 2.4G 的 Wi-Fi 再重试。");
                Log.Write("配网被拒：本机 WLAN IPv4 取不到");
                return;
            }

            PvState.Text = "环境没问题，开始发包…";
            Log.Write($"配网开始：ssid={ssid} 本机={lip} 合并={last?.Coal}");
            if (!Core.Cooee.Start(ssid, pwd, lip))
            {
                Say(InfoBarSeverity.Error, "没能开始：已有流在跑，或 socket 绑定失败（先停掉当前任务 / 检查本机 IP）。");
                return;
            }
            started = true;
            targetSsid = ssid;
            watchTick = 0;
            SetRunningUi(true);
            timer.Start();
        }
        finally
        {
            busy = false;
            if (!started) { PvBar.Visibility = Visibility.Collapsed; BtnStart.IsEnabled = true; }
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        Log.Write("配网被手动停止");
        Core.Cooee.Stop();
        SetRunningUi(false);
    }

    private void SetRunningUi(bool running)
    {
        BtnStart.IsEnabled = !running;
        BtnStop.IsEnabled = running;
        PvBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Tick()
    {
        var st = Core.Cooee.Current;
        PvState.Text = st.Running
            ? $"第 {st.Passes} 轮 · {st.Packets} 个包 · {st.ElapsedSec:F1}s"
            : $"共 {st.Passes} 轮 · {st.Packets} 个包 · {st.ElapsedSec:F1}s";
        if (st.Error != null) Say(InfoBarSeverity.Error, "发送出错：" + st.Error);

        // 看门狗：这台机器的 WLAN 会在两个 SSID 之间自动漫游。一旦中途漂走，
        // 剩下的包就发到别的 AP 的信道上去了 —— 界面还在跳轮数，音响一个字都收不到。
        if (st.Running && ++watchTick % 5 == 0)
        {
            var (now, _) = await EnvCheck.WifiAsync();
            if (!string.IsNullOrEmpty(now) && now != targetSsid)
            {
                Log.Write($"配网中断：电脑漂到 {now}，目标是 {targetSsid}（已发 {st.Passes} 轮）");
                Core.Cooee.Stop();
                Say(InfoBarSeverity.Error,
                    $"发到第 {st.Passes} 轮时电脑自己漫游到了「{now}」，而目标是「{targetSsid}」—— 后面的包它收不到。" +
                    $"先把电脑连回「{targetSsid}」再重来。");
                return;
            }
        }

        if (wasRunning && !st.Running)
        {
            Log.Write($"配网停止：{st.Passes} 轮 / {st.Packets} 包 / {st.ElapsedSec:F1}s" + (st.Error != null ? " 错误=" + st.Error : ""));
            timer.Stop();
            SetRunningUi(false);
            await ReadBackAsync(st.Passes);
        }
        wasRunning = st.Running;
    }

    /// <summary>停下来之后向音响的 system_daemon 要它真正解到的账号 —— 这是唯一能证明"发对了"的读数。</summary>
    private async Task ReadBackAsync(int passes)
    {
        var addr = (SpeakerIpBox.Text ?? "").Trim();
        if (addr == "")
        {
            Say(passes > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                passes > 0 ? "已发送。音响自己回线就是成功；填上它的 IP 可以回读它解到的账号。" : "还没发出任何一轮。");
            return;
        }
        var (sid, pwd, proto) = await CooeeProvisioner.ReadBackAsync(addr);
        if (sid == null)
            Say(InfoBarSeverity.Warning, $"连不上 {addr}:8888 的回读口 —— IP 对不对？它是否已经回线？");
        else
            Say(InfoBarSeverity.Success, $"音响解到：SSID={sid} 密码={pwd}" + (string.IsNullOrEmpty(proto) ? "" : $" 协议={proto}"));
    }

    private void Say(InfoBarSeverity sev, string msg)
    {
        PvResult.Severity = sev;
        PvResult.Message = msg;
        PvResult.IsOpen = true;
    }
}
