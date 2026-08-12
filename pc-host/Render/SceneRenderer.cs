using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PcHost.Render;

/// <summary>
/// Owns the D3D11 device and draws a list of <see cref="SceneObject"/>s from a <see cref="Camera"/>
/// into an offscreen render target, then reads that back to CPU memory for piping into ffmpeg's
/// encoder (replacing ddagrab-as-ffmpeg-input for the curved/3D-placement path -- ffmpeg becomes
/// encode-only here, fed raw frames over stdin, rather than doing its own capture+compositing).
///
/// Ported from tools/render-probe after validating against real hardware there first: the
/// row_major shader pragma (avoids a silent System.Numerics-vs-HLSL matrix packing mismatch),
/// the frame-discard-on-startup fix in <see cref="MonitorCapture"/>, and the curvature sign
/// convention in <see cref="MonitorMesh"/> were all bugs caught by actually rendering real
/// captured desktop content and looking at the output, not by reasoning about the API in the
/// abstract.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    private const string ShaderSource = """
        #pragma pack_matrix(row_major)

        cbuffer Transform : register(b0)
        {
            matrix WorldViewProj;
        };

        struct VSInput { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
        struct PSInput { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

        PSInput VSMain(VSInput input)
        {
            PSInput output;
            output.Pos = mul(float4(input.Pos, 1.0), WorldViewProj);
            output.UV = input.UV;
            return output;
        }

        Texture2D SourceTexture : register(t0);
        SamplerState SourceSampler : register(s0);

        float4 PSMain(PSInput input) : SV_TARGET
        {
            return SourceTexture.Sample(SourceSampler, input.UV);
        }
        """;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    public int Width { get; }
    public int Height { get; }

    private readonly ID3D11Texture2D _renderTarget;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11Texture2D _staging;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11RasterizerState _rasterizerState;

    public SceneRenderer(int width, int height)
    {
        Width = width;
        Height = height;

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(0, out IDXGIAdapter1 adapter);
        using (adapter)
        {
            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 },
                out ID3D11Device device,
                out ID3D11DeviceContext context).CheckError();
            Device = device;
            Context = context;
        }

        _renderTarget = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _rtv = Device.CreateRenderTargetView(_renderTarget);

        _staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        ReadOnlyMemory<byte> vsBlob = Vortice.D3DCompiler.Compiler.Compile(ShaderSource, "VSMain", "scene_vs", "vs_5_0");
        ReadOnlyMemory<byte> psBlob = Vortice.D3DCompiler.Compiler.Compile(ShaderSource, "PSMain", "scene_ps", "ps_5_0");
        _vertexShader = Device.CreateVertexShader(vsBlob.Span);
        _pixelShader = Device.CreatePixelShader(psBlob.Span);
        _inputLayout = Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        }, vsBlob.Span);

        _sampler = Device.CreateSamplerState(SamplerDescription.PointWrap with
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
        });

        _constantBuffer = Device.CreateBuffer(new BufferDescription
        {
            Usage = ResourceUsage.Dynamic,
            ByteWidth = 64,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // CullMode.None deliberately, not a hand-reasoned winding-order guess: validated in
        // tools/render-probe that getting this wrong silently drops geometry with no error, so
        // it's not worth the risk for what's purely a minor perf optimization at this stage.
        _rasterizerState = Device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    public void Render(IReadOnlyList<SceneObject> objects, Camera camera)
    {
        Context.OMSetRenderTargets(_rtv);
        Context.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 1f));
        Context.RSSetViewport(new Viewport(0, 0, Width, Height));
        Context.RSSetState(_rasterizerState);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetInputLayout(_inputLayout);
        Context.VSSetShader(_vertexShader);
        Context.PSSetShader(_pixelShader);
        Context.PSSetSampler(0, _sampler);

        Matrix4x4 viewProj = camera.ViewMatrix * camera.ProjectionMatrix;

        foreach (var obj in objects)
        {
            if (obj.Capture.ShaderResourceView is null)
            {
                continue; // not warmed up yet -- skip this object for this frame rather than block
            }

            obj.EnsureMeshBuilt(Device);

            Matrix4x4 wvp = obj.WorldMatrix * viewProj;
            var mapped = Context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
            unsafe { *(Matrix4x4*)mapped.DataPointer = wvp; }
            Context.Unmap(_constantBuffer, 0);

            Context.IASetVertexBuffer(0, obj.VertexBuffer, 5 * sizeof(float));
            Context.IASetIndexBuffer(obj.IndexBuffer, Format.R16_UInt, 0);
            Context.VSSetConstantBuffer(0, _constantBuffer);
            Context.PSSetShaderResource(0, obj.Capture.ShaderResourceView);
            Context.DrawIndexed((uint)obj.IndexCount, 0, 0);
        }
    }

    /// <summary>
    /// Copies the just-rendered frame to <paramref name="destination"/> as tightly-packed BGRA
    /// rows (stripping D3D's row-pitch padding, if any) -- the format ffmpeg's rawvideo demuxer
    /// expects on stdin with no extra framing.
    /// </summary>
    public void WriteFrameBgra(Stream destination)
    {
        Context.CopyResource(_staging, _renderTarget);
        var mapped = Context.Map(_staging, 0, MapMode.Read);
        try
        {
            int rowBytes = Width * 4;
            if (mapped.RowPitch == rowBytes)
            {
                // Common case: no padding, one shot.
                unsafe
                {
                    var span = new ReadOnlySpan<byte>((void*)mapped.DataPointer, rowBytes * Height);
                    destination.Write(span);
                }
            }
            else
            {
                var rowBuffer = new byte[rowBytes];
                unsafe
                {
                    byte* baseP = (byte*)mapped.DataPointer;
                    for (int y = 0; y < Height; y++)
                    {
                        var rowSpan = new ReadOnlySpan<byte>(baseP + y * mapped.RowPitch, rowBytes);
                        rowSpan.CopyTo(rowBuffer);
                        destination.Write(rowBuffer, 0, rowBytes);
                    }
                }
            }
        }
        finally
        {
            Context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        _rasterizerState.Dispose();
        _constantBuffer.Dispose();
        _sampler.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _staging.Dispose();
        _rtv.Dispose();
        _renderTarget.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
