using IMESupport.Models;
using IMESupport.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IMESupport.Tests;

[TestClass]
public class KeystrokeAnalyzerTests
{
    [TestMethod]
    public void BuildAnalysisHint_ArGatou_DetectsAnomalyAndRestoresRomaji()
    {
        var result = KeystrokeAnalyzer.BuildAnalysisHint("あr瓦当");

        Assert.IsTrue(result.HasAnomaly, "『あr瓦当』はローマ字崩壊異常として検知される必要があります。");
        Assert.AreEqual("あrがとう", result.ReadingText);
        Assert.AreEqual("argatou", result.EstimatedRomaji);
        Assert.IsTrue(result.PromptHint.Contains("argatou"));
    }

    [TestMethod]
    public void BuildAnalysisHint_ConsonantResidualTypos_DetectsAndEstimatesRomaji()
    {
        var r1 = KeystrokeAnalyzer.BuildAnalysisHint("おhよう");
        Assert.IsTrue(r1.HasAnomaly);
        Assert.AreEqual("ohyou", r1.EstimatedRomaji);

        var r2 = KeystrokeAnalyzer.BuildAnalysisHint("sれでは");
        Assert.IsTrue(r2.HasAnomaly);
        Assert.AreEqual("sredeha", r2.EstimatedRomaji);

        var r3 = KeystrokeAnalyzer.BuildAnalysisHint("よろしくおねgいします");
        Assert.IsTrue(r3.HasAnomaly);
        Assert.AreEqual("yorosikuonegisimasu", r3.EstimatedRomaji);

        var r4 = KeystrokeAnalyzer.BuildAnalysisHint("おhようごzいます");
        Assert.IsTrue(r4.HasAnomaly);
        Assert.AreEqual("ohyougozimasu", r4.EstimatedRomaji);
    }

    [TestMethod]
    public void BuildAnalysisHint_NormalJapanese_DoesNotRaiseFalseAnomaly()
    {
        var result = KeystrokeAnalyzer.BuildAnalysisHint("今日は良い天気です。");

        Assert.IsFalse(result.HasAnomaly, "通常の正しい日本語では異常フラグが立たない必要があります。");
        Assert.AreEqual(string.Empty, result.PromptHint);
    }

    [TestMethod]
    public void CleanResponse_RemovesEchoedLabelsAndMarkdown()
    {
        string rawWithTargetHeader =
            "【推敲対象テキスト（この部分のみを修正して出力すること）】\nありがとうございます";
        Assert.AreEqual("ありがとうございます", GeminiService.CleanResponse(rawWithTargetHeader));

        string rawWithFullSections =
            "【直前の文脈（参考情報・出力に含めないこと）】\n先日はお世話になりました。\n\n" +
            "【推敲対象テキスト（この部分のみを修正して出力すること）】\n「ありがとうございます」";
        Assert.AreEqual("ありがとうございます", GeminiService.CleanResponse(rawWithFullSections));

        string rawWithSimpleLabel = "【修正後テキスト】\nおはようございます";
        Assert.AreEqual("おはようございます", GeminiService.CleanResponse(rawWithSimpleLabel));
    }

    [TestMethod]
    public void HistoryManager_GetRecentContext_ReturnsChronologicalRecentItemsExcludingErrors()
    {
        HistoryManager.Clear();

        // 10分前の古い履歴（除外されるべき）
        HistoryManager.Add(new HistoryItem
        {
            Timestamp = DateTime.Now.AddMinutes(-10),
            OriginalText = "古い文脈",
            CorrectedText = "古い文脈",
            Status = "修正不要"
        });

        // 直近の履歴1（古い順で1番目になるべき）
        HistoryManager.Add(new HistoryItem
        {
            Timestamp = DateTime.Now.AddMinutes(-2),
            OriginalText = "本日の会議ですが",
            CorrectedText = "本日の会議ですが、",
            Status = "修正完了"
        });

        // エラー履歴（除外されるべき）
        HistoryManager.Add(new HistoryItem
        {
            Timestamp = DateTime.Now.AddMinutes(-1),
            OriginalText = "エラー行",
            CorrectedText = "エラー行",
            Status = "エラー"
        });

        // 直近の履歴2（古い順で2番目になるべき）
        HistoryManager.Add(new HistoryItem
        {
            Timestamp = DateTime.Now,
            OriginalText = "14時から開始します",
            CorrectedText = "14時から開始します。",
            Status = "修正完了"
        });

        string context = HistoryManager.GetRecentContext(maxCount: 3, withinMinutes: 5);
        Assert.AreEqual("本日の会議ですが、\n14時から開始します。", context);

        HistoryManager.Clear();
    }
}
