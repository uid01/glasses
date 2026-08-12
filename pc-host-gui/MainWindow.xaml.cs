using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcHostGui.Models;
using PcHostGui.Services;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PcHostGui;

public partial class MainWindow : Window
{
    private readonly GuiSettings _settings;
    private readonly GridConfig _grid;
    private readonly BridgeProcessManager _bridge = new();
    private IReadOnlyList<MonitorSource> _detectedMonitors = Array.Empty<MonitorSource>();
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        _grid = _settings.Grid;

        TileWidthBox.Text = _grid.TileWidth.ToString();
        TileHeightBox.Text = _grid.TileHeight.ToString();
        GapXBox.Text = _grid.GapX.ToString();
        GapYBox.Text = _grid.GapY.ToString();
        VirtualCountBox.Text = _settings.VirtualMonitorCount.ToString();
        VirtualWidthBox.Text = _settings.VirtualMonitorWidth.ToString();
        VirtualHeightBox.Text = _settings.VirtualMonitorHeight.ToString();
        VirtualRefreshBox.Text = _settings.VirtualMonitorRefreshRate.ToString();
        PcHostPathBox.Text = string.IsNullOrEmpty(_settings.PcHostExePath) ? GuessPcHostPath() : _settings.PcHostExePath;

        _bridge.OutputReceived += line => Dispatcher.Invoke(() => AppendLog(line));
        _bridge.Exited += () => Dispatcher.Invoke(() =>
        {
            BridgeStatusText.Text = "Stopped (process exited)";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        });

        RebuildMonitorGridPanel();
        UpdateVddStatus();
        SetupTrayIcon();

