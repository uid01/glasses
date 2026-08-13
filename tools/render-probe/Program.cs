using System.Numerics;
using RenderProbe;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

const int RenderWidth = 1920;
const int RenderHeight = 1080;

Console.WriteLine("=== Render probe: multi-object curved-monitor scene ===");

// --- Device setup ---
IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
Console.WriteLine($"Adapter: {adapter.Description1.Description}");

D3D11.D3D11CreateDevice(
    adapter,
    DriverType.Unknown,
    DeviceCreationFlags.BgraSupport,
    new[] { FeatureLevel.Level_11_0 },
    out ID3D11Device device,
    out ID3D11DeviceContext context).CheckError();
Console.WriteLine("D3D11 device created.");

// --- Capture 2 real monitor outputs as textures (with the "discard first frames" fix) ---
ID3D11ShaderResourceView CaptureOutputAsSrv(int outputIndex)
{
    adapter.EnumOutputs((uint)outputIndex, out IDXGIOutput output).CheckError();
    IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
    IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);

    const int discardCount = 5;
    ID3D11Texture2D? captured = null;
    int acquired = 0;
    for (int attempt = 0; attempt < 120 && captured is null; attempt++)
    {
        var result = duplication.AcquireNextFrame(500, out _, out IDXGIResource? resource);
        if (result.Failure)
        {
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
            result.CheckError();
        }

        acquired++;
        if (acquired <= discardCount)
        {
            resource!.Dispose();
            duplication.ReleaseFrame();
            continue;
        }

        using (resource)
        {
            captured = resource!.QueryInterface<ID3D11Texture2D>();
        }
    }

    if (captured is null)
    {
        throw new InvalidOperationException($"Never acquired a frame from output_idx={outputIndex}.");
    }

    using (captured)
    {
        var desc = captured.Description;
        var copyDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        ID3D11Texture2D srvTexture = device.CreateTexture2D(copyDesc);
        context.CopyResource(srvTexture, captured);
        Console.WriteLine($"output_idx={outputIndex}: captured {desc.Width}x{desc.Height} {desc.Format}");
        return device.CreateShaderResourceView(srvTexture);
    }
}

// Capturing once and reusing the same texture for both scene objects: the secondary monitor
// (output_idx=1) is currently idle (confirmed: AcquireNextFrame gave up after 60s of no new
// frames -- correct, expected desktop-duplication behavior for a screen with zero updates, not
// a bug), and a second DuplicateOutput call on the SAME output while the first duplication
// interface is still alive correctly fails with E_INVALIDARG (DXGI only allows one active
// duplication session per output at a time) -- also confirmed real behavior, not a bug, just not
// what this quick geometry-validation test needs to work around. Proper per-monitor duplication
// lifetime management (dispose after use, one session per output) is a real requirement for the
// actual pc-host integration, tracked separately -- this probe's only job right now is proving
// the 3D transform/curvature/camera math is correct.
ID3D11ShaderResourceView srv0 = CaptureOutputAsSrv(0);
ID3D11ShaderResourceView srv1 = srv0;

// --- Scene: two monitor panels -- one flat, one curved, positioned side by side and angled
//     inward, simulating a "wraparound" arrangement (the point of curved + 3D placement). ---
var (flatVerts, flatIdx) = MonitorMesh.Generate(width: 1.6f, height: 0.9f, curvatureDegrees: 0f);
var (curvedVerts, curvedIdx) = MonitorMesh.Generate(width: 1.6f, height: 0.9f, curvatureDegrees: 30f);

(ID3D11Buffer vb, ID3D11Buffer ib, int indexCount) MakeBuffers(MonitorMesh.Vertex[] verts, ushort[] idx)
{
    var vb = device.CreateBuffer(verts.AsSpan(), new BufferDescription
    {
        Usage = ResourceUsage.Immutable,
        ByteWidth = (uint)(verts.Length * 5 * sizeof(float)),
        BindFlags = BindFlags.VertexBuffer,
    });
    var ib = device.CreateBuffer(idx.AsSpan(), new BufferDescription
    {
        Usage = ResourceUsage.Immutable,
        ByteWidth = (uint)(idx.Length * sizeof(ushort)),
        BindFlags = BindFlags.IndexBuffer,
    });
    return (vb, ib, idx.Length);
}

var flatMesh = MakeBuffers(flatVerts, flatIdx);
var curvedMesh = MakeBuffers(curvedVerts, curvedIdx);

// Object world transforms: flat panel dead ahead, curved panel to the right and rotated inward
// (yawed toward the camera) -- deliberately NOT a simple grid, to prove arbitrary 3D placement.
Matrix4x4 flatWorld = Matrix4x4.CreateTranslation(-0.9f, 0f, 2.0f);
Matrix4x4 curvedWorld = Matrix4x4.CreateRotationY(-MathF.PI / 5f) * Matrix4x4.CreateTranslation(0.9f, 0f, 1.7f);

// --- Camera ---
Vector3 cameraPos = new(0f, 0f, 0f);
Matrix4x4 view = Matrix4x4.CreateLookAt(cameraPos, cameraPos + new Vector3(0, 0, 1), Vector3.UnitY);
Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.2f, (float)RenderWidth / RenderHeight, 0.1f, 100f);

