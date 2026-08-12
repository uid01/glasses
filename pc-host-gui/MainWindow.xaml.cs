using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Line = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;
using PcHostGui.Models;
using PcHostGui.Services;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PcHostGui;

public partial class MainWindow : Window
{
    private readonly GuiSettings _settings;
    private readonly GridConfig _grid;
    private readonly SceneConfig _scene;
    private readonly BridgeProcessManager _bridge = new();
    private IReadOnlyList<MonitorSource> _detectedMonitors = Array.Empty<MonitorSource>();
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;

    // ---------- Scene builder (3D drag-drop) state ----------
    private const double ScenePxPerMeter = 55.0;
    private const double SceneOriginX = 240.0;
    private const double SceneOriginY = 270.0;
    private readonly Dictionary<SceneObjectConfig, Border> _sceneVisuals = new();
    private SceneObjectConfig? _selectedSceneObject;
    private bool _isDraggingScenePanel;
    private Point _dragStartMouse;
    private float _dragStartPosX;
    private float _dragStartPosZ;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        _grid = _settings.Grid;
        _scene = _settings.Scene;

        TileWidthBox.Text = _grid.TileWidth.ToString();
        TileHeightBox.Text = _grid.TileHeight.ToString();
        GapXBox.Text = _grid.GapX.ToString();
        GapYBox.Text = _grid.GapY.ToString();
        VirtualCountBox.Text = _settings.VirtualMonitorCount.ToString();
        VirtualWidthBox.Text = _settings.VirtualMonitorWidth.ToString();
        VirtualHeightBox.Text = _settings.VirtualMonitorHeight.ToString();
        VirtualRefreshBox.Text = _settings.VirtualMonitorRefreshRate.ToString();
        PcHostPathBox.Text = string.IsNullOrEmpty(_settings.PcHostExePath) ? GuessPcHostPath() : _settings.PcHostExePath;
        SceneWidthBox.Text = _scene.Width.ToString();
        SceneHeightBox.Text = _scene.Height.ToString();
        LayoutTabControl.SelectedIndex = _settings.UseSceneMode ? 1 : 0;

        _bridge.OutputReceived += line => Dispatcher.Invoke(() => AppendLog(line));
        _bridge.Exited += () => Dispatcher.Invoke(() =>
        {
            BridgeStatusText.Text = "Stopped (process exited)";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        });

        RebuildMonitorGridPanel();
        RebuildSceneCanvas();
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
        if (_selectedSceneObject is not null)
        {
            SelectSceneObject(_selectedSceneObject); // refresh the scene tab's source dropdown too
        }
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

    // ---------- Scene builder (3D drag-drop) ----------

