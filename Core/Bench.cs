using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CentrePC.Core;

/// <summary>
/// Charge maximale du processeur : calcul en virgule flottante (vectoriel), hachage SHA-256 et compression Brotli,
/// chaque résultat comparé à une référence calculée au départ — une différence signale un PC instable
/// (overclocking, XMP, tension, alimentation). Les compteurs sont lus en direct par l'interface.
/// </summary>
public sealed class CpuLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();
    private long _flops, _hashBytes, _compBytes, _errors, _fmaTicks, _hashTicks, _compTicks;
    private static readonly byte[] Data = MakeData();
    private static readonly float Reference = Fma(1.0001f);
    private static readonly byte[] HashReference = SHA256.HashData(Data);
    public int Threads { get; }
    public string FirstError { get; private set; }

    /// <summary>Processeur logique imposé (test cœur par cœur), sinon -1 : Windows répartit.</summary>
    private readonly int _pin = -1;

    /// <summary>Un seul fil de calcul, fixé sur ce processeur logique (test cœur par cœur).</summary>
    public CpuLoad(int threads, int pinnedLogical) : this(threads, pinnedLogical, true) { }

    public CpuLoad(int threads) : this(threads, -1, true) { }

    private CpuLoad(int threads, int pin, bool _)
    {
        Threads = threads;
        _pin = pin;
        for (int i = 0; i < threads; i++)
        {
            var t = new Thread(Work) { IsBackground = true, Priority = pin >= 0 ? ThreadPriority.Normal : ThreadPriority.BelowNormal, Name = "Test processeur " + i };
            _threads.Add(t);
            t.Start();
        }
    }

    public long Flops => Interlocked.Read(ref _flops);
    public long HashBytes => Interlocked.Read(ref _hashBytes);
    public long CompressedBytes => Interlocked.Read(ref _compBytes);
    public long Errors => Interlocked.Read(ref _errors);
    /// <summary>Temps passé dans chaque type de calcul (cumulé sur tous les fils) : le débit se mesure sur ce temps, pas sur la durée totale.</summary>
    public double FmaSeconds => Interlocked.Read(ref _fmaTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
    /// <summary>Débits totaux (tous les fils) : octets hachés et compressés par seconde.</summary>
    public double HashRate => Rate(_hashBytes, _hashTicks);
    public double CompressRate => Rate(_compBytes, _compTicks);
    private double Rate(long work, long ticks)
    {
        long t = Interlocked.Read(ref ticks);
        return t > 0 ? Interlocked.Read(ref work) / (t / (double)System.Diagnostics.Stopwatch.Frequency) * Threads : 0;
    }

    private const int FmaIterations = 200_000;
    private static long FmaFlops => (long)FmaIterations * 8 * Vector<float>.Count * 2;

    private void Work()
    {
        if (_pin >= 0 && _pin < 64) SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << _pin));
        var token = _cts.Token;
        var compressed = new byte[Data.Length + 4096];
        var restored = new byte[Data.Length];
        int round = 0;
        while (!token.IsCancellationRequested)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int kind = round % 3;
            try
            {
                switch (round++ % 3)
                {
                    case 0:
                        if (Fma(1.0001f) != Reference) Error("calcul en virgule flottante");
                        Interlocked.Add(ref _flops, FmaFlops);
                        break;
                    case 1:
                        if (!SHA256.HashData(Data).AsSpan().SequenceEqual(HashReference)) Error("hachage SHA-256");
                        Interlocked.Add(ref _hashBytes, Data.Length);
                        break;
                    default:
                        if (!BrotliEncoder.TryCompress(Data, compressed, out var written, 4, 22)
                            || !BrotliDecoder.TryDecompress(compressed.AsSpan(0, written), restored, out var back)
                            || back != Data.Length || !restored.AsSpan().SequenceEqual(Data))
                            Error("compression");
                        Interlocked.Add(ref _compBytes, Data.Length);
                        break;
                }
            }
            catch (Exception ex) { Error(ex.GetType().Name); }
            long spent = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref kind == 0 ? ref _fmaTicks : ref kind == 1 ? ref _hashTicks : ref _compTicks, spent);
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

    private void Error(string what)
    {
        if (Interlocked.Increment(ref _errors) == 1) FirstError = what;
    }

    /// <summary>8 chaînes indépendantes de multiplications-additions vectorielles (sature les unités de calcul) ; résultat déterministe.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float Fma(float seed)
    {
        var m = new Vector<float>(0.99999f);
        var a = new Vector<float>(0.00001f);
        Vector<float> x0 = new(seed), x1 = new(seed + 0.1f), x2 = new(seed + 0.2f), x3 = new(seed + 0.3f),
                      x4 = new(seed + 0.4f), x5 = new(seed + 0.5f), x6 = new(seed + 0.6f), x7 = new(seed + 0.7f);
        for (int i = 0; i < FmaIterations; i++)
        {
            x0 = x0 * m + a; x1 = x1 * m + a; x2 = x2 * m + a; x3 = x3 * m + a;
            x4 = x4 * m + a; x5 = x5 * m + a; x6 = x6 * m + a; x7 = x7 * m + a;
        }
        return Vector.Sum(x0 + x1 + x2 + x3 + x4 + x5 + x6 + x7);
    }

    /// <summary>1 Mo de données semi-compressibles (texte répétitif mêlé d'aléatoire), identiques à chaque lancement.</summary>
    private static byte[] MakeData()
    {
        var rnd = new Random(1234);
        var b = new byte[1 << 20];
        var words = "centre pc processeur mémoire carte graphique stabilité température ventilateur fréquence "u8.ToArray();
        for (int i = 0; i < b.Length; i++) b[i] = rnd.Next(4) == 0 ? (byte)rnd.Next(256) : words[(i * 7 + rnd.Next(3)) % words.Length];
        return b;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var t in _threads) t.Join(2000);
        _cts.Dispose();
    }
}

