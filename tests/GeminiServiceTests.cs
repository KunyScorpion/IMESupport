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

    [TestMethod]
    public void BuildUserPrompt_WrapsTargetInXmlTagsAndIncludesOptionalSections()
    {
        // 1. 単体入力時でも常に <target> タグで囲まれる
        string singlePrompt = GeminiService.BuildUserPrompt("今日わいい天気だね");
        Assert.AreEqual("<target>\n今日わいい天気だね\n</target>", singlePrompt);

        // 2. 文脈のみ指定時
        string contextOnlyPrompt = GeminiService.BuildUserPrompt("了解です", null, "  明日の会議について  ");
        Assert.AreEqual(
            "<context>\n明日の会議について\n</context>\n\n<target>\n了解です\n</target>",
            contextOnlyPrompt);

        // 3. 文脈 + 打鍵ヒント指定時
        var analysis = KeystrokeAnalyzer.BuildAnalysisHint("あr瓦当");
        string fullPrompt = GeminiService.BuildUserPrompt("あr瓦当", analysis, "先日はお世話になりました。");
        Assert.IsTrue(fullPrompt.Contains("<context>\n先日はお世話になりました。\n</context>"));
        Assert.IsTrue(fullPrompt.Contains("<keystroke_hint>\n読み還元: あrがとう\n推定打鍵: argatou\n</keystroke_hint>"));
        Assert.IsTrue(fullPrompt.EndsWith("<target>\nあr瓦当\n</target>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CleanResponse_PreservesOriginalFormattingAndRemovesXmlEchoes()
    {
        // 原文に元々含まれるカギ括弧は維持される
        Assert.AreEqual(
            "「今日はいい天気だね」",
            GeminiService.CleanResponse("「今日はいい天気だね」", "「今日わいい天気だね」"));

        // 原文にないカギ括弧をAIが勝手につけた場合は除去される
        Assert.AreEqual(
            "ありがとうございます",
            GeminiService.CleanResponse("「ありがとうございます」", "ありがとございます"));

        // 原文が角括弧見出しで始まる場合は削除しない
        Assert.AreEqual(
            "【重要】明日の会議について",
            GeminiService.CleanResponse("【重要】明日の会議について", "【重要】明日の会義について"));

        // 原文が角括弧見出しで始まらない場合はAIが出力したラベルを除去する
        Assert.AreEqual(
            "明日の会議について",
            GeminiService.CleanResponse("【修正後】明日の会議について", "明日の会義について"));

        // XMLタグのオウム返しを除去できる
        Assert.AreEqual(
            "ありがとうございます",
            GeminiService.CleanResponse("<context>前文</context>\n<target>ありがとうございます</target>", "ありがとございます"));
        Assert.AreEqual(
            "ありがとうございます",
            GeminiService.CleanResponse("<target>\nありがとうございます", "ありがとございます"));
        Assert.AreEqual(
            "ありがとうございます",
            GeminiService.CleanResponse("ありがとうございます</target>", "ありがとございます"));

        // 原文がコードブロックで始まる場合はコードブロックを保持する
        const string codeBlockOriginal = "```csharp\nvar x = 1;\n```";
        Assert.AreEqual(
            codeBlockOriginal,
            GeminiService.CleanResponse(codeBlockOriginal, codeBlockOriginal));
    }

    [TestMethod]
    public void ParseCandidatesResponse_PreservesOriginalQuotesAndHeadings()
    {
        var quoted = GeminiService.ParseCandidatesResponse(
            "[\"「今日はいい天気」\", \"「今日は良い天気」\"]",
            "「今日わいい天気」");
        CollectionAssert.AreEqual(new[] { "「今日はいい天気」", "「今日は良い天気」" }, quoted);

        var headed = GeminiService.ParseCandidatesResponse(
            "[\"【重要】明日の会議\", \"【重要】明日の打ち合わせ\"]",
            "【重要】明日の会義");
        CollectionAssert.AreEqual(new[] { "【重要】明日の会議", "【重要】明日の打ち合わせ" }, headed);
    }
}
