using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using NAudio.Wave;
using Windows.Storage.Streams;
using Windows.Media.Control;

namespace GravityTray;

/// <summary>
/// 目标②正解：把"这台电脑正在播放的声音"（Apple Music / 任意播放器）投到音响上，
/// 并把系统级媒体信息（SMTC：标题/艺人/专辑/封面）一起带过去。
///
/// 三段拼起来：
///   1) <see cref="PcmSource"/>  WASAPI loopback 抓默认播放设备 → 转 16bit PCM（设备混合格式通常是 48k/float32）
///   2) <see cref="LiveHttpd"/>  以"超长 Content-Length + 持续喂字节"的 WAV 直播流供出去
///      （这个形状是 2026-10-02 在真机上验过的：音响能连续播 38s+ 不停；见 memory）
///   3) <see cref="Smtc"/>       读 Windows 的媒体会话，换歌时更新 DIDL 元数据
/// </summary>
public sealed class AudioSource : IDisposable
{
    private readonly PcmSource pcm = new();
    private readonly LiveHttpd http = new();
    private readonly UpnpStream upnp = new();
    private CancellationTokenSource? metaCts;
    public static int PortOfSource { get; private set; } = 8123;
    private string lastTitle = "";

    public record struct State(bool Running, int Rate, int Channels, long Bytes, int Listeners, string? Url,
                               string? Title, string? Artist, string? Album, bool HasArt, string? Note);
    private State _st;
    public State Current => _st;
    /// <summary>给状态接口用的即时快照（Current 只是最后一次写入的值，不重算）</summary>
    public State Now() => Snapshot();

    public async Task<(bool ok, string? err)> StartAsync(string? senderIp = null)
    {
        if (_st.Running) return (false, "音源已经在跑");
        var ip = senderIp ?? CooeeProvisioner.LocalWlanIPv4() ?? "";
        if (ip == "") return (false, "取不到本机 WLAN IP");
        try
        {
            pcm.Reset();
            pcm.Start();
            http.Start(pcm, () => Snapshot());
            PortOfSource = http.Port;
            var url = $"http://{ip}:{http.Port}/live.wav";
            var (ok, err) = await upnp.PlayUrlAsync(url, "这台电脑的音频", "audio/wav");
            if (!ok) return (false, "已抓到音频，但推送失败：" + err);
            metaCts = new CancellationTokenSource();
            _ = Task.Run(() => MetaLoopAsync(metaCts.Token));
            _st = _st with { Running = true, Url = url, Rate = pcm.WaveFormat?.SampleRate ?? 0, Channels = pcm.WaveFormat?.Channels ?? 0, Note = null };
            return (true, null);
        }
        catch (Exception e) { Stop(); return (false, e.Message); }
    }

    public void Stop()
    {
        try { metaCts?.Cancel(); } catch { }
        try { _ = upnp.StopAsync(); } catch { }
        pcm.Stop();
        http.Stop();
        _st = new State(false, 0, 0, 0, 0, null, _st.Title, _st.Artist, _st.Album, _st.HasArt, "已停止");
    }

    // 供出的流恒为 16bit 立体声（内部把 5.1/float 都下混了），所以对外报 2 声道
    private State Snapshot() => _st with { Bytes = pcm.Written, Listeners = http.Listeners, Rate = pcm.SampleRate, Channels = 2 };

    /// <summary>换歌就把新的 DIDL 推给渲染端。默认重设 URI（会有约 1 秒的重开间隙）；
    /// 真机上如果 SetNextAVTransportURI 被证明能只改显示不停流，就把 PreferNextOnly 打开。</summary>
    public static bool PreferNextOnly = false;

    private async Task MetaLoopAsync(CancellationToken tok)
    {
        while (!tok.IsCancellationRequested)
        {
            try
            {
                var m = await Smtc.ReadAsync();
                if (m != null && m.Title != lastTitle)
                {
                    lastTitle = m.Title;
                    Smtc.Latch(m);
                    _st = _st with { Title = m.Title, Artist = m.Artist, Album = m.Album, HasArt = m.Art != null && m.Art!.Length > 0 };
                    var url = _st.Url;
                    if (!string.IsNullOrEmpty(url) && _st.Running)
                    {
                        string artUrl = $"http://{CooeeProvisioner.LocalWlanIPv4()}:{http.Port}/art.jpg";
                        if (PreferNextOnly) await upnp.SetNextMetaAsync(url!, m, artUrl);
                        else await upnp.PlayUrlAsync(url!, $"{m.Title}" + (string.IsNullOrEmpty(m.Artist) ? "" : $" - {m.Artist}"), "audio/wav", m, artUrl);
                    }
                }
                else if (m != null) _st = _st with { Title = m.Title, Artist = m.Artist, Album = m.Album, HasArt = m.Art != null };
            }
            catch { /* 没有媒体会话时是正常的 */ }
            try { await Task.Delay(2000, tok); } catch { return; }
        }
    }

