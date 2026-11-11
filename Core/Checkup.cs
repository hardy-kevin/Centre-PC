using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>Un constat du bilan : texte pour le client (clair, sans jargon) et texte technique (valeurs, codes, causes).</summary>
public sealed record CheckupFinding(DeviceStatus Level, string Area, string Client, string Technical, string Advice = null);

/// <summary>Une mesure chiffrée (annexe technique).</summary>
public sealed record CheckupMeasure(string Area, string Name, string Value, string Note = null);

/// <summary>Tout le bilan : inventaire, opérations réalisées, mesures, constats. Converti en rapport HTML imprimable.</summary>
public sealed class CheckupReport
{
    public DateTime Start { get; } = DateTime.Now;
    public DateTime End { get; set; }
    public string Level { get; set; }
    public string Customer { get; set; }
    public string Technician { get; set; }
    public string Machine { get; set; } = Environment.MachineName;
    public List<(string Section, List<(string Key, string Value)> Rows)> Inventory { get; } = new();
    public List<(DateTime Time, string Step, string Text)> Operations { get; } = new();
    public List<CheckupMeasure> Measures { get; } = new();
    public List<CheckupFinding> Findings { get; } = new();
    /// <summary>Tableaux techniques supplémentaires (cœur par cœur, endurance minute par minute…).</summary>
    public List<(string Title, string[] Head, List<string[]> Rows)> Tables { get; } = new();
    /// <summary>Chiffres clés (en tête du rapport, lisibles par tous).</summary>
    public List<(string Name, string Value)> KeyFigures { get; } = new();
    public bool Interrupted { get; set; }

    public void Op(string step, string text) => Operations.Add((DateTime.Now, step, text));
    public void Add(DeviceStatus level, string area, string client, string technical, string advice = null) =>
        Findings.Add(new CheckupFinding(level, area, client, technical, advice));
    public void Measure(string area, string name, string value, string note = null) => Measures.Add(new CheckupMeasure(area, name, value, note));

    public DeviceStatus Worst => Findings.Count == 0 ? DeviceStatus.Ok : Findings.Max(f => f.Level);

    public string VerdictTitle => Worst switch
    {
        DeviceStatus.Problem => "Des problèmes sont à corriger",
        DeviceStatus.Warning => "La machine fonctionne, avec des points à surveiller",
        _ => Interrupted ? "Bilan partiel : rien d'anormal sur la partie effectuée" : "La machine est saine"
    };

