using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IOCompress = System.IO.Compression;

namespace GravityTray;

/// <summary>127.0.0.1:8788 本地 API，等价于原 server.mjs，供面板网页调用</summary>
public class LocalApi : IDisposable
{
    public const int Port = 8788;
    private const int SpeakerApiPort = 7766;
    private const int AdbPort = 7788;
    /// <summary>推流用的本地 HTTP 端口；防火墙规则按这个号放行</summary>
    public const int StreamPort = 8123;
    /// <summary>音源直播服务的端口（由 AudioSource 自己挑，这里给个默认给 UI 显示用）</summary>
    public static int PortOfSource = 8123;

    private readonly HttpListener listener = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly CooeeProvisioner cooee = new();
    private readonly UpnpStream stream = new();
    private readonly AudioSource source = new();
    private string? adbBin;

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public void Start()
    {
        adbBin = FindAdb();
        listener.Prefixes.Add(BaseUrl);
        listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    // ---------- adb ----------
    private static string? FindAdb()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(local)) return local;
        try
        {
            var p = Process.Start(new ProcessStartInfo("adb", "version") { UseShellExecute = false, RedirectStandardOutput = true });
            p?.WaitForExit(3000);
            if (p is { HasExited: true } && p.ExitCode == 0) return "adb";
        }
        catch { /* adb 不在 PATH */ }
        return null;
    }

    private record AdbResult(bool Ok, string? Out, string? Error);

    private AdbResult RunAdb(IEnumerable<string> args, int timeoutMs)
    {
        if (adbBin == null) return new AdbResult(false, null, "adb 不可用");
        try
        {
            var psi = new ProcessStartInfo(adbBin) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return new AdbResult(false, null, "adb 超时"); }
            return new AdbResult(p.ExitCode == 0, so.Result + se.Result, null);
        }
        catch (Exception e) { return new AdbResult(false, null, e.Message); }
    }

    // ---------- 扫描 ----------
    private static async Task<string?> Probe(string ip, int port, int timeoutMs = 350)
    {
        using var c = new TcpClient();
        try
        {
            await c.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return ip;
        }
        catch { return null; }
        finally { c.Close(); }
    }

    private static async Task<List<string>> ScanSubnets(string? customBase)
    {
        var bases = new HashSet<string>();
        if (!string.IsNullOrEmpty(customBase)) bases.Add(customBase);
        else
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        bases.Add(string.Join('.', ua.Address.GetAddressBytes().Take(3)));
            }
        }
        var sem = new SemaphoreSlim(64);
        var tasks = bases.SelectMany(b => Enumerable.Range(1, 254).Select(i => ProbeWith($"{b}.{i}", sem))).ToList();
        var found = await Task.WhenAll(tasks);
        return found.Where(x => x != null).Select(x => x!).ToList();

        static async Task<string?> ProbeWith(string ip, SemaphoreSlim sem)
        {
            await sem.WaitAsync();
            try { return await Probe(ip, SpeakerApiPort); }
            finally { sem.Release(); }
        }
    }

    private async Task<AdbResult> DownloadPlatformToolsAsync()
    {
        const string zipUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
        var zipPath = Path.Combine(AppContext.BaseDirectory, "platform-tools.zip");
        try
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var resp = await client.GetAsync(zipUrl);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            client.Dispose();
            await File.WriteAllBytesAsync(zipPath, bytes);
            var dest = Path.Combine(AppContext.BaseDirectory, "platform-tools");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            IOCompress.ZipFile.ExtractToDirectory(zipPath, AppContext.BaseDirectory);
            File.Delete(zipPath);
            adbBin = FindAdb();
            return new AdbResult(adbBin != null, adbBin, adbBin == null ? "下载后仍未找到 adb" : null);
        }
        catch (Exception e)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            return new AdbResult(false, null, e.Message);
        }
    }

    // ---------- 音响 HTTP ----------
    private async Task<string> SpeakerGet(string ip, string endpoint)
    {
        using var r = await http.GetAsync($"http://{ip}:{SpeakerApiPort}/{endpoint}");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync();
    }

    private async Task<string> SpeakerPost(string ip, string endpoint, object payload)
    {
        using var r = await http.PostAsync($"http://{ip}:{SpeakerApiPort}/{endpoint}",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        return await r.Content.ReadAsStringAsync();
    }

    // ---------- 路由 ----------
    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var p = req.Url?.AbsolutePath ?? "/";
            var ipParam = req.QueryString["ip"];

            if (p is "/" or "/index.html")
            {
                var html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "web", "index.html"));
                await Write(res, 200, html, "text/html; charset=utf-8");
                return;
            }
            if (p == "/icon.png")
            {
                var f = Path.Combine(AppContext.BaseDirectory, "web", "icon.png");
                if (File.Exists(f))
                {
                    var bytes = await File.ReadAllBytesAsync(f);
                    res.ContentType = "image/png"; res.Headers["Cache-Control"] = "no-store";
                    res.ContentLength64 = bytes.Length;
                    await res.OutputStream.WriteAsync(bytes);
                }
                else await Json(res, 404, new { ok = false, error = "icon 未随包发布" });
                return;
            }
            if (p == "/api/ping")
            {
                bool ok = false;
                if (!string.IsNullOrEmpty(ipParam))
                    try { await SpeakerGet(ipParam, "Info"); ok = true; } catch { }
                await Json(res, new { ok });
                return;
            }
            if (p == "/api/scan")
            {
                var found = await ScanSubnets(req.QueryString["base"]);
                await Json(res, new { found });
                return;
            }

            // adb 通道（先于 ip 守卫）
            if (p == "/api/adb/env") { await Json(res, new { available = adbBin != null, bin = adbBin }); return; }
            if (p == "/api/adb/setup" && req.HttpMethod == "POST")
            {
                var r = await DownloadPlatformToolsAsync();
                await Json(res, new { ok = r.Ok, @out = r.Out, error = r.Error });
                return;
            }
            if (p == "/api/adb/connect" && req.HttpMethod == "POST")
            {
                var body = await ReadJson(req);
                var r = RunAdb(new[] { "connect", $"{body.GetProperty("ip").GetString()}:{AdbPort}" }, 10000);
                await Json(res, new { ok = r.Ok, @out = r.Out, error = r.Error });
                return;
            }
            if (p == "/api/adb/shell" && req.HttpMethod == "POST")
            {
                var body = await ReadJson(req);
                var ip = body.GetProperty("ip").GetString()!;
                var cmd = body.GetProperty("cmd").GetString() ?? "";
                if (!Regex.IsMatch(cmd, @"^[\w @.:;/'""=<>&%|+\-,[\](){}*!?#]*$"))
                { await Json(res, 400, new { ok = false, error = "命令含不允许字符" }); return; }
                var r = RunAdb(new[] { "-s", $"{ip}:{AdbPort}", "shell", cmd }, 20000);
                await Json(res, new { ok = r.Ok, @out = r.Out, error = r.Error });
                return;
            }

            // ---------- 配网（cooee 空中长度通道）----------
            if (p == "/api/provision/start" && req.HttpMethod == "POST")
            {
                var b = await ReadJson(req);
                var ssid = b.TryGetProperty("ssid", out var x) ? x.GetString() ?? "" : "";
                var pwd = b.TryGetProperty("pass", out var y) ? y.GetString() ?? "" : "";
                var lip = b.TryGetProperty("local", out var z) && z.ValueKind == JsonValueKind.String
                    ? z.GetString() : CooeeProvisioner.LocalWlanIPv4();
                if (string.IsNullOrEmpty(ssid) || string.IsNullOrEmpty(pwd))
                { await Json(res, 400, new { ok = false, error = "SSID 和密码都要填" }); return; }
                var ok = cooee.Start(ssid, pwd, lip);
                await Json(res, new { ok, local = lip, error = ok ? null : "已有流在跑，或 socket 绑定失败（先停掉当前任务/检查本机 IP）" });
                return;
            }
            if (p == "/api/provision/status")
            {
                var st = cooee.Current;
                string? back = null;
                if (!string.IsNullOrEmpty(ipParam) && !st.Running && st.Passes > 0)
                {
                    var (sid, pw, _) = await CooeeProvisioner.ReadBackAsync(ipParam);
                    if (sid != null) back = sid;
                    if (pw != null) back += $" / {pw}";
                }
                await Json(res, new { running = st.Running, ssid = st.Ssid, passes = st.Passes, packets = st.Packets,
                                      elapsed = Math.Round(st.ElapsedSec, 1), error = st.Error, readback = back });
                return;
            }
            if (p == "/api/provision/stop" && req.HttpMethod == "POST")
            {
                cooee.Stop();
                await Json(res, new { ok = true });
                return;
            }

            // ---------- 环境自检（两处静默失败）----------
            if (p == "/api/env")
            {
                var (coal, coalOk) = await EnvCheck.CoalescingAsync();
                var (curSsid, list) = await EnvCheck.WifiAsync();
                var (fwAllow, fwBlocked) = await EnvCheck.FirewallStateAsync(StreamPort, Environment.ProcessPath ?? "");
                await Json(res, new
                {
                    localIp = CooeeProvisioner.LocalWlanIPv4(),
                    coalescing = new { state = coal, ok = coalOk },
                    firewall = new { ok = fwAllow && !fwBlocked, allow = fwAllow, blocked = fwBlocked, port = StreamPort },
                    wifi = new { current = curSsid, list = list.Select(t => new { ssid = t.ssid, signal = t.signal, channel = t.ch }) },
                });
                return;
            }
            if (p == "/api/env/fix" && req.HttpMethod == "POST")
            {
                var what = (await ReadJson(req)).TryGetProperty("what", out var w) ? w.GetString() : "";
                bool fired = what switch
                {
                    "coalesce" => EnvCheck.Elevate(
                        "Set-NetAdapterAdvancedProperty -Name WLAN -RegistryKeyword '*PacketCoalescing' -RegistryValue 0 -NoRestart"),
                    "firewall" => EnvCheck.FixFirewall(StreamPort, Environment.ProcessPath ?? ""),
                    _ => false,
                };
                await Json(res, new { ok = fired, note = fired ? "已请求管理员权限，请在弹窗里点\"是\"，然后重新自检" : "没有可修的对象或已取消" });
                return;
            }

            // ---------- 投屏播放（UPnP AVTransport）----------
            // ---------- PC 音源：把这台电脑正在放的声音投过去（目标②的正解）----------
            if (p == "/api/source/start" && req.HttpMethod == "POST")
            {
                var (ok, err) = await source.StartAsync();
                await Json(res, new { ok, error = err, @as = source.Now() });
                return;
            }
            if (p == "/api/source/stop" && req.HttpMethod == "POST")
            {
                source.Stop();
                await Json(res, new { ok = true, @as = source.Now() });
                return;
            }
            if (p == "/api/source/status")
            {
                await Json(res, source.Now());
                return;
            }
            if (p == "/api/source/art")
            {
                try
                {
                    var b = await http.GetByteArrayAsync($"http://127.0.0.1:{AudioSource.PortOfSource}/art.jpg");
                    res.ContentType = "image/jpeg"; res.ContentLength64 = b.Length;
                    await res.OutputStream.WriteAsync(b);
                }
                catch { await Json(res, 404, new { ok = false, error = "暂无封面" }); }
                return;
            }

            if (p == "/api/stream/discover")
            {
                var e = await UpnpStream.DiscoverAsync();
                await Json(res, e == null ? new { ok = false, error = "没发现 MediaRenderer" }
                    : new { ok = true, ip = e.Ip, uuid = e.Uuid, av = e.ControlUrl, rc = e.RenderUrl });
                return;
            }
            if (p == "/api/stream/state")
            {
                try
                {
                    var (state, status, pos, uri, err) = await stream.StateAsync();
                    await Json(res, new { ok = true, @state = state, status, pos, uri, err, hits = stream.Server?.Hits ?? 0 });
                }
                catch (Exception e) { await Json(res, new { ok = false, error = e.Message }); }
                return;
            }
            if (p == "/api/stream/play" && req.HttpMethod == "POST")
            {
                var b = await ReadJson(req);
                var url = b.TryGetProperty("url", out var u) ? u.GetString() : null;
                var path = b.TryGetProperty("path", out var pa) ? pa.GetString() : null;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var (ok, err) = await stream.PlayUrlAsync(url.Trim(), Path.GetFileNameWithoutExtension(url.Split('?')[0]) , UpnpStream.MimeOf(url));
                    await Json(res, new { ok, error = err, via = url });
                    return;
                }
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var (ok, err, real) = await stream.PlayLocalFileAsync(path);
                    await Json(res, new { ok, error = err, via = real, note = ok ? null : "如果显示 Success 但没声，先看防火墙是否放行了推流端口" });
                    return;
                }
                await Json(res, 400, new { ok = false, error = "给 url 或 path" });
                return;
            }
            if ((p == "/api/stream/stop" || p == "/api/stream/pause") && req.HttpMethod == "POST")
            {
                var r = p.EndsWith("stop") ? await stream.StopAsync() : await stream.PauseAsync();
                await Json(res, new { ok = r.ok, error = r.err });
                return;
            }
            if (p == "/api/stream/volume" && req.HttpMethod == "POST")
            {
                var v = (await ReadJson(req)).GetProperty("value").GetInt32();
                var r = await stream.SetVolumeAsync(v);
                // 音响有两套音量：DLNA 这套 + 7766 的 CurrentVolume，一起设才听得到
                string? raw = null;
                if (!string.IsNullOrEmpty(ipParam)) try { raw = await SpeakerPost(ipParam, "SetVolume", new { CurrentVolume = v }); } catch { }
                await Json(res, new { ok = r.ok, error = r.err, raw });
                return;
            }
            if (p == "/api/stream/pick")
            {
                // 面板里点"选本地文件"→ 弹原生文件框（必须在 UI 线程上弹）
                string? chosen = null;
                var app = System.Windows.Application.Current;
                if (app == null) { await Json(res, 500, new { ok = false, error = "没有 UI 线程" }); return; }
                app.Dispatcher.Invoke(() =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "选择要投放的音频",
                        Filter = "音频 (*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg|所有文件 (*.*)|*.*",
                        Multiselect = false,
                    };
                    if (dlg.ShowDialog() == true) chosen = dlg.FileName;
                });
                if (string.IsNullOrEmpty(chosen)) { await Json(res, new { ok = false, error = "已取消" }); return; }
                var (pok, perr, purl) = await stream.PlayLocalFileAsync(chosen!);
                await Json(res, new { ok = pok, error = perr, via = purl, name = System.IO.Path.GetFileName(chosen) });
                return;
            }
            if (p == "/api/stream/library")
            {
                var dir = req.QueryString["dir"];
                var roots = new List<string>();
                if (!string.IsNullOrEmpty(dir)) roots.Add(dir);
                else
                {
                    roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
                    roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                }
                var exts = new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg" };
                var files = roots.Where(Directory.Exists).SelectMany(r2 =>
                {
                    try { return new DirectoryInfo(r2).EnumerateFiles("*", SearchOption.AllDirectories).Take(400); }
                    catch { return Enumerable.Empty<FileInfo>(); }
                }).Where(f => exts.Contains(f.Extension.ToLowerInvariant()))
                  .OrderByDescending(f => f.LastWriteTime).Take(120)
                  .Select(f => new { name = Path.GetFileNameWithoutExtension(f.Name), path = f.FullName, kb = f.Length / 1024, mime = UpnpStream.MimeOf(f.Name) });
                await Json(res, new { roots, files });
                return;
            }

            // 封面代理
            if (p == "/api/cover")
            {
                var cu = req.QueryString["url"];
                if (cu == null || !cu.StartsWith("http")) { await Json(res, 400, new { ok = false, error = "bad url" }); return; }
                using var r = await http.GetAsync(cu);
                var bytes = await r.Content.ReadAsByteArrayAsync();
                res.ContentType = r.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                res.Headers["Cache-Control"] = "no-store";
                res.StatusCode = 200;
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes);
                return;
            }

            // 音响 API 代理（需 ip）
            if (p.StartsWith("/api/"))
            {
                if (string.IsNullOrEmpty(ipParam) || !Regex.IsMatch(ipParam, @"^[\w.:-]+$"))
                { await Json(res, 400, new { ok = false, error = "bad ip" }); return; }
                var map = new Dictionary<string, string>
                    { ["/api/status"] = "Status", ["/api/info"] = "Info", ["/api/play"] = "Play", ["/api/pause"] = "Pause", ["/api/next"] = "Next", ["/api/prev"] = "Prev" };
                if (map.TryGetValue(p, out var ep) && req.HttpMethod == "GET")
                {
                    var raw = await SpeakerGet(ipParam, ep);
                    await Write(res, 200, raw, "application/json; charset=utf-8");
                    return;
                }
                if ((p == "/api/set-volume" || p == "/api/set-eq") && req.HttpMethod == "POST")
                {
                    var v = (await ReadJson(req)).GetProperty("value").GetInt32();
                    var raw = p == "/api/set-volume"
                        ? await SpeakerPost(ipParam, "SetVolume", new { CurrentVolume = v })
                        : await SpeakerPost(ipParam, "SetEQMode", new { EQMode = v });
                    await Json(res, new { raw });
                    return;
                }
            }

            await Json(res, 404, new { ok = false, error = "not found" });
        }
        catch (Exception e)
        {
            try { await Json(res, 502, new { ok = false, error = e.Message }); } catch { }
        }
        finally { res.Close(); }
    }

    private static async Task<JsonElement> ReadJson(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        var s = await sr.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(s) ? JsonDocument.Parse("{}").RootElement : JsonDocument.Parse(s).RootElement;
    }

    private static Task Json(HttpListenerResponse res, object obj) => Json(res, 200, obj);
    private static Task Json(HttpListenerResponse res, int code, object obj)
        => Write(res, code, JsonSerializer.Serialize(obj), "application/json; charset=utf-8");

    private static async Task Write(HttpListenerResponse res, int code, string body, string ctype)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = code;
        res.ContentType = ctype;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        try { listener.Stop(); listener.Close(); } catch { }
        try { cooee.Dispose(); } catch { }
        try { stream.Dispose(); } catch { }
        try { source.Dispose(); } catch { }
        http.Dispose();
    }
}
