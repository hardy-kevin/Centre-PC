using System.IO.MemoryMappedFiles;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace CentrePC.Core;

/// <summary>Une carte graphique et ses mesures (valeurs nulles : non fournies par le pilote).</summary>
public sealed class GpuReading
{
    public string Name { get; set; }
    public long Luid { get; set; }
    public double? Temp { get; set; }
    public double? TempMax { get; set; }
    public double? TempWarning { get; set; }
    public double? FanRpm { get; set; }
    public double? FanPercent { get; set; }
    public double? PowerW { get; set; }
    public double? PowerPercent { get; set; }
    public double? ClockMHz { get; set; }
    /// <summary>Raison du bridage donnée par le pilote (« chaleur », « limite de puissance »), si connue.</summary>
    public string Throttle { get; set; }
    /// <summary>Charge (%) : moteur le plus occupé de la carte (mesure de Windows), ou celle de HWiNFO.</summary>
    public double? Usage { get; set; }
    /// <summary>Point le plus chaud de la puce (HWiNFO).</summary>
    public double? Hotspot { get; set; }
}

/// <summary>Un ventilateur ou une pompe (programme de surveillance installé par l'utilisateur).</summary>
public sealed record FanReading(string Name, double Rpm);

/// <summary>Toutes les mesures à un instant donné.</summary>
public sealed class SensorSnapshot
{
    public DateTime Time { get; } = DateTime.Now;
    /// <summary>Utilisation du processeur (%).</summary>
    public double? CpuUsage { get; set; }
    /// <summary>Fréquence réelle moyenne (MHz) et fréquence de base.</summary>
    public double? CpuMHz { get; set; }
    public double? CpuBaseMHz { get; set; }
    /// <summary>Limite de performance imposée par le processeur ou le micrologiciel (100 : aucune).</summary>
    public double? CpuLimit { get; set; }
    public double? CpuTemp { get; set; }
    public string CpuTempSource { get; set; }
    /// <summary>Zone thermique de Windows (souvent la carte mère ; seulement si la valeur est réelle).</summary>
    public double? SystemTemp { get; set; }
    public double? CpuPowerW { get; set; }
    /// <summary>Limites de puissance du processeur (PL1 longue durée, PL2 courte durée), si HWiNFO les donne.</summary>
    public double? CpuPl1 { get; set; }
    public double? CpuPl2 { get; set; }
    /// <summary>Étage d'alimentation du processeur sur la carte mère (VRM / MOS) et chipset.</summary>
    public double? VrmTemp { get; set; }
    public double? ChipsetTemp { get; set; }
    /// <summary>Températures des disques (nom → °C).</summary>
    public Dictionary<string, double> DriveTemps { get; } = new();
    /// <summary>Fréquence réelle de chaque processeur logique (MHz), par numéro.</summary>
    public Dictionary<int, double> CoreMHz { get; } = new();
    public List<FanReading> Fans { get; } = new();
    public List<GpuReading> Gpus { get; } = new();
    /// <summary>Mesures de carte graphique lues dans HWiNFO (fusionnées avec celles de Windows).</summary>
    internal GpuReading ExternalGpu { get; set; }
}

/// <summary>
/// Capteurs lus SANS AUCUN PILOTE ajouté par Centre PC :
/// <list type="bullet">
/// <item>Processeur : compteurs de performance de Windows (utilisation, fréquence réelle, limite de performance = bridage).</item>
/// <item>Cartes graphiques (toutes marques) : données de performance de Windows (celles du Gestionnaire des tâches) —
/// température, ventilateur, puissance ; NVIDIA : bibliothèque du pilote (NVML) pour la consommation, la fréquence et la cause du bridage.</item>
/// <item>Température du processeur et ventilateurs : lus dans LibreHardwareMonitor ou HWiNFO s'ils sont ouverts (leur pilote, choix de l'utilisateur).</item>
/// </list>
/// </summary>
public sealed class Sensors : IDisposable
{
    private readonly Pdh _pdh = new();
    private readonly List<(string name, long luid, uint handle)> _adapters = new();
    private readonly Dictionary<long, (double? max, double? warn)> _caps = new();
    private readonly Nvml _nvml = Nvml.TryOpen();
    private readonly List<double> _zoneHistory = new();

