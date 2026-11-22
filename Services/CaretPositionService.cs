using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace IMESupport.Services;

/// <summary>
/// スクリーン物理ピクセル座標上のアンカー矩形（キャレットまたは選択テキスト範囲）
/// </summary>
public readonly record struct ScreenAnchorRect(
    double Left,
    double Top,
    double Width,
    double Height,
    string Source = "")
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public bool IsValid =>
        !double.IsNaN(Left) && !double.IsInfinity(Left) &&
        !double.IsNaN(Top) && !double.IsInfinity(Top) &&
        !double.IsNaN(Width) && !double.IsInfinity(Width) &&
        !double.IsNaN(Height) && !double.IsInfinity(Height) &&
        Width >= 0 && Height > 0;
}

/// <summary>
/// 対象アプリケーションのテキストカーソル（キャレット）および選択範囲座標の取得・配置計算サービス
/// </summary>
public static class CaretPositionService
{
    private const uint OBJID_CARET = 0xFFFFFFF8;
    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private const double MaxCaretRectHeightPx = 300.0;
    private const double MaxFocusedControlHeightPx = 350.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint dwId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);

    /// <summary>
    /// 対象ウィンドウのキャレット／選択範囲のスクリーン座標矩形を多段フォールバックで非同期取得する
    /// </summary>
    public static async Task<ScreenAnchorRect?> TryGetCaretAnchorRectAsync(IntPtr targetHwnd, int timeoutMs = 350)
    {
        Task<ScreenAnchorRect?> workerTask = Task.Run(() => TryGetCaretAnchorRectCore(targetHwnd));
        Task completed = await Task.WhenAny(workerTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != workerTask)
        {
            return null;
        }

        try
        {
            return await workerTask.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static ScreenAnchorRect? TryGetCaretAnchorRectCore(IntPtr targetHwnd)
    {
        AutomationElement? focusedElement = TryGetFocusedAutomationElement();

        // 1. UI Automation (TextPattern.GetSelection)
        if (focusedElement != null)
        {
            ScreenAnchorRect? textSelectionRect = TryGetUiaTextSelectionRect(focusedElement);
            if (textSelectionRect is { IsValid: true })
            {
                return textSelectionRect;
            }
        }

        // 2. Win32 GetGUIThreadInfo (rcCaret + ClientToScreen)
        GUITHREADINFO guiInfo = GetGuiThreadInfoSafe(targetHwnd);
        ScreenAnchorRect? win32CaretRect = TryGetWin32GuiCaretRect(guiInfo);
        if (win32CaretRect is { IsValid: true })
        {
            return win32CaretRect;
        }

        // 3. MSAA (AccessibleObjectFromWindow + OBJID_CARET)
        ScreenAnchorRect? msaaCaretRect = TryGetMsaaCaretRect(guiInfo.hwndFocus, targetHwnd);
        if (msaaCaretRect is { IsValid: true })
        {
            return msaaCaretRect;
        }

        // 4. UI Automation (フォーカス中の入力コントロール矩形フォールバック)
        if (focusedElement != null)
        {
            ScreenAnchorRect? controlRect = TryGetUiaFocusedControlRect(focusedElement);
            if (controlRect is { IsValid: true })
            {
                return controlRect;
            }
        }

        return null;
    }

    private static AutomationElement? TryGetFocusedAutomationElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch
        {
            return null;
        }
    }

    private static ScreenAnchorRect? TryGetUiaTextSelectionRect(AutomationElement focusedElement)
    {
        try
        {
            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObj) &&
                patternObj is TextPattern textPattern)
            {
                TextPatternRange[]? ranges = textPattern.GetSelection();
                if (ranges != null && ranges.Length > 0 && ranges[0] != null)
                {
                    TextPatternRange range = ranges[0];
                    Rect[] rects = range.GetBoundingRectangles();
                    if (rects == null || rects.Length == 0)
                    {
                        // 未選択時はキャレット直近の1文字に拡張して座標を取得
                        TextPatternRange expanded = range.Clone();
                        expanded.ExpandToEnclosingUnit(TextUnit.Character);
                        rects = expanded.GetBoundingRectangles();
                    }

                    return CombineRects(rects, "UIA.TextPattern");
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static GUITHREADINFO GetGuiThreadInfoSafe(IntPtr targetHwnd)
    {
        var info = new GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<GUITHREADINFO>()
        };

        try
        {
            uint threadId = targetHwnd != IntPtr.Zero
                ? GetWindowThreadProcessId(targetHwnd, out _)
                : 0;
            GetGUIThreadInfo(threadId, ref info);
        }
        catch
        {
        }

        return info;
    }

    private static ScreenAnchorRect? TryGetWin32GuiCaretRect(GUITHREADINFO info)
    {
        try
        {
            if (info.hwndCaret == IntPtr.Zero)
            {
                return null;
            }

            if (info.rcCaret.Left == 0 && info.rcCaret.Top == 0 &&
                info.rcCaret.Right == 0 && info.rcCaret.Bottom == 0)
            {
                return null;
            }

            var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
            if (!ClientToScreen(info.hwndCaret, ref topLeft) ||
                !ClientToScreen(info.hwndCaret, ref bottomRight))
            {
                return null;
            }

            double width = Math.Max(0, bottomRight.X - topLeft.X);
            double height = bottomRight.Y - topLeft.Y;
            if (height <= 0 || height > MaxCaretRectHeightPx)
            {
                return null;
            }

            return new ScreenAnchorRect(topLeft.X, topLeft.Y, width, height, "Win32.GUIThreadInfo");
        }
        catch
        {
            return null;
        }
    }

    private static ScreenAnchorRect? TryGetMsaaCaretRect(IntPtr focusHwnd, IntPtr targetHwnd)
    {
        if (focusHwnd != IntPtr.Zero)
        {
            ScreenAnchorRect? rect = TryQueryMsaaCaret(focusHwnd);
            if (rect is { IsValid: true })
            {
                return rect;
            }
        }

        if (targetHwnd != IntPtr.Zero && targetHwnd != focusHwnd)
        {
            return TryQueryMsaaCaret(targetHwnd);
        }

        return null;
    }

    private static ScreenAnchorRect? TryQueryMsaaCaret(IntPtr hwnd)
    {
        object? accObj = null;
        try
        {
            Guid iid = IidIAccessible;
            int hr = AccessibleObjectFromWindow(hwnd, OBJID_CARET, ref iid, out accObj);
            if (hr != 0 || accObj is not Accessibility.IAccessible acc)
            {
                return null;
            }

            acc.accLocation(out int left, out int top, out int width, out int height, 0);
            if (height <= 0 || height > MaxCaretRectHeightPx)
            {
                return null;
            }

            if (left == 0 && top == 0 && width == 0)
            {
                return null;
            }

            return new ScreenAnchorRect(left, top, Math.Max(0, width), height, "MSAA.Caret");
        }
        catch
        {
            return null;
        }
        finally
        {
            if (accObj != null && Marshal.IsComObject(accObj))
            {
                Marshal.ReleaseComObject(accObj);
            }
        }
    }

    private static ScreenAnchorRect? TryGetUiaFocusedControlRect(AutomationElement focusedElement)
    {
        try
        {
            Rect bounds = focusedElement.Current.BoundingRectangle;
            if (!bounds.IsEmpty &&
                !double.IsNaN(bounds.X) && !double.IsInfinity(bounds.X) &&
                !double.IsNaN(bounds.Y) && !double.IsInfinity(bounds.Y) &&
                !double.IsNaN(bounds.Width) && !double.IsInfinity(bounds.Width) &&
                !double.IsNaN(bounds.Height) && !double.IsInfinity(bounds.Height) &&
                bounds.Width > 0 && bounds.Height > 0 && bounds.Height <= MaxFocusedControlHeightPx)
            {
                return new ScreenAnchorRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height, "UIA.FocusedElement");
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 複数の矩形を1つの外接矩形へ統合する（無効・異常サイズの矩形は除外）
    /// </summary>
    public static ScreenAnchorRect? CombineRects(IEnumerable<Rect>? rects, string source = "")
    {
        if (rects == null)
        {
            return null;
        }

        double minLeft = double.MaxValue;
        double minTop = double.MaxValue;
        double maxRight = double.MinValue;
        double maxBottom = double.MinValue;
        bool hasAny = false;

        foreach (Rect r in rects)
        {
            if (r.IsEmpty ||
                double.IsNaN(r.X) || double.IsInfinity(r.X) ||
                double.IsNaN(r.Y) || double.IsInfinity(r.Y) ||
                double.IsNaN(r.Width) || double.IsInfinity(r.Width) ||
                double.IsNaN(r.Height) || double.IsInfinity(r.Height) ||
                r.Width < 0 || r.Height <= 0 || r.Height > MaxCaretRectHeightPx)
            {
                continue;
            }

            minLeft = Math.Min(minLeft, r.Left);
            minTop = Math.Min(minTop, r.Top);
            maxRight = Math.Max(maxRight, r.Right);
            maxBottom = Math.Max(maxBottom, r.Bottom);
            hasAny = true;
        }

        if (!hasAny)
        {
            return null;
        }

        return new ScreenAnchorRect(
            minLeft,
            minTop,
            Math.Max(0, maxRight - minLeft),
            Math.Max(0, maxBottom - minTop),
            source);
    }

    /// <summary>
    /// アンカー矩形とモニター作業領域に基づき、ポップアップウィンドウの Left / Top (DIP) を算出する
    /// </summary>
    public static (double Left, double Top) CalculatePopupPosition(
        double anchorLeft,
        double anchorTop,
        double anchorBottom,
        double popupWidth,
        double popupHeight,
        double workLeft,
        double workTop,
        double workRight,
        double workBottom,
        double shadowMargin = 10,
        double gap = 6)
    {
        double desiredLeft = anchorLeft - shadowMargin;
        double maxLeft = Math.Max(workLeft, workRight - popupWidth);
        double left = Math.Clamp(desiredLeft, workLeft, maxLeft);

        double belowTop = anchorBottom - shadowMargin + gap;
        double aboveTop = anchorTop - popupHeight + shadowMargin - gap;
        double maxTop = Math.Max(workTop, workBottom - popupHeight);

        double top;
        if (belowTop + popupHeight <= workBottom)
        {
            top = Math.Clamp(belowTop, workTop, maxTop);
        }
        else if (aboveTop >= workTop)
        {
            top = Math.Clamp(aboveTop, workTop, maxTop);
        }
        else
        {
            double spaceBelow = workBottom - anchorBottom;
            double spaceAbove = anchorTop - workTop;
            double preferredTop = spaceAbove > spaceBelow ? aboveTop : belowTop;
            top = Math.Clamp(preferredTop, workTop, maxTop);
        }

        return (left, top);
    }
}
