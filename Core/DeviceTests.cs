using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CentrePC.Core;

/// <summary>
/// Moteurs des outils de test : sons générés (haut-parleurs gauche / droite), capture du micro (niveau en direct,
/// enregistrement et réécoute), manettes Xbox (XInput : boutons, sticks, gâchettes, vibrations), vitesse d'Internet. Rien n'est envoyé ni conservé.
/// </summary>
public static class DeviceTests
{
    // ------------------------------------------------------------------ sons
    /// <summary>Son WAV (16 bits, stéréo) : bip de <paramref name="hz"/> Hz sur la voie gauche et/ou droite.</summary>
    public static byte[] Tone(int hz, double seconds, bool left, bool right, int rate = 44100)
    {
        int n = (int)(rate * seconds);
        var pcm = new short[n * 2];
        int fade = rate / 50;
        for (int i = 0; i < n; i++)
        {
            double env = Math.Min(1, Math.Min(i, n - i) / (double)fade);
            var v = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * 0.3 * env * short.MaxValue);
            pcm[i * 2] = left ? v : (short)0;
            pcm[i * 2 + 1] = right ? v : (short)0;
        }
        return Wav(pcm, 2, rate);
    }

    /// <summary>Fichier WAV en mémoire à partir d'échantillons 16 bits.</summary>
    public static byte[] Wav(short[] samples, int channels, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Joue un son WAV (attend la fin, hors de l'interface).</summary>
    public static Task PlayAsync(byte[] wav) => Task.Run(() => PlaySound(wav, IntPtr.Zero, 0x0004 | 0x0002));   // SND_MEMORY | SND_NODEFAULT

    [DllImport("winmm.dll")] private static extern bool PlaySound(byte[] data, IntPtr module, uint flags);

    // ------------------------------------------------------------------ manettes Xbox (XInput)
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
    }

    [StructLayout(LayoutKind.Sequential)] private struct XInputState { public uint Packet; public Gamepad Pad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputVibration { public ushort Left, Right; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern int XInputGetState(int index, out XInputState state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")] private static extern int XInputSetState(int index, ref XInputVibration vibration);

    /// <summary>État de la manette n° <paramref name="index"/> (0 à 3), ou null si aucune n'est branchée à cet emplacement.</summary>
    public static Gamepad? Pad(int index)
    {
        try { return XInputGetState(index, out var s) == 0 ? s.Pad : null; }
        catch { return null; }   // XInput absent
    }

    /// <summary>Vibration (0 à 1 pour chaque moteur : gauche grave, droite aigu).</summary>
    public static void Vibrate(int index, double left, double right)
    {
        try { var v = new XInputVibration { Left = (ushort)(left * 65535), Right = (ushort)(right * 65535) }; XInputSetState(index, ref v); } catch { }
    }

    // ------------------------------------------------------------------ vitesse d'Internet
    public sealed record NetResult(double PingMs, double JitterMs, double DownMbps, double UpMbps);

    /// <summary>Latence, débit descendant et montant mesurés avec les serveurs de test publics de Cloudflare.</summary>
    public static async Task<NetResult> NetBenchAsync(IProgress<string> progress, CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        progress.Report("Latence…");
        var pings = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            var sw = Stopwatch.StartNew();
            using var r = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=0", ct);
            pings.Add(sw.Elapsed.TotalMilliseconds);
        }
        pings.Sort();
        var best = pings.Take(5).ToList();
        double ping = best.Average(), jitter = best.Zip(best.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(0).Average();

        progress.Report("Téléchargement…");
        double down = 0;
        foreach (var size in new[] { 10_000_000, 25_000_000 })
        {
            var sw = Stopwatch.StartNew();
            var data = await http.GetByteArrayAsync($"https://speed.cloudflare.com/__down?bytes={size}", ct);
            down = Math.Max(down, data.Length * 8 / 1e6 / sw.Elapsed.TotalSeconds);
            if (sw.Elapsed.TotalSeconds > 6) break;   // connexion lente : inutile d'aller plus loin
        }

        progress.Report("Envoi…");
        double up = 0;
        var payload = new byte[10_000_000];
        new Random(3).NextBytes(payload);
        {
            var sw = Stopwatch.StartNew();
            using var content = new System.Net.Http.ByteArrayContent(payload);
            using var r = await http.PostAsync("https://speed.cloudflare.com/__up", content, ct);
            up = payload.Length * 8 / 1e6 / sw.Elapsed.TotalSeconds;
        }
        return new NetResult(ping, jitter, down, up);
    }
}

/// <summary>
/// Capture du micro par défaut (16 kHz, mono) : niveau en direct, et enregistrement de quelques secondes pour réécoute.
/// </summary>
public sealed class MicCapture : IDisposable
{
    private const int Rate = 16000, BufferSamples = 1600, Buffers = 4;   // 100 ms par tampon
    private IntPtr _wave;
    private readonly IntPtr[] _headers = new IntPtr[Buffers];
    private readonly IntPtr[] _data = new IntPtr[Buffers];
    private Thread _thread;
    private volatile bool _running;
    private readonly List<short> _recorded = new();
    private volatile bool _recording;

    /// <summary>Niveau crête (0 à 1), environ 10 fois par seconde, sur un thread de capture.</summary>
    public event Action<float> Level;

    private readonly int _device;

    /// <param name="device">Numéro du micro (waveIn) ; -1 : le micro par défaut de Windows.</param>
    public MicCapture(int device = -1) => _device = device;

    public bool Start()
    {
        var fmt = new WaveFormat { Tag = 1, Channels = 1, Rate = Rate, AvgBytes = Rate * 2, BlockAlign = 2, Bits = 16, Size = 0 };
        if (waveInOpen(out _wave, _device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;   // CALLBACK_NULL
        int hsize = Marshal.SizeOf<WaveHeader>();
        for (int i = 0; i < Buffers; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferSamples * 2);
            _headers[i] = Marshal.AllocHGlobal(hsize);
            Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = BufferSamples * 2 }, _headers[i], false);
            waveInPrepareHeader(_wave, _headers[i], hsize);
            waveInAddBuffer(_wave, _headers[i], hsize);
        }
        if (waveInStart(_wave) != 0) { Dispose(); return false; }
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Micro" };
        _thread.Start();
        return true;
    }

    private void Loop()
    {
        int hsize = Marshal.SizeOf<WaveHeader>();
        var buf = new short[BufferSamples];
        while (_running)
        {
            for (int i = 0; i < Buffers && _running; i++)
            {
                var h = Marshal.PtrToStructure<WaveHeader>(_headers[i]);
                if ((h.Flags & 1) == 0) continue;   // WHDR_DONE
                int n = (int)h.BytesRecorded / 2;
                Marshal.Copy(_data[i], buf, 0, n);
                int peak = 0;
                for (int k = 0; k < n; k++) peak = Math.Max(peak, Math.Abs((int)buf[k]));
                if (_recording) lock (_recorded) _recorded.AddRange(buf.Take(n));
                Level?.Invoke(peak / 32768f);
                h.Flags = 0; h.BytesRecorded = 0;
                Marshal.StructureToPtr(h, _headers[i], false);
                waveInPrepareHeader(_wave, _headers[i], hsize);
                waveInAddBuffer(_wave, _headers[i], hsize);
            }
            Thread.Sleep(15);
        }
    }

    /// <summary>Enregistre <paramref name="seconds"/> secondes puis renvoie le son (WAV) pour la réécoute.</summary>
    public async Task<byte[]> RecordAsync(double seconds)
    {
        lock (_recorded) _recorded.Clear();
        _recording = true;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        _recording = false;
        short[] samples;
        lock (_recorded) samples = _recorded.ToArray();
        return DeviceTests.Wav(samples, 1, Rate);
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        if (_wave != IntPtr.Zero)
        {
            waveInReset(_wave);
            int hsize = Marshal.SizeOf<WaveHeader>();
            for (int i = 0; i < Buffers; i++)
                if (_headers[i] != IntPtr.Zero) waveInUnprepareHeader(_wave, _headers[i], hsize);
            waveInClose(_wave);
            _wave = IntPtr.Zero;
        }
        for (int i = 0; i < Buffers; i++)
        {
            if (_headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_headers[i]); _headers[i] = IntPtr.Zero; }
            if (_data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_data[i]); _data[i] = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { public ushort Tag, Channels; public int Rate, AvgBytes; public ushort BlockAlign, Bits, Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr wave, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr wave);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr wave);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr wave);
}