    public Sensors()
    {
        foreach (var a in Gpu.Adapters())
        {
            var h = Kmt.Open(a.Luid);
            if (h == 0) continue;
            _adapters.Add((a.Name, a.Luid, h));
            var caps = Kmt.Caps(h);
            _caps[a.Luid] = caps;
        }
    }

    public SensorSnapshot Read()
    {
        var s = new SensorSnapshot();
        try
        {
            _pdh.Collect();
            s.CpuUsage = _pdh.Get(Pdh.Utility) is double u ? Math.Min(100, u) : _pdh.Get(Pdh.Time);
            var perf = _pdh.Get(Pdh.Performance);
            s.CpuBaseMHz = _pdh.Get(Pdh.Frequency);
            if (perf is double p && s.CpuBaseMHz is double b && b > 0) s.CpuMHz = b * p / 100;
            s.CpuLimit = _pdh.Get(Pdh.Limit);
            if (s.CpuBaseMHz is double baseMhz && baseMhz > 0)
                foreach (var (lp, pct) in _pdh.PerCorePerformance()) s.CoreMHz[lp] = baseMhz * pct / 100;
            var zone = _pdh.MaxThermalZone();
            if (zone is double z)
            {
                // Zone thermique : beaucoup de PC fixes renvoient une valeur fixe (27,8 °C…) : affichée seulement si elle varie
                _zoneHistory.Add(z);
                if (_zoneHistory.Count > 600) _zoneHistory.RemoveAt(0);
                if (_zoneHistory.Max() - _zoneHistory.Min() >= 1 && z > 20 && z < 120) s.SystemTemp = z;
            }
        }
        catch { }

        Dictionary<long, double> usage = null;
        try { usage = _pdh.GpuUsage(); } catch { }
        foreach (var (name, luid, handle) in _adapters)
        {
            var g = new GpuReading { Name = name, Luid = luid };
            var (temp, fan, power) = Kmt.Perf(handle);
            if (temp > 0) g.Temp = temp;
            if (fan > 0) g.FanRpm = fan;
            if (power > 0) g.PowerPercent = power;
            g.ClockMHz = Kmt.ClockMHz(handle);
            if (_caps.TryGetValue(luid, out var caps)) { g.TempMax = caps.max; g.TempWarning = caps.warn; }
            _nvml?.Fill(g);
            if (usage != null && usage.TryGetValue(luid, out var u)) g.Usage = u;
            s.Gpus.Add(g);
        }

        // Programmes de surveillance ouverts par l'utilisateur : température du processeur et ventilateurs
        if (!External.FromHwinfo(s)) External.FromLibre(s);
        // HWiNFO : complète la carte graphique correspondante (point chaud, consommation en watts, ventilateur, charge)
        if (s.ExternalGpu is { } x)
        {
            var g = s.Gpus.FirstOrDefault(gp => x.Name.Contains(gp.Name.Replace("AMD ", "").Replace("NVIDIA ", "").Replace("Intel(R) ", ""), StringComparison.OrdinalIgnoreCase))
                    ?? s.Gpus.FirstOrDefault();
            if (g != null)
            {
                g.Hotspot ??= x.Hotspot;
                g.PowerW ??= x.PowerW;
                g.Temp ??= x.Temp;
                g.FanRpm ??= x.FanRpm;
                g.ClockMHz ??= x.ClockMHz;
                if (x.Usage != null && (g.Usage == null || x.Usage > g.Usage)) g.Usage = x.Usage;
            }
        }
        return s;
    }

    /// <summary>Ce qui est disponible maintenant (pour l'annoncer avant le test).</summary>
    public static string MonitorRunning() => External.Running();

    public void Dispose()
    {
        _pdh.Dispose();
        foreach (var a in _adapters) Kmt.Close(a.handle);
        _nvml?.Dispose();
    }

