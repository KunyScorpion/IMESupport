using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using IMESupport.Models;

namespace IMESupport.Services;

/// <summary>
/// クリップボードとキー入力模擬によるテキスト取得・自動置換サービス
/// </summary>
public class TextReplacementService
{
    private readonly GeminiService _geminiService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<string>? ProcessingStarted;
    public event Action<CorrectionResult>? ProcessingCompleted;
    public event Action<string>? StatusLogged;

    public TextReplacementService(GeminiService geminiService)
    {
        _geminiService = geminiService;
    }

    #region Win32 API 定義

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const byte VK_LCONTROL = 0xA2;
    private const byte VK_LSHIFT = 0xA0;
    private const byte VK_HOME = 0x24;
    private const byte VK_UP = 0x26;
    private const byte VK_C = 0x43;
    private const byte VK_V = 0x56;

    #endregion

    /// <summary>
    /// 推敲および置換ワークフローを実行
    /// </summary>
    public async Task ExecuteWorkflowAsync(AppSettings settings)
    {
        if (!await _lock.WaitAsync(0))
        {
            StatusLogged?.Invoke("前回の処理が実行中のためスキップしました。");
            return;
        }

        try
        {
            StatusLogged?.Invoke("トリガー受信: テキスト取得を開始します...");

            // 1. 物理キーが離されるのを待つ
            await WaitForModifierKeysUpAsync();

            // 2. 直前のクリップボード内容を退避
            IDataObject? originalClipboard = null;
            if (settings.RestoreClipboard)
            {
                originalClipboard = await GetClipboardDataObjectSafeAsync();
            }

            // 3. テキスト取得を試行
            // まずは選択されているテキストのコピーを試みる
            string selectedText = await TryCopyTextAsync();

            // もし選択テキストが空かつ自動選択が有効な場合、折り返しを考慮して論理行全体（段落先頭まで）を選択して再試行
            if (string.IsNullOrWhiteSpace(selectedText) && settings.AutoSelectLineWhenEmpty)
            {
                StatusLogged?.Invoke("未選択を検知: 折り返しを考慮して行全体を自動選択中...");
                SendSmartLineSelect();
                await Task.Delay(90);
                selectedText = await TryCopyTextAsync();

                // フォールバック: 万一特殊なエディタで取れなかった場合は Shift+Home 単体でも試行
                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    SendShiftHome();
                    await Task.Delay(60);
                    selectedText = await TryCopyTextAsync();
                }
            }

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                StatusLogged?.Invoke("⚠️ 修正対象のテキストを取得できませんでした。");
                if (settings.RestoreClipboard && originalClipboard != null)
                {
                    await RestoreClipboardSafeAsync(originalClipboard);
                }
                return;
            }

            StatusLogged?.Invoke($"テキスト取得成功 ({selectedText.Length}文字): 「{Truncate(selectedText, 25)}」");
            ProcessingStarted?.Invoke(selectedText);

            // 4. Gemini API に推敲リクエスト
            StatusLogged?.Invoke($"Gemini API ({settings.Model}) に推敲リクエスト送信中...");
            var result = await _geminiService.ProofreadAsync(selectedText, settings);

            if (!result.Success)
            {
                StatusLogged?.Invoke($"❌ 推敲エラー: {result.ErrorMessage}");
                if (settings.RestoreClipboard && originalClipboard != null)
                {
                    await RestoreClipboardSafeAsync(originalClipboard);
                }
                ProcessingCompleted?.Invoke(result);
                return;
            }

            // 5. 修正が必要な場合、置換を実行
            if (result.HasChanged)
            {
                StatusLogged?.Invoke($"✨ 修正検知: 「{Truncate(result.CorrectedText, 25)}」へ置換します...");

                // クリップボードに修正後テキストを設定
                bool setOk = await SetClipboardTextSafeAsync(result.CorrectedText);
                if (setOk)
                {
                    await Task.Delay(50);
                    // Ctrl + V で貼り付け
                    SendCtrlKey(VK_V);
                    // アプリケーションが貼り付けを完了するまで待機
                    await Task.Delay(200);
                }
            }
            else
            {
                StatusLogged?.Invoke("推敲完了: 修正の必要はありません（すでに正しい文章です）。");
            }

