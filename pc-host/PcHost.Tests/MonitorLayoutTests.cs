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
        Assert.Equal(1, layout.Rows);
        Assert.Equal(1, layout.Columns);
        Assert.Equal(1, layout.MonitorCount);
        Assert.Equal(1920, layout.CanvasWidth);
        Assert.Equal(1080, layout.CanvasHeight);
    }

    [Fact]
    public void Parse_SingleRow_IsHorizontalStrip()
    {
        var layout = MonitorLayout.Parse("0,1", 1920, 1080);

        Assert.Equal(1, layout.Rows);
        Assert.Equal(2, layout.Columns);
        Assert.Equal(new[] { 0, 1 }, layout.OutputIndices);
        Assert.Equal(3840, layout.CanvasWidth);
        Assert.Equal(1080, layout.CanvasHeight);
    }

    [Fact]
    public void Parse_TwoByTwoGrid_ParsesRowsAndColumns()
    {
        var layout = MonitorLayout.Parse("0,1;2,3", 1920, 1080);

        Assert.Equal(2, layout.Rows);
        Assert.Equal(2, layout.Columns);
        Assert.Equal(4, layout.MonitorCount);
        // Row-major flattening: row0 (0,1) then row1 (2,3).
        Assert.Equal(new[] { 0, 1, 2, 3 }, layout.OutputIndices);
        Assert.Equal(3840, layout.CanvasWidth);
        Assert.Equal(2160, layout.CanvasHeight);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundRowsAndIndices()
    {
        var layout = MonitorLayout.Parse(" 0 , 1 ; 2 , 3 ", 1280, 720);

        Assert.Equal(new[] { 0, 1, 2, 3 }, layout.OutputIndices);
        Assert.Equal(2, layout.Rows);
        Assert.Equal(2, layout.Columns);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => MonitorLayout.Parse("", 1920, 1080));
    }

    [Fact]
    public void Parse_RaggedGrid_Throws()
    {
        Assert.Throws<ArgumentException>(() => MonitorLayout.Parse("0,1,2;3,4", 1920, 1080));
    }

    [Fact]
    public void Parse_NegativeGaps_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitorLayout.Parse("0,1", 1920, 1080, gapX: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitorLayout.Parse("0,1;2,3", 1920, 1080, gapY: -1));
    }

    [Fact]
    public void CanvasSize_IncludesGapsInBothDimensions()
    {
        // 2x2 grid, 1920x1080 tiles, 40px horizontal gap, 20px vertical gap.
        var layout = MonitorLayout.Parse("0,1;2,3", 1920, 1080, gapX: 40, gapY: 20);

        Assert.Equal(3880, layout.CanvasWidth);  // 2*1920 + 40
        Assert.Equal(2180, layout.CanvasHeight); // 2*1080 + 20
    }

    [Fact]
    public void BuildFilterComplex_1x1_SkipsHstackAndVstack()
    {
        string graph = FfmpegCaptureSource.BuildFilterComplex(MonitorLayout.SinglePrimary);

        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1920:1080[v0_0];", graph);
        Assert.DoesNotContain("hstack", graph);
        Assert.DoesNotContain("vstack", graph);
        Assert.EndsWith("[v0_0]format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_1x2_UsesHstackOnly()
    {
        var layout = MonitorLayout.Parse("0,1", 1920, 1080);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1920:1080[v0_0];", graph);
        Assert.Contains("[1:v]hwdownload,format=bgra,scale=1920:1080[v0_1];", graph);
        Assert.DoesNotContain("vstack", graph);
        Assert.EndsWith("[v0_0][v0_1]hstack=inputs=2[row0];[row0]format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_2x1_UsesVstackOnly()
    {
        var layout = MonitorLayout.Parse("0;1", 1920, 1080);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1920:1080[v0_0];", graph);
        Assert.Contains("[1:v]hwdownload,format=bgra,scale=1920:1080[v1_0];", graph);
        Assert.DoesNotContain("hstack", graph);
        // Single-column rows pass the tile straight through as the "row" -- vstack takes v0_0/v1_0 directly.
        Assert.EndsWith("[v0_0][v1_0]vstack=inputs=2,format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_2x2Grid_HstacksRowsThenVstacks()
    {
        var layout = MonitorLayout.Parse("0,1;2,3", 1920, 1080);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        // All 4 monitor inputs scaled.
        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1920:1080[v0_0];", graph);
        Assert.Contains("[1:v]hwdownload,format=bgra,scale=1920:1080[v0_1];", graph);
        Assert.Contains("[2:v]hwdownload,format=bgra,scale=1920:1080[v1_0];", graph);
        Assert.Contains("[3:v]hwdownload,format=bgra,scale=1920:1080[v1_1];", graph);
        // Each row hstacked (no yuv420p yet -- that's deferred to the final vstack step).
        Assert.Contains("[v0_0][v0_1]hstack=inputs=2[row0];", graph);
        Assert.Contains("[v1_0][v1_1]hstack=inputs=2[row1];", graph);
        // Rows vstacked, yuv420p applied only at the very end.
        Assert.EndsWith("[row0][row1]vstack=inputs=2,format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_2x2GridWithGaps_InsertsHorizontalAndVerticalSpacersAtCorrectStreamIndices()
    {
        var layout = MonitorLayout.Parse("0,1;2,3", 1920, 1080, gapX: 40, gapY: 20);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        // 4 monitor inputs occupy stream indices 0-3.
        // Horizontal gap spacers: 1 per row (2 rows x (2 cols - 1) = 2 total), indices 4 and 5.
        Assert.Contains("[4:v]format=bgra[hgap0_0];", graph);
        Assert.Contains("[5:v]format=bgra[hgap1_0];", graph);
        Assert.Contains("[v0_0][hgap0_0][v0_1]hstack=inputs=3[row0];", graph);
        Assert.Contains("[v1_0][hgap1_0][v1_1]hstack=inputs=3[row1];", graph);
        // Vertical gap spacer: 1 (2 rows - 1), index 6 (right after the 2 monitor + 2 hgap inputs).
        Assert.Contains("[6:v]format=bgra[vgap0];", graph);
        Assert.EndsWith("[row0][vgap0][row1]vstack=inputs=3,format=yuv420p[vout]", graph);
    }

    [Fact]
    public void BuildFilterComplex_ThreeMonitorHorizontalStrip_StillWorks()
    {
        // Regression check: the original 1xN horizontal-strip behavior still works unchanged
        // now that it's a special case (Rows == 1) of the general grid logic.
        var layout = MonitorLayout.Parse("2,0,1", 1280, 720);
        string graph = FfmpegCaptureSource.BuildFilterComplex(layout);

        Assert.Contains("[0:v]hwdownload,format=bgra,scale=1280:720[v0_0];", graph);
        Assert.Contains("[1:v]hwdownload,format=bgra,scale=1280:720[v0_1];", graph);
        Assert.Contains("[2:v]hwdownload,format=bgra,scale=1280:720[v0_2];", graph);
        Assert.EndsWith("[v0_0][v0_1][v0_2]hstack=inputs=3[row0];[row0]format=yuv420p[vout]", graph);
    }
}
