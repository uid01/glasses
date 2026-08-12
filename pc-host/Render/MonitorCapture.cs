using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PcHost.Render;

/// <summary>
/// Continuously captures one DXGI output (ddagrab-equivalent, but via direct D3D11 Desktop
/// Duplication instead of shelling out to ffmpeg) as a shader-resource-bindable texture that
/// stays valid between updates. Call <see cref="TryUpdate"/> once per render tick: it does a
/// short, non-blocking-ish poll for a new frame and returns whether the texture actually
/// changed. When the screen is idle (no new frame available -- normal, expected, confirmed via
/// tools/render-probe hitting exactly this with an idle secondary monitor), the *previous*
/// texture is simply reused, which is the correct behavior for continuous capture -- unlike a
/// one-shot capture, there's no need to block waiting for something new.
///
/// Only one <see cref="IDXGIOutputDuplication"/> can be active per output at a time system-wide
/// (confirmed empirically: a second <c>DuplicateOutput</c> call on an output already being
/// duplicated fails with E_INVALIDARG) -- this class owns that lifetime for exactly one output,
/// for as long as it's alive, and releases it on <see cref="Dispose"/>.
/// </summary>
public sealed class MonitorCapture : IDisposable
{
    /// <summary>
    /// The first frame(s) returned right after DuplicateOutput() starts can be blank/unsettled
    /// (even reporting the wrong pixel format before settling) -- confirmed empirically in
    /// tools/render-probe, not documented DXGI behavior to rely on. Discarded once at startup so
    /// the capture never shows that initial blank flash.
    /// </summary>
    private const int FramesToDiscardOnStartup = 5;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;

    private ID3D11Texture2D? _srvTexture;
    private ID3D11ShaderResourceView? _srv;
    private bool _warmedUp;
    private DateTime _lastTransientFailureLogUtc = DateTime.MinValue;

    public int OutputIndex { get; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    /// <summary>Null until the first successful frame has been captured (see <see cref="WarmUp"/>).</summary>
    public ID3D11ShaderResourceView? ShaderResourceView => _srv;

    public MonitorCapture(ID3D11Device device, ID3D11DeviceContext context, int outputIndex)
    {
        _device = device;
        _context = context;
        OutputIndex = outputIndex;

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        using IDXGIAdapter1 adapter = GetAdapter(factory);
        adapter.EnumOutputs((uint)outputIndex, out IDXGIOutput output).CheckError();
        using (output)
        {
            using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(device);
        }
    }

    private static IDXGIAdapter1 GetAdapter(IDXGIFactory1 factory)
    {
        factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        return adapter;
    }

    /// <summary>
    /// Blocks briefly (up to a few seconds) discarding early frames until the capture is
    /// "settled" -- call once after construction, before relying on <see cref="ShaderResourceView"/>
    /// being non-null. Separate from <see cref="TryUpdate"/> so a caller (e.g. a session startup
    /// path) can decide how to handle the (rare, but real -- see MonitorCapture's class doc)
    /// case of an output that genuinely never produces a frame, rather than that blocking
    /// silently inside every tick's update call.
    /// </summary>
    public void WarmUp(int maxAttempts = 120, int perAttemptTimeoutMs = 500)
    {
        if (_warmedUp)
        {
            return;
        }

        int acquired = 0;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var result = _duplication.AcquireNextFrame((uint)perAttemptTimeoutMs, out _, out IDXGIResource? resource);
            if (result.Failure)
            {
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                {
                    continue;
                }
                if (IsTransientCaptureFailure(result.Code))
                {
                    LogTransientFailureRateLimited(result.Code);
                    continue;
                }
                result.CheckError();
            }

            acquired++;
            using (resource)
            {
                if (acquired <= FramesToDiscardOnStartup)
                {
                    _duplication.ReleaseFrame();
                    continue;
                }

                UpdateFromResource(resource!);
                _duplication.ReleaseFrame();
                _warmedUp = true;
                return;
            }
        }

        throw new InvalidOperationException(
            $"output_idx={OutputIndex} never produced a frame within {maxAttempts * perAttemptTimeoutMs}ms " +
            "(confirmed real behavior for a genuinely idle output, not necessarily a bug -- see MonitorCapture's class doc).");
    }

    /// <summary>
    /// Non-blocking-ish poll (a single short AcquireNextFrame call) for a new frame. Returns
    /// true if the texture was updated, false if the screen hasn't changed since the last call
    /// (in which case the existing <see cref="ShaderResourceView"/> is still valid and unchanged
    /// -- this is normal and expected on every tick where nothing on that monitor moved) OR if a
    /// known-transient capture failure was hit (see <see cref="IsTransientCaptureFailure"/>) --
    /// either way, the caller should just keep going and try again next tick.
    /// </summary>
    public bool TryUpdate(int timeoutMs = 0)
    {
        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out _, out IDXGIResource? resource);
        if (result.Failure)
        {
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            {
                return false;
            }

            if (IsTransientCaptureFailure(result.Code))
            {
                LogTransientFailureRateLimited(result.Code);
                return false;
            }

            result.CheckError();
        }

        using (resource)
        {
            UpdateFromResource(resource!);
        }
        _duplication.ReleaseFrame();
        _warmedUp = true;
        return true;
    }

    // E_ACCESSDENIED (0x80070005). Confirmed live: applying a virtual-monitor config change
    // mid-session (which triggers an elevated UAC prompt to reload the driver, see
    // pc-host-gui's VirtualMonitorConfig.ReloadDriverAsync) puts Windows on the "secure
    // desktop" for the prompt's duration, during which desktop duplication legitimately can't
    // capture anything -- AcquireNextFrame fails with this exact code, not a real error.
    // Previously this uncaught exception crashed the ENTIRE render loop (every monitor in the
    // scene, not just the one mid-reload) for the rest of the session. It clears up on its own
    // once the prompt/reload finishes, so it's treated exactly like WaitTimeout: no new frame
    // this tick, keep the existing texture, try again next tick.
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    private static bool IsTransientCaptureFailure(int hresult) => hresult == AccessDeniedHResult;

    private void LogTransientFailureRateLimited(int hresult)
    {
        var now = DateTime.UtcNow;
        if (now - _lastTransientFailureLogUtc < TimeSpan.FromSeconds(2))
        {
            return; // this can fail on every tick for as long as a UAC prompt is up -- don't spam
        }
        _lastTransientFailureLogUtc = now;
        Console.WriteLine($"[render] output_idx={OutputIndex}: transient capture failure (0x{hresult:X8}), skipping frame(s) until it clears");
    }

    private void UpdateFromResource(IDXGIResource resource)
    {
        using ID3D11Texture2D captured = resource.QueryInterface<ID3D11Texture2D>();
        var desc = captured.Description;

        // Recreate the SRV-bindable texture if the source size/format changed (e.g. display
        // mode change) or this is the first frame.
        if (_srvTexture is null || Width != desc.Width || Height != desc.Height)
        {
            _srv?.Dispose();
            _srvTexture?.Dispose();

            _srvTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            _srv = _device.CreateShaderResourceView(_srvTexture);
            Width = desc.Width;
            Height = desc.Height;
        }

        _context.CopyResource(_srvTexture, captured);
    }

    public void Dispose()
    {
        _srv?.Dispose();
        _srvTexture?.Dispose();
        _duplication.Dispose();
    }
}