    /// <summary>
    /// Redraws the whole top-down canvas from <see cref="_scene"/> -- same "clear and rebuild"
    /// philosophy as <see cref="RebuildMonitorGridPanel"/>, since structural changes here (add/
    /// remove/reassign) are infrequent. Live dragging instead moves an existing visual directly
    /// (see <see cref="SceneCanvas_MouseMove"/>) rather than going through this, so drag capture
    /// isn't disturbed mid-gesture.
    /// </summary>
    private void RebuildSceneCanvas()
    {
        SceneCanvas.Children.Clear();
        _sceneVisuals.Clear();

        for (int m = 1; m <= 4; m++)
        {
            double y = SceneOriginY - m * ScenePxPerMeter;
            if (y < 0)
            {
                continue;
            }

            var line = new Line
            {
                X1 = 8,
                X2 = SceneCanvas.Width - 8,
                Y1 = y,
                Y2 = y,
                Stroke = Brushes.DimGray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 4 },
            };
            SceneCanvas.Children.Add(line);

            var label = new TextBlock { Text = $"{m}m", Foreground = Brushes.Gray, FontSize = 9 };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, y - 12);
            SceneCanvas.Children.Add(label);
        }

        // Viewer marker: a fixed triangle at the world origin, pointing toward +Z (up the
        // canvas) -- matches Camera's default position/facing (pc-host/Render/Camera.cs).
        var viewer = new Polygon
        {
            Points = new PointCollection
            {
                new(SceneOriginX, SceneOriginY - 10),
                new(SceneOriginX - 8, SceneOriginY + 6),
                new(SceneOriginX + 8, SceneOriginY + 6),
            },
            Fill = Brushes.DeepSkyBlue,
        };
        SceneCanvas.Children.Add(viewer);

        foreach (var obj in _scene.Objects)
        {
            var border = new Border
            {
                Width = Math.Max(20, obj.PanelWidth * ScenePxPerMeter),
                Height = 16,
                Background = obj.OutputIndex.HasValue ? Brushes.SteelBlue : Brushes.DarkRed,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Cursor = Cursors.SizeAll,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(obj.YawDegrees),
                Tag = obj,
                Child = new TextBlock
                {
                    Text = obj.Label,
                    Foreground = Brushes.White,
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            border.MouseLeftButtonDown += SceneObjectVisual_MouseLeftButtonDown;

            PositionSceneVisual(border, obj);
            SceneCanvas.Children.Add(border);
            _sceneVisuals[obj] = border;
        }

        UpdateSceneSelectionHighlight();
    }

    /// <summary>
    /// World-to-canvas mapping: the viewer/camera sits at the world origin looking toward +Z
    /// (pc-host/Render/Camera.cs's default), so +Z maps to "up the canvas" (decreasing screen Y)
    /// and +X maps to "right" -- a top-down floor-plan view with the viewer at the bottom.
    /// </summary>
    private void PositionSceneVisual(Border border, SceneObjectConfig obj)
    {
        Canvas.SetLeft(border, SceneOriginX + obj.PosX * ScenePxPerMeter - border.Width / 2);
        Canvas.SetTop(border, SceneOriginY - obj.PosZ * ScenePxPerMeter - border.Height / 2);
    }

    private void UpdateSceneSelectionHighlight()
    {
        foreach (var (obj, border) in _sceneVisuals)
        {
            bool selected = ReferenceEquals(obj, _selectedSceneObject);
            border.BorderBrush = selected ? Brushes.Yellow : Brushes.White;
            border.BorderThickness = new Thickness(selected ? 2.5 : 1);
        }
    }

    private void AddSceneObject_Click(object sender, RoutedEventArgs e)
    {
        var obj = _scene.AddObject();
        RebuildSceneCanvas();
        SelectSceneObject(obj);
    }

    private void RemoveSceneObject_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSceneObject is null)
        {
            return;
        }

        _scene.Objects.Remove(_selectedSceneObject);
        _selectedSceneObject = null;
        RebuildSceneCanvas();
        ScenePropertiesPanel.IsEnabled = false;
        ClearScenePropertiesFields();
    }

    /// <summary>
    /// Blanks the property panel's fields so a disabled panel doesn't keep showing the last
    /// selected object's now-meaningless values.
    /// </summary>
    private void ClearScenePropertiesFields()
    {
        SceneOutputCombo.Items.Clear();
        ScenePanelWidthBox.Text = string.Empty;
        ScenePanelHeightBox.Text = string.Empty;
        ScenePosXBox.Text = string.Empty;
        ScenePosYBox.Text = string.Empty;
        ScenePosZBox.Text = string.Empty;
        ScenePitchBox.Text = string.Empty;
        SceneRollBox.Text = string.Empty;
        SceneCurvatureSlider.Value = 0;
        SceneCurvatureLabel.Text = "Curvature: 0 deg";
        SceneYawSlider.Value = 0;
        SceneYawLabel.Text = "Yaw: 0 deg";
    }

    private void SceneObjectVisual_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not SceneObjectConfig obj)
        {
            return;
        }

        SelectSceneObject(obj);

        _isDraggingScenePanel = true;
        _dragStartMouse = e.GetPosition(SceneCanvas);
        _dragStartPosX = obj.PosX;
        _dragStartPosZ = obj.PosZ;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void SceneCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingScenePanel || _selectedSceneObject is null ||
            !_sceneVisuals.TryGetValue(_selectedSceneObject, out var border))
        {
            return;
        }

        var current = e.GetPosition(SceneCanvas);
        double dx = current.X - _dragStartMouse.X;
        double dy = current.Y - _dragStartMouse.Y;

        _selectedSceneObject.PosX = _dragStartPosX + (float)(dx / ScenePxPerMeter);
        _selectedSceneObject.PosZ = _dragStartPosZ - (float)(dy / ScenePxPerMeter);

        PositionSceneVisual(border, _selectedSceneObject);
        ScenePosXBox.Text = _selectedSceneObject.PosX.ToString("0.00");
        ScenePosZBox.Text = _selectedSceneObject.PosZ.ToString("0.00");
    }

    private void SceneCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingScenePanel && _selectedSceneObject is not null &&
            _sceneVisuals.TryGetValue(_selectedSceneObject, out var border))
        {
            border.ReleaseMouseCapture();
        }

        _isDraggingScenePanel = false;
    }

    private void SelectSceneObject(SceneObjectConfig obj)
    {
        _selectedSceneObject = obj;
        UpdateSceneSelectionHighlight();
        ScenePropertiesPanel.IsEnabled = true;

        SceneOutputCombo.SelectionChanged -= SceneOutputCombo_SelectionChanged;
        SceneOutputCombo.Items.Clear();
        SceneOutputCombo.Items.Add(new ComboBoxItem { Content = "-- unassigned --", Tag = null });
        foreach (var mon in _detectedMonitors)
        {
            SceneOutputCombo.Items.Add(new ComboBoxItem { Content = mon.Label, Tag = mon.OutputIndex });
        }
        SceneOutputCombo.SelectedIndex = 0;
        for (int i = 0; i < SceneOutputCombo.Items.Count; i++)
        {
            if (SceneOutputCombo.Items[i] is ComboBoxItem item && Equals(item.Tag, obj.OutputIndex))
            {
                SceneOutputCombo.SelectedIndex = i;
                break;
            }
        }
        SceneOutputCombo.SelectionChanged += SceneOutputCombo_SelectionChanged;

        ScenePanelWidthBox.Text = obj.PanelWidth.ToString("0.00");
        ScenePanelHeightBox.Text = obj.PanelHeight.ToString("0.00");
        ScenePosXBox.Text = obj.PosX.ToString("0.00");
        ScenePosYBox.Text = obj.PosY.ToString("0.00");
        ScenePosZBox.Text = obj.PosZ.ToString("0.00");
        ScenePitchBox.Text = obj.PitchDegrees.ToString("0.0");
        SceneRollBox.Text = obj.RollDegrees.ToString("0.0");

        SceneCurvatureSlider.ValueChanged -= SceneCurvatureSlider_ValueChanged;
        SceneCurvatureSlider.Value = obj.CurvatureDegrees;
        SceneCurvatureSlider.ValueChanged += SceneCurvatureSlider_ValueChanged;
        SceneCurvatureLabel.Text = $"Curvature: {obj.CurvatureDegrees:0} deg";

        SceneYawSlider.ValueChanged -= SceneYawSlider_ValueChanged;
        SceneYawSlider.Value = obj.YawDegrees;
        SceneYawSlider.ValueChanged += SceneYawSlider_ValueChanged;
        SceneYawLabel.Text = $"Yaw: {obj.YawDegrees:0} deg";
    }

    private void SceneOutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedSceneObject is null || SceneOutputCombo.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        _selectedSceneObject.OutputIndex = selected.Tag as int?;
        RebuildSceneCanvas();
    }

    private void SceneCurvatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedSceneObject is null)
        {
            return;
        }

        _selectedSceneObject.CurvatureDegrees = (float)e.NewValue;
        SceneCurvatureLabel.Text = $"Curvature: {e.NewValue:0} deg";
    }

    private void SceneYawSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedSceneObject is null)
        {
            return;
        }

        _selectedSceneObject.YawDegrees = (float)e.NewValue;
        SceneYawLabel.Text = $"Yaw: {e.NewValue:0} deg";
        if (_sceneVisuals.TryGetValue(_selectedSceneObject, out var border) && border.RenderTransform is RotateTransform rt)
        {
            rt.Angle = e.NewValue;
        }
    }

    private void SceneNumericField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SceneWidthBox.Text, out int cw))
        {
            _scene.Width = cw;
        }
        if (int.TryParse(SceneHeightBox.Text, out int ch))
        {
            _scene.Height = ch;
        }

        if (_selectedSceneObject is null)
        {
            return;
        }

        if (float.TryParse(ScenePanelWidthBox.Text, out float pw))
        {
            _selectedSceneObject.PanelWidth = pw;
        }
        if (float.TryParse(ScenePanelHeightBox.Text, out float ph))
        {
            _selectedSceneObject.PanelHeight = ph;
        }
        if (float.TryParse(ScenePosXBox.Text, out float px))
        {
            _selectedSceneObject.PosX = px;
        }
        if (float.TryParse(ScenePosYBox.Text, out float py))
        {
            _selectedSceneObject.PosY = py;
        }
        if (float.TryParse(ScenePosZBox.Text, out float pz))
        {
            _selectedSceneObject.PosZ = pz;
        }
        if (float.TryParse(ScenePitchBox.Text, out float pitch))
        {
            _selectedSceneObject.PitchDegrees = pitch;
        }
        if (float.TryParse(SceneRollBox.Text, out float roll))
        {
            _selectedSceneObject.RollDegrees = roll;
        }

        RebuildSceneCanvas();
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
        bool sceneMode = LayoutTabControl.SelectedIndex == 1;
        _settings.UseSceneMode = sceneMode;

        List<string> args;

        if (sceneMode)
        {
            if (!_scene.IsComplete)
            {
                MessageBox.Show(this, "Assign a monitor source to every panel in the scene first.", "Scene incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // A JSON file, not more CLI flags, because a scene has an arbitrary number of
            // objects each with several fields (position/rotation/curvature/size) -- doesn't
            // fit a flat argument list the way the grid's single spec string does. See
            // pc-host/Render/SceneFileFormat.cs for the format pc-host reads.
            string sceneFilePath = Path.Combine(Path.GetTempPath(), "xrealbridge_scene.json");
            try
            {
                File.WriteAllText(sceneFilePath, _scene.ToSceneFileJson());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to write scene file: {ex.Message}", "Failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            args = new List<string>
            {
                "--scene-file", sceneFilePath,
                "--control-port", _settings.ControlPort.ToString(),
                "--video-port", _settings.VideoPort.ToString(),
                "--input-port", _settings.InputPort.ToString(),
            };
        }
        else
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

            args = new List<string>
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
        }

        _settings.PcHostExePath = PcHostPathBox.Text;
        SettingsStore.Save(_settings);

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
