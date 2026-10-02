using System.Windows;
using IMESupport.Models;

namespace IMESupport.UI;

/// <summary>
/// 推敲履歴ウィンドウのロジック
/// </summary>
public partial class HistoryWindow : Window
{
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
        LvHistory.ItemsSource = null;
        LvHistory.ItemsSource = HistoryManager.GetItems();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("履歴をすべて削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            HistoryManager.Clear();
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