/// <summary>
/// Charge maximale de la carte graphique par Direct3D 11 (inclus dans Windows, aucune bibliothèque ajoutée) :
/// un programme de calcul (multiplications-additions) lancé en continu, découpé en petits lots (jamais plus de quelques
/// dizaines de millisecondes : Windows réinitialise une carte bloquée plus de 2 secondes).
/// Les résultats sont relus et comparés à ceux du premier lot : une différence signale une carte instable.
/// </summary>
public sealed class GpuLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private long _flops, _errors, _computeTicks, _memBytes, _memTicks;
    private double _batchMs;
    /// <summary>Temps passé en calcul (le débit se mesure sur ce temps : les passes mémoire alternent avec le calcul).</summary>
    public double ComputeSeconds => Interlocked.Read(ref _computeTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
    /// <summary>Débit de la mémoire vidéo (octets lus + écrits par seconde).</summary>
    public double MemoryRate
    {
        get { long t = Interlocked.Read(ref _memTicks); return t > 0 ? Interlocked.Read(ref _memBytes) / (t / (double)System.Diagnostics.Stopwatch.Frequency) : 0; }
    }
    public string Name { get; }
    public string Failure { get; private set; }
    public long Flops => Interlocked.Read(ref _flops);
    public long Errors => Interlocked.Read(ref _errors);
    public bool Running => _thread.IsAlive;

    private const int Iterations = 2048, GroupSize = 256, Check = 4096;
    private const long FlopsPerThread = (long)Iterations * 8 * 2;

    private const string Shader = @"
RWStructuredBuffer<float> o : register(u0);
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float s = 1.0 + (id.x % 4096) * 0.000001;
    float x0 = s, x1 = s + 0.1, x2 = s + 0.2, x3 = s + 0.3, x4 = s + 0.4, x5 = s + 0.5, x6 = s + 0.6, x7 = s + 0.7;
    [loop] for (uint i = 0; i < ITER; i++)
    {
        x0 = mad(x0, 0.99999, 0.00001); x1 = mad(x1, 0.99999, 0.00001); x2 = mad(x2, 0.99999, 0.00001); x3 = mad(x3, 0.99999, 0.00001);
        x4 = mad(x4, 0.99999, 0.00001); x5 = mad(x5, 0.99999, 0.00001); x6 = mad(x6, 0.99999, 0.00001); x7 = mad(x7, 0.99999, 0.00001);
    }
    o[id.x] = x0 + x1 + x2 + x3 + x4 + x5 + x6 + x7;
}";

    /// <summary>Passe mémoire : lit et réécrit 256 Mo de mémoire vidéo (sollicite la mémoire et son contrôleur, comme en jeu).</summary>
    private const string MemoryShader = @"
