using PcHost.Capture;
using Xunit;

namespace PcHost.Tests;

public class MonitorLayoutTests
{
    [Fact]
    public void SinglePrimary_IsOneOutputAt1920x1080()
    {
        var layout = MonitorLayout.SinglePrimary;

        Assert.Equal(new[] { 0 }, layout.OutputIndices);
        Assert.Equal(1, layout.MonitorCount);
        Assert.Equal(1920, layout.CanvasWidth);
        Assert.Equal(1080, layout.CanvasHeight);
    }

    [Fact]
    public void Parse_TwoIndices_ComputesWideCanvas()
    {
        var layout = MonitorLayout.Parse("0,1", 1920, 1080);

        Assert.Equal(new[] { 0, 1 }, layout.OutputIndices);
        Assert.Equal(2, layout.MonitorCount);
        Assert.Equal(3840, layout.CanvasWidth);
        Assert.Equal(1080, layout.CanvasHeight);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundIndices()
    {
        var layout = MonitorLayout.Parse(" 0 , 1 , 2 ", 1280, 720);

        Assert.Equal(new[] { 0, 1, 2 }, layout.OutputIndices);
        Assert.Equal(3840, layout.CanvasWidth);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => MonitorLayout.Parse("", 1920, 1080));
    }

    [Fact]
    public void BuildFilterComplex_SingleMonitor_SkipsHstack()
    {
        string graph = FfmpegCaptureSource.BuildFilterComplex(MonitorLayout.SinglePrimary);

        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1920:1080[v0];", graph);
        Assert.DoesNotContain("hstack", graph);
        Assert.EndsWith("[v0]format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_TwoMonitors_TilesLeftToRightInOutputIndexOrder()
    {
        var layout = MonitorLayout.Parse("0,1", 1920, 1080);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1920:1080[v0];", graph);
        Assert.Contains("[1:v]hwdownload,format=bgra,scale=1920:1080[v1];", graph);
        Assert.Contains("[v0][v1]hstack=inputs=2,format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_ThreeMonitors_HstacksAllThree()
    {
        var layout = MonitorLayout.Parse("2,0,1", 1280, 720);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        // Tile order follows OutputIndices order (2,0,1), not numeric output_idx order --
        // the caller controls left-to-right arrangement via the order they pass to --monitors.
        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1280:720[v0];", graph);
        Assert.Contains("[1:v]hwdownload,format=bgra,scale=1280:720[v1];", graph);
        Assert.Contains("[2:v]hwdownload,format=bgra,scale=1280:720[v2];", graph);
        Assert.Contains("[v0][v1][v2]hstack=inputs=3,format=yuv420p[vout]", graph);
    }

    [Fact]
    public void GapWidth_ZeroByDefault_NoEffectOnCanvasWidth()
    {
        var layout = MonitorLayout.Parse("0,1", 1920, 1080);

        Assert.Equal(0, layout.GapWidth);
        Assert.Equal(3840, layout.CanvasWidth);
    }

    [Fact]
    public void GapWidth_AddedBetweenTiles_ExpandsCanvasWidth()
    {
        // 2 tiles of 1920 + 1 gutter of 40 between them = 3880, not 3840.
        var layout = MonitorLayout.Parse("0,1", 1920, 1080, gapWidth: 40);

        Assert.Equal(3880, layout.CanvasWidth);
        Assert.Equal(1080, layout.CanvasHeight);
    }

    [Fact]
    public void GapWidth_WithThreeMonitors_AddsTwoGutters()
    {
        // 3 tiles of 1280 + 2 gutters of 20 = 3880.
        var layout = MonitorLayout.Parse("0,1,2", 1280, 720, gapWidth: 20);

        Assert.Equal(3880, layout.CanvasWidth);
    }

    [Fact]
    public void GapWidth_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitorLayout.Parse("0,1", 1920, 1080, gapWidth: -1));
    }

    [Fact]
    public void BuildFilterComplex_SingleMonitor_IgnoresGapWidth()
    {
        var layout = new MonitorLayout { OutputIndices = new[] { 0 }, TileWidth = 1920, TileHeight = 1080, GapWidth = 40 };
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        Assert.DoesNotContain("hstack", graph);
        Assert.DoesNotContain("gap", graph);
    }

    [Fact]
    public void BuildFilterComplex_TwoMonitorsWithGap_InsertsSpacerBetweenTiles()
    {
        var layout = MonitorLayout.Parse("0,1", 1920, 1080, gapWidth: 40);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1920:1080[v0];", graph);
        Assert.Contains("[1:v]hwdownload,format=bgra,scale=1920:1080[v1];", graph);
        // Gap spacer is stream index 2 (right after the 2 monitor inputs, indices 0 and 1).
        Assert.Contains("[2:v]format=bgra[gap0];", graph);
        Assert.Contains("[v0][gap0][v1]hstack=inputs=3,format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_ThreeMonitorsWithGap_InsertsTwoSpacersAtCorrectStreamIndices()
    {
        var layout = MonitorLayout.Parse("0,1,2", 1280, 720, gapWidth: 20);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        // 3 monitor inputs occupy stream indices 0-2; gap spacers occupy 3 and 4.
        Assert.Contains("[3:v]format=bgra[gap0];", graph);
        Assert.Contains("[4:v]format=bgra[gap1];", graph);
        Assert.Contains("[v0][gap0][v1][gap1][v2]hstack=inputs=5,format=yuv420p[vout]", graph);
    }
}
