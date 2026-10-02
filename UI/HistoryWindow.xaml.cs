using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using IMESupport.Models;
using IMESupport.Services;

namespace IMESupport.UI;

/// <summary>
/// 推敲履歴ウィンドウのロジック（差分色分けハイライト対応）
/// </summary>
public partial class HistoryWindow : Window
{
    private static readonly SolidColorBrush DeletedBgBrush = new(Color.FromRgb(254, 226, 226));
    private static readonly SolidColorBrush DeletedFgBrush = new(Color.FromRgb(220, 38, 38));

    private static readonly SolidColorBrush InsertedBgBrush = new(Color.FromRgb(220, 252, 231));
    private static readonly SolidColorBrush InsertedFgBrush = new(Color.FromRgb(22, 163, 74));

    public HistoryWindow()
    {
        InitializeComponent();
        RefreshList();

        HistoryManager.HistoryUpdated += () =>
        {
            Dispatcher.Invoke(RefreshList);
        };
    }

    private void RefreshList()
    {
        var items = HistoryManager.GetItems();
        LvHistory.ItemsSource = null;
        LvHistory.ItemsSource = items;

        if (items.Count > 0 && LvHistory.SelectedItem == null)
        {
            LvHistory.SelectedIndex = 0;
        }
    }

    private void LvHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LvHistory.SelectedItem is HistoryItem item)
        {
            RenderDiff(item.OriginalText, item.CorrectedText);
        }
        else
        {
            TbDiffBefore.Inlines.Clear();
            TbDiffAfter.Inlines.Clear();
        }
    }

    /// <summary>
    /// 修正前と修正後の差分を解析し、色分けハイライト表示する
    /// </summary>
    private void RenderDiff(string original, string corrected)
    {
        TbDiffBefore.Inlines.Clear();
        TbDiffAfter.Inlines.Clear();

        if (string.IsNullOrEmpty(original) && string.IsNullOrEmpty(corrected))
        {
            return;
        }

        var diffPieces = DiffHelper.CalculateDiff(original, corrected);

        // 1. 修正前（オリジナル）の描画
        foreach (var piece in diffPieces)
        {
            if (piece.Type == DiffType.Unchanged)
            {
                TbDiffBefore.Inlines.Add(new Run(piece.Text));
            }
            else if (piece.Type == DiffType.Deleted)
            {
                // 削除・変更前の箇所：赤色背景 + 取り消し線
                var run = new Run(piece.Text)
                {
                    Background = DeletedBgBrush,
                    Foreground = DeletedFgBrush,
                    TextDecorations = TextDecorations.Strikethrough,
                    FontWeight = FontWeights.Medium
                };
                TbDiffBefore.Inlines.Add(run);
            }
        }

        // 2. 修正後（推敲結果）の描画
        foreach (var piece in diffPieces)
        {
            if (piece.Type == DiffType.Unchanged)
            {
                TbDiffAfter.Inlines.Add(new Run(piece.Text));
            }
            else if (piece.Type == DiffType.Inserted)
            {
                // 追加・変更後の箇所：緑色背景 + 太字
                var run = new Run(piece.Text)
                {
                    Background = InsertedBgBrush,
                    Foreground = InsertedFgBrush,
                    FontWeight = FontWeights.Bold
                };
                TbDiffAfter.Inlines.Add(run);
            }
        }

        // 修正がない場合の表示
        if (!original.Equals(corrected, StringComparison.Ordinal) == false)
        {
            if (TbDiffBefore.Inlines.Count == 0)
            {
                TbDiffBefore.Inlines.Add(new Run(original));
            }
            if (TbDiffAfter.Inlines.Count == 0)
            {
                TbDiffAfter.Inlines.Add(new Run(corrected));
            }
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("推敲履歴をすべて削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            HistoryManager.Clear();
            TbDiffBefore.Inlines.Clear();
            TbDiffAfter.Inlines.Clear();
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (LvHistory.SelectedItem is HistoryItem item && !string.IsNullOrEmpty(item.CorrectedText))
        {
            Clipboard.SetText(item.CorrectedText);
            MessageBox.Show("修正後の文章をクリップボードにコピーしました。", "コピー完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
