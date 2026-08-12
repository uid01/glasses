using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PcHostGui.Services;

/// <summary>
/// Reads/writes VirtualDrivers/Virtual-Display-Driver's vdd_settings.xml and reloads the driver
/// (PnP disable+enable) so a monitor-count/resolution change takes effect without a full system
/// restart -- see pc-host/README.md's "Virtual monitors" section for how this driver was
/// installed and verified on the dev machine (winget install, then run as Administrator).
/// </summary>
public static class VirtualMonitorConfig
{
    public const string SettingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    public static bool IsDriverInstalled => File.Exists(SettingsPath);

    /// <summary>
    /// Updates only &lt;monitors&gt;&lt;count&gt; and the &lt;resolutions&gt; list (one entry
    /// per desired monitor -- the GUI only exposes a single uniform virtual-monitor size for
    /// now, so all entries are currently identical), leaving every other setting
    /// (HardwareCursor, logging, etc.) untouched.
    /// </summary>
    public static void UpdateMonitorCount(int count, int width, int height, int refreshRate)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Virtual monitor count must be at least 1.");
        }

        if (!IsDriverInstalled)
        {
            throw new InvalidOperationException(
                $"Virtual Display Driver isn't installed (expected {SettingsPath}). See pc-host/README.md's \"Virtual monitors\" section.");
        }

        var doc = XDocument.Load(SettingsPath);
        var root = doc.Root ?? throw new InvalidOperationException("vdd_settings.xml has no root element.");

        var monitorsEl = root.Element("monitors");
        if (monitorsEl is null)
        {
            monitorsEl = new XElement("monitors");
            root.AddFirst(monitorsEl);
        }

        var countEl = monitorsEl.Element("count");
        if (countEl is null)
        {
            monitorsEl.Add(new XElement("count", count));
        }
        else
        {
            countEl.Value = count.ToString();
        }

        var resolutionsEl = root.Element("resolutions");
        if (resolutionsEl is null)
        {
            resolutionsEl = new XElement("resolutions");
            root.Add(resolutionsEl);
        }

        resolutionsEl.RemoveAll();
        for (int i = 0; i < count; i++)
        {
            resolutionsEl.Add(new XElement("resolution",
                new XElement("width", width),
                new XElement("height", height),
                new XElement("refresh_rate", refreshRate)));
        }

        doc.Save(SettingsPath);
    }

    /// <summary>
    /// Reloads the driver (PnP device disable+enable) via an elevated PowerShell invocation --
    /// triggers one UAC prompt. Returns once the elevated process exits; throws if the user
    /// declines the prompt (Process.Start surfaces that as a Win32Exception) or the reload
    /// script itself fails (e.g. can't find the device).
    /// </summary>
    public static async Task ReloadDriverAsync()
    {
        const string script =
            "$dev = Get-PnpDevice -FriendlyName 'Virtual Display Driver' -ErrorAction Stop | Select-Object -First 1; " +
            "if (-not $dev) { throw 'Virtual Display Driver device not found in Device Manager.' } " +
            "Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false; " +
            "Start-Sleep -Milliseconds 500; " +
            "Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false;";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-Command", script },
            UseShellExecute = true,
            Verb = "runas", // triggers the UAC elevation prompt
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch the elevated driver-reload process.");
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Driver reload script exited with code {process.ExitCode}. The device may need a manual disable/enable via Device Manager, or a full restart.");
        }
    }
}
