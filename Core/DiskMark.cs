using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CentrePC.Core;

/// <summary>Résultats au format CrystalDiskMark (Mo/s, 1 Mo = 10^6 octets) et IOPS des accès 4 Ko.</summary>
public sealed record DiskMarkResult(double SeqReadMBs, double SeqWriteMBs, double RndQ32ReadMBs, double RndQ32WriteMBs, double RndQ1ReadMBs, double RndQ1WriteMBs)
{
    public double RndQ32ReadIops => RndQ32ReadMBs * 1e6 / 4096;
    public double RndQ1ReadIops => RndQ1ReadMBs * 1e6 / 4096;
}

/// <summary>
/// Mesure de disque selon les tests de référence de CrystalDiskMark, sans le cache de Windows (FILE_FLAG_NO_BUFFERING) :
/// SEQ1M Q8T1 (séquentiel, blocs de 1 Mio, 8 accès en parallèle), RND4K Q32T1 (aléatoire 4 Kio, 32 en parallèle), RND4K Q1T1 (un à la fois).
/// Fichier de test de 1 Gio entièrement écrit d'abord (sinon Windows renvoie des zéros sans lire le disque), supprimé à la fin.
/// </summary>
public static class DiskMark
{
    public static async Task<DiskMarkResult> RunAsync(string drive, long size, double seconds, IProgress<string> progress, CancellationToken ct)
    {
        var dir = string.Equals(Path.GetPathRoot(Path.GetTempPath()), drive, StringComparison.OrdinalIgnoreCase) ? Path.GetTempPath() : drive;
        var path = Path.Combine(dir, "CentrePC-mesure-disque.tmp");
        const FileOptions NoBuffering = (FileOptions)0x20000000;
        try
        {
            using var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous | NoBuffering | FileOptions.DeleteOnClose, size);
            progress?.Report("Écriture séquentielle (SEQ1M Q8T1)…");
            var seqW = await RunAsync(h, size, 1 << 20, 8, write: true, sequential: true, seconds, ct, fillFirst: true);
            progress?.Report("Lecture séquentielle (SEQ1M Q8T1)…");
            var seqR = await RunAsync(h, size, 1 << 20, 8, write: false, sequential: true, seconds, ct);
            progress?.Report("Lecture aléatoire 4 Ko, 32 en parallèle (RND4K Q32T1)…");
            var r32R = await RunAsync(h, size, 4096, 32, write: false, sequential: false, seconds, ct);
            progress?.Report("Écriture aléatoire 4 Ko, 32 en parallèle (RND4K Q32T1)…");
            var r32W = await RunAsync(h, size, 4096, 32, write: true, sequential: false, seconds, ct);
            progress?.Report("Lecture aléatoire 4 Ko, une à la fois (RND4K Q1T1)…");
            var r1R = await RunAsync(h, size, 4096, 1, write: false, sequential: false, seconds, ct);
            progress?.Report("Écriture aléatoire 4 Ko, une à la fois (RND4K Q1T1)…");
            var r1W = await RunAsync(h, size, 4096, 1, write: true, sequential: false, seconds, ct);
            return new DiskMarkResult(seqR, seqW, r32R, r32W, r1R, r1W);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>Une mesure : <paramref name="queue"/> accès simultanés, pendant <paramref name="seconds"/> secondes ; résultat en Mo/s.</summary>
    private static async Task<double> RunAsync(Microsoft.Win32.SafeHandles.SafeFileHandle h, long size, int block, int queue, bool write, bool sequential, double seconds,
                                               CancellationToken ct, bool fillFirst = false)
    {
        long blocks = size / block;
        long next = 0, done = 0;
        var sw = Stopwatch.StartNew();
        var until = TimeSpan.FromSeconds(seconds);
        async Task Worker(int id)
        {
            using var buf = new AlignedBuffer(block);
            new Random(id + 1).NextBytes(buf.Memory.Span);
            var rnd = new Random(1000 + id);
            while (!ct.IsCancellationRequested)
            {
                long index;
                if (sequential)
                {
                    index = Interlocked.Increment(ref next) - 1;
                    if (fillFirst && index >= blocks && sw.Elapsed >= until) break;   // remplissage complet terminé et durée atteinte
                    if (!fillFirst && sw.Elapsed >= until) break;
                    index %= blocks;
                }
                else
                {
                    if (sw.Elapsed >= until) break;
                    index = rnd.NextInt64(blocks);
                }
                if (write) await RandomAccess.WriteAsync(h, buf.Memory, index * block, ct);
                else await RandomAccess.ReadAsync(h, buf.Memory, index * block, ct);
                Interlocked.Add(ref done, block);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, queue).Select(Worker));
        sw.Stop();
        return done / sw.Elapsed.TotalSeconds / 1e6;
    }

    /// <summary>Mémoire alignée sur 4 Ko (exigé par les accès sans cache) : tableau épinglé, décalé jusqu'à une adresse multiple de 4096.</summary>
    private sealed class AlignedBuffer : IDisposable
    {
        public Memory<byte> Memory { get; }
        public AlignedBuffer(int length)
        {
            var array = GC.AllocateArray<byte>(length + 4096, pinned: true);
            long address = Marshal.UnsafeAddrOfPinnedArrayElement(array, 0).ToInt64();
            int offset = (int)((4096 - address % 4096) % 4096);
            Memory = array.AsMemory(offset, length);
        }
        public void Dispose() { }
    }

    // ------------------------------------------------------------------ lien PCI Express des SSD NVMe
    /// <summary>Lien PCIe de chaque disque NVMe : génération et nombre de lignes, actuels et maximum.</summary>
    public sealed record PcieLink(string Disk, int Gen, int Width, int MaxGen, int MaxWidth)
    {
        public bool Degraded => Gen < MaxGen || Width < MaxWidth;
        public string Text => $"PCIe {Gen}.0 ×{Width}" + (Degraded ? $" (le SSD et son emplacement permettent PCIe {MaxGen}.0 ×{MaxWidth})" : "");
    }

    public static List<PcieLink> PcieLinks()
    {
        var res = new List<PcieLink>();
        const string script = @"
Get-PnpDevice -Class DiskDrive -PresentOnly | ForEach-Object {
  $parent = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_Parent -ErrorAction SilentlyContinue).Data
  if ($parent -like 'PCI\*') {
    $p = @{}
    Get-PnpDeviceProperty -InstanceId $parent -KeyName DEVPKEY_PciDevice_CurrentLinkSpeed,DEVPKEY_PciDevice_CurrentLinkWidth,DEVPKEY_PciDevice_MaxLinkSpeed,DEVPKEY_PciDevice_MaxLinkWidth -ErrorAction SilentlyContinue |
      ForEach-Object { $p[$_.KeyName] = $_.Data }
    $_.FriendlyName + '|' + $p['DEVPKEY_PciDevice_CurrentLinkSpeed'] + '|' + $p['DEVPKEY_PciDevice_CurrentLinkWidth'] + '|' + $p['DEVPKEY_PciDevice_MaxLinkSpeed'] + '|' + $p['DEVPKEY_PciDevice_MaxLinkWidth']
  }
}";
        try
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 });
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var f = line.Split('|');
                if (f.Length == 5 && int.TryParse(f[1], out var g) && int.TryParse(f[2], out var w) && int.TryParse(f[3], out var mg) && int.TryParse(f[4], out var mw) && g > 0 && w > 0)
                    res.Add(new PcieLink(f[0], g, w, Math.Max(g, mg), Math.Max(w, mw)));
            }
        }
        catch (Exception ex) { Log.Error("Lien PCIe des disques", ex); }
        return res;
    }
}
