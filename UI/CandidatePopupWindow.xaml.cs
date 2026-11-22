using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace IMESupport.UI;

/// <summary>
/// 複数の推敲候補を提示し、ユーザーに選択させる軽量フローティングウィンドウ
/// </summary>
public partial class CandidatePopupWindow : Window
{
    private readonly TaskCompletionSource<string?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isCompleted;
    private bool _isClosing;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 候補表示用のバインディングアイテム
    /// </summary>
    private sealed class CandidateItem
    {
        public string NumberLabel { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
    }

    public CandidatePopupWindow()
    {
        InitializeComponent();
        Loaded += CandidatePopupWindow_Loaded;
        ContentRendered += CandidatePopupWindow_ContentRendered;
    }

    /// <summary>
    /// 候補一覧を表示し、ユーザーが選択したテキスト（キャンセル時は null）を非同期で返す
    /// </summary>
    public Task<string?> ShowCandidatesAsync(string originalText, IReadOnlyList<string> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        string singleLinePreview = (originalText ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        TxtOriginalPreview.Text = $"原文: {singleLinePreview}";

        var items = candidates
            .Take(3)
            .Select((text, index) => new CandidateItem
            {
                NumberLabel = $"{index + 1}.",
                Text = text
            })
            .ToList();

        LbCandidates.ItemsSource = items;
        LbCandidates.SelectedIndex = 0;

        Show();
        Activate();
        LbCandidates.Focus();

        return _tcs.Task;
    }

    private void CandidatePopupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionNearCursor();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }

        Activate();
        Keyboard.Focus(LbCandidates);
    }

    private void CandidatePopupWindow_ContentRendered(object? sender, EventArgs e)
    {
        PositionNearCursor();
    }

    /// <summary>
    /// 現在のマウスカーソル位置・モニター作業領域・DPIを考慮して画面内にクランプ配置する
    /// </summary>
    private void PositionNearCursor()
    {
        try
        {
            var cursorPos = System.Windows.Forms.Cursor.Position;
            var workingArea = System.Windows.Forms.Screen.FromPoint(cursorPos).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);

            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            double cursorX = cursorPos.X / scaleX;
            double cursorY = cursorPos.Y / scaleY;
            double workLeft = workingArea.Left / scaleX;
            double workTop = workingArea.Top / scaleY;
            double workRight = workingArea.Right / scaleX;
            double workBottom = workingArea.Bottom / scaleY;

            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : 220;

            double left = cursorX + 12;
            double top = cursorY + 18;

            if (left + width > workRight)
            {
                left = cursorX - width - 12;
            }
            if (top + height > workBottom)
            {
                top = cursorY - height - 12;
            }

            double maxLeft = Math.Max(workLeft, workRight - width);
            double maxTop = Math.Max(workTop, workBottom - height);

            Left = Math.Clamp(left, workLeft, maxLeft);
            Top = Math.Clamp(top, workTop, maxTop);
        }
        catch
        {
            // 画面座標取得に失敗した場合は既定位置を維持
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

        switch (key)
        {
            case Key.D1:
            case Key.NumPad1:
                SelectByIndex(0);
                e.Handled = true;
                break;

            case Key.D2:
            case Key.NumPad2:
                SelectByIndex(1);
                e.Handled = true;
                break;

            case Key.D3:
            case Key.NumPad3:
                SelectByIndex(2);
                e.Handled = true;
                break;

            case Key.Enter:
                if (LbCandidates.SelectedItem is CandidateItem selected)
                {
                    CompleteAndClose(selected.Text);
                }
                else
                {
                    SelectByIndex(0);
                }
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Escape:
                CompleteAndClose(null);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = LbCandidates.Items.Count;
        if (count == 0)
        {
            return;
        }

        int current = LbCandidates.SelectedIndex;
        int next = current < 0 ? 0 : (current + delta + count) % count;
        LbCandidates.SelectedIndex = next;
        LbCandidates.ScrollIntoView(LbCandidates.SelectedItem);
    }

    private void SelectByIndex(int index)
    {
        if (index >= 0 && index < LbCandidates.Items.Count &&
            LbCandidates.Items[index] is CandidateItem item)
        {
            CompleteAndClose(item.Text);
        }
    }

    private void ListBoxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem listBoxItem)
        {
            listBoxItem.IsSelected = true;
        }
    }

    private void ListBoxItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: CandidateItem item })
        {
            CompleteAndClose(item.Text);
            e.Handled = true;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_isCompleted && !_isClosing)
        {
            CompleteAndClose(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        if (!_isCompleted)
        {
            _isCompleted = true;
            _tcs.TrySetResult(null);
        }
        base.OnClosed(e);
    }

    private void CompleteAndClose(string? selectedText)
    {
        if (_isCompleted)
        {
            return;
        }

        _isCompleted = true;
        if (!_isClosing)
        {
            _isClosing = true;
            Close();
        }
        _tcs.TrySetResult(selectedText);
    }
}