            // 6. 元のクリップボードを復元（設定されている場合）
            if (settings.RestoreClipboard && originalClipboard != null)
            {
                await Task.Delay(100);
                await RestoreClipboardSafeAsync(originalClipboard);
            }

            ProcessingCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ワークフロー例外: {ex.Message}");
            StatusLogged?.Invoke($"システムエラー: {ex.Message}");
            ProcessingCompleted?.Invoke(new CorrectionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Ctrl+Cを送信し、新しくコピーされたテキストのみを取得する
    /// （古いクリップボード内容を誤って取得しないよう厳密にシーケンス番号をチェック）
    /// </summary>
    private static async Task<string> TryCopyTextAsync()
    {
        uint seqBefore = GetClipboardSequenceNumber();

        // Ctrl + C 送信
        SendCtrlKey(VK_C);

        // クリップボードのシーケンス番号変化を監視 (最大300ms)
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(25);
            uint seqAfter = GetClipboardSequenceNumber();
            if (seqAfter != seqBefore)
            {
                // 新しくコピーが完了した！
                string text = await GetClipboardTextSafeAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        // コピーされなかった場合は絶対に古いクリップボードを返さず空を返す
        return string.Empty;
    }

    /// <summary>
    /// 物理的な修飾キー（Ctrl, Shift, Alt等）が離されるのを待機
    /// </summary>
    private static async Task WaitForModifierKeysUpAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            bool isCtrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool isShiftDown = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            bool isAltDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;

            if (!isCtrlDown && !isShiftDown && !isAltDown)
            {
                break;
            }
            await Task.Delay(15);
        }
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length > max ? oneLine.Substring(0, max) + "..." : oneLine;
    }

    #region キーストローク送信 (keybd_event with micro-delays)

    private static void SendCtrlKey(byte key)
    {
        keybd_event(VK_LCONTROL, 0, 0, UIntPtr.Zero);
        Thread.Sleep(15);

        keybd_event(key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(20);

        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(15);

        keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);
    }

    /// <summary>
    /// 折り返しを考慮して論理行全体（直前の改行から現在位置まで）を自動選択する
    /// </summary>
    private static void SendSmartLineSelect()
    {
        // 1. Ctrl + Shift + Up (段落・論理行の先頭行まで選択を拡張)
        keybd_event(VK_LCONTROL, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);
        keybd_event(VK_LSHIFT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);
        keybd_event(VK_UP, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(20);
        keybd_event(VK_UP, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

        // 2. 続けて Shift + Home (その行の最先頭に合わせる)
        keybd_event(VK_HOME, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(20);
        keybd_event(VK_HOME, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

        keybd_event(VK_LSHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);
        keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);
    }

    private static void SendShiftHome()
    {
        keybd_event(VK_LSHIFT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(15);

        keybd_event(VK_HOME, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(20);

        keybd_event(VK_HOME, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(15);

        keybd_event(VK_LSHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);
    }

    #endregion

    #region クリップボード安全操作 (STAスレッド実行 & リトライ)

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static async Task<string> GetClipboardTextSafeAsync()
    {
        return await RunOnStaThreadAsync(() =>
        {
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        return Clipboard.GetText();
                    }
                }
                catch
                {
                    Thread.Sleep(20);
                }
            }
            return string.Empty;
        });
    }

    private static async Task<bool> SetClipboardTextSafeAsync(string text)
    {
        return await RunOnStaThreadAsync(() =>
        {
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch
                {
                    Thread.Sleep(20);
                }
            }
            return false;
        });
    }

    private static async Task<IDataObject?> GetClipboardDataObjectSafeAsync()
    {
        return await RunOnStaThreadAsync(() =>
        {
            try
            {
                return Clipboard.GetDataObject();
            }
            catch
            {
                return null;
            }
        });
    }

    private static async Task RestoreClipboardSafeAsync(IDataObject original)
    {
        await RunOnStaThreadAsync(() =>
        {
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    Clipboard.SetDataObject(original, true);
                    return true;
                }
                catch
                {
                    Thread.Sleep(20);
                }
            }
            return false;
        });
    }

    #endregion
}
