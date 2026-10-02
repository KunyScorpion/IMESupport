using IMESupport.Models;
using IMESupport.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IMESupport.Tests;

[TestClass]
public class AppSettingsTests
{
    [TestMethod]
    public void DefaultSettings_HasExpectedValues()
    {
        var settings = new AppSettings();

        Assert.AreEqual("gemini-2.5-flash", settings.Model);
        Assert.AreEqual(350, settings.DoubleTapIntervalMs);
        Assert.AreEqual(0x1C, settings.TriggerVirtualKey); // VK_CONVERT
        Assert.AreEqual("Convert", settings.TriggerKeyType);
        Assert.IsFalse(settings.ShowNotification);
        Assert.IsTrue(settings.RestoreClipboard);
        Assert.IsTrue(settings.AutoSelectLineWhenEmpty);
        Assert.IsTrue(settings.SystemPrompt.Contains("タイピングミス"));
        Assert.IsTrue(settings.SystemPrompt.Contains("同音異義語"));
    }

    [TestMethod]
    public void CorrectionResult_HasChanged_WorksCorrectly()
    {
        var resultNoChange = new CorrectionResult
        {
            Success = true,
            OriginalText = "テスト",
            CorrectedText = "テスト"
        };
        Assert.IsFalse(resultNoChange.HasChanged);

        var resultChanged = new CorrectionResult
        {
            Success = true,
            OriginalText = "変改",
            CorrectedText = "変換"
        };
        Assert.IsTrue(resultChanged.HasChanged);

        var resultFailed = new CorrectionResult
        {
            Success = false,
            OriginalText = "変改",
            CorrectedText = "変換"
        };
        Assert.IsFalse(resultFailed.HasChanged);
    }

    [TestMethod]
    public void CleanResponse_RemovesMarkdownAndQuotes()
    {
        Assert.AreEqual("こんにちは", GeminiService.CleanResponse("```markdown\nこんにちは\n```"));
        Assert.AreEqual("こんにちは", GeminiService.CleanResponse("```\nこんにちは\n```"));
        Assert.AreEqual("こんにちは", GeminiService.CleanResponse("「こんにちは」"));
        Assert.AreEqual("こんにちは", GeminiService.CleanResponse("\"こんにちは\""));
        Assert.AreEqual("こんにちは", GeminiService.CleanResponse("  こんにちは  "));
    }
}