    /// <summary>Résumé texte pour le client (à coller dans un e-mail ou un ticket).</summary>
    public string ClientText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bilan de la machine {Machine} — {Start:dddd d MMMM yyyy à HH:mm}" + (string.IsNullOrWhiteSpace(Customer) ? "" : $" — {Customer}"));
        sb.AppendLine($"Résultat : {VerdictTitle}.");
        sb.AppendLine();
        foreach (var f in Findings.OrderByDescending(f => f.Level))
            sb.AppendLine($"{Mark(f.Level)} {f.Area} : {f.Client}" + (f.Advice != null ? $" → {f.Advice}" : ""));
        sb.AppendLine();
        sb.AppendLine($"Durée du bilan : {(End - Start).TotalMinutes:0} min ({Level}). Rapport technique complet disponible.");
        return sb.ToString();
    }

    private static string Mark(DeviceStatus l) => l switch { DeviceStatus.Problem => "✗", DeviceStatus.Warning => "⚠", DeviceStatus.Info => "ℹ", _ => "✓" };

    /// <summary>Rapport HTML autonome et imprimable (PDF depuis le navigateur) : synthèse client, puis annexe technique.</summary>
    public string Html(string version)
    {
        static string E(string s) => WebUtility.HtmlEncode(s ?? "");
        string color(DeviceStatus l) => l switch { DeviceStatus.Problem => "#dc2626", DeviceStatus.Warning => "#d97706", DeviceStatus.Info => "#2563eb", _ => "#059669" };
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"fr\"><head><meta charset=\"utf-8\"><title>").Append(E($"Bilan {Machine} {Start:yyyy-MM-dd}")).Append("</title><style>");
        sb.Append(@"*{-webkit-print-color-adjust:exact;print-color-adjust:exact}
.kf{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:10px;margin:8px 0 6px}.kf div{border:1px solid #e5e7eb;border-radius:10px;padding:8px 12px}
.kf b{display:block;font-size:17px}.kf span{color:#6b7280;font-size:12px}h4{margin:16px 0 4px;font-size:13px;text-transform:uppercase;letter-spacing:.04em;color:#6b7280}
body{font-family:'Segoe UI',system-ui,sans-serif;color:#1f2937;max-width:960px;margin:32px auto;padding:0 24px;line-height:1.45}
h1{font-size:26px;margin:0 0 4px}h2{font-size:18px;margin:32px 0 10px;border-bottom:2px solid #e5e7eb;padding-bottom:6px}h3{font-size:15px;margin:20px 0 8px}
.meta{color:#6b7280;font-size:13px}.verdict{border-radius:12px;padding:16px 20px;margin:20px 0;color:#fff;font-size:18px;font-weight:600}
.f{display:flex;gap:10px;margin:8px 0;align-items:flex-start}.dot{flex:none;width:10px;height:10px;border-radius:50%;margin-top:6px}
.adv{color:#4b5563;font-size:13px}.tech{color:#6b7280;font-size:12.5px;font-family:Consolas,monospace}
table{border-collapse:collapse;width:100%;font-size:13px;margin:6px 0 14px}td,th{border-bottom:1px solid #e5e7eb;padding:5px 8px;text-align:left;vertical-align:top}
th{background:#f9fafb;font-weight:600}td.k{width:34%;color:#4b5563}.ops td:first-child{white-space:nowrap;color:#6b7280;width:70px}
.bar{position:sticky;top:0;background:#fff;padding:8px 0;border-bottom:1px solid #eee;margin-bottom:10px}.bar button{font:inherit;padding:6px 14px;border-radius:8px;border:1px solid #d1d5db;background:#f9fafb;cursor:pointer;margin-right:6px}
.foot{margin-top:40px;color:#9ca3af;font-size:12px}@media print{.bar{display:none}body{margin:0}h2{break-after:avoid}table{break-inside:auto}tr{break-inside:avoid}}
.client .techonly{display:none}");
        sb.Append("</style></head><body>");
        sb.Append("<div class=\"bar\"><button onclick=\"document.body.classList.remove('client')\">Version technique</button>")
          .Append("<button onclick=\"document.body.classList.add('client')\">Version client</button><button onclick=\"print()\">Imprimer / PDF</button></div>");
        sb.Append("<h1>Bilan de la machine ").Append(E(Machine)).Append("</h1><div class=\"meta\">")
          .Append(E($"{Start:dddd d MMMM yyyy}, de {Start:HH:mm} à {End:HH:mm} ({(End - Start).TotalMinutes:0} min) · niveau {Level}"));
        if (!string.IsNullOrWhiteSpace(Customer)) sb.Append(" · client : ").Append(E(Customer));
        if (!string.IsNullOrWhiteSpace(Technician)) sb.Append(" · réalisé par : ").Append(E(Technician));
        sb.Append("</div>");
        sb.Append("<div class=\"verdict\" style=\"background:").Append(color(Worst == DeviceStatus.Info ? DeviceStatus.Ok : Worst)).Append("\">").Append(E(VerdictTitle)).Append("</div>");

        // Chiffres clés
        if (KeyFigures.Count > 0)
        {
            sb.Append("<h2>Chiffres clés</h2><div class=\"kf\">");
            foreach (var (name, value) in KeyFigures) sb.Append("<div><span>").Append(E(name)).Append("</span><b>").Append(E(value)).Append("</b></div>");
            sb.Append("</div>");
        }

        // Synthèse, par degré d'urgence
        sb.Append("<h2>Synthèse</h2>");
        DeviceStatus? group = null;
        foreach (var f in Findings.OrderByDescending(f => f.Level).ThenBy(f => f.Area))
        {
            if (group != f.Level)
            {
                group = f.Level;
                sb.Append("<h4>").Append(f.Level switch { DeviceStatus.Problem => "À corriger", DeviceStatus.Warning => "À surveiller", DeviceStatus.Info => "Bon à savoir", _ => "Vérifié et conforme" }).Append("</h4>");
            }
            sb.Append("<div class=\"f\"><div class=\"dot\" style=\"background:").Append(color(f.Level)).Append("\"></div><div><b>").Append(E(f.Area)).Append(" :</b> ").Append(E(f.Client));
            if (f.Advice != null) sb.Append("<div class=\"adv\">→ ").Append(E(f.Advice)).Append("</div>");
            if (!string.IsNullOrEmpty(f.Technical)) sb.Append("<div class=\"tech techonly\">").Append(E(f.Technical)).Append("</div>");
            sb.Append("</div></div>");
        }

        // Opérations
        sb.Append("<h2>Opérations réalisées</h2><table class=\"ops\">");
        foreach (var (time, step, text) in Operations) sb.Append("<tr><td>").Append(time.ToString("HH:mm:ss")).Append("</td><td><b>").Append(E(step)).Append("</b> — ").Append(E(text)).Append("</td></tr>");
        sb.Append("</table>");

        // Annexe technique
        sb.Append("<div class=\"techonly\"><h2>Annexe technique</h2>");
        foreach (var (section, rows) in Inventory)
        {
            sb.Append("<h3>").Append(E(section)).Append("</h3><table>");
            foreach (var (k, v) in rows) sb.Append("<tr><td class=\"k\">").Append(E(k)).Append("</td><td>").Append(E(v)).Append("</td></tr>");
            sb.Append("</table>");
        }
        if (Measures.Count > 0)
        {
            sb.Append("<h3>Mesures</h3><table><tr><th>Domaine</th><th>Mesure</th><th>Valeur</th><th>Commentaire</th></tr>");
            foreach (var m in Measures) sb.Append("<tr><td>").Append(E(m.Area)).Append("</td><td>").Append(E(m.Name)).Append("</td><td><b>").Append(E(m.Value)).Append("</b></td><td>").Append(E(m.Note)).Append("</td></tr>");
            sb.Append("</table>");
        }
        foreach (var (title, head, rows) in Tables)
        {
            sb.Append("<h3>").Append(E(title)).Append("</h3><table><tr>");
            foreach (var h in head) sb.Append("<th>").Append(E(h)).Append("</th>");
            sb.Append("</tr>");
            foreach (var r in rows) { sb.Append("<tr>"); foreach (var c in r) sb.Append("<td>").Append(E(c)).Append("</td>"); sb.Append("</tr>"); }
            sb.Append("</table>");
        }
        sb.Append("<h3>Méthode</h3><p class=\"adv\">Charges de calcul générées par Centre PC : processeur (multiplications-additions vectorielles, SHA-256, compression Brotli, résultats vérifiés), "
                + "carte graphique (Direct3D 11 : calcul et passes mémoire vidéo, résultats vérifiés), mémoire vive (copies multi-fils), disque (lecture / écriture sans cache Windows). "
                + "Capteurs : compteurs de Windows, données des pilotes graphiques, et HWiNFO s'il était disponible (température du processeur, ventilateurs). "
                + "Historique : journaux d'événements de Windows (30 derniers jours). GFLOPS / TFLOPS : valeurs propres à Centre PC, comparables entre deux bilans Centre PC.</p></div>");
        sb.Append("<div class=\"foot\">Rapport généré par Centre PC ").Append(E(version)).Append(" le ").Append(E(End.ToString("g"))).Append(".</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}

/// <summary>Inventaire de la machine (WMI, registre) et éléments utiles au diagnostic.</summary>
public static class SystemInventory
{
    public sealed class Facts
    {
        public string Os, OsVersion, Build, Board, Bios, BiosDate, PowerPlan, SecureBoot;
        public DateTime? BiosReleased, LastBoot, InstallDate, GpuDriverDate;
        public string Cpu; public int Cores, Threads;
        public double RamInstalled, RamUsable; public int RamModules, RamSlots; public long RamConfigured, RamRated; public string RamType;
        public List<(string Name, string Driver, DateTime? Date)> Gpus = new();
        public List<(string Name, string Kind, string Size, string Health)> Disks = new();
        public bool Laptop;
    }

    private static List<ManagementBaseObject> Q(string wql, string scope = null)
    {
        try
        {
            using var s = scope == null ? new ManagementObjectSearcher(wql) : new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            return s.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch { return new(); }
    }

    private static string S(ManagementBaseObject o, string p) { try { return o?[p]?.ToString()?.Trim(); } catch { return null; } }
    private static long N(ManagementBaseObject o, string p) { try { return o == null ? 0 : Convert.ToInt64(o[p] ?? 0); } catch { return 0; } }
    private static DateTime? D(ManagementBaseObject o, string p) { try { var v = S(o, p); return string.IsNullOrEmpty(v) ? null : ManagementDateTimeConverter.ToDateTime(v); } catch { return null; } }
    private static string Gb(double b) => b >= 1e12 ? $"{b / 1e12:0.##} To" : $"{b / 1073741824.0:0.#} Go";

    public static Facts Collect(CheckupReport report)
    {
        var f = new Facts();
        var os = Q("SELECT Caption, Version, BuildNumber, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        string display = null; long ubr = 0;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = k?.GetValue("DisplayVersion") as string;
            ubr = Convert.ToInt64(k?.GetValue("UBR") ?? 0);
        }
        catch { }
        f.Os = S(os, "Caption"); f.Build = $"{S(os, "BuildNumber")}.{ubr}"; f.OsVersion = display;
        f.LastBoot = os != null ? D(os, "LastBootUpTime") : null; f.InstallDate = os != null ? D(os, "InstallDate") : null;
        var cs = Q("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem").FirstOrDefault();
        var bb = Q("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Q("SELECT SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS").FirstOrDefault();
        f.Board = $"{S(bb, "Manufacturer")} {S(bb, "Product")}".Trim();
        f.Bios = $"{S(bios, "Manufacturer")} {S(bios, "SMBIOSBIOSVersion")}".Trim();
        f.BiosReleased = bios != null ? D(bios, "ReleaseDate") : null;
        f.Laptop = InputChecks.IsLaptop();
        var cpu = Q("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        f.Cpu = S(cpu.FirstOrDefault(), "Name"); f.Cores = (int)cpu.Sum(c => N(c, "NumberOfCores")); f.Threads = (int)cpu.Sum(c => N(c, "NumberOfLogicalProcessors"));
        var modules = Q("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        f.RamModules = modules.Count; f.RamInstalled = modules.Sum(m => (double)N(m, "Capacity"));
        f.RamSlots = (int)Q("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray WHERE Use = 3").Sum(o => N(o, "MemoryDevices"));
        f.RamConfigured = modules.Select(m => N(m, "ConfiguredClockSpeed")).Where(v => v > 0).DefaultIfEmpty(0).Min();
        // Vitesse annoncée : celle de la référence des barrettes (profil XMP / EXPO), WMI ne donnant souvent que la vitesse de base JEDEC
        f.RamRated = modules.Select(m => Math.Max(N(m, "Speed"), DeviceHealth.RatedFromPart(S(m, "PartNumber")))).Where(v => v > 0).DefaultIfEmpty(0).Min();
        f.RamType = N(modules.FirstOrDefault(), "SMBIOSMemoryType") switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 30 => "LPDDR4", 35 => "LPDDR5", _ => null };
        f.RamUsable = N(Q("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault(), "TotalPhysicalMemory");
        foreach (var g in Q("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
            if (!(S(g, "PNPDeviceID") ?? "").StartsWith("ROOT", StringComparison.OrdinalIgnoreCase)) f.Gpus.Add((S(g, "Name"), S(g, "DriverVersion"), D(g, "DriverDate")));
        f.GpuDriverDate = f.Gpus.Select(g => g.Date).Where(d => d != null).DefaultIfEmpty(null).Max();
        foreach (var p in Q("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", @"\\.\root\Microsoft\Windows\Storage"))
        {
            long bus = N(p, "BusType"), media = N(p, "MediaType");
            f.Disks.Add((S(p, "FriendlyName"), bus == 17 ? "SSD NVMe" : media == 4 ? "SSD" : media == 3 ? "Disque dur" : bus == 7 ? "USB" : "Disque",
                         Gb(N(p, "Size")), N(p, "HealthStatus") switch { 0 => "bonne santé", 1 => "avertissement", 2 => "défaillant", _ => "inconnue" }));
        }
        f.PowerPlan = PowerPlan();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            f.SecureBoot = k?.GetValue("UEFISecureBootEnabled") is int sb ? (sb == 1 ? "activé" : "désactivé") : "non disponible (BIOS ancien mode)";
        }
        catch { f.SecureBoot = "inconnu"; }

        report.Inventory.Add(("Système", new()
        {
            ("Machine", $"{Environment.MachineName} · {S(cs, "Manufacturer")} {S(cs, "Model")}".Trim()),
            ("Windows", $"{f.Os} {f.OsVersion} (build {f.Build})"),
            ("Installé le", f.InstallDate?.ToString("d MMMM yyyy") ?? "—"),
            ("Démarré depuis", f.LastBoot is DateTime lb ? $"{(DateTime.Now - lb).TotalDays:0.#} jour(s) (le {lb:g})" : "—"),
            ("Carte mère", string.IsNullOrWhiteSpace(f.Board) ? "—" : f.Board),
            ("BIOS / UEFI", $"{f.Bios}" + (f.BiosReleased is DateTime bd ? $" du {bd:d MMMM yyyy}" : "")),
            ("Démarrage sécurisé (Secure Boot)", f.SecureBoot),
            ("Mode d'alimentation", f.PowerPlan ?? "—"),
            ("Type", f.Laptop ? "Ordinateur portable" : "Ordinateur fixe")
        }));
        report.Inventory.Add(("Processeur et mémoire", new List<(string Key, string Value)>
        {
            ("Processeur", $"{f.Cpu} · {f.Cores} cœurs, {f.Threads} fils"),
            ("Microcode", CpuMicrocode.Assess(f.Cpu).tech),
            ("Mémoire installée", $"{Gb(f.RamInstalled)} en {f.RamModules} barrette(s) sur {f.RamSlots} emplacement(s)" + (f.RamType != null ? $" · {f.RamType}" : "")),
            ("Mémoire utilisable", f.RamUsable > 0 ? Gb(f.RamUsable) : "—"),
            ("Vitesse de la mémoire", f.RamConfigured > 0 ? $"{f.RamConfigured} MT/s (annoncée par les barrettes : {f.RamRated} MT/s)" : "—")
        }.Concat(modules.Select(m => ($"  {S(m, "DeviceLocator")}", $"{Gb(N(m, "Capacity"))} · {S(m, "Manufacturer")} {S(m, "PartNumber")}".Trim()))).ToList()));
        report.Inventory.Add(("Cartes graphiques", f.Gpus.Select(g => (g.Name, $"pilote {g.Driver}" + (g.Date is DateTime d ? $" du {d:d MMMM yyyy}" : ""))).ToList()));
        report.Inventory.Add(("Disques", f.Disks.Select(d => (d.Name, $"{d.Kind} · {d.Size} · santé : {d.Health}")).ToList()));
        return f;
    }

    private static string PowerPlan()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // sortie de powercfg dans la page de code de la console (accents)
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg", "/getactivescheme")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage) });
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = Regex.Match(o, @"\(([^)]+)\)\s*$");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }
}

/// <summary>Historique de stabilité (journaux de Windows, 30 derniers jours) : écrans bleus, arrêts brutaux, erreurs matérielles, pilote graphique, disques, plantages.</summary>
public static class StabilityHistory
{
    public sealed class Result
    {
        public List<(DateTime When, string Code)> BlueScreens = new();
        public List<DateTime> UnexpectedShutdowns = new();
        public int WheaErrors, WheaWarnings, GpuDriverResets, DiskErrors;
        public DateTime? LastGpuReset, LastDiskError;
        public List<(string App, int Count)> AppCrashes = new();
    }

    private static IEnumerable<EventRecord> Read(string log, string filter)
    {
        var list = new List<EventRecord>();
        try
        {
            var q = new EventLogQuery(log, PathType.LogName, $"*[System[{filter} and TimeCreated[timediff(@SystemTime) <= 2592000000]]]");
            using var r = new EventLogReader(q);
            for (var e = r.ReadEvent(); e != null && list.Count < 500; e = r.ReadEvent()) list.Add(e);
        }
        catch { }
        return list;
    }

    public static Result Collect()
    {
        var res = new Result();
        foreach (var e in Read("System", "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)"))
            using (e)
            {
                string code = null;
                try { code = Regex.Match(e.FormatDescription() ?? "", @"0x[0-9a-fA-F]{8}").Value; } catch { }
                res.BlueScreens.Add((e.TimeCreated ?? DateTime.MinValue, string.IsNullOrEmpty(code) ? "code inconnu" : code));
            }
        foreach (var e in Read("System", "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)")) using (e) res.UnexpectedShutdowns.Add(e.TimeCreated ?? DateTime.MinValue);
        foreach (var e in Read("System", "Provider[@Name='Microsoft-Windows-WHEA-Logger']"))
            using (e) { if (e.Level <= 2) res.WheaErrors++; else res.WheaWarnings++; }
        foreach (var e in Read("System", "(Provider[@Name='Display'] and EventID=4101)"))
            using (e) { res.GpuDriverResets++; res.LastGpuReset ??= e.TimeCreated; }
        foreach (var e in Read("System", "((Provider[@Name='disk'] and (EventID=7 or EventID=51 or EventID=153)) or (Provider[@Name='Ntfs'] and EventID=55) or (Provider[@Name='stornvme'] and EventID=11))"))
            using (e) { res.DiskErrors++; res.LastDiskError ??= e.TimeCreated; }
        var crashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Read("Application", "(Provider[@Name='Application Error'] and EventID=1000)"))
            using (e)
            {
                string app = null;
                try { app = e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() : null; } catch { }
                if (!string.IsNullOrEmpty(app)) crashes[app] = crashes.GetValueOrDefault(app) + 1;
            }
        res.AppCrashes = crashes.OrderByDescending(kv => kv.Value).Take(5).Select(kv => (kv.Key, kv.Value)).ToList();
        return res;
    }
}

/// <summary>Débit de la mémoire vive : copies de grands blocs sur plusieurs fils (lecture + écriture comptées).</summary>
public static class MemoryBench
{
    public static double MeasureGBs(double seconds, CancellationToken ct)
    {
        int threads = Math.Max(2, Environment.ProcessorCount / 2);
        const int size = 64 << 20;
        long bytes = 0;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
        {
            var a = GC.AllocateUninitializedArray<byte>(size);
            var b = GC.AllocateUninitializedArray<byte>(size);
            a.AsSpan().Fill(1);
            long local = 0;
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                a.AsSpan().CopyTo(b); b.AsSpan().CopyTo(a);
                local += 4L * size;   // 2 copies, chacune lit et écrit
            }
            Interlocked.Add(ref bytes, local);
        });
        return bytes / sw.Elapsed.TotalSeconds / 1e9;
    }
}
