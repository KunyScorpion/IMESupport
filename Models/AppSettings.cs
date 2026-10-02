using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IMESupport.Models;

/// <summary>
/// アプリケーション設定クラス
/// </summary>
public class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Gemini API キー
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 使用する Gemini モデル名（例: gemini-2.5-flash, gemini-2.0-flash, gemini-3.5-flash-lite 等）
    /// </summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>
    /// ダブルタップ判定の最大間隔（ミリ秒）
    /// </summary>
    public int DoubleTapIntervalMs { get; set; } = 350;

    /// <summary>
    /// トリガーキーの種類 (Convert: 変換キー, NonConvert: 無変換キー, CtrlDouble: Ctrlキー2回, Custom: カスタム)
    /// </summary>
    public string TriggerKeyType { get; set; } = "Convert";

    /// <summary>
    /// トリガーキーの仮想キーコード (デフォルト: VK_CONVERT = 0x1C)
    /// </summary>
    public int TriggerVirtualKey { get; set; } = 0x1C;

    /// <summary>
    /// 修正完了時にトースト/バルーン通知を表示するか
    /// </summary>
    public bool ShowNotification { get; set; } = false;

    /// <summary>
    /// 修正完了時に控えめな効果音を鳴らすか
    /// </summary>
    public bool PlaySoundOnComplete { get; set; } = false;

    /// <summary>
    /// 元のクリップボード内容を復元するか
    /// </summary>
    public bool RestoreClipboard { get; set; } = true;

    /// <summary>
    /// テキスト未選択時に直前の一行を自動選択して推敲するか
    /// </summary>
    public bool AutoSelectLineWhenEmpty { get; set; } = true;

    /// <summary>
    /// Windows起動時の自動起動（スタートアップ登録）
    /// </summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// AIプロンプト
    /// </summary>
    public string SystemPrompt { get; set; } = DefaultPrompt;

    public const string DefaultPrompt =
@"あなたは日本語入力支援・文脈推敲エンジンです。
入力された文章に対して以下の修正を自然に行い、修正後の文章のみを出力してください。

【修正対象】
1. IMEの同音異義語の誤変換（例: 「変改」→「変換」、「機構」→「気候」）
2. 文脈に合わない不自然な言葉遣い・誤用
3. 脱字・送り仮名の誤り
4. タイピングミス（キーの誤打鍵・子音/母音抜け・隣接キー誤打など）による不自然な文字列やカナの予測修正（例: 「ありがとございます」→「ありがとうございます」、「よろしくおねがいしま」→「よろしくお願いします」等）

【制約事項】
- 原文の意味、ニュアンス、口調（敬体/常体、丁寧語の度合い）を勝手に改変しないこと。
- 明らかな誤りがない場合は、原文をそのまま出力すること。
- 出力は修正後のテキストのみとし、解説、前置き、挨拶、Markdownのコードブロック、引用符（「」や""など）は一切出力しないこと。
- 余計な改行を追加しないこと。";

    /// <summary>
    /// 設定ファイルの保存パスを取得
    /// </summary>
    public static string GetSettingsFilePath()
    {
        string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IMESupport"
        );
        Directory.CreateDirectory(appDataDir);
        return Path.Combine(appDataDir, "settings.json");
    }

    /// <summary>
    /// 設定をファイルから読み込む
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            string path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"設定読み込みエラー: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>
    /// 設定をファイルに保存する
    /// </summary>
    public void Save()
    {
        try
        {
            string path = GetSettingsFilePath();
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"設定保存エラー: {ex.Message}");
        }
    }
}
