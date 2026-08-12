using System.Windows.Media.Imaging;

namespace PcHostGui.Models;

/// <summary>
/// One DXGI output (ddagrab <c>output_idx</c>) discovered by <see cref="Services.MonitorScanner"/>,
/// with a captured preview thumbnail so the user can visually confirm which physical/virtual
/// monitor a given index actually is -- deliberately not relying on matching
/// <c>System.Windows.Forms.Screen.AllScreens</c> order against ddagrab's DXGI output enumeration
/// order, since those two orderings are not guaranteed to agree (pc-host/README.md's
/// "Multi-monitor capture" section notes this was verified empirically to match on the dev
/// machine, but "verified on one machine" isn't the same as "guaranteed", especially once a
/// virtual display driver is in the mix). A quick real capture per index sidesteps needing to
/// trust that mapping at all.
/// </summary>
public sealed class MonitorSource
{
    public required int OutputIndex { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public BitmapSource? Thumbnail { get; init; }

    public string Label => $"#{OutputIndex} ({Width}x{Height})";
}
