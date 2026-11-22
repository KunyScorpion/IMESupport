using System.Text.Json.Nodes;
using IMESupport.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IMESupport.Tests;

[TestClass]
public class GeminiServiceTests
{
    [TestMethod]
    public void SupportsThinkingLevel_ReturnsExpectedResultForModels()
    {
        Assert.IsTrue(GeminiService.SupportsThinkingLevel("gemini-4-hydrogen"));
        Assert.IsTrue(GeminiService.SupportsThinkingLevel("gemini-4-neon"));
        Assert.IsTrue(GeminiService.SupportsThinkingLevel("gemini-3.5-flash-lite"));
        Assert.IsTrue(GeminiService.SupportsThinkingLevel("GEMINI-4-HYDROGEN"));

        Assert.IsFalse(GeminiService.SupportsThinkingLevel("gemini-2.5-flash"));
        Assert.IsFalse(GeminiService.SupportsThinkingLevel("gemini-2.0-flash"));
        Assert.IsFalse(GeminiService.SupportsThinkingLevel("gemini-1.5-flash"));
        Assert.IsFalse(GeminiService.SupportsThinkingLevel(string.Empty));
        Assert.IsFalse(GeminiService.SupportsThinkingLevel(null));
    }

    [TestMethod]
    public void ExtractResponseText_SkipsThoughtPartsAndConcatenatesTextParts()
    {
        string json = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  { "thought": true, "text": "内部推論テキスト..." },
                  { "text": "今日は" },
                  { "thought": false, "text": "良い天気です。" }
                ]
              }
            }
          ]
        }
        """;

        JsonNode? root = JsonNode.Parse(json);
        Assert.AreEqual("今日は良い天気です。", GeminiService.ExtractResponseText(root));
        Assert.AreEqual(string.Empty, GeminiService.ExtractResponseText(null));
        Assert.AreEqual(string.Empty, GeminiService.ExtractResponseText(JsonNode.Parse("{}")));
    }

    [TestMethod]
    public void ParseCandidatesResponse_HandlesStandardMarkdownDuplicatesLimitAndFallback()
    {
        // 1. 通常のJSON配列
        var normal = GeminiService.ParseCandidatesResponse("[\"候補A\", \"候補B\", \"候補C\"]");
        CollectionAssert.AreEqual(new[] { "候補A", "候補B", "候補C" }, normal);

        // 2. Markdownコードブロック付き + 前後説明文付きJSON
        var markdown = GeminiService.ParseCandidatesResponse(
            "以下が候補です。\n```json\n[\"「変換する」\", \"変更する\"]\n```\n以上");
        CollectionAssert.AreEqual(new[] { "変換する", "変更する" }, markdown);

        // 3. 空要素・重複除去（順序維持）および最大3件制限
        var filtered = GeminiService.ParseCandidatesResponse(
            "[\"\", \"  \", \"候補1\", \"候補2\", \"候補1\", \"候補3\", \"候補4\"]");
        CollectionAssert.AreEqual(new[] { "候補1", "候補2", "候補3" }, filtered);

        // 4. 空文字・空配列
        Assert.AreEqual(0, GeminiService.ParseCandidatesResponse(string.Empty).Count);
        Assert.AreEqual(0, GeminiService.ParseCandidatesResponse("[]").Count);

        // 5. 不正JSON時のフォールバック
        var fallback = GeminiService.ParseCandidatesResponse("```markdown\nフォールバック結果\n```");
        CollectionAssert.AreEqual(new[] { "フォールバック結果" }, fallback);
    }
}
