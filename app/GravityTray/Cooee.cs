using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace GravityTray;

/// <summary>
/// 纯 Windows 配网引擎：逐条复刻 libcooee.so 的 send_cooee（Broadcom Cooee/Airkiss）。
///
/// 帧 = [0x20][帧长][nonce 前 8 字节=0] + AES-128-CCM(明文TLV, AAD=那10字节头) + tag(8)
/// 明文 TLV = 00|ssidLen|SSID| 03|pwdLen|PWD| (02|04|发送端IPv4 —— 只在有 IP 时追加)
/// 每轮 i（0..帧长-1）连发 3 包，端口一律 1503，然后自旋到 i 轮的绝对期限：
///   ② 239.254.frame[G].frame[G+1]  长度 = G      （G 每轮 +2，G+2≥帧长 归零）
///   ③ 255.255.255.255               长度 = i + 20
///   ④ 255.255.255.255               长度 = frame[i] + 180
/// 0 长信标包手机恒不发（条件是 beacon_int/interval==0，而 beacon_int=interval<<2），这里默认也不发。
/// ★ 长度取的是**加密后的帧**的字节，不是明文 TLV —— 这是能不能解出来的关键。
/// </summary>
public sealed class CooeeProvisioner : IDisposable
{
    private const int Port = 1503;
    private const int HeadOff = 20;       // adds r2, #0x14
    private const int DataOff = 180;      // adds r2, #0xb4
    private const int FrameOverhead = 18; // 10 字节头 + 8 字节 tag
    private const int MutexPort = 15103;  // 单实例互斥：两条流并发会把轮序打成噪声
    private const string BeaconIp = "239.246.0.0";
    private const string BroadcastIp = "255.255.255.255";

    private static readonly byte[] Key = "abcdabcdabcdabcd"u8.ToArray();
    private static readonly byte[] Nonce = BuildNonce();
    private static byte[] BuildNonce()
    {
        var n = new byte[13];                 // memset 13 字节，再在 +8 处写 "wiced"
        Encoding.ASCII.GetBytes("wiced").CopyTo(n.AsSpan(8));
        return n;
    }

    private UdpClient? sock, mutex;
    private CancellationTokenSource? cts;
    private Task? loop;
    private readonly object gate = new();

    public record struct Status(bool Running, string Ssid, int Passes, long Packets, double ElapsedSec, string? Error, string? Result, int FoundVolume);

    private Status _st = new(false, "", 0, 0, 0, null, null, 0);
    public Status Current { get { lock (gate) { return _st; } } }

    /// <summary>构造那一帧 39（或其它长度）字节；senderIp 为空则不带 02|04 段</summary>
    public static byte[] BuildFrame(string ssid, string pwd, IPAddress? senderIp)
    {
        var s = Encoding.UTF8.GetBytes(ssid); if (s.Length > 32) s = s[..32];
        var p = Encoding.UTF8.GetBytes(pwd); if (p.Length > 64) p = p[..64];
        var tlv = new List<byte>();
        tlv.Add(0x00); tlv.Add((byte)s.Length); tlv.AddRange(s);
        tlv.Add(0x03); tlv.Add((byte)p.Length); tlv.AddRange(p);
        if (senderIp != null) { tlv.Add(0x02); tlv.Add(0x04); tlv.AddRange(senderIp.GetAddressBytes()); }
        var body = tlv.ToArray();

        var head = new byte[10];
        head[0] = 0x20;
        head[1] = (byte)(body.Length + FrameOverhead);
        Array.Copy(Nonce, 0, head, 2, 8);

        var ct = new byte[body.Length];
        var tag = new byte[8];
        using var ccm = new AesCcm(Key);
        ccm.Encrypt(Nonce, body, ct, tag, head);      // AAD = 那 10 字节头，tag 8 字节
        var frame = new byte[10 + ct.Length + tag.Length];
        Array.Copy(head, frame, 10);
        Array.Copy(ct, 0, frame, 10, ct.Length);
        Array.Copy(tag, 0, frame, 10 + ct.Length, 8);
        return frame;
    }

