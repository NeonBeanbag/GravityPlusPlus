using Microsoft.Win32;

namespace GravityPanel;

/// <summary>开机自启：HKCU\...\Run 里一个值，装了就是装了，不做任务计划程序。</summary>
internal static class StartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Gravity++";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return (string?)k?.GetValue(ValueName) == $"\"{ExePath}\"";
            }
            catch { return false; }
        }
    }

    public static void Set(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) k.SetValue(ValueName, $"\"{ExePath}\"");
            else k.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e) { Log.Write("自启设置失败: " + e); }
    }
}
