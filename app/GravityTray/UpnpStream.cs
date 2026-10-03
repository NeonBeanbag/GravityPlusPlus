using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GravityTray;

/// <summary>
/// 目标②：把 PC 上的音频用标准 UPnP AVTransport 推到音响播放。
/// 实测要点：① 它是 MediaRenderer:1，但 UUID **每次开机都变**，必须每次 SSDP 发现，不能写死；
/// ② GetVersion/GetProtocolInfo 这台设备回 401，别拿它们做能力探测；
/// ③ 拉流是设备主动 GET 我们的 HTTP，所以**入站端口必须放行**，否则界面一切正常但永远 LOADING。
/// </summary>
public sealed class UpnpStream : IDisposable
{
    private const string AvType = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string RcType = "urn:schemas-upnp-org:service:RenderingControl:1";

    public record Endpoint(string ControlUrl, string? RenderUrl, string Ip, string Uuid);
    private Endpoint? ep;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private FileServer? server;
    private int serverPort;

    public int ServerPort => serverPort;
    public FileServer? Server => server;

    // ---------- 发现 ----------
    public static async Task<Endpoint?> DiscoverAsync(int ms = 2500)
    {
        var locs = new HashSet<string>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
            var q = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
            await udp.SendAsync(q, q.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                var remain = until - DateTime.UtcNow;
                if (remain <= TimeSpan.Zero) break;
                using (var cts = new CancellationTokenSource(remain))
                {
                try
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    var t = Encoding.Latin1.GetString(r.Buffer);
                    foreach (Match m in Regex.Matches(t, @"LOCATION:\s*(\S+)", RegexOptions.IgnoreCase))
                        if (m.Groups[1].Value.Contains("desc.xml", StringComparison.OrdinalIgnoreCase)) locs.Add(m.Groups[1].Value);
                }
                catch (OperationCanceledException) { continue; }
                catch (SocketException) { break; }
                catch { break; }
                }
            }
        }
        catch { return null; }
        foreach (var loc in locs)
        {
            var e = await ParseDescAsync(loc);
            if (e != null) return e;
        }
        return null;
    }

    private static async Task<Endpoint?> ParseDescAsync(string loc)
    {
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var xml = await h.GetStringAsync(loc);
            if (!xml.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase)) return null;
            string? Ctrl(string svc) => Regex.Match(xml,
                "<serviceType>urn:schemas-upnp-org:service:" + svc + @":1</serviceType>[\s\S]*?<controlURL>([^<]+)</controlURL>").Groups[1].Value is { Length: > 0 } v ? v : null;
            var av = Ctrl("AVTransport"); if (av == null) return null;
            var baseU = new Uri(loc);
            var uuid = Regex.Match(loc, @"/dev/([0-9a-f-]{36})/", RegexOptions.IgnoreCase).Groups[1].Value;
            return new Endpoint(new Uri(baseU, av).ToString(), Ctrl("RenderingControl") is { } rc ? new Uri(baseU, rc).ToString() : null,
                baseU.Host, uuid);
        }
        catch { return null; }
    }

    public async Task<Endpoint> EnsureAsync() => ep ??= await DiscoverAsync() ?? throw new InvalidOperationException("没找到音响的 UPnP 服务（确认它已联网、和 PC 同一子网）");

    // ---------- SOAP ----------
    private async Task<(int code, string body, string? err)> SoapAsync(string url, string svc, string action, string inner)
    {
        var xml = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{svc}\">{inner}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(xml, new UTF8Encoding(false), "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{svc}#{action}\"");
        try
        {
            using var r = await http.SendAsync(req);
            var b = await r.Content.ReadAsStringAsync();
            string? err = Regex.Match(b, @"<errorCode>(\d+)</errorCode>").Success ? Regex.Match(b, @"<errorCode>(\d+)</errorCode>").Groups[1].Value : null;
            return ((int)r.StatusCode, b, err);
        }
        catch (Exception e) { return (0, "", e.Message); }
    }

    public static string? Val(string xml, string tag)
    {
        var m = Regex.Match(xml, "<" + tag + @"(?:[^>]*)>([^<]*)<");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    // ---------- 播放 ----------
    /// <summary>推一个可被音响访问的 URL；DIDL 里的 res 必须给，否则它只显示标题不拉流</summary>
    public Task<(bool ok, string? err)> PlayUrlAsync(string url, string title, string mime) => PlayUrlAsync(url, title, mime, null, null);

    public static string Didl(string url, string title, string mime, Smtc.Meta? meta = null, string? artUrl = null)
        => $"<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
           $"<item id=\"0\" parent=\"-1\" restricted=\"0\"><dc:title>{WebUtility.HtmlEncode(meta?.Title ?? title)}</dc:title>" +
           (string.IsNullOrEmpty(meta?.Artist) ? "" : $"<dc:creator>{WebUtility.HtmlEncode(meta!.Artist)}</dc:creator>") +
           (string.IsNullOrEmpty(meta?.Album) ? "" : $"<upnp:album>{WebUtility.HtmlEncode(meta!.Album)}</upnp:album>") +
           (string.IsNullOrEmpty(artUrl) ? "" : $"<upnp:albumArtURI>{WebUtility.HtmlEncode(artUrl!)}</upnp:albumArtURI>") +
           $"<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
           $"<res protocolInfo=\"http-get:*:{mime}:*\">{WebUtility.HtmlEncode(url)}</res></item></DIDL-Lite>";

    public async Task<(bool ok, string? err)> PlayUrlAsync(string url, string title, string mime, Smtc.Meta? meta, string? artUrl)
    {
        var e = await EnsureAsync();
        var didl = Didl(url, title, mime, meta, artUrl);
        var inner = $"<InstanceID>0</InstanceID><CurrentURI>{WebUtility.HtmlEncode(url)}</CurrentURI><CurrentURIMetaData>{WebUtility.HtmlEncode(didl)}</CurrentURIMetaData>";
        var (c1, b1, e1) = await SoapAsync(e.ControlUrl, AvType, "SetAVTransportURI", inner);
        if (c1 != 200) return (false, $"SetAVTransportURI {e1 ?? c1.ToString()}");
        var (c2, _, e2) = await SoapAsync(e.ControlUrl, AvType, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");
        return c2 == 200 ? (true, null) : (false, $"Play {e2 ?? c2.ToString()}");
    }

    /// <summary>推本地音频文件：必要时起一个支持 Range 的 HTTP 服务，并在播放期间保持存活</summary>
    public async Task<(bool ok, string? err, string? url)> PlayLocalFileAsync(string path, string? senderIp = null)
    {
        if (!File.Exists(path)) return (false, "文件不存在", null);
        var ip = senderIp ?? CooeeProvisioner.LocalWlanIPv4() ?? throw new InvalidOperationException("取不到本机 WLAN IP");
        server ??= FileServer.Start(8123);
        if (server == null) return (false, "本地 HTTP 服务起不来", null);
        serverPort = server.Port;
        server.Serve(path);
        var url = $"http://{ip}:{serverPort}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        (bool ok, string? err) res = await PlayUrlAsync(url, Path.GetFileNameWithoutExtension(path), MimeOf(path));
        return (res.ok, res.err, url);
    }

    public static string MimeOf(string? path) => (path == null ? "" : Path.GetExtension(path)).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".mp4" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        _ => "audio/mpeg",
    };

    /// <summary>试路径：换歌时只塞"下一首"的 DIDL，期望渲染端只改显示不重开流（真机上要验）</summary>
    public async Task<(bool ok, string? err)> SetNextMetaAsync(string url, Smtc.Meta meta, string? artUrl)
    {
        var e = await EnsureAsync();
        var didl = Didl(url, meta.Title, "audio/wav", meta, artUrl);
        var inner = $"<InstanceID>0</InstanceID><NextURI>{WebUtility.HtmlEncode(url)}</NextURI><NextURIMetaData>{WebUtility.HtmlEncode(didl)}</NextURIMetaData>";
        var (c, _, err) = await SoapAsync(e.ControlUrl, AvType, "SetNextAVTransportURI", inner);
        return c == 200 ? (true, null) : (false, err ?? c.ToString());
    }

    public async Task<(bool ok, string? err)> StopAsync()
    {
        var e = await EnsureAsync();
        var (c, _, err) = await SoapAsync(e.ControlUrl, AvType, "Stop", "<InstanceID>0</InstanceID>");
        return c == 200 ? (true, null) : (false, err ?? c.ToString());
    }

    public async Task<(bool ok, string? err)> PauseAsync()
    {
        var e = await EnsureAsync();
        var (c, _, err) = await SoapAsync(e.ControlUrl, AvType, "Pause", "<InstanceID>0</InstanceID>");
        return c == 200 ? (true, null) : (false, err ?? c.ToString());
    }

    public async Task<(string state, string status, string pos, string uri, string? err)> StateAsync()
    {
        var e = await EnsureAsync();
        var t = await SoapAsync(e.ControlUrl, AvType, "GetTransportInfo", "<InstanceID>0</InstanceID>");
        var p = await SoapAsync(e.ControlUrl, AvType, "GetPositionInfo", "<InstanceID>0</InstanceID>");
        return (Val(t.body, "CurrentTransportState") ?? "?", Val(t.body, "CurrentTransportStatus") ?? "?",
                Val(p.body, "RelTime") ?? "?", Val(p.body, "TrackURI") ?? "", t.err ?? p.err);
    }

    public async Task<(bool ok, string? err)> SetVolumeAsync(int v)
    {
        var e = await EnsureAsync();
        if (e.RenderUrl == null) return (false, "设备没有 RenderingControl");
        var (c, _, err) = await SoapAsync(e.RenderUrl, RcType, "SetMute", "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>0</DesiredMute>");
        var (c2, _, err2) = await SoapAsync(e.RenderUrl, RcType, "SetVolume", $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{Math.Clamp(v, 0, 100)}</DesiredVolume>");
        return c2 == 200 ? (true, null) : (false, err2 ?? err ?? c2.ToString());
    }

    public void Dispose()
    {
        try { server?.Dispose(); } catch { }
        http.Dispose();
    }
}

/// <summary>只干一件事：把当前选中的音频文件按 HTTP 吐出去，支持 Range（DLNA 拉流必需），
/// 并且**播放期间不能关**（提前关会中途停）。请求计数是判据：入站被防火墙挡掉时它是 0。</summary>
public sealed class FileServer : IDisposable
{
    private readonly TcpListener lis;
    private string? file;
    private long hits;
    public int Port { get; }
    public long Hits => Interlocked.Read(ref hits);

    private FileServer(int port)
    {
        Port = port;
        lis = new TcpListener(IPAddress.Any, port);
        lis.Start();
        _ = Task.Run(AcceptLoop);
    }

    public static FileServer? Start(int preferPort = 8123)
    {
        for (var p = preferPort; p < preferPort + 12; p++)
        {
            try { return new FileServer(p); } catch { }
        }
        return null;
    }

    public void Serve(string path) => file = path;

    private async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = await lis.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        {
            c.ReceiveTimeout = 4000; c.SendTimeout = 15000;
            // 关键：写完就 dispose 会让还没排空的发送缓冲被 RST 掉，
            // 音响拿到的是截断文件 → MediaPlayer 停在 LOADING。linger 让 close 等数据发完。
            c.Client.LingerState = new LingerOption(true, 10);
            c.Client.NoDelay = true;
            try
            {
                var s = c.GetStream();
                var head = new byte[4096];
                int n = 0;
                while (n < head.Length)
                {
                    var r = await s.ReadAsync(head.AsMemory(n, head.Length - n));
                    if (r <= 0) break;
                    n += r;
                    if (IndexOf(head, n, "\r\n\r\n") >= 0) break;
                }
                var txt = Encoding.Latin1.GetString(head, 0, n);
                var m = Regex.Match(txt, @"^(GET|HEAD) (\S+)");
                if (!m.Success) return;
                Interlocked.Increment(ref hits);
                var f = file;
                if (f == null || !File.Exists(f)) { await WriteRaw(s, "404 Not Found", "text/plain", Array.Empty<byte>(), null); return; }
                var body = await File.ReadAllBytesAsync(f);
                var rng = Regex.Match(txt, @"Range: bytes=(\d*)-(\d*)", RegexOptions.IgnoreCase);
                if (rng.Success && rng.Groups[1].Value.Length > 0)
                {
                    var from = long.Parse(rng.Groups[1].Value, CultureInfo.InvariantCulture);
                    var to = rng.Groups[2].Value.Length > 0 ? long.Parse(rng.Groups[2].Value, CultureInfo.InvariantCulture) : body.Length - 1;
                    to = Math.Min(to, body.Length - 1);
                    if (from > to) { await WriteRaw(s, "416 Requested Range Not Satisfiable", "text/plain", Array.Empty<byte>(), null); return; }
                    var slice = body.AsSpan((int)from, (int)(to - from + 1)).ToArray();
                    await WriteRaw(s, "206 Partial Content", UpnpStream.MimeOf(f), slice,
                        $"Content-Range: bytes {from}-{to}/{body.Length}\r\nAccept-Ranges: bytes");
                }
                else
                {
                    await WriteRaw(s, "200 OK", UpnpStream.MimeOf(f), m.Groups[1].Value == "HEAD" ? Array.Empty<byte>() : body, "Accept-Ranges: bytes");
                }
                // 半关闭：只关发送方向，等对端读完再收连接
                try { c.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
            catch { /* 客户端中途断开是常态 */ }
        }
    }

    private static async Task WriteRaw(NetworkStream s, string status, string ctype, byte[] body, string? extra)
    {
        var hdr = $"HTTP/1.1 {status}\r\nContent-Type: {ctype}\r\nContent-Length: {body.Length}\r\nServer: Gravity++/1.0\r\nConnection: close\r\n";
        if (extra != null) hdr += extra + "\r\n";
        hdr += "\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(hdr));
        if (body.Length > 0) await s.WriteAsync(body);
        await s.FlushAsync();
    }

    private static int IndexOf(byte[] buf, int len, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i + n.Length <= len; i++)
        {
            var ok = true;
            for (var j = 0; j < n.Length; j++) if (buf[i + j] != n[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    public void Dispose() { try { lis.Stop(); } catch { } }
}
