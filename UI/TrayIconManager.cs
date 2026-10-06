using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using IMESupport.Models;
using IMESupport.Services;

namespace IMESupport.UI;

/// <summary>
/// アプリケーションの動作ステータス
/// </summary>
public enum AppStatus
{
    Idle,        // 通常待機中（青）
    Processing,  // 推敲処理中（橙）
    Success,     // 修正あり（緑）
    NoChange,    // 修正なし（灰）
    Error,       // エラー発生（赤）
    Paused       // 一時停止中（暗灰）
}

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

    private readonly System.Windows.Forms.Timer _resetTimer;
    private ToolStripMenuItem? _pauseMenuItem;
    private ToolStripMenuItem? _startupMenuItem;
    private ToolStripMenuItem? _notificationMenuItem;

    private AppStatus _currentStatus = AppStatus.Idle;

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

        _resetTimer = new System.Windows.Forms.Timer
        {
            Interval = 6000 // 6秒後に通常待機状態に戻す
        };
        _resetTimer.Tick += (s, e) =>
        {
            _resetTimer.Stop();
            if (!_hookService.IsPaused)
            {
                SetStatus(AppStatus.Idle, "IMESupport (AI日本語入力支援 - キー2回で推敲)", autoReset: false);
            }
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateThemedIcon(AppStatus.Idle),
            Visible = true,
            Text = "IMESupport (AI日本語入力支援 - キー2回で推敲)"
        };

        InitializeContextMenu();

        // アイコンダブルクリックで推敲履歴を開く
        _notifyIcon.DoubleClick += (s, e) => _openHistoryAction();
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

        // 履歴（ダブルクリックでも起動可能）
        var historyItem = new ToolStripMenuItem("📜 推敲履歴(&H)...", null, (s, e) => _openHistoryAction());
        historyItem.Font = new Font(contextMenu.Font, System.Drawing.FontStyle.Bold);
        contextMenu.Items.Add(historyItem);

        // 設定
        var settingsItem = new ToolStripMenuItem("⚙️ 設定(&S)...", null, (s, e) => _openSettingsAction());
        contextMenu.Items.Add(settingsItem);

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

        // デスクトップ通知トグル（必要な場合のみ）
        _notificationMenuItem = new ToolStripMenuItem("🔔 デスクトップ通知を表示(&N)", null, (s, e) =>
        {
            if (s is ToolStripMenuItem item)
            {
                bool next = !item.Checked;
                item.Checked = next;
                _settings.ShowNotification = next;
                _settings.Save();
            }
        });
        _notificationMenuItem.Checked = _settings.ShowNotification;
        contextMenu.Items.Add(_notificationMenuItem);

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

    /// <summary>
    /// ステータスに応じてトレイアイコンとツールチップを更新
    /// </summary>
    public void SetStatus(AppStatus status, string tooltipText, bool autoReset = true)
    {
        _currentStatus = status;
        _resetTimer.Stop();

        try
        {
            _notifyIcon.Icon = CreateThemedIcon(status);

            // ツールチップ文字数は最大63文字（Win32 NotifyIcon制限対策）
            string safeText = tooltipText.Length > 63 ? tooltipText.Substring(0, 60) + "..." : tooltipText;
            _notifyIcon.Text = safeText;

            // 成功・変化なし・エラー時は一定時間後にアイドル状態へ自動リセット
            if (autoReset && (status == AppStatus.Success || status == AppStatus.NoChange || status == AppStatus.Error))
            {
                _resetTimer.Start();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"アイコン更新エラー: {ex.Message}");
        }
    }

    public void UpdatePauseState(bool isPaused)
    {
        if (_pauseMenuItem != null)
        {
            _pauseMenuItem.Checked = isPaused;
        }

        if (isPaused)
        {
            SetStatus(AppStatus.Paused, "IMESupport (一時停止中)", autoReset: false);
        }
        else
        {
            SetStatus(AppStatus.Idle, "IMESupport (AI日本語入力支援 - キー2回で推敲)", autoReset: false);
        }
    }

    /// <summary>
    /// デスクトップ通知（バルーン通知）の表示（設定でONの場合のみ）
    /// </summary>
    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (!_settings.ShowNotification) return;

        string displayMessage = message.Length > 150 ? message.Substring(0, 150) + "..." : message;
        _notifyIcon.ShowBalloonTip(3000, title, displayMessage, icon);
    }

    /// <summary>
    /// ステータスに応じたモダンな高品質アプリアイコンを動的生成（万年筆ペン先 ＋ AIキラキラ星）
    /// </summary>
    public static Icon CreateThemedIcon(AppStatus status)
    {
        // ステータスに応じたグラデーションカラーの選定
        Color primaryColor;
        Color secondaryColor;

        switch (status)
        {
            case AppStatus.Success:
                // 修正あり: 鮮烈なエメラルドグリーン（緑）
                primaryColor = Color.FromArgb(16, 185, 129);
                secondaryColor = Color.FromArgb(5, 150, 105);
                break;

            case AppStatus.NoChange:
                // 修正なし: 落ち着いたスレートグレー（灰）
                primaryColor = Color.FromArgb(100, 116, 139);
                secondaryColor = Color.FromArgb(71, 85, 105);
                break;

            case AppStatus.Error:
                // エラー: 警告のクリムゾンレッド（赤）
                primaryColor = Color.FromArgb(239, 68, 68);
                secondaryColor = Color.FromArgb(220, 38, 38);
                break;

            case AppStatus.Processing:
                // 推敲処理中: 輝くアンバーオレンジ（橙）
                primaryColor = Color.FromArgb(245, 158, 11);
                secondaryColor = Color.FromArgb(217, 119, 6);
                break;

            case AppStatus.Paused:
                // 一時停止: ダークスレート
                primaryColor = Color.FromArgb(71, 85, 105);
                secondaryColor = Color.FromArgb(51, 65, 85);
                break;

            case AppStatus.Idle:
            default:
                // 通常待機: ロイヤルブルー（青）
                primaryColor = Color.FromArgb(59, 130, 246);
                secondaryColor = Color.FromArgb(29, 78, 216);
                break;
        }

        int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        // 1. 角丸背景矩形 (Squircle)
        Rectangle rect = new Rectangle(1, 1, size - 2, size - 2);
        int radius = 7;
        using (var path = CreateRoundedRectanglePath(rect, radius))
        {
            using (var brush = new LinearGradientBrush(rect, primaryColor, secondaryColor, 60f))
            {
                g.FillPath(brush, path);
            }
            // 微細なエッジ枠線
            using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
            {
                g.DrawPath(pen, path);
            }
        }

        // 2. 万年筆のペン先 (Pen Nib) のベクター描画
        using (var nibPath = new GraphicsPath())
        {
            PointF tip = new PointF(13f, 25f);
            PointF leftWaist = new PointF(7f, 17f);
            PointF leftTop = new PointF(8.5f, 10f);
            PointF rightTop = new PointF(17.5f, 10f);
            PointF rightWaist = new PointF(19f, 17f);

            nibPath.AddLine(tip, leftWaist);
            nibPath.AddLine(leftWaist, leftTop);
            nibPath.AddLine(leftTop, rightTop);
            nibPath.AddLine(rightTop, rightWaist);
            nibPath.CloseFigure();

            using (var nibBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
            {
                g.FillPath(nibBrush, nibPath);
            }

            // ペン先の中央スリット線 ＆ 呼吸穴
            using (var slitPen = new Pen(primaryColor, 1.2f))
            {
                g.DrawLine(slitPen, 13f, 25f, 13f, 16f);
            }
            using (var holeBrush = new SolidBrush(primaryColor))
            {
                g.FillEllipse(holeBrush, 11.8f, 14.8f, 2.4f, 2.4f);
            }
        }

        // 3. AI Sparkle (右上のキラキラ4点星)
        using (var starPath = new GraphicsPath())
        {
            PointF center = new PointF(23.5f, 8.5f);
            float outerR = 5.2f;
            float innerR = 1.4f;

            PointF[] starPoints = new PointF[]
            {
                new PointF(center.X, center.Y - outerR), // 上
                new PointF(center.X + innerR, center.Y - innerR),
                new PointF(center.X + outerR, center.Y), // 右
                new PointF(center.X + innerR, center.Y + innerR),
                new PointF(center.X, center.Y + outerR), // 下
                new PointF(center.X - innerR, center.Y + innerR),
                new PointF(center.X - outerR, center.Y), // 左
                new PointF(center.X - innerR, center.Y - innerR),
            };
            starPath.AddPolygon(starPoints);

            using (var starBrush = new SolidBrush(Color.White))
            {
                g.FillPath(starBrush, starPath);
            }
        }

        // 4. 小さな副光（キラキラアクセント）
        using (var miniStar = new GraphicsPath())
        {
            PointF c2 = new PointF(27.5f, 16.5f);
            float r = 2.0f;
            miniStar.AddLine(c2.X, c2.Y - r, c2.X + 0.6f, c2.Y);
            miniStar.AddLine(c2.X + r, c2.Y, c2.X + 0.6f, c2.Y + 0.6f);
            miniStar.AddLine(c2.X, c2.Y + r, c2.X - 0.6f, c2.Y + 0.6f);
            miniStar.AddLine(c2.X - r, c2.Y, c2.X - 0.6f, c2.Y);
            miniStar.CloseFigure();
            using (var b2 = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            {
                g.FillPath(b2, miniStar);
            }
        }

        IntPtr hIcon = bitmap.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _resetTimer.Stop();
        _resetTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
