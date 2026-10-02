using System.Threading;
using System.Windows;
using System.Windows.Forms;
using IMESupport.Models;
using IMESupport.Services;
using IMESupport.UI;

namespace IMESupport;

/// <summary>
/// アプリケーションエントリーポイント・ライフサイクル管理
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "IMESupport_Unique_Application_Mutex_2026";

    private AppSettings _settings = null!;
    private GeminiService _geminiService = null!;
    private TextReplacementService _replacementService = null!;
    private KeyboardHookService _hookService = null!;
    private TrayIconManager _trayManager = null!;

    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;

    private static void LogDebug(string message)
    {
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IMESupport", "debug.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, args) =>
        {
            LogDebug($"[DispatcherUnhandledException] {args.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogDebug($"[AppDomain UnhandledException] {args.ExceptionObject}");
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LogDebug("Application_Startup 開始");

        // 1. 多重起動防止
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        LogDebug($"Mutex チェック: createdNew = {createdNew}");
        if (!createdNew)
        {
            LogDebug("既に実行中のため終了します。");
            MessageBox.Show(
                "IMESupport は既に起動しています。タスクトレイ（右下の通知領域）の青い「AI」アイコンを確認してください。",
                "既に実行中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 2. 設定読み込み
        _settings = AppSettings.Load();

        // 3. サービス初期化
        _geminiService = new GeminiService();
        _replacementService = new TextReplacementService(_geminiService);
        _hookService = new KeyboardHookService();

        // 4. イベント配線
        _hookService.TriggerDetected += OnHookTriggerDetected;
        _replacementService.ProcessingStarted += OnProcessingStarted;
        _replacementService.ProcessingCompleted += OnProcessingCompleted;
        _replacementService.StatusLogged += OnStatusLogged;

        // 5. タスクトレイアイコン初期化
        _trayManager = new TrayIconManager(
            _settings,
            _hookService,
            OpenSettingsWindow,
            OpenHistoryWindow);

        // 6. キー監視開始
        _hookService.Start(_settings);

        // 7. 初回起動チェック（APIキー未設定の場合は設定画面を表示）
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            OpenSettingsWindow();
            _trayManager.ShowNotification(
                "IMESupport へようこそ！",
                "最初にご自身の Gemini API キーを入力・保存してください。",
                ToolTipIcon.Info);
        }
        else
        {
            _trayManager.ShowNotification(
                "IMESupport 常駐中",
                $"{_settings.TriggerKeyType} 2回連続押しでAI推敲が実行されます。",
                ToolTipIcon.Info);
        }

        _hookService.KeyActivityLogged += msg => LogDebug($"[KeyHook] {msg}");
        LogDebug("Application_Startup 正常完了。イベントループ待機中。");
    }

    private void OnHookTriggerDetected(object? sender, EventArgs e)
    {
        LogDebug("OnHookTriggerDetected 受信。ワークフロー開始。");
        // バックグラウンドで推敲・置換ワークフローを実行
        _ = _replacementService.ExecuteWorkflowAsync(_settings);
    }

    private void OnProcessingStarted(string targetText)
    {
        LogDebug($"推敲処理開始: {targetText}");
    }

    private void OnStatusLogged(string message)
    {
        LogDebug($"[TextService] {message}");
        if (_settings.ShowNotification && (message.StartsWith("⚠️") || message.StartsWith("❌")))
        {
            Dispatcher.Invoke(() =>
            {
                _trayManager.ShowNotification("IMESupport", message, ToolTipIcon.Warning);
            });
        }
    }

    private void OnProcessingCompleted(CorrectionResult result)
    {
        LogDebug($"推敲処理完了: Success={result.Success}, HasChanged={result.HasChanged}, Original='{result.OriginalText}', Corrected='{result.CorrectedText}'");
        Dispatcher.Invoke(() =>
        {
            // 履歴に記録
            HistoryManager.Add(new HistoryItem
            {
                OriginalText = result.OriginalText,
                CorrectedText = result.CorrectedText,
                Status = result.Success ? (result.HasChanged ? "修正完了" : "修正不要") : "エラー",
                Model = _settings.Model
            });

            if (_settings.ShowNotification)
            {
                if (!result.Success)
                {
                    _trayManager.ShowNotification(
                        "推敲エラー",
                        result.ErrorMessage ?? "処理中にエラーが発生しました。",
                        ToolTipIcon.Warning);
                }
                else if (result.HasChanged)
                {
                    string message = $"「{Truncate(result.OriginalText, 30)}」\n→「{Truncate(result.CorrectedText, 30)}」";
                    _trayManager.ShowNotification("✨ AI修正完了", message, ToolTipIcon.Info);
                }
            }

            if (result.Success && result.HasChanged && _settings.PlaySoundOnComplete)
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        });
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > max ? text.Substring(0, max) + "..." : text;
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settings, _geminiService, _hookService);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
    }

    private void OpenHistoryWindow()
    {
        if (_historyWindow == null || !_historyWindow.IsLoaded)
        {
            _historyWindow = new HistoryWindow();
        }

        _historyWindow.Show();
        _historyWindow.Activate();
        if (_historyWindow.WindowState == WindowState.Minimized)
        {
            _historyWindow.WindowState = WindowState.Normal;
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        LogDebug($"Application_Exit 呼び出し: ExitCode = {e.ApplicationExitCode}");
        _hookService?.Dispose();
        _trayManager?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
