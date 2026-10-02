using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using IMESupport.Models;
using IMESupport.Services;

namespace IMESupport.UI;

/// <summary>
/// 設定画面のロジック
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly GeminiService _geminiService;
    private readonly KeyboardHookService _hookService;
    private bool _isApiKeyVisible = false;
    private bool _isInitializing = true;

    public SettingsWindow(AppSettings settings, GeminiService geminiService, KeyboardHookService hookService)
    {
        InitializeComponent();
        _settings = settings;
        _geminiService = geminiService;
        _hookService = hookService;

        LoadSettingsToUi();

        // キー検知ログのライブ表示
        _hookService.KeyActivityLogged += OnKeyActivityLogged;

        _isInitializing = false;
    }

    private void OnKeyActivityLogged(string message)
    {
        Dispatcher.Invoke(() =>
        {
            if (TxtLiveKeyMonitor != null)
            {
                TxtLiveKeyMonitor.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
            }
        });
    }

    private void LoadSettingsToUi()
    {
        PbApiKey.Password = _settings.ApiKey;
        TbApiKeyVisible.Text = _settings.ApiKey;

        CmbModel.Text = _settings.Model;
        SldInterval.Value = _settings.DoubleTapIntervalMs;

        // トリガーキー選択の同期
        for (int i = 0; i < CmbTriggerKey.Items.Count; i++)
        {
            if (CmbTriggerKey.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == _settings.TriggerKeyType)
            {
                CmbTriggerKey.SelectedIndex = i;
                break;
            }
        }
        if (CmbTriggerKey.SelectedIndex < 0)
        {
            CmbTriggerKey.SelectedIndex = 0;
        }

        ChkAutoSelectLine.IsChecked = _settings.AutoSelectLineWhenEmpty;
        ChkShowNotification.IsChecked = _settings.ShowNotification;
        ChkRestoreClipboard.IsChecked = _settings.RestoreClipboard;
        ChkAutoStart.IsChecked = StartupManager.IsStartupEnabled();

        TbPrompt.Text = _settings.SystemPrompt;
    }

    private void SaveUiToSettings()
    {
        _settings.ApiKey = _isApiKeyVisible ? TbApiKeyVisible.Text.Trim() : PbApiKey.Password.Trim();
        _settings.Model = string.IsNullOrWhiteSpace(CmbModel.Text) ? "gemini-3.5-flash-lite" : CmbModel.Text.Trim();
        _settings.DoubleTapIntervalMs = (int)SldInterval.Value;

        if (CmbTriggerKey.SelectedItem is ComboBoxItem selectedItem)
        {
            string tag = selectedItem.Tag?.ToString() ?? "CtrlDouble";
            _settings.TriggerKeyType = tag;
            _settings.TriggerVirtualKey = tag switch
            {
                "NonConvert" => KeyboardHookService.VK_NONCONVERT,
                "CtrlDouble" => KeyboardHookService.VK_CONTROL,
                "ShiftDouble" => KeyboardHookService.VK_SHIFT,
                "AltDouble" => KeyboardHookService.VK_MENU,
                _ => KeyboardHookService.VK_CONVERT
            };
        }

        _settings.AutoSelectLineWhenEmpty = ChkAutoSelectLine.IsChecked ?? true;
        _settings.ShowNotification = ChkShowNotification.IsChecked ?? true;
        _settings.RestoreClipboard = ChkRestoreClipboard.IsChecked ?? true;
        _settings.SystemPrompt = TbPrompt.Text;

        bool autoStart = ChkAutoStart.IsChecked ?? false;
        _settings.AutoStart = autoStart;
        StartupManager.SetStartup(autoStart);

        _settings.Save();
        _hookService.UpdateSettings(_settings);
    }

    private void CmbTriggerKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        SaveUiToSettings();
    }

    private void SldInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        _settings.DoubleTapIntervalMs = (int)e.NewValue;
        _hookService.UpdateSettings(_settings);
    }

    private void BtnToggleApiKey_Click(object sender, RoutedEventArgs e)
    {
        _isApiKeyVisible = !_isApiKeyVisible;
        if (_isApiKeyVisible)
        {
            TbApiKeyVisible.Text = PbApiKey.Password;
            PbApiKey.Visibility = Visibility.Collapsed;
            TbApiKeyVisible.Visibility = Visibility.Visible;
            BtnToggleApiKey.Content = "隠す";
        }
        else
        {
            PbApiKey.Password = TbApiKeyVisible.Text;
            TbApiKeyVisible.Visibility = Visibility.Collapsed;
            PbApiKey.Visibility = Visibility.Visible;
            BtnToggleApiKey.Content = "表示";
        }
    }

    private async void BtnTestApi_Click(object sender, RoutedEventArgs e)
    {
        string apiKey = _isApiKeyVisible ? TbApiKeyVisible.Text.Trim() : PbApiKey.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            TxtApiStatus.Text = "⚠️ APIキーを入力してください。";
            TxtApiStatus.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            return;
        }

        BtnTestApi.IsEnabled = false;
        TxtApiStatus.Text = "⏳ 接続テスト中...";
        TxtApiStatus.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));

        var tempSettings = new AppSettings
        {
            ApiKey = apiKey,
            Model = string.IsNullOrWhiteSpace(CmbModel.Text) ? "gemini-3.5-flash-lite" : CmbModel.Text.Trim(),
            SystemPrompt = TbPrompt.Text
        };

        var result = await _geminiService.ProofreadAsync("テスト確認", tempSettings);

        if (result.Success)
        {
            TxtApiStatus.Text = "✅ 接続成功！Gemini APIと正常に通信できました。";
            TxtApiStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else
        {
            TxtApiStatus.Text = $"❌ エラー: {result.ErrorMessage}";
            TxtApiStatus.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
        }

        BtnTestApi.IsEnabled = true;
    }

    private async void BtnRunTest_Click(object sender, RoutedEventArgs e)
    {
        string apiKey = _isApiKeyVisible ? TbApiKeyVisible.Text.Trim() : PbApiKey.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("推敲テストを行うには、まずGemini APIキーを入力してください。", "APIキーが必要です", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string inputText = TbTestInput.Text;
        if (string.IsNullOrWhiteSpace(inputText))
        {
            MessageBox.Show("テスト文章を入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnRunTest.IsEnabled = false;
        TbTestOutput.Text = "AIが推敲・修正中...";

        var tempSettings = new AppSettings
        {
            ApiKey = apiKey,
            Model = string.IsNullOrWhiteSpace(CmbModel.Text) ? "gemini-3.5-flash-lite" : CmbModel.Text.Trim(),
            SystemPrompt = TbPrompt.Text
        };

        var result = await _geminiService.ProofreadAsync(inputText, tempSettings);

        if (result.Success)
        {
            TbTestOutput.Text = result.CorrectedText;
        }
        else
        {
            TbTestOutput.Text = $"エラー: {result.ErrorMessage}";
        }

        BtnRunTest.IsEnabled = true;
    }

    private void BtnResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("プロンプトを初期設定に戻しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            TbPrompt.Text = AppSettings.DefaultPrompt;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        Hide();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        SaveUiToSettings();
        Hide();
    }
}
