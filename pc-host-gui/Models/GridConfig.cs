using System;
using System.Collections.Generic;
using System.Linq;

namespace PcHostGui.Models;

/// <summary>
/// The rows x columns arrangement of monitors (real or virtual, by DXGI <c>output_idx</c>) to
/// tile into one canvas, plus tile size and gaps -- everything <c>pc-host --monitors</c> and
/// friends need. Mirrors pc-host's own <c>MonitorLayout</c> model
/// (pc-host/Capture/MonitorLayout.cs) but lives here separately since this side additionally
/// needs to represent *unassigned* cells while the user is still building the layout in the UI,
/// which pc-host's model has no reason to support.
/// </summary>
public sealed class GridConfig
{
    /// <summary>Row-major. A null cell means "not yet assigned a monitor".</summary>
    public List<List<int?>> Cells { get; set; } = new() { new List<int?> { null } };

    public int TileWidth { get; set; } = 1920;
    public int TileHeight { get; set; } = 1080;
    public int GapX { get; set; }
    public int GapY { get; set; }

    public int Rows => Cells.Count;
    public int Columns => Cells.Count > 0 ? Cells[0].Count : 0;

    public bool IsComplete => Cells.Count > 0 && Cells.All(row => row.All(cell => cell.HasValue));

    /// <summary>
    /// Builds pc-host's <c>--monitors</c> grid-spec string (e.g. <c>"0,1;2,3"</c>). Throws if
    /// any cell is still unassigned -- callers should check <see cref="IsComplete"/> first and
    /// surface that to the user rather than relying on this exception for UI flow.
    /// </summary>
    public string ToGridSpec()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException("Every grid cell needs a monitor assigned before the bridge can start.");
        }

        return string.Join(';', Cells.Select(row => string.Join(',', row.Select(c => c!.Value))));
    }

    public void AddRow() => Cells.Add(Enumerable.Repeat((int?)null, Columns == 0 ? 1 : Columns).ToList());

    public void RemoveRow()
    {
        if (Rows > 1)
        {
            Cells.RemoveAt(Cells.Count - 1);
        }
    }

    public void AddColumn()
    {
        foreach (var row in Cells)
        {
            row.Add(null);
        }
    }

    public void RemoveColumn()
    {
        if (Columns > 1)
        {
            foreach (var row in Cells)
            {
                row.RemoveAt(row.Count - 1);
            }
        }
    }
}