    public void Dispose() { Stop(); pcm.Dispose(); http.Dispose(); upnp.Dispose(); }
}

/// <summary>WASAPI 回环抓取 → 边抓边转 16bit，写进一个"最近 N 秒"的环形缓冲，供直播端按游标读</summary>
public sealed class PcmSource : IDisposable
{
    private WasapiLoopbackCapture? cap;
    private byte[] ring = new byte[4 * 1024 * 1024];      // 48k/16bit/2ch ≈ 192KB/s → 能存 ~20 秒
    private long written;
    private readonly object g = new();
    public WaveFormat? WaveFormat { get; private set; }
    public int SampleRate => WaveFormat?.SampleRate ?? 48000;
    public int Channels => Math.Min(WaveFormat?.Channels ?? 2, 2);
    public long Written => Interlocked.Read(ref written);

    public void Reset() { lock (g) { written = 0; Array.Clear(ring); } }

    public void Start()
    {
        cap = new WasapiLoopbackCapture();                 // 默认播放设备的回环 = 这台电脑正在放的所有声音
        WaveFormat = cap.WaveFormat;
        cap.DataAvailable += (_, e) =>
        {
            var src = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, src, src.Length);
            var pcm16 = To16(src);
            lock (g)
            {
                foreach (var b in pcm16) ring[written % ring.Length] = b;
                written += pcm16.Length;
            }
        };
        cap.StartRecording();
    }

    private byte[] To16(byte[] src)
    {
        var fmt = WaveFormat!;
        int inCh = Math.Max(fmt.Channels, 1);
        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16 && inCh <= 2) return src;
        // 绝大多数情况：IEEE float 32bit（可能还是 5.1/7.1），一律混成立体声 16bit
        int frames = src.Length / (4 * inCh);
        var outb = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            float l = 0, r = 0;
            for (var c = 0; c < inCh; c++)
            {
                var v = Math.Clamp(BitConverter.ToSingle(src, (i * inCh + c) * 4), -1f, 1f);
                if (c == 0) l = v; else if (c == 1) r = v; else { l += v * 0.5f; r += v * 0.5f; }
            }
            var ql = (short)(Math.Clamp(l, -1f, 1f) * 32767);
            var qr = (short)(Math.Clamp(r, -1f, 1f) * 32767);
            outb[i * 4] = (byte)(ql & 0xff); outb[i * 4 + 1] = (byte)(ql >> 8);
            outb[i * 4 + 2] = (byte)(qr & 0xff); outb[i * 4 + 3] = (byte)(qr >> 8);
        }
        return outb;
    }

    /// <summary>从环形缓冲取 [from, from+len)；落后太多就从尾部追（丢帧，直播的正常行为）</summary>
    public (byte[] data, long next)? Read(long from, int len)
    {
        lock (g)
        {
            var now = written;
            if (now - from > ring.Length / 2) from = Math.Max(0, now - ring.Length / 2);
            if (from + len <= now)
            {
                var b = new byte[len];
                for (var i = 0; i < len; i++) b[i] = ring[(from + i) % ring.Length];
                return (b, from + len);
            }
            int avail = (int)Math.Max(0, now - from);
            if (avail <= 0) return null;
            var bb = new byte[avail];
            for (var i = 0; i < avail; i++) bb[i] = ring[(from + i) % ring.Length];
            return (bb, from + avail);
        }
    }

    public void Stop() { try { cap?.StopRecording(); } catch { } cap = null; }
    public void Dispose() { Stop(); cap?.Dispose(); }
}

/// <summary>只服务两个路径：/live.wav（连续 PCM 直播）与 /art.jpg（当前封面）。
/// 形状照搬真机验过的写法：200 + 确定的超长 Content-Length + 持续 write。</summary>
public sealed class LiveHttpd : IDisposable
{
    private TcpListener? lis;
    private PcmSource? src;
    private Func<AudioSource.State>? snap;
    private int listeners;
    public int Port { get; private set; }
    public int Listeners => Volatile.Read(ref listeners);
    private byte[] art = Array.Empty<byte>();

    public void Start(PcmSource s, Func<AudioSource.State> state)
    {
        src = s; snap = state;
        for (var p = 8123; p < 8140; p++)
        {
            try { lis = new TcpListener(IPAddress.Any, p); lis.Start(); Port = p; _ = Task.Run(AcceptLoop); return; }
            catch { lis = null; }
        }
        throw new InvalidOperationException("8123-8139 都占用，起不了音源服务");
    }
    public void Stop() { try { lis?.Stop(); } catch { } lis = null; }
    public void SetArt(byte[]? j) { art = j ?? Array.Empty<byte>(); }

    private async Task AcceptLoop()
    {
        while (lis != null)
        {
            TcpClient c;
            try { c = await lis.AcceptTcpClientAsync(); } catch { return; }
            Interlocked.Increment(ref listeners);
            _ = Task.Run(async () => { try { await Handle(c); } finally { Interlocked.Decrement(ref listeners); c.Dispose(); } });
        }
    }

