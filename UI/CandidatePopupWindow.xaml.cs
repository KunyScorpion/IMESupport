using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using IMESupport.Services;

namespace IMESupport.UI;

/// <summary>
/// 複数の推敲候補を提示し、ユーザーに選択させる軽量フローティングウィンドウ
/// </summary>
public partial class CandidatePopupWindow : Window
{
    private const double OuterShadowMarginDip = 10.0;
    private const double AnchorGapDip = 6.0;

    private static readonly SolidColorBrush InsertedBgBrush = CreateFrozenBrush(220, 252, 231);
    private static readonly SolidColorBrush InsertedFgBrush = CreateFrozenBrush(22, 163, 74);

    private readonly TaskCompletionSource<string?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScreenAnchorRect? _anchorRect;
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
        public IReadOnlyList<DiffPiece> DiffPieces { get; init; } = Array.Empty<DiffPiece>();
    }

    public static readonly DependencyProperty DiffPiecesProperty =
        DependencyProperty.RegisterAttached(
            "DiffPieces",
            typeof(IReadOnlyList<DiffPiece>),
            typeof(CandidatePopupWindow),
            new PropertyMetadata(null, OnDiffPiecesChanged));

    public static IReadOnlyList<DiffPiece>? GetDiffPieces(DependencyObject obj) =>
        (IReadOnlyList<DiffPiece>?)obj.GetValue(DiffPiecesProperty);

    public static void SetDiffPieces(DependencyObject obj, IReadOnlyList<DiffPiece>? value) =>
        obj.SetValue(DiffPiecesProperty, value);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void OnDiffPiecesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Inlines.Clear();
        if (e.NewValue is IReadOnlyList<DiffPiece> pieces)
        {
            foreach (DiffPiece piece in pieces)
            {
                if (piece.Type == DiffType.Unchanged)
                {
                    textBlock.Inlines.Add(new Run(piece.Text));
                }
                else if (piece.Type == DiffType.Inserted)
                {
                    textBlock.Inlines.Add(new Run(piece.Text)
                    {
                        Background = InsertedBgBrush,
                        Foreground = InsertedFgBrush,
                        FontWeight = FontWeights.Bold
                    });
                }
            }
        }

        if (textBlock.Inlines.Count == 0 &&
            textBlock.DataContext is CandidateItem item &&
            !string.IsNullOrEmpty(item.Text))
        {
            textBlock.Inlines.Add(new Run(item.Text));
        }
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
    public Task<string?> ShowCandidatesAsync(
        string originalText,
        IReadOnlyList<string> candidates,
        ScreenAnchorRect? anchorRect = null)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        _anchorRect = anchorRect;
        string safeOriginal = originalText ?? string.Empty;
        string singleLinePreview = safeOriginal.Replace("\r", " ").Replace("\n", " ").Trim();
        TxtOriginalPreview.Text = $"原文: {singleLinePreview}";

        var items = candidates
            .Take(3)
            .Select((text, index) => new CandidateItem
            {
                NumberLabel = $"{index + 1}.",
                Text = text,
                DiffPieces = DiffHelper.CalculateDiff(safeOriginal, text)
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
        PositionPopup();

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
        PositionPopup();
    }

    /// <summary>
    /// キャレット／選択範囲アンカー（取得失敗時はマウスカーソル）基準で、モニター作業領域内に配置する
    /// </summary>
    private void PositionPopup()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : 220;

            System.Drawing.Rectangle workingArea;
            double anchorLeft;
            double anchorTop;
            double anchorBottom;

            if (_anchorRect is { IsValid: true } anchor)
            {
                int centerPxX = (int)Math.Round(anchor.Left + anchor.Width / 2.0);
                int centerPxY = (int)Math.Round(anchor.Top + anchor.Height / 2.0);
                workingArea = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(centerPxX, centerPxY)).WorkingArea;

                anchorLeft = anchor.Left / scaleX;
                anchorTop = anchor.Top / scaleY;
                anchorBottom = anchor.Bottom / scaleY;
            }
            else
            {
                var cursorPos = System.Windows.Forms.Cursor.Position;
                workingArea = System.Windows.Forms.Screen.FromPoint(cursorPos).WorkingArea;

                anchorLeft = cursorPos.X / scaleX;
                anchorTop = cursorPos.Y / scaleY;
                anchorBottom = anchorTop + 18.0;
            }

            double workLeft = workingArea.Left / scaleX;
            double workTop = workingArea.Top / scaleY;
            double workRight = workingArea.Right / scaleX;
            double workBottom = workingArea.Bottom / scaleY;

            (double left, double top) = CaretPositionService.CalculatePopupPosition(
                anchorLeft,
                anchorTop,
                anchorBottom,
                width,
                height,
                workLeft,
                workTop,
                workRight,
                workBottom,
                OuterShadowMarginDip,
                AnchorGapDip);

            Left = left;
            Top = top;
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