RWStructuredBuffer<float4> m : register(u1);
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    m[id.x] = mad(m[id.x], 0.5, 1.0);
}";

    public GpuLoad(Gpu.Adapter adapter)
    {
        Name = adapter.Name;
        _thread = new Thread(() => Work(adapter)) { IsBackground = true, Name = "Test carte graphique" };
        _thread.Start();
    }

    private void Work(Gpu.Adapter target)
    {
        IntPtr adapter = IntPtr.Zero, device = IntPtr.Zero, ctx = IntPtr.Zero, cs = IntPtr.Zero, buffer = IntPtr.Zero, staging = IntPtr.Zero, uav = IntPtr.Zero, code = IntPtr.Zero,
               memCode = IntPtr.Zero, memCs = IntPtr.Zero, memBuffer = IntPtr.Zero, memUav = IntPtr.Zero;
        var native = Gpu.Adapters(keepNative: true);
        try
        {
            adapter = native.FirstOrDefault(a => a.Luid == target.Luid)?.Native ?? IntPtr.Zero;
            if (adapter == IntPtr.Zero) { Failure = "carte graphique introuvable"; return; }
            if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, null, 0, 7, out device, out var level, out ctx) != 0 || level < 0xb000)
            { Failure = "Direct3D 11 indisponible sur cette carte"; return; }

            // Programme de calcul
            var src = Encoding.ASCII.GetBytes(Shader.Replace("ITER", Iterations.ToString()));
            if (D3DCompile(src, (UIntPtr)src.Length, "test", IntPtr.Zero, IntPtr.Zero, "main", "cs_5_0", 1 << 15, 0, out code, out var errors) != 0)
            {
                Failure = "compilation du programme de test impossible";
                Com.Release(errors);
                return;
            }
            Com.Release(errors);
            var ptr = Com.Call<BlobPtr>(code, 3)(code);
            var len = Com.Call<BlobSize>(code, 4)(code);
            if (Com.Call<CreateComputeShader>(device, 18)(device, ptr, len, IntPtr.Zero, out cs) != 0) { Failure = "programme refusé par la carte"; return; }

            // Résultats : un nombre par fil d'exécution ; copie lisible des premiers pour la vérification
            const int maxGroups = 65535;
            uint elements = maxGroups * GroupSize;
            var desc = new BUFFER_DESC { ByteWidth = elements * 4, Usage = 0, BindFlags = 0x80, MiscFlags = 0x40, StructureByteStride = 4 };
            if (Com.Call<CreateBuffer>(device, 3)(device, ref desc, IntPtr.Zero, out buffer) != 0) { Failure = "mémoire de la carte insuffisante"; return; }
            var sdesc = new BUFFER_DESC { ByteWidth = Check * 4, Usage = 3, CPUAccessFlags = 0x20000 };
            if (Com.Call<CreateBuffer>(device, 3)(device, ref sdesc, IntPtr.Zero, out staging) != 0) { Failure = "mémoire insuffisante"; return; }
            var udesc = new UAV_DESC { Format = 0, ViewDimension = 1, FirstElement = 0, NumElements = elements };
            if (Com.Call<CreateUav>(device, 8)(device, buffer, ref udesc, out uav) != 0) { Failure = "vue de calcul refusée"; return; }
            // Passe mémoire (facultative : sans elle, le calcul seul continue)
            var msrc = Encoding.ASCII.GetBytes(MemoryShader);
            if (D3DCompile(msrc, (UIntPtr)msrc.Length, "memoire", IntPtr.Zero, IntPtr.Zero, "main", "cs_5_0", 1 << 15, 0, out memCode, out var merrors) == 0)
            {
                Com.Call<CreateComputeShader>(device, 18)(device, Com.Call<BlobPtr>(memCode, 3)(memCode), Com.Call<BlobSize>(memCode, 4)(memCode), IntPtr.Zero, out memCs);
                var mdesc = new BUFFER_DESC { ByteWidth = elements * 16, Usage = 0, BindFlags = 0x80, MiscFlags = 0x40, StructureByteStride = 16 };
                if (memCs != IntPtr.Zero && Com.Call<CreateBuffer>(device, 3)(device, ref mdesc, IntPtr.Zero, out memBuffer) == 0)
                {
                    var mu = new UAV_DESC { Format = 0, ViewDimension = 1, FirstElement = 0, NumElements = elements };
                    Com.Call<CreateUav>(device, 8)(device, memBuffer, ref mu, out memUav);
                }
            }
            Com.Release(merrors);
            bool memory = memUav != IntPtr.Zero;
            var setShader = Com.Call<CSSetShader>(ctx, 69);
            setShader(ctx, cs, IntPtr.Zero, 0);
            Com.Call<CSSetUavs>(ctx, 68)(ctx, 0, memory ? 2u : 1u, memory ? new[] { uav, memUav } : new[] { uav }, IntPtr.Zero);

            var dispatch = Com.Call<Dispatch>(ctx, 41);
            var copy = Com.Call<CopyRegion>(ctx, 46);
            var box = new BOX { Right = Check * 4, Bottom = 1, Back = 1 };   // seulement les premiers résultats : relecture rapide
            var map = Com.Call<Map>(ctx, 14);
            var unmap = Com.Call<Unmap>(ctx, 15);
            float[] reference = null;
            var sample = new float[Check];
            uint groups = 16;
            int repeats = 1, memRepeats = 1;
            long memPassBytes = (long)elements * 16 * 2;
            var sw = new System.Diagnostics.Stopwatch();
            while (!_cts.IsCancellationRequested)
            {
                sw.Restart();
                for (int r = 0; r < repeats; r++) dispatch(ctx, groups, 1, 1);
                copy(ctx, staging, 0, 0, 0, 0, buffer, 0, ref box);
                if (map(ctx, staging, 0, 1, 0, out var mapped) != 0) { Failure = "la carte ne répond plus (pilote réinitialisé ?)"; Interlocked.Increment(ref _errors); return; }
                Marshal.Copy(mapped.Data, sample, 0, Check);
                unmap(ctx, staging, 0);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                if (reference == null) { if (groups * GroupSize >= Check) reference = (float[])sample.Clone(); }
                else if (!sample.AsSpan().SequenceEqual(reference)) Interlocked.Increment(ref _errors);
                Interlocked.Add(ref _flops, FlopsPerThread * groups * GroupSize * repeats);
                Volatile.Write(ref _batchMs, ms);
                // Lots d'environ 40 ms : charge continue sans risque de réinitialisation par Windows
                Interlocked.Add(ref _computeTicks, sw.Elapsed.Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond);
                if (ms < 25) { if (groups < maxGroups) groups = (uint)Math.Min(maxGroups, groups * 2); else repeats = Math.Min(64, repeats * 2); }
                else if (ms > 80) { if (repeats > 1) repeats /= 2; else if (groups > 16) groups /= 2; }

                // Lot mémoire (≈ 30 ms), puis retour au calcul
                if (memory && !_cts.IsCancellationRequested)
                {
                    sw.Restart();
                    setShader(ctx, memCs, IntPtr.Zero, 0);
                    for (int r = 0; r < memRepeats; r++) dispatch(ctx, maxGroups, 1, 1);
                    setShader(ctx, cs, IntPtr.Zero, 0);
                    copy(ctx, staging, 0, 0, 0, 0, buffer, 0, ref box);
                    if (map(ctx, staging, 0, 1, 0, out var m2) != 0) { Failure = "la carte ne répond plus (pilote réinitialisé ?)"; Interlocked.Increment(ref _errors); return; }
                    unmap(ctx, staging, 0);
                    sw.Stop();
                    Interlocked.Add(ref _memBytes, memPassBytes * memRepeats);
                    Interlocked.Add(ref _memTicks, sw.Elapsed.Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond);
                    double mms = sw.Elapsed.TotalMilliseconds;
                    if (mms < 20) memRepeats = Math.Min(256, memRepeats * 2); else if (mms > 60 && memRepeats > 1) memRepeats /= 2;
                }
            }
        }
        catch (Exception ex) { Failure = ex.Message; Log.Error("Test carte graphique", ex); }
        finally
        {
            Com.Release(uav); Com.Release(staging); Com.Release(buffer); Com.Release(cs); Com.Release(code);
            Com.Release(memUav); Com.Release(memBuffer); Com.Release(memCs); Com.Release(memCode);
            Com.Release(ctx); Com.Release(device);
            foreach (var a in native) Com.Release(a.Native);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(3000);
        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct BUFFER_DESC { public uint ByteWidth; public int Usage; public uint BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)] private struct UAV_DESC { public int Format, ViewDimension; public uint FirstElement, NumElements, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BOX { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct MAPPED { public IntPtr Data; public uint RowPitch, DepthPitch; }

    private delegate IntPtr BlobPtr(IntPtr self);
    private delegate UIntPtr BlobSize(IntPtr self);
    private delegate int CreateComputeShader(IntPtr self, IntPtr code, UIntPtr length, IntPtr linkage, out IntPtr shader);
    private delegate int CreateBuffer(IntPtr self, ref BUFFER_DESC desc, IntPtr init, out IntPtr buffer);
    private delegate int CreateUav(IntPtr self, IntPtr resource, ref UAV_DESC desc, out IntPtr view);
    private delegate void CSSetShader(IntPtr self, IntPtr shader, IntPtr instances, uint count);
    private delegate void CSSetUavs(IntPtr self, uint start, uint count, IntPtr[] views, IntPtr initialCounts);
    private delegate void Dispatch(IntPtr self, uint x, uint y, uint z);
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

/// <summary>Résultat d'un test (gardé dans l'historique pour comparer d'une fois sur l'autre).</summary>
public sealed class BenchResult
{
    public DateTime Date { get; set; }
    /// <summary>« Performances » ou « Endurance 15 min ».</summary>
    public string Kind { get; set; }
    public string Cpu { get; set; }
    public string Gpu { get; set; }
    public double CpuSingleGflops { get; set; }
    public double CpuAllGflops { get; set; }
    public double CpuHashGBs { get; set; }
    public double CpuCompMBs { get; set; }
    public double GpuTflops { get; set; }
    /// <summary>Débit de la mémoire vidéo (Go/s) et charge moyenne de la carte pendant le test (%).</summary>
    public double GpuMemGBs { get; set; }
    public double? GpuUsageAvg { get; set; }
    public long Errors { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTempMax { get; set; }
    public double? CpuLimitMin { get; set; }
    public string Verdict { get; set; }
    /// <summary>Version de la méthode de mesure : seuls les résultats de même version sont comparés.</summary>
    public int Method { get; set; }
    public const int CurrentMethod = 2;
}

/// <summary>Moteur de test (Windows) partagé par l'interface : nom du processeur.</summary>
public static class Bench
{
    public static string CpuName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processeur";
        }
        catch { return "Processeur"; }
    }

    /// <summary>Programmes de surveillance (température du processeur, ventilateurs) : installés ? (chemin de l'exécutable)</summary>
    public static string FindMonitor(string which)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = which == "hwinfo"
            ? new[] { Path.Combine(pf, @"HWiNFO64\HWiNFO64.EXE"), Path.Combine(pf, @"HWiNFO64\HWiNFO64.exe") }
            : new[] { Path.Combine(pf, @"LibreHardwareMonitor\LibreHardwareMonitor.exe") };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        // Installation par winget (paquet portable) : %LOCALAPPDATA%\Microsoft\WinGet\Packages\<id>_…\
        try
        {
            var root = Path.Combine(local, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(root))
                foreach (var dir in Directory.EnumerateDirectories(root, which == "hwinfo" ? "REALiX.HWiNFO*" : "LibreHardwareMonitor*"))
                {
                    var exe = Directory.EnumerateFiles(dir, which == "hwinfo" ? "HWiNFO64.exe" : "LibreHardwareMonitor.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (exe != null) return exe;
                }
        }
        catch { }
        return null;
    }
}

/// <summary>Un cœur physique : numéro, premier processeur logique, nombre de fils (2 avec l'Hyper-Threading), type (performance / efficacité).</summary>
public sealed record CpuCore(int Index, int FirstLogical, int Logicals, int Efficiency);

/// <summary>Cœurs physiques du processeur (Windows), dans l'ordre ; « Efficiency » plus grand = cœur de performance (P-core).</summary>
public static class CpuTopology
{
    public static List<CpuCore> Cores()
    {
        var res = new List<CpuCore>();
        uint len = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);   // RelationProcessorCore
        if (len == 0) return res;
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return res;
            int offset = 0;
            while (offset < len)
            {
                int size = Marshal.ReadInt32(buf, offset + 4);
                // PROCESSOR_RELATIONSHIP : Flags (8), EfficiencyClass (9), Reserved[20], GroupCount (30), GROUP_AFFINITY (32 : Mask, Group)
                byte efficiency = Marshal.ReadByte(buf, offset + 8 + 1);
                ulong mask = (ulong)Marshal.ReadInt64(buf, offset + 8 + 24);
                ushort group = (ushort)Marshal.ReadInt16(buf, offset + 8 + 24 + 8);
                if (group == 0 && mask != 0)
                {
                    int first = System.Numerics.BitOperations.TrailingZeroCount(mask);
                    res.Add(new CpuCore(res.Count, first, System.Numerics.BitOperations.PopCount(mask), efficiency));
                }
                if (size <= 0) break;
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res.OrderBy(c => c.FirstLogical).Select((c, i) => c with { Index = i }).ToList();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}

/// <summary>
/// Microcode du processeur (ses « mises à jour internes », livrées par le BIOS et parfois par Windows Update).
/// Intel Core 13e / 14e génération de bureau : défaut de dégradation « Vmin Shift » corrigé par 0x12B (sept. 2024)
/// puis complété par 0x12F (mai 2025). Sous 0x12B, le processeur peut s'abîmer de façon irréversible.
/// </summary>
public static class CpuMicrocode
{
    /// <summary>Révision en cours (après Windows) et révision chargée par le BIOS (0 : inconnue).</summary>
    public static (uint current, uint bios) Read()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            // REG_BINARY de 8 octets (révision dans les 4 derniers) ; selon les versions de Windows : nombre ou autre taille
            static uint Rev(object v) => v switch
            {
                byte[] b when b.Length >= 8 => BitConverter.ToUInt32(b, 4) != 0 ? BitConverter.ToUInt32(b, 4) : BitConverter.ToUInt32(b, 0),
                byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                long l => (uint)(l >> 32 != 0 ? l >> 32 : l),
                int i => (uint)i,
                _ => 0
            };
            var cur = k?.GetValue("Update Revision");
            var prev = k?.GetValue("Previous Update Revision");
            if (!_logged)
            {
                _logged = true;
                string Show(object v) => v switch { null => "absent", byte[] b => "binaire " + BitConverter.ToString(b), _ => $"{v.GetType().Name} {v}" };
                Log.Info($"Microcode : Update Revision = {Show(cur)} ; Previous Update Revision = {Show(prev)} ; valeurs : {string.Join(", ", k?.GetValueNames() ?? Array.Empty<string>())}");
            }
            return (Rev(cur), Rev(prev));
        }
        catch { return (0, 0); }
    }

    private static bool _logged;

    /// <summary>Processeur concerné : Intel Core i5 / i7 / i9 de 13e ou 14e génération pour PC de bureau (pas les portables H, HX, U, P).</summary>
    public static bool Affected(string cpu)
    {
        var m = System.Text.RegularExpressions.Regex.Match(cpu ?? "", @"(13th|14th) Gen Intel\(R\) Core\(TM\) i[579]-1[34]\d{3}([A-Z]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && !System.Text.RegularExpressions.Regex.IsMatch(m.Groups[2].Value, "[HUP]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>État : (niveau, texte client, texte technique). Ok si le processeur n'est pas concerné ou à jour.</summary>
    public static (DeviceStatus level, string text, string tech) Assess(string cpu)
    {
        var (cur, bios) = Read();
        var tech = cur == 0 ? "Révision du microcode inconnue." : $"Microcode 0x{cur:X}" + (bios != 0 && bios != cur ? $" (BIOS : 0x{bios:X}, mis à jour par Windows)" : "");
        if (cur == 0)
            return Affected(cpu)
                ? (DeviceStatus.Info, "Révision du microcode du processeur illisible : vérifiez que le BIOS est postérieur à septembre 2024 (correctif du défaut de dégradation Intel).", tech)
                : (DeviceStatus.Ok, null, tech);
        if (!Affected(cpu)) return (DeviceStatus.Ok, $"Microcode du processeur : 0x{cur:X}.", tech);
        if (cur < 0x12B)
            return (DeviceStatus.Problem, $"Le microcode du processeur (0x{cur:X}) ne corrige pas le défaut de dégradation des Intel 13e / 14e génération : "
                                        + "le processeur peut s'abîmer définitivement. Mettez le BIOS à jour sans attendre.", tech + " · minimum sûr : 0x12B (septembre 2024), recommandé : 0x12F (mai 2025).");
        if (cur < 0x12F)
            return (DeviceStatus.Warning, $"Le microcode du processeur (0x{cur:X}) corrige le défaut de dégradation, mais pas sa dernière variante (0x12F, mai 2025). "
                                        + "Une mise à jour du BIOS est recommandée.", tech + " · recommandé : 0x12F.");
        return (DeviceStatus.Ok, $"Microcode du processeur à jour (0x{cur:X}) : protégé contre le défaut de dégradation des Intel 13e / 14e génération.", tech);
    }
}