    private async Task Handle(TcpClient c)
    {
        c.Client.LingerState = new LingerOption(true, 8);
        var s = c.GetStream();
        var head = new byte[2048]; int n = 0;
        while (n < head.Length)
        {
            int r;
            try { r = await s.ReadAsync(head.AsMemory(n, head.Length - n)); } catch { return; }
            if (r <= 0) break;
            n += r;
            if (Encoding.ASCII.GetString(head, 0, n).Contains("\r\n\r\n")) break;
        }
        var txt = Encoding.Latin1.GetString(head, 0, n);
        var path = System.Text.RegularExpressions.Regex.Match(txt, @"^(?:GET|HEAD) (\S+)").Groups[1].Value;
        if (path.StartsWith("/art"))
        {
            var b = art;
            await W(s, b.Length == 0 ? "404 Not Found" : "200 OK", "image/jpeg", b);
            return;
        }
        if (path.StartsWith("/state"))
        {
            var j = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(snap?.Invoke()));
            await W(s, "200 OK", "application/json", j);
            return;
        }
        // /live.wav —— 48k/16bit/2ch，谎称 6 小时长度，然后一直喂
        var ps = src!;
        int rate = ps.SampleRate, ch = ps.Channels;
        long claimed = (long)rate * ch * 2 * 6 * 3600;
        var hdr = WavHeader((uint)Math.Min(claimed, uint.MaxValue - 44));
        var bytes = hdr.Length + claimed;
        s.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {bytes}\r\nAccept-Ranges: bytes\r\nServer: GravitySource/1.0\r\nConnection: close\r\n\r\n"));
        await s.FlushAsync();
        await s.WriteAsync(hdr);
        var from = ps.Written;
        var tick = Stopwatch.Frequency / 10;
        var next = Stopwatch.GetTimestamp();
        while (true)
        {
            var rd = ps.Read(from, rate / 10 * ch * 2);          // 每 ~100ms 一块
            if (rd == null)
            {
                if (Stopwatch.GetTimestamp() < next) { await Task.Delay(10); continue; }
                next = Stopwatch.GetTimestamp() + tick;            // 没数据（静音）也要按时钟推，否则播放器会以为到底
                await s.WriteAsync(new byte[rate / 10 * ch * 2]);
                from += rate / 10 * ch * 2;
                continue;
            }
            var (data, nf) = rd.Value;
            await s.WriteAsync(data);
            from = nf;
            next += tick;
            var wait = next - Stopwatch.GetTimestamp();
            if (wait > 0) await Task.Delay((int)(wait * 1000 / Stopwatch.Frequency));
        }
    }

    private static byte[] WavHeader(uint dataLen)
    {
        var h = new byte[44];
        var w = new BinaryWriter(new MemoryStream(h));
        w.Write("RIFF".ToCharArray());          // 4
        w.Write((int)(36 + dataLen));           // RFF size-8
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(48000); w.Write(192000); w.Write((short)4); w.Write((short)16);
        w.Write("data".ToCharArray());
        w.Write((int)dataLen);
        w.Flush();
        return h;
    }

    private static async Task W(NetworkStream s, string status, string ctype, byte[] body)
    {
        var h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {ctype}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(h); await s.WriteAsync(body); await s.FlushAsync();
    }

    public void Dispose() { Stop(); }
}

/// <summary>Windows 系统级媒体会话（Apple Music / 浏览器 / 播放器都会往这里写）：标题/艺人/专辑/封面</summary>
public static class Smtc
{
    public record Meta(string Title, string Artist, string Album, byte[]? Art);
    private static Meta? latched;

    public static void Latch(Meta m) => latched = m;

    private static Task<T> Win<T>(this Windows.Foundation.IAsyncOperation<T> op)
    {
        var tcs = new TaskCompletionSource<T>();
        op.Completed = (a, s) => { try { tcs.TrySetResult(a.GetResults()); } catch (Exception e) { tcs.TrySetException(e); } };
        return tcs.Task;
    }

    public static async Task<Meta?> ReadAsync()
    {
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().Win();
            var sess = mgr.GetCurrentSession() ?? mgr.GetSessions().FirstOrDefault();
            if (sess == null) return null;
            var mi = await sess.TryGetMediaPropertiesAsync().Win();
            byte[]? art = null;
            if (mi.Thumbnail != null)
            {
                using var ras = await mi.Thumbnail.OpenReadAsync();
                using var rs = ras.AsStreamForRead();
                using var ms = new MemoryStream();
                await rs.CopyToAsync(ms);
                art = ms.ToArray().Length > 200 ? ms.ToArray() : null;   // 太小的图不如不发
            }
            return new Meta(mi.Title ?? "", mi.Artist ?? "", mi.AlbumTitle ?? "", art);
        }
        catch { return null; }
    }
}
