using System.Text;
using GravityTray;

namespace GravityPanel;

/// <summary>页面共享的引擎实例。配网流是长任务，必须活过页面切换，所以放在这里而不是页内。</summary>
internal static class Core
{
    /// <summary>推流用的本机 HTTP 端口，防火墙入站规则也按它来查</summary>
    public const int StreamPort = 8123;

    public static readonly CooeeProvisioner Cooee = new();
    public static readonly SpeakerApi Speaker = new();

    /// <summary>EQMode 的序号与官方控制端一致</summary>
    public static readonly string[] EqNames = ["标准", "Dirac", "收音机", "流行", "古典", "摇滚"];

    /// <summary>环境自检的一次读数。页面和 --probe 都读它，判据只有一份。</summary>
    internal record EnvInfo(string? LocalIp, string Coal, bool CoalOk,
                            bool FwAllow, bool FwBlocked, string CurSsid,
                            List<(string ssid, int ch)> Nets)
    {
        public bool FwOk => FwAllow && !FwBlocked;
        public int ChannelOf(string ssid) => Nets.FirstOrDefault(t => t.ssid == ssid).ch;
    }

    internal static async Task<EnvInfo> ReadEnvAsync()
    {
        var (coal, coalOk) = await EnvCheck.CoalescingAsync();
        var (allow, blocked) = await EnvCheck.FirewallStateAsync(StreamPort, Environment.ProcessPath ?? "");
        var (cur, list) = await EnvCheck.WifiAsync();
        return new EnvInfo(CooeeProvisioner.LocalWlanIPv4(), coal, coalOk, allow, blocked, cur,
                           list.Select(t => (t.ssid, t.ch)).ToList());
    }
}

/// <summary>把字节解码成图喂给 Image。封面一律自己解码：音响给的是 URL，SMTC 给的是流，两条路都要落到这一步。</summary>
internal static class Art
{
    public static async Task SetAsync(Microsoft.UI.Xaml.Controls.Image box, byte[] bytes)
    {
        if (bytes is not { Length: > 200 }) return;
        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var writer = new Windows.Storage.Streams.DataWriter(ras.GetOutputStreamAt(0));
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        ras.Seek(0);
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        await bmp.SetSourceAsync(ras);
        box.Source = bmp;
    }
}

/// <summary>无头探针：`Gravity++.exe --probe env --out 路径`，不开窗口、不抢前台，
/// 用来在没有音响的场合证明"页面读的那条数据通路本身是通的"。</summary>
internal static class Probe
{
    internal static void Run(string what, string? outPath)
    {
        var text = what switch
        {
            "env" => EnvText().GetAwaiter().GetResult(),
            "send" => SendText().GetAwaiter().GetResult(),
            "link" => LinkText().GetAwaiter().GetResult(),
            _ => "未知探针：" + what + "（可用：env | send | link）\n",
        };
        if (string.IsNullOrEmpty(outPath)) Console.Out.Write(text);
        else System.IO.File.WriteAllText(outPath, text);
    }

    /// <summary>只验"我们这一侧发得出去吗"：发 3 秒假账号就停。
    /// 注意：音响此刻若在配网模式，它会把 probe 当成真网络去连（连不上而已）。</summary>
    private static async Task<string> SendText()
    {
        var ip = CooeeProvisioner.LocalWlanIPv4();
        var sb = new StringBuilder($"localIp={ip ?? "-"}\n");
        if (ip == null) return sb.Append("result=拒绝：取不到 WLAN IPv4\n").ToString();
        if (!Core.Cooee.Start("probe-ssid", "probe-pass", ip))
            return sb.Append("result=Start 返回 false（已有流在跑 / 绑定失败）\n").ToString();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CooeeProvisioner.Status st = default;
        while (sw.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(500);
            st = Core.Cooee.Current;
        }
        Core.Cooee.Stop();
        await Task.Delay(200);
        st = Core.Cooee.Current;
        return sb.Append($"passes={st.Passes} packets={st.Packets} elapsed={st.ElapsedSec:F2}s error={st.Error ?? "-"}\n")
                 .Append($"速率≈{(st.Passes > 0 ? st.Packets / Math.Max(0.1, st.ElapsedSec) : 0):F0} 包/秒（一轮 3×帧长 个包，节奏 8ms）\n")
                 .ToString();
    }

    private static async Task<string> EnvText()
    {
        var e = await Core.ReadEnvAsync();
        return $"localIp={e.LocalIp ?? "-"}\n"
             + $"coalescing={e.Coal} ok={e.CoalOk}\n"
             + $"firewall allow={e.FwAllow} blocked={e.FwBlocked} ok={e.FwOk} port={Core.StreamPort}\n"
             + $"wifiCurrent={e.CurSsid}\n"
             + $"wifiSeen={string.Join(" | ", e.Nets.Select(t => $"{t.ssid}@ch{t.ch}"))}\n";
    }

    /// <summary>验「正在播放」卡的数据通路：音响现在听谁 + 本机 SMTC 认到了什么。</summary>
    private static async Task<string> LinkText()
    {
        var ip = Prefs.SpeakerIp ?? "";
        var sb = new StringBuilder($"speakerIp={(ip == "" ? "-" : ip)}\n");
        if (ip == "" || !await Core.Speaker.PingAsync(ip)) return sb.Append("speaker=ping-fail\n").ToString();
        var s = await Core.Speaker.ReadAsync(ip);
        sb.Append($"inputSource={s.InputSource}\n")
          .Append($"deviceStatus={s.Now.Status} volume={s.Volume?.ToString() ?? "-"} eq={s.Now.EqMode}\n")
          .Append($"speakerPlayList={s.Now.Title}/{s.Now.Artist}\n");
        var m = await Smtc.ReadAsync();
        sb.Append(m is null ? "smtc=none\n"
                             : $"smtc={m.Title}/{m.Artist}/{m.Album} art={(m.Art?.Length ?? 0)}B\n");
        return sb.ToString();
    }
}
