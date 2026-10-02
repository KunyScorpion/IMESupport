namespace IMESupport.Services;

/// <summary>
/// 差分の種類
/// </summary>
public enum DiffType
{
    Unchanged, // 変更なし
    Deleted,   // 修正前（削除/変更前）
    Inserted   // 修正後（追加/変更後）
}

/// <summary>
/// 差分ピース
/// </summary>
public class DiffPiece
{
    public DiffType Type { get; set; }
    public string Text { get; set; } = string.Empty;

    public DiffPiece(DiffType type, string text)
    {
        Type = type;
        Text = text;
    }
}

/// <summary>
/// 文章の差分（Diff）計算ヘルパー
/// </summary>
public static class DiffHelper
{
    /// <summary>
    /// 修正前と修正後のテキストから文字単位の差分ピース一覧を生成（最長共通部分列アルゴリズム）
    /// </summary>
    public static List<DiffPiece> CalculateDiff(string oldText, string newText)
    {
        if (string.IsNullOrEmpty(oldText) && string.IsNullOrEmpty(newText))
        {
            return new List<DiffPiece>();
        }
        if (string.IsNullOrEmpty(oldText))
        {
            return new List<DiffPiece> { new(DiffType.Inserted, newText) };
        }
        if (string.IsNullOrEmpty(newText))
        {
            return new List<DiffPiece> { new(DiffType.Deleted, oldText) };
        }
        if (oldText == newText)
        {
            return new List<DiffPiece> { new(DiffType.Unchanged, oldText) };
        }

        int m = oldText.Length;
        int n = newText.Length;

        // 長すぎるテキストの場合は簡易分割（最大2000文字までフルLCS）
        if (m > 2000 || n > 2000)
        {
            return new List<DiffPiece>
            {
                new(DiffType.Deleted, oldText),
                new(DiffType.Inserted, newText)
            };
        }

        // DPテーブルによるLCS計算
        int[,] dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (oldText[i - 1] == newText[j - 1])
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }
        }

        // バックトラックして差分リストを構築
        var rawPieces = new List<DiffPiece>();
        int x = m;
        int y = n;

        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && oldText[x - 1] == newText[y - 1])
            {
                rawPieces.Add(new DiffPiece(DiffType.Unchanged, oldText[x - 1].ToString()));
                x--;
                y--;
            }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            {
                rawPieces.Add(new DiffPiece(DiffType.Inserted, newText[y - 1].ToString()));
                y--;
            }
            else if (x > 0 && (y == 0 || dp[x, y - 1] < dp[x - 1, y]))
            {
                rawPieces.Add(new DiffPiece(DiffType.Deleted, oldText[x - 1].ToString()));
                x--;
            }
        }

        rawPieces.Reverse();

        // 連続する同種ピースをマージして読みやすくまとめる
        var mergedPieces = new List<DiffPiece>();
        foreach (var piece in rawPieces)
        {
            if (mergedPieces.Count > 0 && mergedPieces[^1].Type == piece.Type)
            {
                mergedPieces[^1].Text += piece.Text;
            }
            else
            {
                mergedPieces.Add(new DiffPiece(piece.Type, piece.Text));
            }
        }

        return mergedPieces;
    }
}