    /// <summary>本机 WLAN 上正在用的 IPv4（要填进 TLV 的 02|04 段，也是 socket 绑定地址）</summary>
    public static string? LocalWlanIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    return ua.Address.ToString();
        }
        return null;
    }

    public bool Start(string ssid, string pwd, string? localIp, int paceMs = 8, int gapMs = 1000)
    {
        lock (gate)
        {
            if (_st.Running) return false;
            // 互斥：抢不到这个端口说明已有实例在打
            try { mutex = new UdpClient(AddressFamily.InterNetwork); mutex.Client.Bind(new IPEndPoint(IPAddress.Any, MutexPort)); }
            catch { mutex?.Dispose(); mutex = null; return false; }

            byte[] frame;
            try { frame = BuildFrame(ssid, pwd, string.IsNullOrEmpty(localIp) ? null : IPAddress.Parse(localIp)); }
            catch (Exception e) { _st = _st with { Running = false, Error = e.Message }; mutex.Dispose(); mutex = null; return false; }

            sock = new UdpClient(AddressFamily.InterNetwork);
            if (!string.IsNullOrEmpty(localIp)) sock.Client.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
            sock.EnableBroadcast = true;
            try { sock.Ttl = 2; } catch { }

            cts = new CancellationTokenSource();
            _st = new Status(true, ssid, 0, 0, 0, null, null, 0);
            loop = Task.Run(() => RunAsync(frame, paceMs, gapMs, cts.Token));
            return true;
        }
    }

    private async Task RunAsync(byte[] frame, int paceMs, int gapMs, CancellationToken tok)
    {
        var fp = frame.Length;
        // 载荷内容设备看不见（它只量 802.11 帧长），给足最长包即可
        var payload = new byte[DataOff + 256];
        Array.Fill(payload, (byte)0x41);
        var sw = Stopwatch.StartNew();
        long pkts = 0; int passes = 0, G = 0;
        string? err = null;
        try
        {
            while (!tok.IsCancellationRequested)
            {
                var next = Stopwatch.GetTimestamp();
                for (var i = 0; i < fp; i++)
                {
                    if (tok.IsCancellationRequested) break;
                    next += Stopwatch.Frequency * paceMs / 1000;
                    // 一轮 3 包连发，不 await，节奏完全由 deadline 决定
                    Send(BroadcastGroup(frame, G), G); pkts++;
                    G += 2; if (G >= fp) G = 0;
                    Send(BroadcastIp, i + HeadOff); pkts++;
                    Send(BroadcastIp, frame[i] + DataOff); pkts++;
                    SpinTo(next);
                }
                passes++;
                lock (gate) _st = _st with { Passes = passes, Packets = pkts, ElapsedSec = sw.Elapsed.TotalSeconds, Error = err };
                if (gapMs > 0) await Task.Delay(gapMs, tok).ContinueWith(_ => { });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { err = e.Message; }
        finally
        {
            lock (gate) _st = _st with { Running = false, Passes = passes, Packets = pkts, ElapsedSec = sw.Elapsed.TotalSeconds, Error = err };
        }

        void Send(string ip, int len)
        {
            try { sock!.Send(payload, Math.Min(len, payload.Length), ip, Port); }
            catch (Exception e) { err ??= e.Message; }
        }
    }

    private static string BroadcastGroup(byte[] frame, int G)
        => $"239.254.{frame[G]}.{(G + 1 < frame.Length ? frame[G + 1] : 0)}";

    private static void SpinTo(long stamp)
    {
        while (Stopwatch.GetTimestamp() < stamp) Thread.SpinWait(40);
    }

    public void Stop()
    {
        lock (gate)
        {
            try { cts?.Cancel(); } catch { }
            try { sock?.Close(); } catch { }
            try { mutex?.Close(); } catch { }
            sock = null; mutex = null;
            _st = _st with { Running = false };
        }
    }

    /// <summary>结果回读：system_daemon 在配网窗口结束后能给出解出的 SSID/密码。
    /// 注意：窗口内 App 会独占那条单连接 socket，我们多半只能事后读到。</summary>
    public static async Task<(string? ssid, string? pwd, string? proto)> ReadBackAsync(string ip, int timeoutMs = 1500)
    {
        var ssid = Parse(await Ask(ip, "BSS0000E", timeoutMs));
        var pwd = Parse(await Ask(ip, "BPA0000E", timeoutMs));
        var proto = Parse(await Ask(ip, "BPROTOCE", timeoutMs));
        return (ssid, pwd, proto);

        static string? Parse(string? r)
        {
            if (string.IsNullOrEmpty(r)) return null;
            var m = Regex.Match(r, @"^0*(\d{1,2})(.*)$");
            if (!m.Success) return r;
            var len = int.Parse(m.Groups[1].Value);
            var val = m.Groups[2].Value;
            return len > 0 && len <= val.Length ? val[..len] : val;
        }
    }

    private static async Task<string?> Ask(string ip, string cmd, int timeoutMs)
    {
        try
        {
            using var c = new TcpClient();
            if (!await Guard(c.ConnectAsync(ip, 8888), timeoutMs)) return null;
            var n = Encoding.ASCII.GetBytes(cmd);
            await Guard(c.GetStream().WriteAsync(n).AsTask(), timeoutMs);
            var buf = new byte[128];
            var got = await GuardR(c.GetStream().ReadAsync(buf).AsTask(), timeoutMs);
            return got is int g && g > 0 ? Encoding.Latin1.GetString(buf, 0, g) : null;
        }
        catch { return null; }
    }

    // TcpClient 没有真正的连接超时，用 WhenAny + Delay 兜住
    private static async Task<bool> Guard(Task t, int ms)
    {
        if (await Task.WhenAny(t, Task.Delay(ms)) != t) return false;
        try { await t; return true; } catch { return false; }
    }

    private static async Task<T?> GuardR<T>(Task<T> t, int ms) where T : struct
    {
        if (await Task.WhenAny(t, Task.Delay(ms)) != t) return null;
        try { return await t; } catch { return null; }
    }

    public void Dispose()
    {
        try { cts?.Cancel(); } catch { }
        try { sock?.Dispose(); } catch { }
        try { mutex?.Dispose(); } catch { }
    }
}

/// <summary>环境体检：这两项都会造成"接口回 Success 但设备毫无反应"的静默失败</summary>
public static class EnvCheck
{
    /// <summary>网卡「数据包合并」(PacketCoalescing)：开着的时候一轮 3 个小包会被合成一次空口发送</summary>
    public static async Task<(string state, bool ok)> CoalescingAsync()
    {
        var o = await Ps(@"(Get-NetAdapterAdvancedProperty -Name WLAN -ErrorAction SilentlyContinue | Where-Object RegistryKeyword -eq '*PacketCoalescing').DisplayValue");
        var v = o.Trim();
        if (string.IsNullOrEmpty(v)) return ("读不到（无线网卡名不是 WLAN？）", false);
        return (v, v.Contains("禁用") || v.Contains("Disabled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>当前连接的 WiFi 名 + 可见网络名（配网表单的默认值/下拉）。
    /// 这版 Windows 的 netsh 不支持 xml 输出，所以直接调 netsh（不过 PowerShell，省掉引号地狱），
    /// 并且只用 ASCII 标签 "SSID" 做锚点——中文标签在 OEM 码页下会被弄乱，数字和 SSID 本身是安全的。</summary>
    public static async Task<(string ssid, List<(string ssid, int signal, int ch)> list)> WifiAsync()
    {
        var cur = "";
        var iface = await Run("netsh", new[] { "wlan", "show", "interfaces" });
        var m = Regex.Match(iface, @"^\s*SSID\s*[:：]\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (m.Success) cur = m.Groups[1].Value.Trim();

        var list = new List<(string ssid, int signal, int ch)>();
        var txt = await Run("netsh", new[] { "wlan", "show", "networks", "mode=bssid" });
        foreach (Match mm in Regex.Matches(txt, @"(?<![A-Za-z])SSID\s*\d*\s*[:：]\s*(.+)"))
        {
            var name = mm.Groups[1].Value.Trim();
            if (name.Length == 0 || name.Contains(":", StringComparison.Ordinal) && name.Replace(":", "").Replace("-", "").Length > 14) continue;
            // 同一个 SSID 会有多个 BSSID 块，取第一次出现；信号=块内唯一的百分数
            if (list.Any(t => t.ssid == name)) continue;
            var tail = txt.Substring(mm.Index, Math.Min(320, txt.Length - mm.Index));
            var sig = Regex.Match(tail, @"(\d+)\s*%");
            var chn = Regex.Match(tail, @"(?:Channel|频道)\s*[:：]\s*(\d+)");
            list.Add((name, sig.Success ? int.Parse(sig.Groups[1].Value) : 0, chn.Success ? int.Parse(chn.Groups[1].Value) : 0));
        }
        if (!string.IsNullOrEmpty(cur) && !list.Any(t => t.ssid == cur)) list.Insert(0, (cur, 100, 0));
        list = list.OrderByDescending(t => t.signal).ToList();
        return (cur, list);
    }

    /// <summary>推流端口的入站状态。注意 Block 优先于 Allow：Windows 在程序第一次监听时
    /// 若放行弹窗被忽略，会自动生成一条 Block 规则 —— 表现就是"UPnP 全成功但本机一次请求都收不到"，
    /// 所以这里必须把 blocked 单独报出来。</summary>
    public static async Task<(bool allow, bool blocked)> FirewallStateAsync(int port, string exe)
    {
        var script = "$ErrorActionPreference='SilentlyContinue';"
            + "$e='" + exe.ToLowerInvariant().Replace("'", "''") + "';"
            + "$rs=@(Get-NetFirewallApplicationFilter | Where-Object { $_.Program -and $_.Program.ToLower() -eq $e } | ForEach-Object { $_ | Get-NetFirewallRule });"
            + "$rs+=@(Get-NetFirewallPortFilter | Where-Object { $_.LocalPort -eq " + port + " } | ForEach-Object { $_ | Get-NetFirewallRule });"
            + "$rs | ForEach-Object { if ($_.Action -eq 'Allow') { 'ALLOW' } elseif ($_.Action -eq 'Block') { 'BLOCK' } }";
        var o = (await Run("powershell.exe", new[] { "-NoProfile", "-Command", script }, 14000)).ToUpperInvariant();
        return (o.Contains("ALLOW"), o.Contains("BLOCK"));
    }
    public static string RuleName(int port) => $"Gravity++ push stream inbound {port}";

    /// <summary>一键修：删掉本 exe 的 Block 规则 + 加按路径绑定的 Allow 规则（弹 UAC）</summary>
    public static bool FixFirewall(int port, string exe)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "fix-firewall.ps1");
        if (!File.Exists(script)) return false;
        return Elevate($"& '{script}' -Port {port} -Exe '{exe}'");
    }

    /// <summary>提权执行一次性修复（PowerShell 会弹 UAC）</summary>
    public static bool Elevate(string psCommand)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + "\"" + psCommand.Replace("\"", "\\\"") + "\"",
                UseShellExecute = true,     // 提权必须走 ShellExecute，同时不能设 CreateNoWindow
                Verb = "runas",
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>直接跑外部程序（参数走 ArgumentList，不经过 shell，免引号/转义问题）</summary>
    private static async Task<string> Run(string file, string[] args, int timeoutMs = 9000)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return so.Result + se.Result;
        }
        catch { return ""; }
    }

    private static async Task<string> Ps(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command " + cmd)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (cmd.Contains("xml")) psi.StandardOutputEncoding = Encoding.UTF8;   // netsh 的 xml 输出声明是 UTF-8
            using var p = Process.Start(psi)!;
            var so = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(8000);
            return so;
        }
        catch { return ""; }
    }
}
