using System.Diagnostics;
using Microsoft.Win32;

namespace IMESupport.Services;

/// <summary>
/// Windows起動時の自動起動（スタートアップ）管理クラス
/// </summary>
public static class StartupManager
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "IMESupport";

    /// <summary>
    /// スタートアップに登録されているか確認
    /// </summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// スタートアップ登録を設定
    /// </summary>
    public static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"スタートアップ設定変更エラー: {ex.Message}");
        }
    }
}