        Closing += MainWindow_Closing;
    }

    /// <summary>
    /// Best-effort default for local development, where this exe's build output sits at
    /// pc-host-gui/bin/&lt;config&gt;/net8.0-windows/ next to a sibling pc-host/bin/&lt;config&gt;/net8.0-windows/PcHost.exe.
    /// A packaged install has no such relationship -- that's what the Browse button is for.
    /// </summary>
    private static string GuessPcHostPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "pc-host", "bin", "Debug", "net8.0-windows", "PcHost.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "pc-host", "bin", "Release", "net8.0-windows", "PcHost.exe")),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    // ---------- Scan ----------

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanStatusText.Text = "Scanning...";
        var progress = new Progress<string>(msg => ScanStatusText.Text = msg);

        try
        {
            _detectedMonitors = await MonitorScanner.ScanAsync(progress);
            ScanStatusText.Text = $"Found {_detectedMonitors.Count} monitor(s).";
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = "Scan failed -- see log.";
            AppendLog($"[scan] failed: {ex.Message}");
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }

        RenderThumbnails();
        RebuildMonitorGridPanel(); // refresh dropdown choices with the newly scanned monitors
    }

    private void RenderThumbnails()
    {
        ThumbnailPanel.Children.Clear();
        foreach (var mon in _detectedMonitors)
        {
            var stack = new StackPanel { Margin = new Thickness(4), Width = 160 };
            var img = new Image { Source = mon.Thumbnail, Height = 100, Stretch = Stretch.Uniform };
            var label = new TextBlock
            {
                Text = mon.Label,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.White,
            };
            stack.Children.Add(img);
            stack.Children.Add(label);
            ThumbnailPanel.Children.Add(stack);
        }
    }

    // ---------- Grid designer ----------

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        _grid.AddRow();
        RebuildMonitorGridPanel();
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        _grid.RemoveRow();
        RebuildMonitorGridPanel();
    }

    private void AddColumn_Click(object sender, RoutedEventArgs e)
    {
        _grid.AddColumn();
        RebuildMonitorGridPanel();
    }

    private void RemoveColumn_Click(object sender, RoutedEventArgs e)
    {
        _grid.RemoveColumn();
        RebuildMonitorGridPanel();
    }

    /// <summary>
    /// Regenerates the visual grid of ComboBoxes from scratch on any structural change (row/col
    /// add/remove, or a fresh scan changing the available monitor list) rather than trying to
    /// incrementally patch a bound collection -- simpler to reason about correctly for a grid
    /// that can grow/shrink in both dimensions, at the cost of losing in-progress ComboBox
    /// selections that don't correspond to an already-assigned _grid.Cells value (there aren't
    /// any such values, since every SelectionChanged handler writes straight back into
    /// _grid.Cells, so this is not actually lossy in practice).
    /// </summary>
    private void RebuildMonitorGridPanel()
    {
        MonitorGridPanel.Children.Clear();
        MonitorGridPanel.RowDefinitions.Clear();
        MonitorGridPanel.ColumnDefinitions.Clear();

        for (int r = 0; r < _grid.Rows; r++)
        {
            MonitorGridPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int c = 0; c < _grid.Columns; c++)
        {
            MonitorGridPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (int r = 0; r < _grid.Rows; r++)
        {
            for (int c = 0; c < _grid.Columns; c++)
            {
                int row = r;
                int col = c;

                var combo = new ComboBox();
                combo.Items.Add(new ComboBoxItem { Content = "-- unassigned --", Tag = null });
                foreach (var mon in _detectedMonitors)
                {
                    combo.Items.Add(new ComboBoxItem { Content = mon.Label, Tag = mon.OutputIndex });
                }

                int? currentValue = _grid.Cells[row][col];
                combo.SelectedIndex = 0;
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (combo.Items[i] is ComboBoxItem item && Equals(item.Tag, currentValue))
                    {
                        combo.SelectedIndex = i;
                        break;
                    }
                }

                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem selected)
                    {
                        _grid.Cells[row][col] = selected.Tag as int?;
                    }
                };

                Grid.SetRow(combo, r);
                Grid.SetColumn(combo, c);
                MonitorGridPanel.Children.Add(combo);
            }
        }
    }

    // ---------- Virtual monitors ----------

    private void UpdateVddStatus()
    {
        VddStatusText.Text = VirtualMonitorConfig.IsDriverInstalled
            ? $"Driver installed ({VirtualMonitorConfig.SettingsPath})."
            : "Driver NOT detected. Install VirtualDrivers/Virtual-Display-Driver first (see pc-host/README.md's \"Virtual monitors\" section).";
    }

    private async void ApplyVirtualMonitors_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(VirtualCountBox.Text, out int count) || count < 1 ||
            !int.TryParse(VirtualWidthBox.Text, out int width) ||
            !int.TryParse(VirtualHeightBox.Text, out int height) ||
            !int.TryParse(VirtualRefreshBox.Text, out int refresh))
        {
            MessageBox.Show(this, "Enter valid numbers for count/size/refresh rate.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            VirtualMonitorConfig.UpdateMonitorCount(count, width, height, refresh);
            AppendLog($"[vdd] wrote vdd_settings.xml: count={count}, {width}x{height}@{refresh}");
            AppendLog("[vdd] requesting elevated driver reload (UAC prompt)...");
            await VirtualMonitorConfig.ReloadDriverAsync();
            AppendLog("[vdd] driver reloaded. Click 'Scan Monitors' again to see the new virtual monitor(s).");

            _settings.VirtualMonitorCount = count;
            _settings.VirtualMonitorWidth = width;
            _settings.VirtualMonitorHeight = height;
            _settings.VirtualMonitorRefreshRate = refresh;
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendLog($"[vdd] FAILED: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Virtual monitor update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateVddStatus();
    }

    // ---------- Bridge ----------

    private void BrowsePcHost_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PcHost.exe|PcHost.exe|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            PcHostPathBox.Text = dialog.FileName;
        }
    }

    private void StartBridge_Click(object sender, RoutedEventArgs e)
    {
        if (!_grid.IsComplete)
        {
            MessageBox.Show(this, "Assign a monitor to every grid cell first.", "Grid incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TileWidthBox.Text, out int tw) || !int.TryParse(TileHeightBox.Text, out int th) ||
            !int.TryParse(GapXBox.Text, out int gx) || !int.TryParse(GapYBox.Text, out int gy))
        {
            MessageBox.Show(this, "Enter valid numbers for tile size/gaps.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _grid.TileWidth = tw;
        _grid.TileHeight = th;
        _grid.GapX = gx;
        _grid.GapY = gy;
        _settings.PcHostExePath = PcHostPathBox.Text;
        SettingsStore.Save(_settings);

        var args = new List<string>
        {
            "--monitors", _grid.ToGridSpec(),
            "--tile-width", tw.ToString(),
            "--tile-height", th.ToString(),
            "--gap-x", gx.ToString(),
            "--gap-y", gy.ToString(),
            "--control-port", _settings.ControlPort.ToString(),
            "--video-port", _settings.VideoPort.ToString(),
            "--input-port", _settings.InputPort.ToString(),
        };

        try
        {
            LogBox.Clear();
            _bridge.Start(PcHostPathBox.Text, args);
            AppendLog($"[gui] started: {PcHostPathBox.Text} {string.Join(' ', args)}");
            BridgeStatusText.Text = "Running";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog($"[gui] failed to start bridge: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopBridge_Click(object sender, RoutedEventArgs e)
    {
        _bridge.Stop();
        BridgeStatusText.Text = "Stopped";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        AppendLog("[gui] bridge stopped.");
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // ---------- System tray ----------

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_trayIcon))]
    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            Text = "XrealBridge Monitor Config",
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _isExiting = true;
            Application.Current.Shutdown();
        });
        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Closing the window (the X button) minimizes to tray instead of exiting, so the bridge
    /// keeps running in the background -- the whole point of a tray icon. Only the tray menu's
    /// "Exit" (which sets _isExiting first) actually terminates the app.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            _trayIcon?.Dispose();
            _bridge.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
