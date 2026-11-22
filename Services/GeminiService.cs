using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IMESupport.Models;

namespace IMESupport.Services;

/// <summary>
/// Gemini API を利用した文章推敲サービス
/// </summary>
public class GeminiService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const string CandidateOverrideInstruction =
        """
        # 出力形式の上書き指示（複数候補モード）
        上記の「出力ルール」を上書きし、以下のルールでJSON文字列配列のみを出力すること。
        - `<target>` 内の原文に対する修正候補を、最も確度の高い順に最大3件作成し、JSON文字列配列（例: ["候補1", "候補2", "候補3"]）として出力すること。
        - 候補1は「最小介入での正確な誤字・誤変換修正（原文の口調・記号を厳守）」とすること。
        - 候補2は「漢字変換の最適化や、同音異義語の有力な別解釈がある場合の修正案」とすること。
        -候補3は「原文の意図・口調の方向性を保ちつつ、語順や言い回しを大胆に組み替えてより自然で読みやすく洗練させたリライト案」とすること。ただし冗長にせず、元の文字数と同等かそれより簡潔にまとめること。
        - 候補同士の重複は避け、各候補はテキスト本体のみ（番号や解説なし）とすること。
        """;

    private static readonly Regex TargetXmlTagRegex = new(
        @"<target>\s*([\s\S]*?)\s*</target>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LeadingTargetOpenTagRegex = new(
        @"^(?:<(?:context|keystroke_hint)>[\s\S]*?</(?:context|keystroke_hint)>\s*)*<target>\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingTargetCloseTagRegex = new(
        @"\s*</target>$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly (string Open, string Close)[] QuotePairs =
    [
        ("「", "」"),
        ("\"", "\""),
        ("“", "”")
    ];

    private static readonly Regex TargetSectionHeaderRegex = new(
        @"【推敲対象テキスト[^】]*】\s*",
        RegexOptions.Compiled);

    private static readonly Regex LeadingBracketLabelRegex = new(
        @"^(?:【[^】]+】\s*)+",
        RegexOptions.Compiled);

    /// <summary>
    /// 指定モデルが thinkingLevel パラメータに対応しているかを判定する
    /// </summary>
    public static bool SupportsThinkingLevel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        return modelName.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) ||
               modelName.Contains("gemini-4", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveThinkingLevel(string modelName, string? thinkingLevel)
    {
        if (!SupportsThinkingLevel(modelName) || string.IsNullOrWhiteSpace(thinkingLevel))
        {
            return null;
        }

        string normalized = thinkingLevel.Trim().ToUpperInvariant();
        if (normalized is "OFF" or "DEFAULT")
        {
            return null;
        }

        return normalized;
    }

    private static int GetMaxOutputTokens(string? resolvedThinkingLevel)
    {
        return resolvedThinkingLevel is "MEDIUM" or "HIGH" ? 8192 : 4096;
    }

    private static Dictionary<string, object> BuildGenerationConfig(
        double temperature,
        string? resolvedThinkingLevel,
        bool jsonArrayMode = false)
    {
        var config = new Dictionary<string, object>
        {
            ["temperature"] = temperature,
            ["topP"] = 0.8,
            ["maxOutputTokens"] = GetMaxOutputTokens(resolvedThinkingLevel)
        };

        if (resolvedThinkingLevel != null)
        {
            config["thinkingConfig"] = new Dictionary<string, object>
            {
                ["thinkingLevel"] = resolvedThinkingLevel
            };
        }

        if (jsonArrayMode)
        {
            config["responseMimeType"] = "application/json";
            config["responseSchema"] = new Dictionary<string, object>
            {
                ["type"] = "ARRAY",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "STRING"
                }
            };
        }

        return config;
    }

    /// <summary>
    /// Gemini APIレスポンスの candidates[0].content.parts から思考パート (thought: true) を除外し、本文テキストのみを結合して返す
    /// </summary>
    public static string ExtractResponseText(JsonNode? rootNode)
    {
        var parts = rootNode?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        if (parts == null || parts.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part == null) continue;

            var thoughtNode = part["thought"];
            if (thoughtNode != null && thoughtNode.GetValueKind() == JsonValueKind.True)
            {
                continue;
            }

            var textNode = part["text"];
            if (textNode == null) continue;

            string? text = textNode.GetValueKind() == JsonValueKind.String
                ? textNode.GetValue<string>()
                : textNode.ToString();

            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(text);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 指定されたテキストを Gemini API を使って推敲・誤変換修正する
    /// </summary>
    /// <param name="inputText">修正対象のテキスト</param>
    /// <param name="settings">アプリ設定</param>
    /// <param name="analysis">キーストローク解析結果（省略時は内部で自動解析）</param>
    /// <param name="recentContext">直前のセッション文脈テキスト（任意）</param>
    /// <returns>修正後のテキスト（エラー時は原文またはエラー情報）</returns>
    public async Task<CorrectionResult> ProofreadAsync(
        string inputText,
        AppSettings settings,
        KeystrokeAnalysisResult? analysis = null,
        string? recentContext = null)
    {
        var apiResponse = await SendGenerateContentAsync(
            inputText,
            settings,
            settings.GetEffectiveSystemPrompt(),
            temperature: 0.1,
            jsonArrayMode: false,
            analysis,
            recentContext);

        if (!apiResponse.Success)
        {
            return new CorrectionResult
            {
                Success = false,
                OriginalText = inputText,
                CorrectedText = inputText,
                ErrorMessage = apiResponse.ErrorMessage
            };
        }

        // 余計な記号やコードブロック・ラベルのクリーニング
        string cleanedText = CleanResponse(apiResponse.Text.Trim(), inputText);

        return new CorrectionResult
        {
            Success = true,
            OriginalText = inputText,
            CorrectedText = cleanedText
        };
    }

    /// <summary>
    /// 指定されたテキストに対して最大3件の修正・言い換え候補を Gemini API から取得する
    /// </summary>
    /// <param name="inputText">修正対象のテキスト</param>
    /// <param name="settings">アプリ設定</param>
    /// <param name="analysis">キーストローク解析結果（省略時は内部で自動解析）</param>
    /// <param name="recentContext">直前のセッション文脈テキスト（任意）</param>
    /// <returns>候補リストを含む取得結果</returns>
    public async Task<CandidateResult> GetCandidatesAsync(
        string inputText,
        AppSettings settings,
        KeystrokeAnalysisResult? analysis = null,
        string? recentContext = null)
    {
        string baseSystemPrompt = settings.GetEffectiveSystemPrompt().TrimEnd();
        string systemPrompt = string.IsNullOrEmpty(baseSystemPrompt)
            ? CandidateOverrideInstruction
            : $"{baseSystemPrompt}\n\n{CandidateOverrideInstruction}";

        var apiResponse = await SendGenerateContentAsync(
            inputText,
            settings,
            systemPrompt,
            temperature: 0.35,
            jsonArrayMode: true,
            analysis,
            recentContext);

        if (!apiResponse.Success)
        {
            return new CandidateResult
            {
                Success = false,
                OriginalText = inputText,
                ErrorMessage = apiResponse.ErrorMessage
            };
        }

        List<string> candidates = ParseCandidatesResponse(apiResponse.Text, inputText);
        if (candidates.Count == 0)
        {
            return new CandidateResult
            {
                Success = false,
                OriginalText = inputText,
                ErrorMessage = "候補を取得できませんでした。"
            };
        }

        return new CandidateResult
        {
            Success = true,
            OriginalText = inputText,
            Candidates = candidates
        };
    }

    private async Task<(bool Success, string Text, string? ErrorMessage)> SendGenerateContentAsync(
        string inputText,
        AppSettings settings,
        string systemPrompt,
        double temperature,
        bool jsonArrayMode,
        KeystrokeAnalysisResult? analysis,
        string? recentContext)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            return (false, string.Empty, "入力テキストが空です。");
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return (false, string.Empty, "Gemini API キーが設定されていません。設定画面からAPIキーを入力してください。");
        }

        analysis ??= KeystrokeAnalyzer.BuildAnalysisHint(inputText);
        string userPrompt = BuildUserPrompt(inputText, analysis, recentContext);

        string modelName = string.IsNullOrWhiteSpace(settings.Model) ? "gemini-2.5-flash" : settings.Model.Trim();
        string? resolvedThinkingLevel = ResolveThinkingLevel(modelName, settings.ThinkingLevel);
        var generationConfig = BuildGenerationConfig(temperature, resolvedThinkingLevel, jsonArrayMode);
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={settings.ApiKey.Trim()}";

        try
        {
            var requestBody = new
            {
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt }
                    }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = userPrompt }
                        }
                    }
                },
                generationConfig
            };

            string jsonContent = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync(url, content);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                string errorDetail = ParseErrorMessage(responseString);
                return (false, string.Empty, $"Gemini API エラー (ステータス {(int)response.StatusCode}): {errorDetail}");
            }

            var rootNode = JsonNode.Parse(responseString);
            var candidates = rootNode?["candidates"]?.AsArray();
            if (candidates == null || candidates.Count == 0)
            {
                return (false, string.Empty, "Gemini から候補が返されませんでした。");
            }

            string resultText = ExtractResponseText(rootNode);
            if (string.IsNullOrEmpty(resultText))
            {
                return (false, string.Empty, "空のレスポンスが返されました。");
            }

            return (true, resultText, null);
        }
        catch (TaskCanceledException)
        {
            return (false, string.Empty, "APIリクエストがタイムアウトしました。");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"通信エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// 推敲対象テキスト・キーストローク解析結果・直前文脈からユーザープロンプトを組み立てる
    /// </summary>
    public static string BuildUserPrompt(
        string inputText,
        KeystrokeAnalysisResult? analysis = null,
        string? recentContext = null)
    {
        bool hasContext = !string.IsNullOrWhiteSpace(recentContext);
        bool hasAnomaly = analysis is { HasAnomaly: true } && !string.IsNullOrWhiteSpace(analysis.EstimatedRomaji);

        var sb = new StringBuilder();

        if (hasContext)
        {
            sb.Append("<context>\n");
            sb.Append(recentContext!.Trim());
            sb.Append("\n</context>\n\n");
        }

        if (hasAnomaly && analysis != null)
        {
            sb.Append("<keystroke_hint>\n");
            sb.Append($"読み還元: {analysis.ReadingText}\n");
            sb.Append($"推定打鍵: {analysis.EstimatedRomaji}");
            sb.Append("\n</keystroke_hint>\n\n");
        }

        sb.Append("<target>\n");
        sb.Append(inputText);
        sb.Append("\n</target>");

        return sb.ToString();
    }

    /// <summary>
    /// 候補一覧のJSONレスポンスをパースし、クリーニング・重複排除して最大3件の候補リストを返す
    /// </summary>
    public static List<string> ParseCandidatesResponse(string? rawText, string? originalText = null)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new List<string>();
        }

        string unwrapped = StripMarkdownCodeFence(rawText.Trim());

        if (!TryParseJsonArray(unwrapped, out JsonArray? jsonArray))
        {
            int start = unwrapped.IndexOf('[');
            int end = unwrapped.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                string slice = unwrapped.Substring(start, end - start + 1);
                TryParseJsonArray(slice, out jsonArray);
            }
        }

        if (jsonArray != null)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in jsonArray)
            {
                if (item is JsonValue val && val.TryGetValue<string>(out string? itemText))
                {
                    string cleaned = CleanResponse(itemText, originalText);
                    if (!string.IsNullOrWhiteSpace(cleaned) && seen.Add(cleaned))
                    {
                        results.Add(cleaned);
                        if (results.Count >= 3)
                        {
                            break;
                        }
                    }
                }
            }

            return results;
        }

        // JSON配列のパースに失敗した場合は単一テキストとしてフォールバック
        string fallback = CleanResponse(rawText, originalText);
        return string.IsNullOrWhiteSpace(fallback)
            ? new List<string>()
            : new List<string> { fallback };
    }

    private static string StripMarkdownCodeFence(string text)
    {
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
        return text;
    }

    private static bool TryParseJsonArray(string text, out JsonArray? array)
    {
        array = null;
        try
        {
            if (JsonNode.Parse(text) is JsonArray parsed)
            {
                array = parsed;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// APIからの返答から余分な装飾（Markdownコードブロック、XML/旧形式セクションタグ、不要な引用符など）を剥がす
    /// </summary>
    public static string CleanResponse(string text, string? originalText = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();
        string? trimmedOriginal = originalText?.Trim();
        string? trimmedStartOriginal = originalText?.TrimStart();

        // 1. 先頭・末尾のコードブロック除去（原文自体がコードブロックで始まらない場合のみ）
        if (trimmedStartOriginal == null || !trimmedStartOriginal.StartsWith("```", StringComparison.Ordinal))
        {
            text = StripMarkdownCodeFence(text).Trim();
        }

        // 2. XMLタグ（<target>...</target> 等）のオウム返しを除去
        if (originalText == null || !originalText.Contains("<target>", StringComparison.OrdinalIgnoreCase))
        {
            Match xmlMatch = TargetXmlTagRegex.Match(text);
            if (xmlMatch.Success)
            {
                text = xmlMatch.Groups[1].Value.Trim();
            }
            else
            {
                text = LeadingTargetOpenTagRegex.Replace(text, string.Empty);
                text = TrailingTargetCloseTagRegex.Replace(text, string.Empty).Trim();
            }
        }

        // 3. 万が一 AI が【推敲対象テキスト...】のセクションごとオウム返しした場合、その見出し以降のみを抽出
        if (originalText == null || !originalText.Contains("【推敲対象テキスト", StringComparison.Ordinal))
        {
            Match targetMatch = TargetSectionHeaderRegex.Match(text);
            if (targetMatch.Success)
            {
                text = text.Substring(targetMatch.Index + targetMatch.Length).Trim();
            }
        }

        // 4. 先頭に残った【修正後テキスト】などの角括弧ラベルを除去（原文が「【」で始まる場合は維持）
        if (trimmedStartOriginal == null || !trimmedStartOriginal.StartsWith("【", StringComparison.Ordinal))
        {
            text = LeadingBracketLabelRegex.Replace(text, string.Empty).Trim();
        }

        // 5. 先頭・末尾の引用符を剥がす（原文が同じ引用符ペアで囲まれていない場合のみ）
        foreach (var (open, close) in QuotePairs)
        {
            if (IsWrappedWith(text, open, close))
            {
                if (trimmedOriginal == null || !IsWrappedWith(trimmedOriginal, open, close))
                {
                    text = text.Substring(open.Length, text.Length - open.Length - close.Length);
                }
                break;
            }
        }

        return text.Trim();
    }

    private static bool IsWrappedWith(string value, string open, string close)
    {
        return value.Length >= open.Length + close.Length &&
               value.StartsWith(open, StringComparison.Ordinal) &&
               value.EndsWith(close, StringComparison.Ordinal);
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
    public bool IsCancelled { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public string CorrectedText { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public bool HasChanged => Success && !IsCancelled && !string.Equals(OriginalText, CorrectedText, StringComparison.Ordinal);
}

/// <summary>
/// 複数候補取得結果
/// </summary>
public class CandidateResult
{
    public bool Success { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public List<string> Candidates { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
