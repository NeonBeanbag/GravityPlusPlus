using System.IO;
using Windows.Media.Control;

namespace GravityPanel;

/// <summary>Windows 系统级媒体会话（Apple Music / 浏览器 / 播放器都会往这里写）：标题/艺人/专辑/封面。
/// 音响在 AirPlay 会话里不回曲目，所以面板上"正在播放"只能信这份。</summary>
public sealed record SmtcMeta(string Title, string Artist, string Album, byte[]? Art);

public static class Smtc
{
    private static Task<T> Win<T>(this Windows.Foundation.IAsyncOperation<T> op)
    {
        var tcs = new TaskCompletionSource<T>();
        op.Completed = (a, s) => { try { tcs.TrySetResult(a.GetResults()); } catch (Exception e) { tcs.TrySetException(e); } };
        return tcs.Task;
    }

    public static async Task<SmtcMeta?> ReadAsync()
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
            return new SmtcMeta(mi.Title ?? "", mi.Artist ?? "", mi.AlbumTitle ?? "", art);
        }
        catch { return null; }
    }
}
