using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using IMESupport.Models;
using IMESupport.Services;

namespace IMESupport.UI;

/// <summary>
/// タスクトレイ（NotifyIcon）常駐管理クラス
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private readonly KeyboardHookService _hookService;
    private readonly Action _openSettingsAction;
    private readonly Action _openHistoryAction;

    private ToolStripMenuItem? _pauseMenuItem;
    private ToolStripMenuItem? _startupMenuItem;

    public TrayIconManager(
        AppSettings settings,
        KeyboardHookService hookService,
        Action openSettingsAction,
        Action openHistoryAction)
    {
        _settings = settings;
        _hookService = hookService;
        _openSettingsAction = openSettingsAction;
        _openHistoryAction = openHistoryAction;

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateAppIcon(Color.FromArgb(59, 130, 246)), // 鮮やかな青アイコン
            Visible = true,
            Text = "IMESupport (AI日本語入力支援 - 変換キー2回)"
        };

        InitializeContextMenu();

        _notifyIcon.DoubleClick += (s, e) => _openSettingsAction();
    }

    private void InitializeContextMenu()
    {
        var contextMenu = new ContextMenuStrip();

        // タイトル
        var titleItem = new ToolStripMenuItem("IMESupport (AI日本語入力支援)")
        {
            Enabled = false,
            Font = new Font(contextMenu.Font, System.Drawing.FontStyle.Bold)
        };
        contextMenu.Items.Add(titleItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        // 設定
        var settingsItem = new ToolStripMenuItem("⚙️ 設定(&S)...", null, (s, e) => _openSettingsAction());
        contextMenu.Items.Add(settingsItem);

        // 履歴
        var historyItem = new ToolStripMenuItem("📜 推敲履歴(&H)...", null, (s, e) => _openHistoryAction());
        contextMenu.Items.Add(historyItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        // 一時停止
        _pauseMenuItem = new ToolStripMenuItem("⏸️ 機能を一時停止(&P)", null, (s, e) =>
        {
            bool newPaused = !_hookService.IsPaused;
            _hookService.SetPaused(newPaused);
            UpdatePauseState(newPaused);
        });
        _pauseMenuItem.Checked = _hookService.IsPaused;
        contextMenu.Items.Add(_pauseMenuItem);

        // 通知トグル
        var notificationMenuItem = new ToolStripMenuItem("🔔 デスクトップ通知を表示(&N)", null, (s, e) =>
        {
            if (s is ToolStripMenuItem item)
            {
                bool next = !item.Checked;
                item.Checked = next;
                _settings.ShowNotification = next;
                _settings.Save();
            }
        });
        notificationMenuItem.Checked = _settings.ShowNotification;
        contextMenu.Items.Add(notificationMenuItem);

        // スタートアップ登録
        _startupMenuItem = new ToolStripMenuItem("🚀 Windows起動時に自動実行(&A)", null, (s, e) =>
        {
            bool current = StartupManager.IsStartupEnabled();
            StartupManager.SetStartup(!current);
            _settings.AutoStart = !current;
            _settings.Save();
            if (_startupMenuItem != null)
            {
                _startupMenuItem.Checked = !current;
            }
        });
        _startupMenuItem.Checked = StartupManager.IsStartupEnabled();
        contextMenu.Items.Add(_startupMenuItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        // 終了
        var exitItem = new ToolStripMenuItem("❌ 終了(&X)", null, (s, e) =>
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            Application.Current.Shutdown();
        });
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    public void UpdatePauseState(bool isPaused)
    {
        if (_pauseMenuItem != null)
        {
            _pauseMenuItem.Checked = isPaused;
        }

        if (isPaused)
        {
            _notifyIcon.Icon = CreateAppIcon(Color.Gray);
            _notifyIcon.Text = "IMESupport (一時停止中)";
        }
        else
        {
            _notifyIcon.Icon = CreateAppIcon(Color.FromArgb(59, 130, 246));
            _notifyIcon.Text = "IMESupport (AI日本語入力支援 - 変換キー2回)";
        }
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (!_settings.ShowNotification) return;

        // バルーン通知は長すぎると表示が切れるため短縮
        string displayMessage = message.Length > 150 ? message.Substring(0, 150) + "..." : message;
        _notifyIcon.ShowBalloonTip(3000, title, displayMessage, icon);
    }

    /// <summary>
    /// トレイ用のアプリアイコンを動的生成（外部.icoファイル依存なし）
    /// </summary>
    private static Icon CreateAppIcon(Color primaryColor)
    {
        int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // 角丸の背景矩形
        using var brush = new SolidBrush(primaryColor);
        using var path = new GraphicsPath();
        int radius = 8;
        path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
        path.AddArc(size - radius * 2, 0, radius * 2, radius * 2, 270, 90);
        path.AddArc(size - radius * 2, size - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(0, size - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);

        // "AI" テキスト描画
        using var textBrush = new SolidBrush(Color.White);
        using var font = new Font("Arial", 11, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("AI", font, textBrush, new RectangleF(0, 0, size, size), sf);

        IntPtr hIcon = bitmap.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