// --- Render target ---
var renderTarget = device.CreateTexture2D(new Texture2DDescription
{
    Width = RenderWidth,
    Height = RenderHeight,
    MipLevels = 1,
    ArraySize = 1,
    Format = Format.B8G8R8A8_UNorm,
    SampleDescription = new SampleDescription(1, 0),
    Usage = ResourceUsage.Default,
    BindFlags = BindFlags.RenderTarget,
});
var rtv = device.CreateRenderTargetView(renderTarget);

// --- Shaders (row_major pragma avoids a silent transpose mismatch between System.Numerics'
//     row-major matrix layout and HLSL's column-major default packing). ---
const string shaderSource = """
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

ReadOnlyMemory<byte> vsBlob = Vortice.D3DCompiler.Compiler.Compile(shaderSource, "VSMain", "probe_vs", "vs_5_0");
ReadOnlyMemory<byte> psBlob = Vortice.D3DCompiler.Compiler.Compile(shaderSource, "PSMain", "probe_ps", "ps_5_0");
var vertexShader = device.CreateVertexShader(vsBlob.Span);
var pixelShader = device.CreatePixelShader(psBlob.Span);
var inputLayout = device.CreateInputLayout(new[]
{
    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
}, vsBlob.Span);

var sampler = device.CreateSamplerState(SamplerDescription.PointWrap with
{
    Filter = Filter.MinMagMipLinear,
    AddressU = TextureAddressMode.Clamp,
    AddressV = TextureAddressMode.Clamp,
    AddressW = TextureAddressMode.Clamp,
});

var constantBuffer = device.CreateBuffer(new BufferDescription
{
    Usage = ResourceUsage.Dynamic,
    ByteWidth = 64, // one 4x4 float matrix
    BindFlags = BindFlags.ConstantBuffer,
    CPUAccessFlags = CpuAccessFlags.Write,
});

void DrawObject((ID3D11Buffer vb, ID3D11Buffer ib, int indexCount) mesh, Matrix4x4 world, ID3D11ShaderResourceView texture)
{
    Matrix4x4 wvp = world * view * proj;
    var mapped = context.Map(constantBuffer, 0, MapMode.WriteDiscard);
    unsafe { *(Matrix4x4*)mapped.DataPointer = wvp; }
    context.Unmap(constantBuffer, 0);

    context.IASetVertexBuffer(0, mesh.vb, 5 * sizeof(float));
    context.IASetIndexBuffer(mesh.ib, Format.R16_UInt, 0);
    context.VSSetConstantBuffer(0, constantBuffer);
    context.PSSetShaderResource(0, texture);
    context.DrawIndexed((uint)mesh.indexCount, 0, 0);
}

// DIAGNOSTIC: cull mode set to None rather than trusting a hand-reasoned winding-order
// prediction -- removes winding as a variable while verifying position/curvature render
// correctly at all; can reintroduce back-face culling later as a pure optimization once the
// rest is confirmed working.
var rasterizerState = device.CreateRasterizerState(RasterizerDescription.CullNone);
context.RSSetState(rasterizerState);

// --- Draw ---
context.OMSetRenderTargets(rtv);
context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0.05f, 0.05f, 0.08f, 1f));
context.RSSetViewport(new Viewport(0, 0, RenderWidth, RenderHeight));
context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
context.IASetInputLayout(inputLayout);
context.VSSetShader(vertexShader);
context.PSSetShader(pixelShader);
context.PSSetSampler(0, sampler);

DrawObject(flatMesh, flatWorld, srv0);
DrawObject(curvedMesh, curvedWorld, srv1);
Console.WriteLine("Drew 2 objects (1 flat, 1 curved+rotated).");

// --- Readback ---
var staging = device.CreateTexture2D(new Texture2DDescription
{
    Width = RenderWidth,
    Height = RenderHeight,
    MipLevels = 1,
    ArraySize = 1,
    Format = Format.B8G8R8A8_UNorm,
    SampleDescription = new SampleDescription(1, 0),
    Usage = ResourceUsage.Staging,
    CPUAccessFlags = CpuAccessFlags.Read,
});
context.CopyResource(staging, renderTarget);
var mappedOut = context.Map(staging, 0, MapMode.Read);
try
{
    string outPath = Path.Combine(Path.GetTempPath(), "render_probe_scene.ppm");
    using var fs = new FileStream(outPath, FileMode.Create);
    fs.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{RenderWidth} {RenderHeight}\n255\n"));

    var rowBuffer = new byte[RenderWidth * 3];
    unsafe
    {
        byte* srcBase = (byte*)mappedOut.DataPointer;
        for (int y = 0; y < RenderHeight; y++)
        {
            byte* row = srcBase + y * mappedOut.RowPitch;
            for (int x = 0; x < RenderWidth; x++)
            {
                rowBuffer[x * 3 + 0] = row[x * 4 + 2];
                rowBuffer[x * 3 + 1] = row[x * 4 + 1];
                rowBuffer[x * 3 + 2] = row[x * 4 + 0];
            }
            fs.Write(rowBuffer, 0, rowBuffer.Length);
        }
    }
    Console.WriteLine($"Saved: {outPath}");
}
finally
{
    context.Unmap(staging, 0);
}

Console.WriteLine("Done.");
