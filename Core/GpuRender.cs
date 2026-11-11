using System.Runtime.InteropServices;
using System.Text;

namespace CentrePC.Core;

/// <summary>
/// Charge graphique de rendu 3D (Direct3D 11, inclus dans Windows) : une scène calculée pixel par pixel (lancer de rayons,
/// ombres douces, éclairage) rendue en continu en 2560 × 1440, comme un test de contrainte de type 3DMark.
/// Compte les images rendues : le test de contrainte compare ensuite la pire boucle à la meilleure (stabilité en %).
/// Les images ne sont pas affichées (rendu hors écran) : le PC reste utilisable et rien ne dépend de l'écran.
/// </summary>
public sealed class GpuRenderLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private long _frames, _ticks;
    public string Failure { get; private set; }
    public string Name { get; }
    public long Frames => Interlocked.Read(ref _frames);
    /// <summary>Temps de rendu cumulé (secondes) : images par seconde = images / temps.</summary>
    public double Seconds => Interlocked.Read(ref _ticks) / (double)System.Diagnostics.Stopwatch.Frequency;
    public const int Width = 2560, Height = 1440;

    private const string Shaders = @"
cbuffer C : register(b0) { float time; float3 pad; }
float4 vs(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float map(float3 p)
{
    float3 q = frac(p) - 0.5;
    float d = length(q) - 0.25 - 0.05 * sin(p.x * 3 + time);
    return min(d, p.y + 1.0 + 0.2 * sin(p.x) * cos(p.z));
}
float4 ps(float4 pos : SV_Position) : SV_Target
{
    float2 uv = (pos.xy - float2(1280, 720)) / 720.0;
    float3 ro = float3(time * 0.7, 0.3, time);
    float3 rd = normalize(float3(uv, 1.5));
    float3 light = normalize(float3(0.6, 0.8, 0.3));
    float t = 0;
    [loop] for (int i = 0; i < 160; i++) { float d = map(ro + rd * t); if (d < 0.001 || t > 60) break; t += d * 0.8; }
    float3 p = ro + rd * t;
    float2 e = float2(0.001, 0);
    float3 n = normalize(float3(map(p + e.xyy) - map(p - e.xyy), map(p + e.yxy) - map(p - e.yxy), map(p + e.yyx) - map(p - e.yyx)));
    float sh = 1, s = 0.02;
    [loop] for (int j = 0; j < 48; j++) { float h = map(p + n * 0.01 + light * s); sh = min(sh, 8 * h / s); s += clamp(h, 0.02, 0.5); if (sh < 0.01) break; }
    float ao = 0;
    [unroll] for (int k = 1; k <= 5; k++) { float hk = 0.05 * k; ao += hk - map(p + n * hk); }
    float3 col = (0.15 + 0.85 * saturate(dot(n, light)) * sh) * (0.5 + 0.5 * cos(float3(0, 2, 4) + p.x + p.z)) * (1 - saturate(ao * 2)) * exp(-0.03 * t);
    return float4(col, 1);
}";

    public GpuRenderLoad(Gpu.Adapter adapter)
    {
        Name = adapter.Name;
        _thread = new Thread(() => Work(adapter)) { IsBackground = true, Name = "Rendu 3D" };
        _thread.Start();
    }

    private static IntPtr Compile(string entry, string profile, out string error)
    {
        error = null;
        var src = Encoding.ASCII.GetBytes(Shaders);
        if (D3DCompile(src, (UIntPtr)src.Length, "rendu", IntPtr.Zero, IntPtr.Zero, entry, profile, 1 << 15, 0, out var code, out var errors) != 0)
        {
            if (errors != IntPtr.Zero) error = Marshal.PtrToStringAnsi(Com.Call<BlobPtr>(errors, 3)(errors));
            Com.Release(errors);
            return IntPtr.Zero;
        }
        Com.Release(errors);
        return code;
    }

    private void Work(Gpu.Adapter target)
    {
        IntPtr device = IntPtr.Zero, ctx = IntPtr.Zero, vsCode = IntPtr.Zero, psCode = IntPtr.Zero, vs = IntPtr.Zero, ps = IntPtr.Zero,
               tex = IntPtr.Zero, rtv = IntPtr.Zero, staging = IntPtr.Zero, cb = IntPtr.Zero;
        var native = Gpu.Adapters(keepNative: true);
        try
        {
            var adapter = native.FirstOrDefault(a => a.Luid == target.Luid)?.Native ?? IntPtr.Zero;
            if (adapter == IntPtr.Zero) { Failure = "carte graphique introuvable"; return; }
            if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, null, 0, 7, out device, out var level, out ctx) != 0 || level < 0xb000)
            { Failure = "Direct3D 11 indisponible"; return; }
            vsCode = Compile("vs", "vs_5_0", out var e1);
            psCode = Compile("ps", "ps_5_0", out var e2);
            if (vsCode == IntPtr.Zero || psCode == IntPtr.Zero) { Failure = "compilation de la scène impossible " + (e1 ?? e2); return; }
            Com.Call<CreateShader>(device, 12)(device, Com.Call<BlobPtr>(vsCode, 3)(vsCode), Com.Call<BlobSize>(vsCode, 4)(vsCode), IntPtr.Zero, out vs);
            Com.Call<CreateShader>(device, 15)(device, Com.Call<BlobPtr>(psCode, 3)(psCode), Com.Call<BlobSize>(psCode, 4)(psCode), IntPtr.Zero, out ps);
            if (vs == IntPtr.Zero || ps == IntPtr.Zero) { Failure = "programmes de rendu refusés par la carte"; return; }

            var td = new TEX2D { Width = Width, Height = Height, MipLevels = 1, ArraySize = 1, Format = 28, SampleCount = 1, Usage = 0, BindFlags = 0x20 };
            if (Com.Call<CreateTexture2D>(device, 5)(device, ref td, IntPtr.Zero, out tex) != 0) { Failure = "image de rendu impossible à créer"; return; }
            if (Com.Call<CreateRtv>(device, 9)(device, tex, IntPtr.Zero, out rtv) != 0) { Failure = "cible de rendu refusée"; return; }
            var sd = new TEX2D { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = 28, SampleCount = 1, Usage = 3, CPUAccessFlags = 0x20000 };
            if (Com.Call<CreateTexture2D>(device, 5)(device, ref sd, IntPtr.Zero, out staging) != 0) { Failure = "mémoire insuffisante"; return; }
            var bd = new BUFFER_DESC { ByteWidth = 16, Usage = 0, BindFlags = 0x4 };
            if (Com.Call<CreateBuffer>(device, 3)(device, ref bd, IntPtr.Zero, out cb) != 0) { Failure = "mémoire insuffisante"; return; }

            Com.Call<SetShader>(ctx, 11)(ctx, vs, IntPtr.Zero, 0);
            Com.Call<SetShader>(ctx, 9)(ctx, ps, IntPtr.Zero, 0);
            Com.Call<SetBuffers>(ctx, 16)(ctx, 0, 1, new[] { cb });
            Com.Call<SetTopology>(ctx, 24)(ctx, 4);   // liste de triangles
            Com.Call<SetTargets>(ctx, 33)(ctx, 1, new[] { rtv }, IntPtr.Zero);
            var vp = new VIEWPORT { Width = Width, Height = Height, MaxDepth = 1 };
            Com.Call<SetViewports>(ctx, 44)(ctx, 1, ref vp);
            var draw = Com.Call<Draw>(ctx, 13);
            var update = Com.Call<Update>(ctx, 48);
            var copy = Com.Call<CopyRegion>(ctx, 46);
            var map = Com.Call<Map>(ctx, 14);
            var unmap = Com.Call<Unmap>(ctx, 15);
            var box = new BOX { Right = 1, Bottom = 1, Back = 1 };
            var constants = new float[4];
            int batch = 1;
            float time = 0;
            var sw = new System.Diagnostics.Stopwatch();
            while (!_cts.IsCancellationRequested)
            {
                sw.Restart();
                for (int i = 0; i < batch; i++)
                {
                    constants[0] = time += 1f / 60;
                    update(ctx, cb, 0, IntPtr.Zero, constants, 0, 0);
                    draw(ctx, 3, 0);
                }
                copy(ctx, staging, 0, 0, 0, 0, tex, 0, ref box);
                if (map(ctx, staging, 0, 1, 0, out _) != 0) { Failure = "la carte ne répond plus (pilote réinitialisé ?)"; return; }
                unmap(ctx, staging, 0);
                sw.Stop();
                Interlocked.Add(ref _frames, batch);
                Interlocked.Add(ref _ticks, sw.ElapsedTicks);
                double ms = sw.Elapsed.TotalMilliseconds;
                // Lots d'environ 100 ms : mesure régulière, jamais assez longs pour que Windows réinitialise la carte
                if (ms < 60 && batch < 512) batch *= 2; else if (ms > 200 && batch > 1) batch /= 2;
            }
        }
        catch (Exception ex) { Failure = ex.Message; Log.Error("Rendu 3D", ex); }
        finally
        {
            Com.Release(cb); Com.Release(staging); Com.Release(rtv); Com.Release(tex); Com.Release(ps); Com.Release(vs);
            Com.Release(psCode); Com.Release(vsCode); Com.Release(ctx); Com.Release(device);
            foreach (var a in native) Com.Release(a.Native);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(5000);
        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TEX2D { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CPUAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct BUFFER_DESC { public uint ByteWidth; public int Usage; public uint BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)] private struct VIEWPORT { public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth; }
    [StructLayout(LayoutKind.Sequential)] private struct BOX { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct MAPPED { public IntPtr Data; public uint RowPitch, DepthPitch; }

    private delegate IntPtr BlobPtr(IntPtr self);
    private delegate UIntPtr BlobSize(IntPtr self);
    private delegate int CreateShader(IntPtr self, IntPtr code, UIntPtr length, IntPtr linkage, out IntPtr shader);
    private delegate int CreateTexture2D(IntPtr self, ref TEX2D desc, IntPtr init, out IntPtr tex);
    private delegate int CreateRtv(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
    private delegate int CreateBuffer(IntPtr self, ref BUFFER_DESC desc, IntPtr init, out IntPtr buffer);
    private delegate void SetShader(IntPtr self, IntPtr shader, IntPtr instances, uint count);
    private delegate void SetBuffers(IntPtr self, uint start, uint count, IntPtr[] buffers);
    private delegate void SetTopology(IntPtr self, int topology);
    private delegate void SetTargets(IntPtr self, uint count, IntPtr[] views, IntPtr depth);
    private delegate void SetViewports(IntPtr self, uint count, ref VIEWPORT vp);
    private delegate void Draw(IntPtr self, uint count, uint start);
    private delegate void Update(IntPtr self, IntPtr dst, uint sub, IntPtr box, float[] data, uint rowPitch, uint depthPitch);
    private delegate void CopyRegion(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref BOX box);
    private delegate int Map(IntPtr self, IntPtr resource, uint sub, int type, uint flags, out MAPPED mapped);
    private delegate void Unmap(IntPtr self, IntPtr resource, uint sub);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int[] levels, uint count, uint sdk,
                                                out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] src, UIntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target,
                                         uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
}
