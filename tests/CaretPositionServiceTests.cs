using System.Windows;
using IMESupport.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IMESupport.Tests;

[TestClass]
public class CaretPositionServiceTests
{
    [TestMethod]
    public void CalculatePopupPosition_PlacesBelowAnchorWhenSpaceAvailable()
    {
        var (left, top) = CaretPositionService.CalculatePopupPosition(
            anchorLeft: 300,
            anchorTop: 200,
            anchorBottom: 224,
            popupWidth: 440,
            popupHeight: 240,
            workLeft: 0,
            workTop: 0,
            workRight: 1920,
            workBottom: 1040,
            shadowMargin: 10,
            gap: 6);

        // カードの見た目左端 (left + 10) が anchorLeft (300) に揃う
        Assert.AreEqual(290, left, 0.001);
        // カードの見た目上端 (top + 10) が anchorBottom (224) + gap (6) = 230 になる
        Assert.AreEqual(220, top, 0.001);
    }

    [TestMethod]
    public void CalculatePopupPosition_FlipsAboveAnchorNearBottomEdge()
    {
        var (left, top) = CaretPositionService.CalculatePopupPosition(
            anchorLeft: 400,
            anchorTop: 940,
            anchorBottom: 964,
            popupWidth: 440,
            popupHeight: 240,
            workLeft: 0,
            workTop: 0,
            workRight: 1920,
            workBottom: 1040,
            shadowMargin: 10,
            gap: 6);

        Assert.AreEqual(390, left, 0.001);
        // 下側 (960 + 240 = 1200 > 1040) に収まらないため上側へ折り返し:
        // カードの見た目下端 (top + 240 - 10) が anchorTop (940) - gap (6) = 934 になる
        Assert.AreEqual(704, top, 0.001);
    }

    [TestMethod]
    public void CalculatePopupPosition_ClampsInsideMultiMonitorWorkingArea()
    {
        // 左側に位置する負座標サブモニター (-1920 .. 0) での右端はみ出しクランプ
        var (left, top) = CaretPositionService.CalculatePopupPosition(
            anchorLeft: -150,
            anchorTop: 100,
            anchorBottom: 122,
            popupWidth: 440,
            popupHeight: 240,
            workLeft: -1920,
            workTop: 0,
            workRight: 0,
            workBottom: 1080,
            shadowMargin: 10,
            gap: 6);

        // workRight (0) - popupWidth (440) = -440 にクランプされる
        Assert.AreEqual(-440, left, 0.001);
        Assert.AreEqual(118, top, 0.001);
    }

    [TestMethod]
    public void CombineRects_UnionsValidRectsAndIgnoresInvalidOnes()
    {
        var rects = new[]
        {
            Rect.Empty,
            new Rect(10, 20, 100, 0),    // 高さ 0 のため無効
            new Rect(10, 20, 100, 400),  // 高さ > 300 の巨大矩形のため無効
            new Rect(100, 500, 180, 22), // 有効行1
            new Rect(120, 524, 140, 22)  // 有効行2
        };

        ScreenAnchorRect? combined = CaretPositionService.CombineRects(rects, "UIA.TextPattern");
        Assert.IsNotNull(combined);
        Assert.AreEqual("UIA.TextPattern", combined.Value.Source);
        Assert.AreEqual(100, combined.Value.Left, 0.001);
        Assert.AreEqual(500, combined.Value.Top, 0.001);
        Assert.AreEqual(180, combined.Value.Width, 0.001);  // maxRight(280) - minLeft(100)
        Assert.AreEqual(46, combined.Value.Height, 0.001);  // maxBottom(546) - minTop(500)
    }
}
