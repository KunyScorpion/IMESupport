using System.Diagnostics;
using System.Runtime.InteropServices;
using IMESupport.Models;

namespace IMESupport.Services;

/// <summary>
/// 低レベルキーボードフックによるショートカット・ダブルタップ検知サービス
/// </summary>
public class KeyboardHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    public const int VK_CONVERT = 0x1C;      // 変換キー
    public const int VK_NONCONVERT = 0x1D;   // 無変換キー
    public const int VK_CONTROL = 0x11;      // Ctrlキー
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_SHIFT = 0x10;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_MENU = 0x12;        // Altキー
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;

    private string _triggerKeyType = "Convert";
    private int _targetVkCode = VK_CONVERT;
    private int _intervalMs = 350;
    private bool _isPaused = false;

    // ダブルタップ検知用ステート
    private bool _isFirstKeyDown = false;
    private long _firstKeyUpTimestamp = 0;

    /// <summary>
    /// ダブルタップトリガーが検知されたときのイベント
    /// </summary>
    public event EventHandler? TriggerDetected;

    /// <summary>
    /// デバッグ・設定画面用：キーが押されたときのログ通知イベント
    /// </summary>
    public event Action<string>? KeyActivityLogged;

    public KeyboardHookService()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// キー監視を開始
    /// </summary>
    public void Start(AppSettings settings)
    {
        UpdateSettings(settings);

        if (_hookId == IntPtr.Zero)
        {
            // 低レベルフック (WH_KEYBOARD_LL) はグローバルフックのため、IntPtr.Zero で登録可能
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);

            if (_hookId == IntPtr.Zero)
            {
                // フォールバック: モジュールハンドルを取得して試行
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);

                if (_hookId == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    KeyActivityLogged?.Invoke($"❌ フック登録に失敗しました (エラーコード: {err})");
                    return;
                }
            }

            KeyActivityLogged?.Invoke("✅ キーフックが正常に開始されました。");
        }
    }

    /// <summary>
    /// 設定内容を更新
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        _triggerKeyType = settings.TriggerKeyType;
        _targetVkCode = settings.TriggerVirtualKey;
        _intervalMs = settings.DoubleTapIntervalMs;
        ResetState();
    }

    private void ResetState()
    {
        _isFirstKeyDown = false;
        _firstKeyUpTimestamp = 0;
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        ResetState();
    }

    public bool IsPaused => _isPaused;

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            KeyActivityLogged?.Invoke("キーフックを停止しました。");
        }
    }

    /// <summary>
    /// 押されたキーが設定された対象キーと一致するか判定
    /// </summary>
    private bool IsTargetKey(int vkCode)
    {
        return _triggerKeyType switch
        {
            "Convert" => vkCode == VK_CONVERT,
            "NonConvert" => vkCode == VK_NONCONVERT,
            "CtrlDouble" => vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL,
            "ShiftDouble" => vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT,
            "AltDouble" => vkCode is VK_MENU or VK_LMENU or VK_RMENU,
            _ => vkCode == _targetVkCode
        };
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_isPaused)
        {
            int message = wParam.ToInt32();
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)hookStruct.vkCode;

            bool isKeyDown = (message == WM_KEYDOWN || message == WM_SYSKEYDOWN);
            bool isKeyUp = (message == WM_KEYUP || message == WM_SYSKEYUP);

            if (IsTargetKey(vkCode))
            {
                long now = Stopwatch.GetTimestamp();

                if (isKeyDown)
                {
                    // 2回目のキー押下判定（1回目のKeyUpから指定時間以内か）
                    if (_firstKeyUpTimestamp > 0)
                    {
                        long elapsedMs = (long)((now - _firstKeyUpTimestamp) * 1000.0 / Stopwatch.Frequency);

                        if (elapsedMs <= _intervalMs)
                        {
                            // 🎯 ダブルタップ検知成功！
                            ResetState();
                            KeyActivityLogged?.Invoke($"🎯 ダブルタップ検知！ ({_triggerKeyType}, 間隔: {elapsedMs}ms)");

                            // トリガー発火
                            Task.Run(() =>
                            {
                                try
                                {
                                    TriggerDetected?.Invoke(this, EventArgs.Empty);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"トリガー実行例外: {ex.Message}");
                                }
                            });

                            // 変換キーなどの場合、2回目のキー入力を抑制してIME再変換や不要な動作を防ぐ
                            return (IntPtr)1;
                        }
                        else
                        {
                            // 時間切れのため、今回のKeyDownを新たな1回目とする
                            _isFirstKeyDown = true;
                            _firstKeyUpTimestamp = 0;
                        }
                    }
                    else
                    {
                        // 1回目のKeyDown
                        _isFirstKeyDown = true;
                    }
                }
                else if (isKeyUp)
                {
                    if (_isFirstKeyDown)
                    {
                        // 1回目の打鍵完了（KeyUp）
                        _isFirstKeyDown = false;
                        _firstKeyUpTimestamp = now;
                        KeyActivityLogged?.Invoke($"1回目タップ完了: {_triggerKeyType}");
                    }
                }
            }
            else
            {
                // 全く無関係なキーが押されたらダブルタップカウントをリセット
                // （ただし、修飾キーやIMEメッセージの場合は除外）
                if (isKeyDown && vkCode != 0xE5) // 0xE5: VK_PROCESSKEY (IMEイベント)
                {
                    ResetState();
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
