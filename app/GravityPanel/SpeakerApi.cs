using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GravityPanel;

/// <summary>
/// 音响 7766 HTTP 接口的原生客户端（旧 WebView 面板里那层 /api/* 桥的等价物，直连不再绕 8788）。
///
/// 两个字段名是踩过坑的：音量必须叫 CurrentVolume、EQ 必须叫 EQMode，
/// 写错接口照样回 Success，但设备毫无反应。所以凡是写命令都要回读确认。
/// </summary>
public sealed class SpeakerApi : IDisposable
{
    private const int ApiPort = 7766;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public record NowPlaying(string Title, string Artist, string? CoverUrl, string Status, int EqMode);
    public record Snapshot(NowPlaying Now, int? Volume, string InputSource);

    /// <summary>Id 取音响自己的 deviceID（就是它的网卡 MAC），DHCP 换 IP 换不掉它。</summary>
    public record Device(string Ip, string Name, string Id);

    private static readonly HttpClient probeHttp = new() { Timeout = TimeSpan.FromSeconds(2.5) };

    public static bool LooksLikeIp(string s)
        => !string.IsNullOrWhiteSpace(s) && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\w.:-]+$");

    private Task<JsonElement> Get(string ip, string endpoint) => Send(ip, endpoint, null);
    private Task<JsonElement> Post(string ip, string endpoint, object payload)
        => Send(ip, endpoint, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private async Task<JsonElement> Send(string ip, string endpoint, HttpContent? content)
    {
        if (!LooksLikeIp(ip)) throw new ArgumentException("bad ip");
        var url = $"http://{ip}:{ApiPort}/{endpoint}";
        var resp = content is null ? await http.GetAsync(url) : await http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>能不能连上：Info 是最轻的探活</summary>
    public async Task<bool> PingAsync(string ip)
    {
        try { await Get(ip, "Info"); return true; } catch { return false; }
    }

    public async Task<Snapshot> ReadAsync(string ip)
    {
        var s = await Get(ip, "Status");
        var d = s.TryGetProperty("data", out var x) ? x : s;
        var track = d.GetProperty("playList").GetProperty("trackList")[0];
        var cover = Str(track, "coverUrl");
        var now = new NowPlaying(
            Str(track, "trackTitle"), Str(track, "artistName"),
            string.IsNullOrEmpty(cover) ? null : cover,
            Str(d, "status"),
            d.TryGetProperty("EQMode", out var eq) && eq.TryGetInt32(out var eqv) ? eqv : -1);

        int? vol = null;
        try
        {
            var i = await Get(ip, "Info");
            var di = i.TryGetProperty("data", out var y) ? y : i;
            if (di.TryGetProperty("currentVolume", out var cv) && cv.TryGetInt32(out var cvv)) vol = cvv;
        }
        catch { /* 音量读不到不影响播放信息 */ }
        return new Snapshot(now, vol, Str(d, "inputSource"));
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public Task PlayAsync(string ip) => Get(ip, "Play");
    public Task PauseAsync(string ip) => Get(ip, "Pause");
    public Task NextAsync(string ip) => Get(ip, "Next");
    public Task PrevAsync(string ip) => Get(ip, "Prev");
    public Task SetVolumeAsync(string ip, int v) => Post(ip, "SetVolume", new { CurrentVolume = v });
    public Task SetEqAsync(string ip, int mode) => Post(ip, "SetEQMode", new { EQMode = mode });

    /// <summary>封面由音响给的是第三方 CDN 地址，直接给 Image 有时拿不到；这里取字节再喂进去。</summary>
    public async Task<byte[]?> CoverAsync(string url, int timeoutMs = 6000)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            return await c.GetByteArrayAsync(url);
        }
        catch { return null; }
    }

    /// <summary>认一台地址上的音响：/Info 能答就算数，名字和 deviceID 一起拿回来。</summary>
    public async Task<Device?> IdentifyAsync(string ip)
    {
        try
        {
            if (!LooksLikeIp(ip)) return null;
            using var resp = await probeHttp.GetAsync($"http://{ip}:{ApiPort}/Info");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var d = doc.RootElement.TryGetProperty("data", out var x) ? x : doc.RootElement;
            var name = Str(d, "deviceName");
            if (name == "") return null;
            return new Device(ip, name, Str(d, "deviceID"));
        }
        catch { return null; }
    }

    /// <summary>音响的 DLNA 接收端会应答 SSDP 组播，回包里的地址就是它当前的 IP。
    /// 每个本机 IPv4 各发一份：多网卡（Tailscale / 以太网）时不绑口就可能从别的接口出去，音响收不到。</summary>
    public async Task<List<string>> SsdpAsync(int timeoutMs = 1500)
    {
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var req = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
        var mcast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        var jobs = LocalIPv4s().Select(async bindIp =>
        {
            try
            {
                using var c = new UdpClient(new IPEndPoint(bindIp, 0));
                c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                c.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, bindIp.GetAddressBytes());
                await c.SendAsync(req, mcast);
                await c.SendAsync(req, mcast);
                var deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64)));
                    UdpReceiveResult r;
                    try { r = await c.ReceiveAsync(cts.Token); } catch { return; }
                    var text = Encoding.UTF8.GetString(r.Buffer);
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"LOCATION:\s*https?://([\d.]{7,15})",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    found.TryAdd(m.Success ? m.Groups[1].Value : r.RemoteEndPoint.Address.ToString(), 0);
                }
            }
            catch (Exception ex) { Log.Write($"SSDP 从 {bindIp} 发不出去: {ex.Message}"); }
        });
        await Task.WhenAll(jobs);
        return found.Keys.ToList();
    }

    /// <summary>找音响：先问 SSDP（快、而且认得出换了 IP 的同一台），组播被路由器挡掉时退回扫段。</summary>
    public async Task<List<Device>> FindAllAsync()
    {
        var ips = await SsdpAsync();
        if (ips.Count == 0) ips = await ScanAsync();
        var probed = await Task.WhenAll(ips.Select(IdentifyAsync));
        return probed.OfType<Device>().ToList();
    }

    private static List<IPAddress> LocalIPv4s()
    {
        var list = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    list.Add(ua.Address);
        }
        return list;
    }

    /// <summary>局域网扫音响：对本机每个 IPv4 网段的 7766 做一次并发 TCP 探测</summary>
    public async Task<List<string>> ScanAsync(int timeoutMs = 350)
    {
        var bases = LocalIPv4s().Select(a => string.Join('.', a.GetAddressBytes().Take(3))).ToHashSet();
        var sem = new SemaphoreSlim(64);
        var tasks = bases.SelectMany(b => Enumerable.Range(1, 254).Select(i => ProbeAsync($"{b}.{i}", sem, timeoutMs))).ToList();
        var got = await Task.WhenAll(tasks);
        return got.Where(x => x != null).Select(x => x!).ToList();

        static async Task<string?> ProbeAsync(string ip, SemaphoreSlim s, int ms)
        {
            await s.WaitAsync();
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(ip, ApiPort).WaitAsync(TimeSpan.FromMilliseconds(ms));
                return ip;
            }
            catch { return null; }
            finally { s.Release(); }
        }
    }

    public void Dispose() => http.Dispose();
}
