using PcHostGui.Models;
using Xunit;

namespace PcHostGui.Tests;

public class GridConfigTests
{
    [Fact]
    public void NewGridConfig_StartsAsOneByOneUnassigned()
    {
        var grid = new GridConfig();

        Assert.Equal(1, grid.Rows);
        Assert.Equal(1, grid.Columns);
        Assert.False(grid.IsComplete);
    }

    [Fact]
    public void ToGridSpec_Throws_WhenAnyCellUnassigned()
    {
        var grid = new GridConfig();
        Assert.Throws<InvalidOperationException>(() => grid.ToGridSpec());
    }

    [Fact]
    public void ToGridSpec_SingleCell_ProducesSingleIndex()
    {
        var grid = new GridConfig();
        grid.Cells[0][0] = 0;

        Assert.True(grid.IsComplete);
        Assert.Equal("0", grid.ToGridSpec());
    }

    [Fact]
    public void AddColumn_ThenAssign_ProducesCommaSeparatedRow()
    {
        var grid = new GridConfig();
        grid.AddColumn();
        Assert.Equal(2, grid.Columns);

        grid.Cells[0][0] = 5;
        grid.Cells[0][1] = 7;

        Assert.Equal("5,7", grid.ToGridSpec());
    }

    [Fact]
    public void AddRow_ThenAssign_ProducesSemicolonSeparatedRows_MatchingPcHostGridSyntax()
    {
        // This exact format ("0,1;2,3") must match pc-host's own MonitorLayout.Parse grid
        // syntax exactly -- see pc-host/Capture/MonitorLayout.cs and
        // pc-host/PcHost.Tests/MonitorLayoutTests.cs on the other side of this same contract.
        var grid = new GridConfig();
        grid.AddColumn();
        grid.AddRow();

        grid.Cells[0][0] = 0;
        grid.Cells[0][1] = 1;
        grid.Cells[1][0] = 2;
        grid.Cells[1][1] = 3;

        Assert.Equal("0,1;2,3", grid.ToGridSpec());
    }

    [Fact]
    public void RemoveRow_NeverGoesBelowOneRow()
    {
        var grid = new GridConfig();
        grid.RemoveRow();

        Assert.Equal(1, grid.Rows);
    }

    [Fact]
    public void RemoveColumn_NeverGoesBelowOneColumn()
    {
        var grid = new GridConfig();
        grid.RemoveColumn();

        Assert.Equal(1, grid.Columns);
    }

    [Fact]
    public void AddRow_NewRowIsUnassignedAndSameWidthAsExisting()
    {
        var grid = new GridConfig();
        grid.AddColumn(); // now 1x2
        grid.AddRow();    // now 2x2

        Assert.Equal(2, grid.Rows);
        Assert.Equal(2, grid.Columns);
        Assert.All(grid.Cells[1], cell => Assert.Null(cell));
    }

    [Fact]
    public void AddColumn_AddsToEveryExistingRow()
    {
        var grid = new GridConfig();
        grid.AddRow();    // 2x1
        grid.AddColumn(); // 2x2

        Assert.Equal(2, grid.Rows);
        Assert.Equal(2, grid.Columns);
        Assert.All(grid.Cells, row => Assert.Equal(2, row.Count));
    }

    [Fact]
    public void IsComplete_FalseIfAnySingleCellUnassigned()
    {
        var grid = new GridConfig();
        grid.AddColumn();
        grid.Cells[0][0] = 0;
        // Cells[0][1] left unassigned.

        Assert.False(grid.IsComplete);
    }
}