    // ================================================================== compteurs de performance (pdh.dll)
    private sealed class Pdh : IDisposable
    {
        public const string Utility = @"\Processor Information(_Total)\% Processor Utility",
                            Time = @"\Processor Information(_Total)\% Processor Time",
                            Performance = @"\Processor Information(_Total)\% Processor Performance",
                            Frequency = @"\Processor Information(_Total)\Processor Frequency",
                            Limit = @"\Processor Information(_Total)\% Performance Limit",
                            Zones = @"\Thermal Zone Information(*)\High Precision Temperature",
                            GpuEngines = @"\GPU Engine(*)\Utilization Percentage",
                            PerCore = @"\Processor Information(*)\% Processor Performance";
        private readonly IntPtr _query;
        private readonly Dictionary<string, IntPtr> _counters = new();

        public Pdh()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            foreach (var path in new[] { Utility, Time, Performance, Frequency, Limit, Zones, GpuEngines, PerCore })
                if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var c) == 0) _counters[path] = c;
            PdhCollectQueryData(_query);
        }

        public void Collect() { if (_query != IntPtr.Zero) PdhCollectQueryData(_query); }

        public double? Get(string path)
        {
            if (!_counters.TryGetValue(path, out var c)) return null;
            return PdhGetFormattedCounterValue(c, 0x200 | 0x8000, out _, out var v) == 0 && v.Status == 0 ? v.Value : null;   // DOUBLE | NOCAP100
        }

        /// <summary>Plus haute zone thermique (dixièmes de kelvin → °C).</summary>
        public double? MaxThermalZone()
        {
            if (!_counters.TryGetValue(Zones, out var c)) return null;
            uint size = 0;
            PdhGetFormattedCounterArray(c, 0x200, ref size, out _, IntPtr.Zero);
            if (size == 0) return null;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(c, 0x200, ref size, out var count, buf) != 0) return null;
                double? best = null;
                int item = IntPtr.Size + 16;
                for (int i = 0; i < count; i++)
                {
                    var status = Marshal.ReadInt32(buf, i * item + IntPtr.Size);
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf, i * item + IntPtr.Size + 8));
                    if (status != 0 || value <= 0) continue;
                    var celsius = value / 10 - 273.15;
                    if (best == null || celsius > best) best = celsius;
                }
                return best;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Charge de chaque carte graphique : moteur le plus occupé (3D, calcul, copie…), toutes applications additionnées —
        /// comme le Gestionnaire des tâches. Instances « pid_…_luid_0x…_0x…_phys_0_eng_N_engtype_X ».
        /// </summary>
        public Dictionary<long, double> GpuUsage()
        {
            var res = new Dictionary<long, double>();
            if (!_counters.TryGetValue(GpuEngines, out var c)) return res;
            uint size = 0;
            PdhGetFormattedCounterArray(c, 0x200, ref size, out _, IntPtr.Zero);
            if (size == 0) return res;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(c, 0x200, ref size, out var count, buf) != 0) return res;
                var engines = new Dictionary<(long, string), double>();
                int item = IntPtr.Size + 16;
                for (int i = 0; i < count; i++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * item)) ?? "";
                    if (Marshal.ReadInt32(buf, i * item + IntPtr.Size) != 0) continue;
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf, i * item + IntPtr.Size + 8));
                    var m = System.Text.RegularExpressions.Regex.Match(name, @"luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_\d+_eng_(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    long luid = (Convert.ToInt64(m.Groups[1].Value, 16) << 32) | Convert.ToInt64(m.Groups[2].Value, 16);
                    var key = (luid, m.Groups[3].Value);
                    engines[key] = engines.GetValueOrDefault(key) + value;
                }
                foreach (var ((luid, _), v) in engines) res[luid] = Math.Min(100, Math.Max(res.GetValueOrDefault(luid), v));
            }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }

        /// <summary>Performance de chaque processeur logique (% de la fréquence de base), instances « groupe,numéro ».</summary>
        public Dictionary<int, double> PerCorePerformance()
        {
            var res = new Dictionary<int, double>();
            if (!_counters.TryGetValue(PerCore, out var c)) return res;
            uint size = 0;
            PdhGetFormattedCounterArray(c, 0x200 | 0x8000, ref size, out _, IntPtr.Zero);
            if (size == 0) return res;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(c, 0x200 | 0x8000, ref size, out var count, buf) != 0) return res;
                int item = IntPtr.Size + 16;
                for (int i = 0; i < count; i++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * item)) ?? "";
                    if (Marshal.ReadInt32(buf, i * item + IntPtr.Size) != 0) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(name, @"^0,(\d+)$");
                    if (m.Success) res[int.Parse(m.Groups[1].Value)] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf, i * item + IntPtr.Size + 8));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }

        public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); }

        [StructLayout(LayoutKind.Explicit)] private struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
    }

    // ================================================================== cartes graphiques : données de Windows (gdi32, D3DKMT)
    private static class Kmt
    {
        public static uint Open(long luid)
        {
            var o = new OPENADAPTERFROMLUID { Luid = luid };
            return D3DKMTOpenAdapterFromLuid(ref o) == 0 ? o.Adapter : 0;
        }

        public static void Close(uint handle) { var c = new CLOSEADAPTER { Adapter = handle }; D3DKMTCloseAdapter(ref c); }

        /// <summary>
        /// Interroge le pilote avec la première valeur de type acceptée : le pilote vérifie la taille attendue,
        /// ce qui évite toute confusion entre les structures voisines.
        /// </summary>
        private static byte[] Query(uint handle, int size, params int[] types)
        {
            var buf = new byte[size];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                foreach (var t in types)
                {
                    Array.Clear(buf);
                    var q = new QUERYADAPTERINFO { Adapter = handle, Type = t, Data = h.AddrOfPinnedObject(), Size = (uint)size };
                    if (D3DKMTQueryAdapterInfo(ref q) == 0) return buf;
                }
            }
            catch { }
            finally { h.Free(); }
            return null;
        }

        // D3DKMT_ADAPTER_PERFDATA : index, 5 × 64 bits (mémoire, bande passante), FanRPM, Power (0,1 %), Temperature (0,1 °C), PowerStateOverride
        public static (double temp, double fan, double power) Perf(uint handle)
        {
            var b = Query(handle, 64, 61, 62);
            if (b == null) return (0, 0, 0);
            return (BitConverter.ToUInt32(b, 56) / 10.0, BitConverter.ToUInt32(b, 48), BitConverter.ToUInt32(b, 52) / 10.0);
        }

        // D3DKMT_ADAPTER_PERFDATACAPS : index, bande passante max (×2), MaxFanRPM, TemperatureMax, TemperatureWarning (0,1 °C)
        public static (double? max, double? warn) Caps(uint handle)
        {
            // index (0), MaxMemoryBandwidth (8), MaxPCIEBandwidth (16), MaxFanRPM (24), TemperatureMax (28), TemperatureWarning (32) : 40 octets alignés
            var b = Query(handle, 40, 62, 63);
            if (b == null) return (null, null);
            double max = BitConverter.ToUInt32(b, 28) / 10.0, warn = BitConverter.ToUInt32(b, 32) / 10.0;
            return (max > 30 ? max : null, warn > 30 ? warn : null);
        }

        // D3DKMT_NODE_PERFDATA (moteur 0 = 3D) : NodeOrdinal, index, Frequency (Hz), MaxFrequency, MaxFrequencyOC, tensions, latence
        public static double? ClockMHz(uint handle)
        {
            var b = Query(handle, 56, 60, 59);
            if (b == null) return null;
            var hz = BitConverter.ToUInt64(b, 8);
            return hz > 0 ? hz / 1e6 : null;
        }

        [StructLayout(LayoutKind.Sequential)] private struct OPENADAPTERFROMLUID { public long Luid; public uint Adapter; }
        [StructLayout(LayoutKind.Sequential)] private struct CLOSEADAPTER { public uint Adapter; }
        [StructLayout(LayoutKind.Sequential)] private struct QUERYADAPTERINFO { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
        [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OPENADAPTERFROMLUID o);
        [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);
        [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO q);
    }

    // ================================================================== NVIDIA : NVML (livrée avec le pilote, aucun pilote ajouté)
    private sealed class Nvml : IDisposable
    {
        private readonly IntPtr _lib;
        private readonly List<(string name, IntPtr dev)> _devices = new();
        private delegate int NoArg();
        private delegate int CountFn(out uint count);
        private delegate int HandleFn(uint index, out IntPtr dev);
        private delegate int NameFn(IntPtr dev, byte[] name, uint length);
        private delegate int UIntFn(IntPtr dev, out uint value);
        private delegate int UIntArgFn(IntPtr dev, int arg, out uint value);
        private delegate int ULongFn(IntPtr dev, out ulong value);
        private UIntArgFn _temp, _clock;
        private UIntFn _fan, _power, _limit;
        private ULongFn _reasons;

        public static Nvml TryOpen()
        {
            try
            {
                foreach (var path in new[] { "nvml.dll", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll") })
                    if (NativeLibrary.TryLoad(path, out var lib))
                    {
                        var n = new Nvml(lib);
                        if (n._devices.Count > 0) return n;
                        n.Dispose();
                    }
            }
            catch { }
            return null;
        }

        private T Fn<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(_lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

        private Nvml(IntPtr lib)
        {
            _lib = lib;
            if (Fn<NoArg>("nvmlInit_v2")?.Invoke() != 0) return;
            var count = Fn<CountFn>("nvmlDeviceGetCount_v2");
            var handle = Fn<HandleFn>("nvmlDeviceGetHandleByIndex_v2");
            var name = Fn<NameFn>("nvmlDeviceGetName");
            _temp = Fn<UIntArgFn>("nvmlDeviceGetTemperature");
            _clock = Fn<UIntArgFn>("nvmlDeviceGetClockInfo");
            _fan = Fn<UIntFn>("nvmlDeviceGetFanSpeed");
            _power = Fn<UIntFn>("nvmlDeviceGetPowerUsage");
            _limit = Fn<UIntFn>("nvmlDeviceGetEnforcedPowerLimit");
            _reasons = Fn<ULongFn>("nvmlDeviceGetCurrentClocksEventReasons") ?? Fn<ULongFn>("nvmlDeviceGetCurrentClocksThrottleReasons");
            if (count == null || handle == null || count(out var n) != 0) return;
            for (uint i = 0; i < n; i++)
            {
                if (handle(i, out var dev) != 0) continue;
                var buf = new byte[96];
                var label = name?.Invoke(dev, buf, (uint)buf.Length) == 0 ? Encoding.UTF8.GetString(buf).TrimEnd('\0') : "";
                _devices.Add((label, dev));
            }
        }

        /// <summary>Complète les mesures de la carte NVIDIA du même nom.</summary>
        public void Fill(GpuReading g)
        {
            var d = _devices.FirstOrDefault(x => x.name.Length > 0 && g.Name.Contains(x.name.Replace("NVIDIA ", ""), StringComparison.OrdinalIgnoreCase));
            if (d.dev == IntPtr.Zero) return;
            try
            {
                if (_temp?.Invoke(d.dev, 0, out var t) == 0 && t > 0) g.Temp = t;
                if (_fan?.Invoke(d.dev, out var f) == 0) g.FanPercent = f;
                if (_power?.Invoke(d.dev, out var mw) == 0 && mw > 0)
                {
                    g.PowerW = mw / 1000.0;
                    if (_limit?.Invoke(d.dev, out var lim) == 0 && lim > 0) g.PowerPercent = mw * 100.0 / lim;
                }
                if (_clock?.Invoke(d.dev, 0, out var mhz) == 0 && mhz > 0) g.ClockMHz = mhz;
                if (_reasons?.Invoke(d.dev, out var r) == 0)
                    g.Throttle = (r & (0x20 | 0x40)) != 0 ? "chaleur" : (r & (0x4 | 0x80)) != 0 ? "limite de puissance" : (r & 0x8) != 0 ? "protection matérielle" : null;
            }
            catch { }
        }

        public void Dispose()
        {
            try { Fn<NoArg>("nvmlShutdown")?.Invoke(); } catch { }
            NativeLibrary.Free(_lib);
        }
    }

    // ================================================================== programmes de surveillance de l'utilisateur
    private static class External
    {
        /// <summary>Nom du programme de surveillance dont les données sont lisibles maintenant, sinon null.</summary>
        public static string Running()
        {
            if (OpenHwinfo() is { } m) { m.Dispose(); return "HWiNFO"; }
            try
            {
                using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\LibreHardwareMonitor"), new ObjectQuery("SELECT Identifier FROM Sensor"));
                if (s.Get().Count > 0) return "LibreHardwareMonitor";
            }
            catch { }
            return null;
        }

        private static bool _logged, _openLogged;

        private static MemoryMappedFile OpenHwinfo()
        {
            Exception last = null;
            foreach (var n in new[] { @"Global\HWiNFO_SENS_SM2", "HWiNFO_SENS_SM2" })
                try { return MemoryMappedFile.OpenExisting(n, MemoryMappedFileRights.Read); } catch (Exception ex) { last = ex; }
            // HWiNFO ouvert mais mémoire partagée illisible : noté une fois dans le journal (diagnostic)
            if (!_openLogged && System.Diagnostics.Process.GetProcessesByName("HWiNFO64").Length > 0)
            {
                _openLogged = true;
                Log.Info("HWiNFO ouvert, mémoire partagée inaccessible : " + last?.GetType().Name + " " + last?.Message);
            }
            return null;
        }

        /// <summary>HWiNFO, mémoire partagée (option « Shared Memory Support ») : en-tête puis capteurs et mesures.</summary>
        public static bool FromHwinfo(SensorSnapshot s)
        {
            using var mm = OpenHwinfo();
            if (mm == null) return false;
            try
            {
                using var v = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (v.ReadUInt32(0) != 0x53695748) return false;   // « HWiS » (« DEAD » : arrêté)
                uint offSensor = v.ReadUInt32(20), sizeSensor = v.ReadUInt32(24), numSensor = v.ReadUInt32(28);
                uint offReading = v.ReadUInt32(32), sizeReading = v.ReadUInt32(36), numReading = v.ReadUInt32(40);
                var sensorNames = new string[numSensor];
                for (uint i = 0; i < numSensor; i++) sensorNames[i] = Ansi(v, offSensor + i * sizeSensor + 8, 128);
                double? cpu = null; string cpuLabel = null; int cpuRank = 99;
                GpuReading gpu = null;
                for (uint i = 0; i < numReading; i++)
                {
                    long o = offReading + i * sizeReading;
                    uint type = v.ReadUInt32(o), sensor = v.ReadUInt32(o + 4);
                    // Nom d'origine (anglais) pour reconnaître la mesure ; nom affiché (traduit ou renommé) pour l'affichage
                    var orig = Ansi(v, o + 12, 128);
                    var label = Ansi(v, o + 140, 128);
                    if (string.IsNullOrEmpty(label)) label = orig;
                    double value = v.ReadDouble(o + 284);
                    var sensorName = sensor < sensorNames.Length ? sensorNames[sensor] : "";
                    // Carte graphique (capteur « GPU [#0]: AMD Radeon … ») : point chaud, consommation, ventilateur, charge
                    if (System.Text.RegularExpressions.Regex.IsMatch(sensorName, @"^GPU\b|radeon|geforce|nvidia|intel arc", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        if (gpu == null) gpu = new GpuReading { Name = sensorName };
                        if (sensorName != gpu.Name) continue;   // première carte seulement
                        var l = orig.ToLowerInvariant();
                        if (type == 1 && (l.Contains("hot spot") || l.Contains("hotspot"))) gpu.Hotspot = value;
                        else if (type == 1 && l == "gpu temperature") gpu.Temp = value;
                        else if (type == 5 && (l.Contains("total board power") || (gpu.PowerW == null && (l.Contains("gpu power") || l.Contains("asic power") || l.Contains("gpu ppt"))))) gpu.PowerW = value;
                        else if (type == 3 && gpu.FanRpm == null && value > 0) gpu.FanRpm = value;
                        else if (type == 7 && (l == "gpu utilization" || l.Contains("gpu d3d usage") || l == "gpu core load")) gpu.Usage = Math.Max(gpu.Usage ?? 0, value);
                        else if (type == 6 && (l == "gpu clock" || l.Contains("gpu clock (effective)")) && gpu.ClockMHz == null) gpu.ClockMHz = value;
                        continue;
                    }
                    var lo = orig.ToLowerInvariant();
                    // Disques (capteur « S.M.A.R.T.: modèle » ou « Drive: modèle ») : température
                    if (System.Text.RegularExpressions.Regex.IsMatch(sensorName, @"^(S\.M\.A\.R\.T\.|Drive)\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        if (type == 1 && lo.Contains("temperature") && value > 0 && value < 120)
                        {
                            var drive = System.Text.RegularExpressions.Regex.Replace(sensorName, @"^(S\.M\.A\.R\.T\.|Drive)\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                            if (!s.DriveTemps.TryGetValue(drive, out var dt) || value > dt) s.DriveTemps[drive] = value;
                        }
                        continue;
                    }
                    // Carte mère : étage d'alimentation (VRM / MOS) et chipset
                    if (type == 1 && System.Text.RegularExpressions.Regex.IsMatch(lo, @"\bvrm\b|\bmos\b|vr mos|mosfet|\bvr\b") && value > 0 && value < 150) { s.VrmTemp = Math.Max(s.VrmTemp ?? 0, value); continue; }
                    if (type == 1 && System.Text.RegularExpressions.Regex.IsMatch(lo, @"\bpch\b|chipset") && value > 0 && value < 150) { s.ChipsetTemp = Math.Max(s.ChipsetTemp ?? 0, value); continue; }
                    // Processeur : consommation et limites de puissance
                    if (type == 5 && lo == "cpu package power") { s.CpuPowerW = value; continue; }
                    if (type == 5 && lo.StartsWith("pl1 power limit") && value > 0) { s.CpuPl1 = value; continue; }
                    if (type == 5 && lo.StartsWith("pl2 power limit") && value > 0) { s.CpuPl2 = value; continue; }
                    if (type == 1)   // température
                    {
                        int rank = Math.Min(CpuRank(orig, sensorName), CpuRank(label, sensorName));
                        if (rank < cpuRank && value > 0 && value < 130) { cpu = value; cpuRank = rank; cpuLabel = label; }
                    }
                    else if (type == 3 && !System.Text.RegularExpressions.Regex.IsMatch(sensorName, "gpu|nvidia|radeon|geforce|arc", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        s.Fans.Add(new FanReading(label, value));   // ventilateurs (ceux des cartes graphiques sont déjà dans leur carte)
                }
                if (cpu != null) { s.CpuTemp = cpu; s.CpuTempSource = $"HWiNFO ({cpuLabel})"; }
                s.ExternalGpu = gpu;
                if (!_logged)
                {
                    _logged = true;
                    Log.Info($"HWiNFO : {numSensor} capteurs, {numReading} mesures, température du processeur : {(cpu != null ? $"{cpuLabel} = {cpu:0} °C" : "non trouvée")}, {s.Fans.Count} ventilateurs");
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Température du processeur la plus représentative : boîtier / Tctl, puis moyenne des cœurs, puis toute autre mesure « CPU ».</summary>
        private static int CpuRank(string label, string sensor)
        {
            var l = label.ToLowerInvariant();
            if (l.Contains("cpu package") || l.Contains("tctl") || l.Contains("tdie") || l == "package") return 0;
            if (l.Contains("core max") || l.Contains("core (max)")) return 1;
            if (l.Contains("cpu") && !l.Contains("gpu")) return 2;
            if (System.Text.RegularExpressions.Regex.IsMatch(sensor, @"^cpu|intel core|ryzen|processor", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && l.StartsWith("core")) return 3;
            return 99;
        }

        private static string Ansi(MemoryMappedViewAccessor v, long offset, int length)
        {
            var b = new byte[length];
            v.ReadArray(offset, b, 0, length);
            int end = Array.IndexOf(b, (byte)0);
            return Encoding.Latin1.GetString(b, 0, end < 0 ? length : end).Trim();
        }

        /// <summary>LibreHardwareMonitor ouvert : ses capteurs sont publiés dans WMI (root\LibreHardwareMonitor).</summary>
        public static bool FromLibre(SensorSnapshot s)
        {
            try
            {
                using var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\LibreHardwareMonitor"),
                    new ObjectQuery("SELECT Name, SensorType, Value, Identifier FROM Sensor WHERE SensorType = 'Temperature' OR SensorType = 'Fan' OR SensorType = 'Power'"));
                double? cpu = null; string cpuLabel = null; int cpuRank = 99;
                bool any = false;
                foreach (ManagementBaseObject o in q.Get())
                {
                    any = true;
                    var id = o["Identifier"]?.ToString() ?? "";
                    var name = o["Name"]?.ToString() ?? "";
                    var type = o["SensorType"]?.ToString();
                    double value = Convert.ToDouble(o["Value"] ?? 0);
                    bool isCpu = id.StartsWith("/intelcpu", StringComparison.OrdinalIgnoreCase) || id.StartsWith("/amdcpu", StringComparison.OrdinalIgnoreCase);
                    if (type == "Temperature" && isCpu)
                    {
                        int rank = CpuRank(name.StartsWith("Core (Tctl", StringComparison.OrdinalIgnoreCase) ? "tctl" : "cpu " + name, "cpu");
                        if (name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) || name.Contains("Tctl")) rank = 0;
                        if (rank < cpuRank && value > 0 && value < 130) { cpu = value; cpuRank = rank; cpuLabel = name; }
                    }
                    else if (type == "Fan" && !id.Contains("gpu", StringComparison.OrdinalIgnoreCase)) s.Fans.Add(new FanReading(name, value));
                    else if (type == "Power" && isCpu && name.Contains("Package", StringComparison.OrdinalIgnoreCase)) s.CpuPowerW = value;
                }
                if (cpu != null) { s.CpuTemp = cpu; s.CpuTempSource = $"LibreHardwareMonitor ({cpuLabel})"; }
                return any;
            }
            catch { return false; }
        }
    }
}

/// <summary>Cartes graphiques présentes (DXGI) : nom, identifiant, mémoire dédiée ; utilisé par les capteurs et le test.</summary>
public static class Gpu
{
    public sealed record Adapter(string Name, long Luid, long DedicatedMemory, int Index, IntPtr Native);

    /// <summary>Cartes matérielles (sans le rendu logiciel de Windows), la plus puissante (mémoire dédiée) en premier.</summary>
    public static List<Adapter> Adapters(bool keepNative = false)
    {
        var list = new List<Adapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                if (Com.Call<EnumAdapters1>(factory, 12)(factory, i, out var adapter) != 0) break;
                var desc = new DXGI_ADAPTER_DESC1();
                Com.Call<GetDesc1>(adapter, 10)(adapter, out desc);
                if ((desc.Flags & 2) != 0 || desc.VendorId == 0x1414) { Com.Release(adapter); continue; }   // logiciel / Microsoft Basic Render
                // Même carte annoncée deux fois par Windows (identifiants différents) : une seule fois, la première (celle de l'affichage)
                if (list.Any(a => a.Name == desc.Description && a.DedicatedMemory == (long)desc.DedicatedVideoMemory)) { Com.Release(adapter); continue; }
                list.Add(new Adapter(desc.Description, ((long)desc.LuidHigh << 32) | desc.LuidLow, (long)desc.DedicatedVideoMemory, (int)i, keepNative ? adapter : IntPtr.Zero));
                if (!keepNative) Com.Release(adapter);
            }
        }
        catch { }
        finally { Com.Release(factory); }
        return list.OrderByDescending(a => a.DedicatedMemory).ToList();
    }

    private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    private delegate int GetDesc1(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
}

/// <summary>Appel d'une méthode d'une interface COM par sa position dans la table virtuelle (sans bibliothèque ajoutée).</summary>
internal static class Com
{
    public static T Call<T>(IntPtr obj, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

    public static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Marshal.Release(obj); }
}
