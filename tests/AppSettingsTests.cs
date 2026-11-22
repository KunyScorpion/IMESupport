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
        Assert.AreEqual("LOW", settings.ThinkingLevel);
        Assert.IsFalse(settings.EnableCandidatePopup);
        Assert.AreEqual(string.Empty, settings.CustomDictionaryRules);
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

        var resultCancelled = new CorrectionResult
        {
            Success = true,
            IsCancelled = true,
            OriginalText = "変改",
            CorrectedText = "変換"
        };
        Assert.IsFalse(resultCancelled.HasChanged);
    }

    [TestMethod]
    public void GetEffectiveSystemPrompt_HandlesEmptyWhitespaceAndCustomRules()
    {
        var settingsEmpty = new AppSettings
        {
            SystemPrompt = "基本プロンプト",
            CustomDictionaryRules = string.Empty
        };
        Assert.AreEqual("基本プロンプト", settingsEmpty.GetEffectiveSystemPrompt());

        var settingsWhitespace = new AppSettings
        {
            SystemPrompt = "基本プロンプト",
            CustomDictionaryRules = "   \r\n\t  "
        };
        Assert.AreEqual("基本プロンプト", settingsWhitespace.GetEffectiveSystemPrompt());

        var settingsWithRules = new AppSettings
        {
            SystemPrompt = "基本プロンプト\n\n",
            CustomDictionaryRules = "  主人公名は『蒼真』  "
        };
        Assert.AreEqual(
            "基本プロンプト\n\n【ユーザー定義の固有名詞・表記ゆれ統一ルール（最優先で遵守すること）】\n主人公名は『蒼真』",
            settingsWithRules.GetEffectiveSystemPrompt());
    }

    [TestMethod]
    public void MigratePromptIfLegacy_MigratesLegacyAndEmptyButPreservesCustom()
    {
        // 空・null・空白のみは新デフォルトへ移行
        Assert.AreEqual(AppSettings.DefaultPrompt, AppSettings.MigratePromptIfLegacy(null));
        Assert.AreEqual(AppSettings.DefaultPrompt, AppSettings.MigratePromptIfLegacy(string.Empty));
        Assert.AreEqual(AppSettings.DefaultPrompt, AppSettings.MigratePromptIfLegacy("   \r\n\t  "));

        // v1 / v2 の旧デフォルトは改行コードの違いを吸収して新デフォルトへ移行
        Assert.AreEqual(AppSettings.DefaultPrompt, AppSettings.MigratePromptIfLegacy(AppSettings.LegacyDefaultPromptV1));
        Assert.AreEqual(AppSettings.DefaultPrompt, AppSettings.MigratePromptIfLegacy(AppSettings.LegacyDefaultPromptV1.Replace("\r\n", "\n")));
        Assert.AreEqual(AppSettings.DefaultPrompt, AppSettings.MigratePromptIfLegacy(AppSettings.LegacyDefaultPromptV2));
        Assert.AreEqual(AppSettings.DefaultPrompt, AppSettings.MigratePromptIfLegacy(AppSettings.LegacyDefaultPromptV2.Replace("\r\n", "\n")));

        // ユーザーが独自編集したプロンプトはそのまま維持
        const string customPrompt = "あなた専用のカスタムプロンプトです。";
        Assert.AreEqual(customPrompt, AppSettings.MigratePromptIfLegacy(customPrompt));
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

    [TestMethod]
    public async Task ClipboardBackupAndRestore_OverwrittenClipboard_RestoresOriginalText()
    {
        const string originalText = "退避前のオリジナル文字列_12345";
        const string tempText = "AI推敲中の一時文字列_99999";

        // 1. 初期状態をセットして退避
        await TextReplacementService.SetClipboardTextSafeAsync(originalText);
        var snapshot = await TextReplacementService.BackupClipboardSafeAsync();
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(originalText, snapshot.Text);

        // 2. 推敲・置換処理と同様にクリップボードを別テキストで上書き
        await TextReplacementService.SetClipboardTextSafeAsync(tempText);
        string current = await TextReplacementService.GetClipboardTextSafeAsync();
        Assert.AreEqual(tempText, current);

        // 3. スナップショットから復元し、元の文字列に戻っているか検証
        bool restored = await TextReplacementService.RestoreClipboardSafeAsync(snapshot);
        Assert.IsTrue(restored);

        string afterRestore = await TextReplacementService.GetClipboardTextSafeAsync();
        Assert.AreEqual(originalText, afterRestore);
    }
}
