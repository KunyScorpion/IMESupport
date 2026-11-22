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

    /// <summary>
    /// ポップアップ候補表示モード時にユーザーへ候補を提示し選択結果を受け取るデリゲート（UI層から注入）
    /// </summary>
    public Func<string, IReadOnlyList<string>, ScreenAnchorRect?, Task<string?>>? CandidateSelector { get; set; }

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
            IntPtr targetHwnd = GetForegroundWindow();

            // 1. 物理キーが離されるのを待つ
            await WaitForModifierKeysUpAsync();

            // 2. 直前のクリップボード内容を退避（実データをメモリへディープコピー）
            ClipboardSnapshot? originalClipboard = null;
            if (settings.RestoreClipboard)
            {
                originalClipboard = await BackupClipboardSafeAsync();
            }

            bool pasted = false;
            try
            {
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
                    return;
                }

                StatusLogged?.Invoke($"テキスト取得成功 ({selectedText.Length}文字): 「{Truncate(selectedText, 25)}」");
                ProcessingStarted?.Invoke(selectedText);

                // 4. キーストローク逆変換解析と直前文脈の取得（レイテンシ0ms）
                var analysis = KeystrokeAnalyzer.BuildAnalysisHint(selectedText);
                if (analysis.HasAnomaly)
                {
                    StatusLogged?.Invoke($"🔍 ローマ字崩壊ヒント生成: 読み=「{Truncate(analysis.ReadingText, 20)}」, 推定打鍵=「{Truncate(analysis.EstimatedRomaji, 25)}」");
                }
                string recentContext = HistoryManager.GetRecentContext(maxCount: 3, withinMinutes: 5);

                // 5. Gemini API に推敲リクエスト（ポップアップモード／ダイレクト置換モード分岐）
                CorrectionResult result;
                bool needFocusRestore = false;

                if (settings.EnableCandidatePopup && CandidateSelector != null)
                {
                    ScreenAnchorRect? anchorRect = await CaretPositionService.TryGetCaretAnchorRectAsync(targetHwnd);
                    StatusLogged?.Invoke(anchorRect is { IsValid: true } rect
                        ? $"📍 キャレット位置取得成功 ({rect.Source}): ({rect.Left:F0}, {rect.Top:F0}) {rect.Width:F0}x{rect.Height:F0}"
                        : "📍 キャレット位置を取得できないためマウスカーソル位置へ表示します。");

                    StatusLogged?.Invoke($"Gemini API ({settings.Model}) に候補取得リクエスト送信中...");
                    var candidateResult = await _geminiService.GetCandidatesAsync(selectedText, settings, analysis, recentContext);

                    if (!candidateResult.Success)
                    {
                        StatusLogged?.Invoke($"❌ 推敲エラー: {candidateResult.ErrorMessage}");
                        ProcessingCompleted?.Invoke(new CorrectionResult
                        {
                            Success = false,
                            OriginalText = selectedText,
                            CorrectedText = selectedText,
                            ErrorMessage = candidateResult.ErrorMessage
                        });
                        return;
                    }

                    var candidates = candidateResult.Candidates;
                    if (candidates.Count == 0 ||
                        (candidates.Count == 1 && string.Equals(candidates[0], selectedText, StringComparison.Ordinal)))
                    {
                        result = new CorrectionResult
                        {
                            Success = true,
                            OriginalText = selectedText,
                            CorrectedText = selectedText
                        };
                    }
                    else
                    {
                        StatusLogged?.Invoke($"候補提示 ({candidates.Count}件): ユーザーの選択を待機中...");
                        string? chosen = await CandidateSelector(selectedText, candidates, anchorRect);

                        if (chosen == null)
                        {
                            StatusLogged?.Invoke("候補選択がキャンセルされました。");
                            ProcessingCompleted?.Invoke(new CorrectionResult
                            {
                                Success = true,
                                IsCancelled = true,
                                OriginalText = selectedText,
                                CorrectedText = selectedText
                            });
                            return;
                        }

                        result = new CorrectionResult
                        {
                            Success = true,
                            OriginalText = selectedText,
                            CorrectedText = chosen
                        };
                        needFocusRestore = true;
                    }
                }
                else
                {
                    StatusLogged?.Invoke($"Gemini API ({settings.Model}) に推敲リクエスト送信中...");
                    result = await _geminiService.ProofreadAsync(selectedText, settings, analysis, recentContext);

                    if (!result.Success)
                    {
                        StatusLogged?.Invoke($"❌ 推敲エラー: {result.ErrorMessage}");
                        ProcessingCompleted?.Invoke(result);
                        return;
                    }
                }

                // 6. 修正が必要な場合、置換を実行
                if (result.HasChanged)
                {
                    StatusLogged?.Invoke($"✨ 修正検知: 「{Truncate(result.CorrectedText, 25)}」へ置換します...");

                    // クリップボードに修正後テキストを設定
                    bool setOk = await SetClipboardTextSafeAsync(result.CorrectedText);
                    if (setOk)
                    {
                        if (needFocusRestore)
                        {
                            await WaitForModifierKeysUpAsync();
                            await RestoreForegroundWindowAsync(targetHwnd);
                        }

                        await Task.Delay(50);
                        // Ctrl + V で貼り付け
                        SendCtrlKey(VK_V);
                        pasted = true;
                        // アプリケーションが貼り付けを完了するまで待機
                        await Task.Delay(200);
                    }
                }
                else
                {
                    StatusLogged?.Invoke("推敲完了: 修正の必要はありません（すでに正しい文章です）。");
                }

                ProcessingCompleted?.Invoke(result);
            }
            finally
            {
                // 7. 元のクリップボードを確実に復元（正常終了・キャンセル・エラー問わず実行）
                if (settings.RestoreClipboard && originalClipboard != null)
                {
                    if (pasted)
                    {
                        await Task.Delay(100);
                    }
                    await RestoreClipboardSafeAsync(originalClipboard);
                }
            }
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
        await Task.Delay(35);
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
            await Task.Delay(20);
            string expandedText = await TryCopyTextAsync();

            // 長さが変わらない（これ以上左に行けない＝テキストの最先頭に達した）
            if (string.IsNullOrEmpty(expandedText) || expandedText.Length == currentText.Length)
            {
                break;
            }

            // 新しく含まれた先頭文字をチェック：改行であれば本来の行頭だったため1文字戻して即座に終了
            char firstChar = expandedText[0];
            if (firstChar == '\r' || firstChar == '\n')
            {
                SendShiftRight();
                await Task.Delay(20);
                return currentText;
            }

            // 先頭が改行文字でない＝自動折り返し（ワードラップ）の途中であるため、前の折り返し行の先頭まで一気に選択を広げる
            SendShiftHome();
            await Task.Delay(25);
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
    /// ポップアップ終了後、元の入力対象ウィンドウへフォーカスを戻して待機する
    /// </summary>
    private static async Task RestoreForegroundWindowAsync(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero || GetForegroundWindow() == targetHwnd)
        {
            return;
        }

        SetForegroundWindow(targetHwnd);

        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(20);
            if (GetForegroundWindow() == targetHwnd)
            {
                return;
            }

            if (i == 5)
            {
                SetForegroundWindow(targetHwnd);
            }
        }
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

    #region クリップボード安全操作 (メインSTAスレッド優先 & 実データスナップショット退避)

    /// <summary>
    /// クリップボードの実データをメモリ上に保持するスナップショットクラス
    /// </summary>
    public sealed class ClipboardSnapshot
    {
        public string? Text { get; set; }
        public System.Windows.Media.Imaging.BitmapSource? Image { get; set; }
        public System.Collections.Specialized.StringCollection? FileDropList { get; set; }
        public Dictionary<string, object> ExtraFormats { get; } = new();

        public bool IsEmpty =>
            string.IsNullOrEmpty(Text) &&
            Image == null &&
            (FileDropList == null || FileDropList.Count == 0) &&
            ExtraFormats.Count == 0;
    }

    // 追加で保持を試みるテキスト系リッチフォーマット
    private static readonly string[] SafeExtraFormats =
    [
        DataFormats.Html,
        DataFormats.Rtf,
        DataFormats.CommaSeparatedValue
    ];

    /// <summary>
    /// WPFのメインSTAスレッド（Dispatcher）が存在する場合は優先利用し、
    /// 単体テスト環境など Application.Current が無い場合のみ専用STAスレッドで実行する
    /// </summary>
    private static Task<T> RunOnStaThreadAsync<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            if (dispatcher.CheckAccess())
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }
            return dispatcher.InvokeAsync(func).Task;
        }

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

    /// <summary>
    /// Windows クリップボード履歴 (Win+V) やクラウド同期への登録を抑止するフラグを付与する
    /// </summary>
    private static void ApplyClipboardHistoryExclusionFlags(System.Windows.DataObject dataObj)
    {
        try
        {
            dataObj.SetData("ExcludeClipboardContentFromMonitorProcessing", true);
            dataObj.SetData("CanIncludeInClipboardHistory", 0);
            dataObj.SetData("CanUploadToCloudClipboard", 0);
        }
        catch
        {
            // OS環境によりフォーマット登録が失敗しても本体処理には影響させない
        }
    }

    internal static async Task<string> GetClipboardTextSafeAsync()
    {
        for (int retry = 0; retry < 5; retry++)
        {
            string? text = await RunOnStaThreadAsync<string?>(() =>
            {
                try
                {
                    return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                }
                catch
                {
                    return null;
                }
            });

            if (text != null)
            {
                return text;
            }
            await Task.Delay(20);
        }
        return string.Empty;
    }

    internal static async Task<bool> SetClipboardTextSafeAsync(string text)
    {
        for (int retry = 0; retry < 5; retry++)
        {
            bool ok = await RunOnStaThreadAsync(() =>
            {
                try
                {
                    var dataObj = new System.Windows.DataObject();
                    dataObj.SetText(text);
                    ApplyClipboardHistoryExclusionFlags(dataObj);
                    Clipboard.SetDataObject(dataObj, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            });

            if (ok)
            {
                return true;
            }
            await Task.Delay(20);
        }
        return false;
    }

    /// <summary>
    /// 現在のクリップボードから実データをメモリへ吸い出してスナップショットを作成する
    /// </summary>
    internal static async Task<ClipboardSnapshot?> BackupClipboardSafeAsync()
    {
        for (int retry = 0; retry < 5; retry++)
        {
            var snapshot = await RunOnStaThreadAsync<ClipboardSnapshot?>(() =>
            {
                try
                {
                    var snap = new ClipboardSnapshot();

                    if (Clipboard.ContainsText())
                    {
                        snap.Text = Clipboard.GetText();
                    }

                    if (Clipboard.ContainsImage())
                    {
                        try
                        {
                            var img = Clipboard.GetImage();
                            if (img != null)
                            {
                                if (img.CanFreeze) img.Freeze();
                                snap.Image = img;
                            }
                        }
                        catch { }
                    }

                    if (Clipboard.ContainsFileDropList())
                    {
                        try
                        {
                            var files = Clipboard.GetFileDropList();
                            if (files != null && files.Count > 0)
                            {
                                snap.FileDropList = files;
                            }
                        }
                        catch { }
                    }

                    IDataObject? current = Clipboard.GetDataObject();
                    if (current != null)
                    {
                        foreach (string format in SafeExtraFormats)
                        {
                            try
                            {
                                if (current.GetDataPresent(format, false))
                                {
                                    object? data = current.GetData(format, false);
                                    if (data != null)
                                    {
                                        snap.ExtraFormats[format] = data;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    return snap;
                }
                catch
                {
                    return null;
                }
            });

            if (snapshot != null)
            {
                return snapshot;
            }
            await Task.Delay(20);
        }
        return null;
    }

    /// <summary>
    /// 退避したスナップショットから新しい DataObject を構築してクリップボードへ復元する
    /// </summary>
    internal static async Task<bool> RestoreClipboardSafeAsync(ClipboardSnapshot snapshot)
    {
        for (int retry = 0; retry < 5; retry++)
        {
            bool restored = await RunOnStaThreadAsync(() =>
            {
                try
                {
                    if (snapshot.IsEmpty)
                    {
                        Clipboard.Clear();
                        return true;
                    }

                    var dataObj = new System.Windows.DataObject();

                    if (!string.IsNullOrEmpty(snapshot.Text))
                    {
                        dataObj.SetText(snapshot.Text);
                    }

                    if (snapshot.Image != null)
                    {
                        dataObj.SetImage(snapshot.Image);
                    }

                    if (snapshot.FileDropList != null && snapshot.FileDropList.Count > 0)
                    {
                        dataObj.SetFileDropList(snapshot.FileDropList);
                    }

                    foreach (var kvp in snapshot.ExtraFormats)
                    {
                        try
                        {
                            dataObj.SetData(kvp.Key, kvp.Value);
                        }
                        catch { }
                    }

                    // 復元時の再セットでもクリップボード履歴が重複登録されないようにする
                    ApplyClipboardHistoryExclusionFlags(dataObj);

                    Clipboard.SetDataObject(dataObj, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            });

            if (restored)
            {
                return true;
            }
            await Task.Delay(30);
        }
        return false;
    }

    #endregion
}
