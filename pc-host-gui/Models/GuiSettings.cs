namespace PcHostGui.Models;

/// <summary>
/// Everything persisted between launches, via <see cref="Services.SettingsStore"/>. Plain
/// data -- no logic -- so it round-trips through System.Text.Json without any custom converters.
/// </summary>
public sealed class GuiSettings
{
    public GridConfig Grid { get; set; } = new();
    public SceneConfig Scene { get; set; } = new();

    /// <summary>
    /// Which layout tab was active last time the bridge was started -- remembered so the app
    /// reopens on whichever mode the user was actually using instead of always defaulting back
    /// to the grid.
    /// </summary>
    public bool UseSceneMode { get; set; }

    /// <summary>
    /// Path to PcHost.exe. Defaults to the sibling pc-host/ build output relative to this repo's
    /// layout during development; a packaged install would need this pointed somewhere real --
    /// there's a Browse button in the UI for that, this is just a reasonable starting guess.
    /// </summary>
    public string PcHostExePath { get; set; } = string.Empty;

    public int ControlPort { get; set; } = 9000;
    public int VideoPort { get; set; } = 9001;
    public int InputPort { get; set; } = 9002;

    public int VirtualMonitorCount { get; set; } = 1;
    public int VirtualMonitorWidth { get; set; } = 1920;
    public int VirtualMonitorHeight { get; set; } = 1080;
    public int VirtualMonitorRefreshRate { get; set; } = 60;
}
