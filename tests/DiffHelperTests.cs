using IMESupport.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IMESupport.Tests;

[TestClass]
public class DiffHelperTests
{
    [TestMethod]
    public void CalculateDiff_IdenticalStrings_ReturnsUnchanged()
    {
        var pieces = DiffHelper.CalculateDiff("こんにちは", "こんにちは");
        Assert.AreEqual(1, pieces.Count);
        Assert.AreEqual(DiffType.Unchanged, pieces[0].Type);
        Assert.AreEqual("こんにちは", pieces[0].Text);
    }

    [TestMethod]
    public void CalculateDiff_ModifiedString_DetectsChanges()
    {
        var pieces = DiffHelper.CalculateDiff("今日わ変改です", "今日は変換です");

        // Deleted と Inserted が検出されること
        Assert.IsTrue(pieces.Any(p => p.Type == DiffType.Deleted && p.Text.Contains("わ")));
        Assert.IsTrue(pieces.Any(p => p.Type == DiffType.Inserted && p.Text.Contains("は")));
        Assert.IsTrue(pieces.Any(p => p.Type == DiffType.Deleted && p.Text.Contains("改")));
        Assert.IsTrue(pieces.Any(p => p.Type == DiffType.Inserted && p.Text.Contains("換")));
    }
}
