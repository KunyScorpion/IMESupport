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
    private const byte VK_LEFT = 0x25;
    private const byte VK_RIGHT = 0x27;
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
            // まずはユーザーが既に選択しているテキストのコピーを試みる
            string selectedText = await TryCopyTextAsync();

            // もし未選択かつ自動選択が有効な場合、マウスのトリプルクリックと同様に「現在の1段落」のみを選択
            if (string.IsNullOrWhiteSpace(selectedText) && settings.AutoSelectLineWhenEmpty)
            {
                StatusLogged?.Invoke("未選択を検知: 現在の1段落（折り返し含む）を自動選択中...");
                selectedText = await TrySelectCurrentParagraphAsync();
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
    /// マウスのトリプルクリックと同様に、現在の段落（直前の改行から現在位置まで）のみを自動選択する
    /// </summary>
    private static async Task<string> TrySelectCurrentParagraphAsync()
    {
        // 1. まず現在の折り返し行の行頭まで選択 (Shift + Home)
        SendShiftHome();
        await Task.Delay(60);
        string currentText = await TryCopyTextAsync();

        if (string.IsNullOrEmpty(currentText))
        {
            return string.Empty;
        }

        // 2. 折り返しを遡って段落の先頭（直前の改行またはテキスト先頭）まで拡張（最大15行分遡る）
        for (int i = 0; i < 15; i++)
        {
            // 1文字左へ選択を広げてみる (Shift + Left)
            SendShiftLeft();
            await Task.Delay(35);
            string expandedText = await TryCopyTextAsync();

            // 長さが変わらない（これ以上左に行けない＝テキストの最先頭に達した）
            if (expandedText.Length == currentText.Length)
            {
                break;
            }

            // 新しく含まれた先頭文字をチェック
            char firstChar = expandedText[0];
            if (firstChar == '\n' || firstChar == '\r')
            {
                // 直前の改行に達した！
                // 改行自体は選択範囲から外すため、1文字右に戻す (Shift + Right)
                SendShiftRight();
                await Task.Delay(35);
                return currentText;
            }

            // 改行ではない ＝ これは画面端での自動折り返し（ワードラップ）！
            // その折り返し行の最先頭まで選択を伸ばす (Shift + Home)
            SendShiftHome();
            await Task.Delay(35);
            string lineText = await TryCopyTextAsync();

            if (string.IsNullOrEmpty(lineText) || lineText.Length <= expandedText.Length)
            {
                currentText = expandedText;
                break;
            }
            currentText = lineText;
        }

        return currentText;
    }

    /// <summary>
    /// Ctrl+Cを送信し、新しくコピーされたテキストのみを取得する
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

        // コピーされなかった場合は空を返す
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

    private static void SendShiftLeft()
    {
        keybd_event(VK_LSHIFT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);

        keybd_event(VK_LEFT, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(15);

        keybd_event(VK_LEFT, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

        keybd_event(VK_LSHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);
    }

    private static void SendShiftRight()
    {
        keybd_event(VK_LSHIFT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);

        keybd_event(VK_RIGHT, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(15);

        keybd_event(VK_RIGHT, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

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
