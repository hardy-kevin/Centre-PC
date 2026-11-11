namespace CentrePC.Core;

/// <summary>
/// Test de stabilité de la mémoire vive (même principe qu'OCCT ou TestMem) : la plus grande partie de la mémoire libre est remplie
/// de motifs (adresse, aléatoire, inversé, damier), relue et vérifiée en boucle, sur plusieurs fils. Une seule différence = erreur :
/// barrette défectueuse, XMP / EXPO trop poussé, contrôleur mémoire instable. Seule la mémoire libre peut être testée depuis Windows.
/// </summary>
public sealed class MemTest : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();
    private readonly List<ulong[]> _blocks = new();
    private long _verified, _errors, _passes;
    public long BytesTested { get; }
    public int Threads { get; }
    public string FirstError { get; private set; }
    public long Errors => Interlocked.Read(ref _errors);
    /// <summary>Octets écrits puis vérifiés depuis le début.</summary>
    public long Verified => Interlocked.Read(ref _verified);
    /// <summary>Passages complets sur toute la mémoire réservée (tous les motifs).</summary>
    public long Passes => Interlocked.Read(ref _passes);

    private const int BlockWords = 32 << 20;   // 256 Mo par bloc

    /// <summary>Réserve la mémoire (fraction de la mémoire libre, en gardant de quoi faire tourner Windows) puis lance le test.</summary>
    public MemTest(int threads, double fraction = 0.8)
    {
        Threads = Math.Max(1, threads);
        var st = new MEMORYSTATUSEX { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref st);
        long target = (long)Math.Max(0, st.AvailPhys * fraction - 1.5 * (1L << 30));
        int count = (int)Math.Max(1, target / (BlockWords * 8L));
        try
        {
            for (int i = 0; i < count; i++) _blocks.Add(GC.AllocateUninitializedArray<ulong>(BlockWords));
        }
        catch (OutOfMemoryException) { }
        BytesTested = _blocks.Count * (long)BlockWords * 8;
        for (int t = 0; t < Threads; t++)
        {
            int id = t;
            var th = new Thread(() => Work(id)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Test mémoire " + t };
            _threads.Add(th);
            th.Start();
        }
    }

    private void Work(int id)
    {
        var mine = _blocks.Where((_, i) => i % Threads == id).ToList();
        ulong seed = 0x9E3779B97F4A7C15UL * (ulong)(id + 1);
        int pattern = 0;
        while (!_cts.IsCancellationRequested)
        {
            for (int b = 0; b < mine.Count && !_cts.IsCancellationRequested; b++)
            {
                var a = mine[b];
                ulong baseAddr = (ulong)b << 40 | (ulong)id << 56;
                ulong s = seed + (ulong)pattern * 0x2545F4914F6CDD1DUL + (ulong)b;
                // Écriture
                switch (pattern % 4)
                {
                    case 0: for (int i = 0; i < a.Length; i++) a[i] = baseAddr + (ulong)i; break;                         // adresse dans l'adresse
                    case 1: { ulong x = s; for (int i = 0; i < a.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; a[i] = x; } break; }   // aléatoire
                    case 2: for (int i = 0; i < a.Length; i++) a[i] = ~(baseAddr + (ulong)i); break;                      // inversé
                    default: for (int i = 0; i < a.Length; i++) a[i] = (i & 1) == 0 ? 0xAAAAAAAAAAAAAAAAUL : 0x5555555555555555UL; break;   // damier
                }
                // Relecture et vérification
                long bad = 0;
                switch (pattern % 4)
                {
                    case 0: for (int i = 0; i < a.Length; i++) if (a[i] != baseAddr + (ulong)i) bad++; break;
                    case 1: { ulong x = s; for (int i = 0; i < a.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; if (a[i] != x) bad++; } break; }
                    case 2: for (int i = 0; i < a.Length; i++) if (a[i] != ~(baseAddr + (ulong)i)) bad++; break;
                    default: for (int i = 0; i < a.Length; i++) if (a[i] != ((i & 1) == 0 ? 0xAAAAAAAAAAAAAAAAUL : 0x5555555555555555UL)) bad++; break;
                }
                if (bad > 0 && Interlocked.Add(ref _errors, bad) == bad)
                    FirstError = $"motif {(pattern % 4) switch { 0 => "adresse", 1 => "aléatoire", 2 => "inversé", _ => "damier" }}, bloc {b} du fil {id}";
                Interlocked.Add(ref _verified, (long)a.Length * 8);
            }
            pattern++;
            if (id == 0 && pattern % 4 == 0) Interlocked.Increment(ref _passes);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var t in _threads) t.Join(5000);
        _blocks.Clear();
        _cts.Dispose();
        GC.Collect();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
