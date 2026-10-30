namespace IMESupport.Models;

/// <summary>
/// 推敲履歴アイテム
/// </summary>
public class HistoryItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string OriginalText { get; set; } = string.Empty;
    public string CorrectedText { get; set; } = string.Empty;
    public bool HasChanged => !string.Equals(OriginalText, CorrectedText, StringComparison.Ordinal);
    public string Status { get; set; } = "成功";
    public string Model { get; set; } = string.Empty;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");

    public string DisplayStatus => HasChanged ? "✨ 修正あり" : (Status == "エラー" ? "⚠️ エラー" : "修正なし");
    public string StatusBadgeColor => HasChanged ? "#10B981" : (Status == "エラー" ? "#EF4444" : "#9CA3AF");
}

/// <summary>
/// 推敲履歴管理
/// </summary>
public static class HistoryManager
{
    private static readonly List<HistoryItem> _items = new();
    private static readonly object _lock = new();
    private const int MaxItems = 100;

    public static event Action? HistoryUpdated;

    public static void Add(HistoryItem item)
    {
        lock (_lock)
        {
            _items.Insert(0, item);
            if (_items.Count > MaxItems)
            {
                _items.RemoveAt(_items.Count - 1);
            }
        }
        HistoryUpdated?.Invoke();
    }

    public static IReadOnlyList<HistoryItem> GetItems()
    {
        lock (_lock)
        {
            return _items.ToList();
        }
    }

    /// <summary>
    /// 直近の推敲履歴からセッション文脈テキスト（古い順）を構築して取得します。
    /// </summary>
    /// <param name="maxCount">取得する最大件数（デフォルト3件）</param>
    /// <param name="withinMinutes">有効とする直近の分数（デフォルト5分）</param>
    /// <returns>直前の文脈テキスト（該当なしの場合は空文字列）</returns>
    public static string GetRecentContext(int maxCount = 3, int withinMinutes = 5)
    {
        if (maxCount <= 0 || withinMinutes <= 0)
        {
            return string.Empty;
        }

        DateTime cutoff = DateTime.Now.AddMinutes(-withinMinutes);

        lock (_lock)
        {
            var recentTexts = _items
                .Where(item => item.Status != "エラー" &&
                               !string.IsNullOrWhiteSpace(item.CorrectedText) &&
                               item.Timestamp >= cutoff)
                .Take(maxCount)
                .Select(item => item.CorrectedText.Trim())
                .Reverse()
                .ToList();

            return recentTexts.Count > 0 ? string.Join("\n", recentTexts) : string.Empty;
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
        HistoryUpdated?.Invoke();
    }
}
