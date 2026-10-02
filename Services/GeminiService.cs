using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using IMESupport.Models;

namespace IMESupport.Services;

/// <summary>
/// Gemini API を利用した文章推敲サービス
/// </summary>
public class GeminiService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// 指定されたテキストを Gemini API を使って推敲・誤変換修正する
    /// </summary>
    /// <param name="inputText">修正対象のテキスト</param>
    /// <param name="settings">アプリ設定</param>
    /// <returns>修正後のテキスト（エラー時は原文またはnull）</returns>
    public async Task<CorrectionResult> ProofreadAsync(string inputText, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            return new CorrectionResult
            {
                Success = false,
                OriginalText = inputText,
                CorrectedText = inputText,
                ErrorMessage = "入力テキストが空です。"
            };
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return new CorrectionResult
            {
                Success = false,
                OriginalText = inputText,
                CorrectedText = inputText,
                ErrorMessage = "Gemini API キーが設定されていません。設定画面からAPIキーを入力してください。"
            };
        }

        string modelName = string.IsNullOrWhiteSpace(settings.Model) ? "gemini-2.5-flash" : settings.Model.Trim();
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={settings.ApiKey.Trim()}";

        try
        {
            var requestBody = new
            {
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = settings.SystemPrompt }
                    }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = inputText }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.1,
                    topP = 0.8,
                    maxOutputTokens = 2048
                }
            };

            string jsonContent = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync(url, content);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                string errorDetail = ParseErrorMessage(responseString);
                return new CorrectionResult
                {
                    Success = false,
                    OriginalText = inputText,
                    CorrectedText = inputText,
                    ErrorMessage = $"Gemini API エラー (ステータス {(int)response.StatusCode}): {errorDetail}"
                };
            }

            var rootNode = JsonNode.Parse(responseString);
            var candidates = rootNode?["candidates"]?.AsArray();
            if (candidates == null || candidates.Count == 0)
            {
                return new CorrectionResult
                {
                    Success = false,
                    OriginalText = inputText,
                    CorrectedText = inputText,
                    ErrorMessage = "Gemini から候補が返されませんでした。"
                };
            }

            var parts = candidates[0]?["content"]?["parts"]?.AsArray();
            string? resultText = parts?[0]?["text"]?.ToString();

            if (string.IsNullOrEmpty(resultText))
            {
                return new CorrectionResult
                {
                    Success = false,
                    OriginalText = inputText,
                    CorrectedText = inputText,
                    ErrorMessage = "空のレスポンスが返されました。"
                };
            }

            // 余計な記号やコードブロックのクリーニング
            string cleanedText = CleanResponse(resultText.Trim());

            return new CorrectionResult
            {
                Success = true,
                OriginalText = inputText,
                CorrectedText = cleanedText
            };
        }
        catch (TaskCanceledException)
        {
            return new CorrectionResult
            {
                Success = false,
                OriginalText = inputText,
                CorrectedText = inputText,
                ErrorMessage = "APIリクエストがタイムアウトしました。"
            };
        }
        catch (Exception ex)
        {
            return new CorrectionResult
            {
                Success = false,
                OriginalText = inputText,
                CorrectedText = inputText,
                ErrorMessage = $"通信エラー: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// APIからの返答から余分な装飾（Markdownの引用やコードブロックなど）を剥がす
    /// </summary>
    public static string CleanResponse(string text)
    {
        // 先頭・末尾の ``` や ```markdown を削除
        if (text.StartsWith("```") && text.EndsWith("```"))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text.Substring(firstNewline + 1);
            }
            if (text.EndsWith("```"))
            {
                text = text.Substring(0, text.Length - 3).Trim();
            }
        }

        // 先頭・末尾の引用符を剥がす（誤って付けられた場合）
        if ((text.StartsWith("「") && text.EndsWith("」")) ||
            (text.StartsWith("\"") && text.EndsWith("\"")) ||
            (text.StartsWith("“") && text.EndsWith("”")))
        {
            text = text.Substring(1, text.Length - 2);
        }

        return text.Trim();
    }

    /// <summary>
    /// APIエラーレスポンスのJSONからエラーメッセージを抽出
    /// </summary>
    private static string ParseErrorMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var message = node?["error"]?["message"]?.ToString();
            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }
        }
        catch
        {
        }
        return json.Length > 200 ? json.Substring(0, 200) + "..." : json;
    }
}

/// <summary>
/// 推敲結果
/// </summary>
public class CorrectionResult
{
    public bool Success { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public string CorrectedText { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public bool HasChanged => Success && !string.Equals(OriginalText, CorrectedText, StringComparison.Ordinal);
}
