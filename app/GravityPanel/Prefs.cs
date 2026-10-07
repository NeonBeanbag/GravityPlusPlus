using System.IO;
using System.Text.Json;

namespace GravityPanel;

/// <summary>少量偏好落盘在 %LOCALAPPDATA%\Gravity++\panel.json（旧面板用的是浏览器 localStorage）。</summary>
internal static class Prefs
{
    private sealed record Model(string? SpeakerIp, string? SpeakerId, bool PreferNextOnly, string? Hotkey);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gravity++", "panel.json");

    private static Model _m = Load();

    private static Model Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath)) ?? new(null, null, false, null)
                : new(null, null, false, null);
        }
        catch { return new(null, null, false, null); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_m));
        }
        catch (Exception e) { Log.Write("偏好写入失败: " + e); }
    }

    public static string? SpeakerIp
    {
        get => _m.SpeakerIp;
        set { _m = _m with { SpeakerIp = value }; Save(); }
    }

    /// <summary>连过的那台音响的 deviceID（MAC）。音响换 IP 时靠它认回同一台，不会串到邻居的音响上去。</summary>
    public static string? SpeakerId
    {
        get => _m.SpeakerId;
        set { _m = _m with { SpeakerId = value }; Save(); }
    }

    public static bool PreferNextOnly
    {
        get => _m.PreferNextOnly;
        set { _m = _m with { PreferNextOnly = value }; Save(); }
    }

    /// <summary>呼起面板的全局热键，写法如 "Ctrl+Alt+G"。null = 用默认值。</summary>
    public static string? Hotkey
    {
        get => _m.Hotkey;
        set { _m = _m with { Hotkey = value }; Save(); }
    }
}
