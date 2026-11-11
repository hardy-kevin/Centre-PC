using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Bilan complet de la machine (hors clavier, souris…) : inventaire, santé, historique de stabilité, mesures au repos,
/// performances et stabilité du processeur (global et cœur par cœur), de la mémoire, de la carte graphique, endurance combinée,
/// refroidissement, disques, Internet ; puis instantané figé de l'installation comparé au bilan précédent.
/// Trois niveaux préréglés (Express, Standard, Approfondi), chaque étape activable. Rapport HTML : synthèse client + annexe technique.
/// </summary>
public partial class MainWindow
{
    private sealed class CkPlan
    {
        public string Level = "Standard";
        public bool Idle = true, Cpu = true, Ram = true, Cores = true, GpuStep = true, Endurance = true, Cooldown = true, Disks = true, AllDisks, Net = true, Mem = true;
        public int IdleSec = 20, CpuSingleSec = 10, CpuAllSec = 15, CoreSec = 10, GpuSec = 20, EnduranceMin = 5, CooldownSec = 30, MemMin = 10, StressLoops = 10;
        public bool Stress = true;

        public static CkPlan For(string level) => level switch
        {
            "Express" => new CkPlan { Level = level, IdleSec = 10, CpuSingleSec = 8, CpuAllSec = 12, Cores = false, GpuSec = 15, Endurance = false, Cooldown = false, Mem = false, Stress = false },
            "Approfondi" => new CkPlan { Level = level, IdleSec = 30, CpuSingleSec = 15, CpuAllSec = 20, CoreSec = 30, GpuSec = 30, EnduranceMin = 20, CooldownSec = 60, AllDisks = true, MemMin = 60, StressLoops = 20 },
            _ => new CkPlan()
        };

        public int EstimateSeconds(int cores, int disks) =>
            25 + (Idle ? IdleSec : 0) + (Cpu ? CpuSingleSec + CpuAllSec : 0) + (Ram ? 6 : 0) + (Cores ? CoreSec * cores : 0) + (GpuStep ? GpuSec : 0)
               + (Endurance ? EnduranceMin * 60 : 0) + (Mem ? MemMin * 60 + 10 : 0) + (Stress ? StressLoops * (StressLoops >= 20 ? 60 : 30) + 5 : 0) + (Endurance && Cooldown ? CooldownSec : 0) + (Disks ? 45 * (AllDisks ? disks : 1) : 0) + (Net ? 25 : 0);
    }

    private CkPlan _ckPlan = CkPlan.For("Standard");
    private string _ckCustomer, _ckTechnician;
    private CancellationTokenSource _ckCts;

    // ================================================================== préparation
    private void BuildCheckupTest()
    {
        var p = _ckPlan;
        int cores = Math.Max(1, CpuTopology.Cores().Count);
        var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
        DeviceTestHost.Children.Add(TestText("Vérifie toute la machine et produit un rapport : un résumé clair pour le client et une annexe technique détaillée. "
            + "Chaque bilan enregistre aussi un instantané de l'installation (Windows, mises à jour, pilotes, applications, mesures) : le suivant montre ce qui a changé entre les deux."));

        DeviceTestHost.Children.Add(Section("Niveau"));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)>
        {
            ($"Express · ≈ {CkPlan.For("Express").EstimateSeconds(cores, fixedDrives.Count) / 60 + 1} min", "Express"),
            ($"Standard · ≈ {CkPlan.For("Standard").EstimateSeconds(cores, fixedDrives.Count) / 60 + 1} min", "Standard"),
            ($"Approfondi · ≈ {CkPlan.For("Approfondi").EstimateSeconds(cores, fixedDrives.Count) / 60 + 1} min", "Approfondi")
        }, p.Level, v => { _ckPlan = CkPlan.For((string)v); OpenDeviceTest("checkup"); }));
        DeviceTestHost.Children.Add(TestText(p.Level switch
        {
            "Express" => "Contrôle rapide : santé, historique, performances. Idéal avant / après une intervention simple.",
            "Approfondi" => "Protocole de validation des techniciens : stabilité mémoire + processeur 1 heure (façon OCCT), contrainte graphique 20 boucles (façon 3DMark, seuil 97 %), "
                          + "endurance 20 min, chaque cœur, tous les disques façon CrystalDiskMark, températures processeur / carte graphique / VRM / SSD. Pour une nouvelle installation ou un PC instable.",
            _ => "Bilan recommandé : tout est vérifié, avec 10 min de stabilité mémoire + processeur, une contrainte graphique de 10 boucles et 5 min d'endurance."
        }, 12.5));

        DeviceTestHost.Children.Add(Section("Étapes"));
        var steps = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var estimate = TestText("", 13, subtle: false);
        void Refresh() => estimate.Text = $"Durée estimée : environ {p.EstimateSeconds(cores, fixedDrives.Count) / 60 + 1} minutes.";
        void Step(string text, bool value, Action<bool> set) { var c = Check(text, value, v => { set(v); Refresh(); }); c.Margin = new Thickness(0, 0, 18, 8); steps.Children.Add(c); }
        steps.Children.Add(new TextBlock { Text = "Toujours : inventaire, santé des périphériques, historique de stabilité (30 jours), instantané", Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8), Width = 900, TextWrapping = TextWrapping.Wrap });
        Step($"Mesures au repos ({p.IdleSec} s)", p.Idle, v => p.Idle = v);
        Step("Processeur", p.Cpu, v => p.Cpu = v);
        Step("Mémoire vive (débit)", p.Ram, v => p.Ram = v);
        Step($"Chaque cœur ({p.CoreSec} s × {cores})", p.Cores, v => p.Cores = v);
        Step("Carte graphique", p.GpuStep, v => p.GpuStep = v);
        Step($"Contrainte graphique ({p.StressLoops} boucles, façon 3DMark)", p.Stress, v => p.Stress = v);
        Step($"Stabilité mémoire + processeur ({p.MemMin} min, façon OCCT)", p.Mem, v => p.Mem = v);
        Step("Endurance processeur + carte graphique", p.Endurance, v => p.Endurance = v);
        Step("Refroidissement après l'effort", p.Cooldown, v => p.Cooldown = v);
        Step(fixedDrives.Count > 1 ? "Disques" : "Disque", p.Disks, v => p.Disks = v);
        if (fixedDrives.Count > 1) Step($"Tous les disques ({fixedDrives.Count})", p.AllDisks, v => p.AllDisks = v);
        Step("Internet", p.Net, v => p.Net = v);
        DeviceTestHost.Children.Add(steps);
        DeviceTestHost.Children.Add(TestText("Durée du test de stabilité mémoire + processeur (1 heure = validation complète) :", 12.5));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)> { ("10 min", 10), ("30 min", 30), ("1 heure", 60), ("2 heures", 120) }, p.MemMin, v => { p.MemMin = (int)v; OpenDeviceTest("checkup"); }));
        DeviceTestHost.Children.Add(TestText("Durée de l'endurance :", 12.5));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)> { ("5 min", 5), ("10 min", 10), ("20 min", 20), ("30 min", 30) }, p.EnduranceMin, v => { p.EnduranceMin = (int)v; Refresh(); }));
        var managed = Check("Température du processeur et ventilateurs avec HWiNFO (géré par Centre PC, retiré à la fin)", _benchManaged, v => _benchManaged = v);
        managed.Margin = new Thickness(0, 4, 0, 10);
        DeviceTestHost.Children.Add(managed);

        DeviceTestHost.Children.Add(Section("Rapport"));
        var who = new WrapPanel();
        TextBox Field(string label, string value, Action<string> set)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 8) };
            sp.Children.Add(new TextBlock { Text = label, Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var t = new TextBox { Text = value ?? "", Width = 280 };
            t.TextChanged += (_, _) => set(t.Text);
            sp.Children.Add(t);
            who.Children.Add(sp);
            return t;
        }
        Field("Client ou machine (facultatif)", _ckCustomer, v => _ckCustomer = v);
        Field("Réalisé par (facultatif)", _ckTechnician, v => _ckTechnician = v);
        DeviceTestHost.Children.Add(who);
        Refresh();
        DeviceTestHost.Children.Add(estimate);
        DeviceTestHost.Children.Add(TestText("Le PC reste utilisable mais sera lent pendant les phases de charge : fermez jeux et applications lourdes pour des mesures fiables.", 12));
        DeviceTestHost.Children.Add(TestButton("", "Lancer le bilan", StartCheckup, accent: true));

        // Bilans précédents
        try
        {
            var reports = Directory.EnumerateFiles(MachineSnapshot.Folder, "Bilan *.html").OrderByDescending(File.GetLastWriteTime).Take(5).ToList();
            if (reports.Count > 0)
            {
                DeviceTestHost.Children.Add(Section("Bilans précédents"));
                foreach (var r in reports)
                {
                    var path = r;
                    var row = new WrapPanel();
                    row.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(r), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) });
                    row.Children.Add(TestButton("", "Ouvrir", () => OpenTarget(path)));
                    DeviceTestHost.Children.Add(row);
                }
                DeviceTestHost.Children.Add(TestButton("", "Ouvrir le dossier des bilans", () => OpenTarget(MachineSnapshot.Folder)));
            }
        }
        catch { }
    }

    private void StartCheckup()
    {
        _monitorWatch?.Stop();
        var plan = _ckPlan;
        if (_benchManaged && _hwinfo == null && Sensors.MonitorRunning() == null) { _ = PrepareHwinfoAsync(() => _ = RunCheckupAsync(plan)); return; }
        _ = RunCheckupAsync(plan);
    }

    // ================================================================== déroulement
    private sealed class PhaseStats
    {
        public List<double> Cpu = new(), Gpu = new(), CpuT = new(), GpuT = new(), Hot = new(), Pow = new(), Use = new(), MHz = new(), Usage = new();
        public double? LimitMin;
        public int Limited, Seconds;
        public Dictionary<string, double> FanMax = new();
        public HashSet<string> FanStopped = new();
        public long Errors;
        public string FirstError, Aborted;
        public List<string[]> Minutes = new();
        public Dictionary<int, double> CoreMHz = new();
        public List<double> Vrm = new(), Chip = new(), CpuPow = new();
        public Dictionary<string, double> DriveMax = new();
        public double? Pl1, Pl2;

        public static double Avg(List<double> l) => l.Count == 0 ? 0 : l.Average();
        public static double Steady(List<double> l) => l.Count == 0 ? 0 : l.Skip(l.Count / 4).Average();
        public static double? Max(List<double> l) => l.Count == 0 ? null : l.Max();
    }

    private async Task RunCheckupAsync(CkPlan plan)
    {
        _ckCts?.Cancel();
        var cts = _ckCts = new CancellationTokenSource();
        var ct = cts.Token;
        var report = new CheckupReport { Level = plan.Level, Customer = _ckCustomer, Technician = _ckTechnician };
        var snap = new MachineSnapshot { Date = report.Start, Level = plan.Level, Machine = report.Machine };
        var cores = CpuTopology.Cores();
        var adapter = Gpu.Adapters().FirstOrDefault(g => g.Luid == _benchGpuLuid) ?? Gpu.Adapters().FirstOrDefault();
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.AvailableFreeSpace > 2L << 30)
                              .OrderBy(d => string.Equals(d.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        if (!plan.AllDisks) drives = drives.Take(1).ToList();

        // Liste des étapes affichée, avec leur état
        var names = new List<string> { "Inventaire et instantané", "Santé des périphériques", "Historique de stabilité" };
        if (plan.Idle) names.Add("Mesures au repos");
        if (plan.Cpu) names.Add("Processeur");
        if (plan.Ram) names.Add("Mémoire vive");
        if (plan.Cores && cores.Count > 0) names.Add("Chaque cœur");
        if (plan.GpuStep && adapter != null) names.Add("Carte graphique");
        if (plan.Stress && adapter != null) names.Add("Contrainte graphique");
        if (plan.Mem) names.Add("Stabilité mémoire + processeur");
        if (plan.Endurance) names.Add($"Endurance ({plan.EnduranceMin} min)");
        if (plan.Endurance && plan.Cooldown) names.Add("Refroidissement");
        if (plan.Disks && drives.Count > 0) names.Add(drives.Count > 1 ? "Disques" : "Disque");
        if (plan.Net) names.Add("Internet");

        DeviceTestHost.Children.Clear();
        var title = TestText($"Bilan {plan.Level.ToLowerInvariant()} en cours…", 15, subtle: false);
        DeviceTestHost.Children.Add(title);
        DeviceTestHost.Children.Add(Bar(out var progress, "#7C5CFF", 8));
        var live = TestText("", 13, subtle: false);
        live.Margin = new Thickness(0, 10, 0, 8);
        DeviceTestHost.Children.Add(live);
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var rows = new Dictionary<string, (TextBlock icon, TextBlock text)>();
        foreach (var n in names)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var icon = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), Opacity = 0.4, Width = 22, Margin = new Thickness(0, 2, 6, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            dp.Children.Add(icon);
            var t = new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
            dp.Children.Add(t);
            list.Children.Add(dp);
            rows[n] = (icon, t);
        }
        DeviceTestHost.Children.Add(list);
        var temps = new LiveChart("Températures (°C)") { Height = 130, Margin = new Thickness(0, 0, 0, 10) };
        DeviceTestHost.Children.Add(temps);
        DeviceTestHost.Children.Add(TestButton("", "Arrêter le bilan (le rapport porte sur ce qui a été fait)", () => cts.Cancel()));
        _testCleanup = () => { cts.Cancel(); };

        Sensors sensors = null;
        try { sensors = new Sensors(); sensors.Read(); } catch (Exception ex) { Log.Error("Capteurs", ex); }
        SetThreadExecutionState(0x80000000 | 0x1 | 0x2);
        int total = plan.EstimateSeconds(cores.Count, drives.Count);
        var started = DateTime.Now;

        void Progress() => progress.Width = Math.Min(1, (DateTime.Now - started).TotalSeconds / Math.Max(1, total)) * Math.Max(0, ((FrameworkElement)progress.Parent).ActualWidth);
        void Begin(string n) { if (rows.TryGetValue(n, out var r)) { r.icon.Text = ""; r.icon.Opacity = 1; r.icon.Foreground = (Brush)FindResource("Accent"); r.text.Opacity = 1; r.text.FontWeight = FontWeights.SemiBold; } live.Text = n + "…"; Progress(); }
        void End(string n, string result, DeviceStatus level = DeviceStatus.Ok)
        {
            if (!rows.TryGetValue(n, out var r)) return;
            var (_, c, glyph) = StatusLook(level);
            r.icon.Text = glyph; r.icon.Foreground = BrushOfStatic(c); r.text.FontWeight = FontWeights.Normal;
            r.text.Text = $"{n} — {result}";
            report.Op(n, result);
        }

        // Charge mesurée seconde par seconde (capteurs, débits, sécurité)
        async Task<PhaseStats> Phase(int seconds, CpuLoad cpu, GpuLoad gpu, string label, bool minutes = false, MemTest mem = null)
        {
            var st = new PhaseStats();
            long lf = 0, lg = 0; double ls = 0, lc = 0;
            var phaseStart = DateTime.Now;
            try
            {
                for (int i = 0; i < seconds && !ct.IsCancellationRequested; i++)
                {
                    try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
                    var s = sensors != null ? await Task.Run(() => { try { return sensors.Read(); } catch { return null; } }) : null;
                    st.Seconds++;
                    double cr = 0, gr = 0;
                    if (cpu != null) { var f = cpu.Flops; var fs = cpu.FmaSeconds; cr = fs > ls ? (f - lf) / (fs - ls) * cpu.Threads / 1e9 : 0; lf = f; ls = fs; if (i >= 2) st.Cpu.Add(cr); }
                    if (gpu != null) { var f = gpu.Flops; var c = gpu.ComputeSeconds; gr = c > lc ? (f - lg) / (c - lc) / 1e12 : 0; lg = f; lc = c; if (i >= 2 && gr > 0) st.Gpu.Add(gr); }
                    var g = s?.Gpus.FirstOrDefault(x => adapter != null && x.Luid == adapter.Luid) ?? s?.Gpus.FirstOrDefault();
                    if (s?.CpuTemp is double t) st.CpuT.Add(t);
                    if (g?.Temp is double gt) st.GpuT.Add(gt);
                    if (g?.Hotspot is double h) st.Hot.Add(h);
                    if (g?.PowerW is double pw) st.Pow.Add(pw);
                    if (g?.Usage is double gu) st.Use.Add(gu);
                    if (s?.CpuUsage is double cu) st.Usage.Add(cu);
                    if (s?.CpuMHz is double m) st.MHz.Add(m);
                    if (s?.VrmTemp is double vr) st.Vrm.Add(vr);
                    if (s?.ChipsetTemp is double chp) st.Chip.Add(chp);
                    if (s?.CpuPowerW is double cpw) st.CpuPow.Add(cpw);
                    st.Pl1 ??= s?.CpuPl1; st.Pl2 ??= s?.CpuPl2;
                    if (s != null) foreach (var (dn, dtv) in s.DriveTemps) st.DriveMax[dn] = Math.Max(st.DriveMax.GetValueOrDefault(dn), dtv);
                    if (s != null) foreach (var (lp, mhz) in s.CoreMHz) st.CoreMHz[lp] = mhz;
                    if (s?.CpuLimit is double lim && cpu != null && cpu.Threads > 1) { st.LimitMin = Math.Min(st.LimitMin ?? 100, lim); if (lim < 99) st.Limited++; }
                    if (s != null)
                        foreach (var fan in s.Fans)
                        {
                            if (fan.Rpm > st.FanMax.GetValueOrDefault(fan.Name)) st.FanMax[fan.Name] = fan.Rpm;
                            if (fan.Rpm < 1 && st.FanMax.GetValueOrDefault(fan.Name) > 200) st.FanStopped.Add(fan.Name);
                        }
                    st.Errors = (cpu?.Errors ?? 0) + (gpu?.Errors ?? 0) + (mem?.Errors ?? 0);
                    st.FirstError ??= cpu?.FirstError ?? (mem?.Errors > 0 ? "mémoire : " + mem.FirstError : null);
                    if (minutes && st.Seconds % 60 == 0)
                        st.Minutes.Add(new[] { $"{st.Seconds / 60} min", s?.CpuTemp is double a ? $"{a:0} °C" : "—", s?.CpuMHz is double b ? $"{b:0} MHz" : "—", $"{cr:0}",
                                               g?.Temp is double c2 ? $"{c2:0} °C" : "—", g?.Hotspot is double d ? $"{d:0} °C" : "—", g?.PowerW is double e ? $"{e:0} W" : "—", gpu != null ? $"{gr:0.00}" : "—" });
                    temps.Add("Processeur", "#F59E0B", s?.CpuTemp);
                    if (adapter != null) temps.Add("Carte graphique", "#22C55E", g?.Temp);
                    temps.Redraw();
                    live.Text = $"{label} · {st.Seconds / 60} min {st.Seconds % 60:00} s / {seconds / 60} min" + (mem != null ? $" · mémoire {mem.BytesTested / 1073741824.0:0} Go, {mem.Verified / 1e12:0.##} To vérifiés" : "")
                              + (cpu != null ? $" · processeur {cr:0} GFLOPS" : "") + (gpu != null ? $" · carte graphique {gr:0.00} TFLOPS" : "")
                              + (s?.CpuTemp is double t3 ? $" · {t3:0} °C" : "") + (g?.Temp is double t4 && gpu != null ? $" · GPU {t4:0} °C" : "") + (st.Errors > 0 ? $" · {st.Errors} erreur(s) !" : "");
                    Progress();
                    if (gpu?.Failure != null) { st.Aborted = "carte graphique : " + gpu.Failure; break; }
                    if (s?.CpuTemp >= 98) { st.Aborted = $"arrêt de sécurité, processeur à {s.CpuTemp:0} °C"; break; }
                    if (g?.Temp is double gt2 && gpu != null && gt2 >= (g.TempMax is double gm ? gm - 2 : 95)) { st.Aborted = $"arrêt de sécurité, carte graphique à {gt2:0} °C"; break; }
                    if (st.Errors > 0 && (cpu?.Threads > 1 || gpu != null || mem != null)) { st.Aborted = mem?.Errors > 0 ? "erreur de mémoire" : "erreur de calcul"; break; }
                    if (s?.VrmTemp >= 120) { st.Aborted = $"arrêt de sécurité, VRM à {s.VrmTemp:0} °C"; break; }
                }
            }
            finally { cpu?.Dispose(); gpu?.Dispose(); mem?.Dispose(); }
            return st;
        }

        // Températures de la carte mère et des disques, consommation du processeur face à ses limites : relevés « dans la norme »
        var thermalDone = new HashSet<string>();
        void Thermals(PhaseStats st)
        {
            if (PhaseStats.Max(st.Vrm) is double v)
            {
                report.Measure("Températures", "Étage d'alimentation (VRM) max", $"{v:0} °C");
                if (thermalDone.Add("vrm") || v >= 95)
                    report.Add(v >= 110 ? DeviceStatus.Problem : v >= 95 ? DeviceStatus.Warning : DeviceStatus.Ok, "Carte mère",
                        v >= 95 ? $"L'étage d'alimentation du processeur (VRM) monte à {v:0} °C : élevé." : $"Étage d'alimentation (VRM) à {v:0} °C au maximum : dans la norme (sous 95 °C).",
                        "Seuils retenus : < 95 °C normal, 95-110 °C élevé, > 110 °C critique (composants prévus jusqu'à 125 °C environ).",
                        v >= 95 ? "Améliorer le flux d'air autour du socket (ventilateur de façade / arrière, ventirad à flux vertical)." : null);
                report.KeyFigures.Add(("VRM max", $"{v:0} °C"));
            }
            if (PhaseStats.Max(st.Chip) is double c) report.Measure("Températures", "Chipset max", $"{c:0} °C", c >= 85 ? "élevé" : "normal");
            foreach (var (d, t) in st.DriveMax)
            {
                report.Measure("Températures", $"Disque {d} max", $"{t:0} °C", t >= 80 ? "critique : le disque ralentit pour se protéger" : t >= 70 ? "élevé" : "normal");
                if (t >= 70 && thermalDone.Add("ssd" + d))
                    report.Add(t >= 80 ? DeviceStatus.Problem : DeviceStatus.Warning, "Disques", $"Le disque {d} chauffe : {t:0} °C.", "NVMe : ralentissement automatique vers 75-80 °C.", "Ajouter un dissipateur sur le SSD ou améliorer le flux d'air.");
            }
            if (PhaseStats.Max(st.CpuPow) is double w)
            {
                report.Measure("Processeur", "Consommation maximale", $"{w:0} W", st.Pl1 is double l1 ? $"limites PL1 {l1:0} W / PL2 {st.Pl2:0} W" : null);
                if (st.Pl1 is double pl1 && thermalDone.Add("pl"))
                    report.Add(w > (st.Pl2 ?? pl1) * 1.05 ? DeviceStatus.Warning : DeviceStatus.Ok, "Processeur",
                        w > (st.Pl2 ?? pl1) * 1.05 ? $"Le processeur consomme {w:0} W, au-delà de ses limites ({pl1:0} / {st.Pl2:0} W) : limites débridées par la carte mère."
                                                    : $"Consommation du processeur dans la norme : {w:0} W au maximum pour des limites de {pl1:0} W (longue durée) et {st.Pl2:0} W (courte durée).",
                        "Mesure HWiNFO « CPU Package Power » et limites PL1 / PL2 actives.", w > (st.Pl2 ?? pl1) * 1.05 ? "Appliquer les limites Intel / AMD dans le BIOS (profil « Intel Default » ou équivalent)." : null);
            }
        }

        var facts = (SystemInventory.Facts)null;
        try
        {
            // 1. Inventaire et instantané
            var n = names[0]; Begin(n);
            facts = await Task.Run(() => SystemInventory.Collect(report));
            await Task.Run(snap.CaptureInstallation);
            snap.Windows = $"{facts.Os} {facts.OsVersion}"; snap.Build = facts.Build; snap.Bios = facts.Bios + (facts.BiosReleased is DateTime br ? $" ({br:yyyy-MM-dd})" : "");
            snap.Board = facts.Board; snap.Cpu = facts.Cpu; snap.Ram = $"{facts.RamInstalled / 1073741824.0:0} Go {facts.RamType} {facts.RamConfigured} MT/s ({facts.RamModules} barrette(s))";
            snap.Gpus = facts.Gpus.Select(g => $"{g.Name} {g.Driver}").ToList(); snap.Disks = facts.Disks.Select(d => $"{d.Name} {d.Size}").ToList();
            if (facts.PowerPlan != null && facts.PowerPlan.Contains("conomi", StringComparison.OrdinalIgnoreCase) && !facts.Laptop)
                report.Add(DeviceStatus.Warning, "Alimentation", "Le PC est en mode « économie d'énergie » : il est volontairement bridé.",
                           $"Mode d'alimentation actif : {facts.PowerPlan}.", "Passer en mode « Équilibré » ou « Performances élevées » (Paramètres > Système > Alimentation).");
            if (facts.LastBoot is DateTime lb && (DateTime.Now - lb).TotalDays > 30)
                report.Add(DeviceStatus.Info, "Système", $"Le PC n'a pas redémarré depuis {(DateTime.Now - lb).TotalDays:0} jours.", "Démarrage rapide de Windows : « Arrêter » ne remet pas le système à zéro.",
                           "Redémarrer (et non éteindre) de temps en temps applique les mises à jour et libère la mémoire.");
            if (facts.BiosReleased is DateTime bd && (DateTime.Now - bd).TotalDays > 3 * 365)
                report.Add(DeviceStatus.Info, "BIOS", $"Le BIOS date de {bd:yyyy} : des versions plus récentes corrigent souvent stabilité et compatibilité.",
                           $"{facts.Bios} du {bd:d MMMM yyyy}, carte mère {facts.Board}.", "Vérifier sur le site du fabricant de la carte mère (opération délicate : à confier à un technicien si besoin).");
            if (facts.GpuDriverDate is DateTime gd && (DateTime.Now - gd).TotalDays > 365)
                report.Add(DeviceStatus.Info, "Carte graphique", $"Le pilote graphique date de {gd:MMMM yyyy}.", string.Join(" · ", facts.Gpus.Select(g => $"{g.Name} {g.Driver}")),
                           "Le mettre à jour (page Mises à jour de Centre PC ou logiciel du fabricant).");
            var micro = CpuMicrocode.Assess(facts.Cpu);
            if (CpuMicrocode.Affected(facts.Cpu))
                report.Add(micro.level, "Processeur", micro.text, micro.tech,
                           micro.level == DeviceStatus.Ok ? null : "Mettre à jour le BIOS depuis la page du fabricant de la carte mère (bouton dans Mises à jour).");
            if (facts.SecureBoot == "désactivé")
                report.Add(DeviceStatus.Info, "Sécurité", "Le démarrage sécurisé (Secure Boot) est désactivé.", "UEFISecureBootEnabled = 0.", "L'activer dans le BIOS protège contre les logiciels malveillants au démarrage (requis par certains jeux).");
            End(n, $"{facts.Cpu}, {facts.RamInstalled / 1073741824.0:0} Go, {facts.Gpus.Count} carte(s) graphique(s), {facts.Disks.Count} disque(s), {snap.Apps.Count} applications, {snap.Drivers.Count} pilotes, {snap.Updates.Count} mises à jour relevés");

            // 2. Santé des périphériques
            n = names[1]; Begin(n);
            int healthIssues = 0;
            foreach (var key in new[] { "problems", "storage", "memory", "display", "network", "battery", "audio" })
            {
                if (ct.IsCancellationRequested) break;
                DeviceCard card = null;
                try { card = await DeviceHealth.Collector(key)(); } catch { }
                if (card == null) continue;
                report.Inventory.Add(($"État : {card.Title}", card.Lines.Select(l => ("", l)).Concat(card.Groups.SelectMany(g => g.Gauges.Select(x => ($"{g.Title} {x.Label}", x.Detail)))).ToList()));
                foreach (var issue in card.Issues.Where(i => !i.Text.StartsWith("Corrigé par Centre PC")))
                {
                    report.Add(issue.Level, card.Title, issue.Text, "");
                    if (issue.Level >= DeviceStatus.Warning) healthIssues++;
                }
            }
            End(n, healthIssues == 0 ? "aucun problème" : $"{healthIssues} point(s) à vérifier", healthIssues == 0 ? DeviceStatus.Ok : DeviceStatus.Warning);

            // 3. Historique de stabilité
            n = names[2]; Begin(n);
            var h = await Task.Run(StabilityHistory.Collect);
            snap.Metrics["Écrans bleus (30 j)"] = h.BlueScreens.Count;
            snap.Metrics["Arrêts brutaux (30 j)"] = h.UnexpectedShutdowns.Count;
            report.KeyFigures.Insert(0, ("Incidents (30 jours)", h.BlueScreens.Count + h.UnexpectedShutdowns.Count + h.WheaErrors == 0 ? "aucun" : $"{h.BlueScreens.Count} écran(s) bleu(s), {h.UnexpectedShutdowns.Count} arrêt(s)"));
            report.Measure("Stabilité (30 jours)", "Écrans bleus", h.BlueScreens.Count.ToString(), string.Join(", ", h.BlueScreens.Take(5).Select(b => $"{b.When:d/MM HH:mm} {b.Code}")));
            report.Measure("Stabilité (30 jours)", "Arrêts brutaux (Kernel-Power 41)", h.UnexpectedShutdowns.Count.ToString());
            report.Measure("Stabilité (30 jours)", "Erreurs matérielles WHEA", $"{h.WheaErrors} erreur(s), {h.WheaWarnings} corrigée(s)");
            report.Measure("Stabilité (30 jours)", "Réinitialisations du pilote graphique (Display 4101)", h.GpuDriverResets.ToString());
            report.Measure("Stabilité (30 jours)", "Erreurs disque (disk, Ntfs, stornvme)", h.DiskErrors.ToString());
            if (h.AppCrashes.Count > 0) report.Measure("Stabilité (30 jours)", "Applications qui plantent", string.Join(", ", h.AppCrashes.Select(a => $"{a.App} ({a.Count})")));
            var hl = DeviceStatus.Ok;
            if (h.BlueScreens.Count > 0)
            {
                hl = DeviceStatus.Problem;
                report.Add(DeviceStatus.Problem, "Stabilité", $"{h.BlueScreens.Count} écran(s) bleu(s) ces 30 derniers jours.",
                    "Codes d'arrêt : " + string.Join(", ", h.BlueScreens.Select(b => $"{b.Code} le {b.When:g}")),
                    "Les codes indiquent la cause : pilote, mémoire (testez la RAM), stockage ou surchauffe.");
            }
            if (h.UnexpectedShutdowns.Count > h.BlueScreens.Count)
            {
                hl = (DeviceStatus)Math.Max((int)hl, (int)DeviceStatus.Warning);
                report.Add(DeviceStatus.Warning, "Stabilité", $"{h.UnexpectedShutdowns.Count} arrêt(s) brutal(aux) : le PC s'est éteint sans passer par « Arrêter ».",
                    "Kernel-Power 41 : " + string.Join(", ", h.UnexpectedShutdowns.Take(5).Select(d => d.ToString("g"))),
                    "Coupure de courant, bouton maintenu, ou plantage complet (alimentation, surchauffe, instabilité).");
            }
            if (h.WheaErrors > 0 || h.WheaWarnings > 0)
            {
                var lvl = h.WheaErrors > 0 ? DeviceStatus.Problem : DeviceStatus.Warning;
                hl = (DeviceStatus)Math.Max((int)hl, (int)lvl);
                report.Add(lvl, "Matériel", $"Le matériel a signalé {h.WheaErrors + h.WheaWarnings} erreur(s) à Windows (processeur, mémoire ou bus PCI Express).",
                    $"WHEA-Logger : {h.WheaErrors} erreur(s) graves, {h.WheaWarnings} corrigée(s).", "Souvent un overclocking / XMP instable, une barrette ou une carte d'extension mal enfoncée.");
            }
            if (h.GpuDriverResets > 0)
            {
                hl = (DeviceStatus)Math.Max((int)hl, (int)DeviceStatus.Warning);
                report.Add(DeviceStatus.Warning, "Carte graphique", $"Le pilote graphique a cessé de répondre {h.GpuDriverResets} fois (écran noir quelques secondes, jeu fermé).",
                    $"Display 4101, dernier le {h.LastGpuReset:g}.", "Mettre à jour ou réinstaller le pilote graphique ; vérifier la température et l'alimentation de la carte.");
            }
            if (h.DiskErrors > 0)
            {
                hl = DeviceStatus.Problem;
                report.Add(DeviceStatus.Problem, "Disques", $"Windows a enregistré {h.DiskErrors} erreur(s) de lecture ou d'écriture sur un disque.",
                    $"Journaux disk / Ntfs / stornvme, dernière le {h.LastDiskError:g}.", "Sauvegarder les fichiers importants, puis vérifier le câble et la santé du disque.");
            }
            if (h.AppCrashes.Count > 0)
                report.Add(DeviceStatus.Info, "Applications", $"Applications qui se sont fermées brutalement ce mois-ci : {string.Join(", ", h.AppCrashes.Take(3).Select(a => a.App))}.",
                    string.Join(", ", h.AppCrashes.Select(a => $"{a.App} × {a.Count}")));
            if (hl == DeviceStatus.Ok) report.Add(DeviceStatus.Ok, "Stabilité", "Aucun écran bleu, arrêt brutal ni erreur matérielle depuis 30 jours.", "Journaux Système : WER 1001, Kernel-Power 41, WHEA, Display 4101, disk : 0.");
            End(n, hl == DeviceStatus.Ok ? "aucun incident en 30 jours" : "incidents relevés", hl);

            // 4. Repos
            if (plan.Idle && !ct.IsCancellationRequested)
            {
                n = "Mesures au repos"; Begin(n);
                var st = await Phase(plan.IdleSec, null, null, "Repos (rien ne doit tourner)");
                double use = PhaseStats.Avg(st.Usage);
                report.Measure("Repos", "Utilisation du processeur", $"{use:0} %");
                if (st.CpuT.Count > 0) { report.Measure("Repos", "Température du processeur", $"{PhaseStats.Avg(st.CpuT):0} °C"); snap.Metrics["Processeur au repos °C"] = Math.Round(PhaseStats.Avg(st.CpuT)); }
                if (st.GpuT.Count > 0) report.Measure("Repos", "Température de la carte graphique", $"{PhaseStats.Avg(st.GpuT):0} °C");
                foreach (var (fan, rpm) in st.FanMax) report.Measure("Repos", $"Ventilateur {fan}", $"{rpm:0} tr/min");
                var lvl = DeviceStatus.Ok;
                if (use > 20)
                {
                    lvl = DeviceStatus.Warning;
                    report.Add(DeviceStatus.Warning, "Processeur", $"Sans rien lancer, le processeur travaille déjà à {use:0} % : un programme tourne en arrière-plan.",
                        $"% Processor Utility moyen sur {st.Seconds} s : {use:0.0} %.", "Regarder le Gestionnaire des tâches (onglet Processus, trier par Processeur) : mise à jour, antivirus, logiciel indésirable…");
                }
                if (PhaseStats.Avg(st.CpuT) > 60 && !facts.Laptop)
                {
                    lvl = DeviceStatus.Warning;
                    report.Add(DeviceStatus.Warning, "Refroidissement", $"Le processeur est déjà à {PhaseStats.Avg(st.CpuT):0} °C au repos.",
                        $"Moyenne sur {st.Seconds} s, utilisation {use:0} %.", "Vérifier le ventirad (bien fixé, pâte thermique) et le flux d'air du boîtier.");
                }
                End(n, $"processeur à {use:0} %" + (st.CpuT.Count > 0 ? $", {PhaseStats.Avg(st.CpuT):0} °C" : ""), lvl);
            }

            // 5. Processeur
            double single = 0, all = 0;
            if (plan.Cpu && !ct.IsCancellationRequested)
            {
                n = "Processeur"; Begin(n);
                var s1 = await Phase(plan.CpuSingleSec, new CpuLoad(1), null, "Processeur, 1 cœur");
                var cpuAll = new CpuLoad(Environment.ProcessorCount);
                var s2 = await Phase(plan.CpuAllSec, cpuAll, null, $"Processeur, {Environment.ProcessorCount} fils");
                single = PhaseStats.Steady(s1.Cpu); all = PhaseStats.Steady(s2.Cpu);
                report.Measure("Processeur", "Calcul sur 1 cœur", $"{single:0} GFLOPS", PhaseStats.Max(s1.MHz) is double m1 ? $"jusqu'à {m1:0} MHz" : null);
                report.Measure("Processeur", $"Calcul sur {Environment.ProcessorCount} fils", $"{all:0} GFLOPS", PhaseStats.Avg(s2.MHz) > 0 ? $"{PhaseStats.Avg(s2.MHz):0} MHz en moyenne" : null);
                report.Measure("Processeur", "Hachage SHA-256 (tous les fils)", $"{cpuAll.HashRate / 1e9:0.0} Go/s");
                report.Measure("Processeur", "Compression Brotli (tous les fils)", $"{cpuAll.CompressRate / 1e6:0} Mo/s");
                if (PhaseStats.Max(s2.CpuT) is double tmax) report.Measure("Processeur", "Température maximale (charge courte)", $"{tmax:0} °C");
                snap.Metrics["Processeur 1 cœur GFLOPS"] = Math.Round(single); snap.Metrics["Processeur tous les cœurs GFLOPS"] = Math.Round(all);
                long errs = s1.Errors + s2.Errors;
                if (errs > 0) report.Add(DeviceStatus.Problem, "Processeur", "Le processeur a fait des erreurs de calcul : la machine n'est pas stable.",
                                         $"{errs} erreur(s) ({s1.FirstError ?? s2.FirstError}).", "Revenir aux réglages d'usine du BIOS (overclocking, XMP / EXPO, tensions), puis refaire le bilan.");
                report.KeyFigures.Add(("Processeur, 1 cœur", $"{single:0} GFLOPS"));
                report.KeyFigures.Add(("Processeur, tous les cœurs", $"{all:0} GFLOPS"));
                End(n, $"{single:0} GFLOPS sur 1 cœur, {all:0} GFLOPS sur tous" + (errs > 0 ? $", {errs} erreur(s)" : ", calculs justes"), errs > 0 ? DeviceStatus.Problem : DeviceStatus.Ok);
            }

            // 6. Mémoire vive
            if (plan.Ram && !ct.IsCancellationRequested)
            {
                n = "Mémoire vive"; Begin(n);
                double gbs = await Task.Run(() => MemoryBench.MeasureGBs(5, ct));
                double theory = facts.RamConfigured * 8.0 * Math.Max(1, Math.Min(2, facts.RamModules)) / 1000;
                report.Measure("Mémoire vive", "Débit en copie (lecture + écriture)", $"{gbs:0} Go/s", theory > 0 ? $"maximum théorique {theory:0} Go/s ({Math.Min(2, Math.Max(1, facts.RamModules))} canal/canaux à {facts.RamConfigured} MT/s)" : null);
                report.KeyFigures.Add(("Mémoire vive", $"{gbs:0} Go/s"));
                if (facts.RamRated > facts.RamConfigured + 150 && facts.RamConfigured > 0)
                    report.Measure("Mémoire vive", "Avec le profil XMP / EXPO", $"≈ {facts.RamRated * 8.0 * Math.Max(1, Math.Min(2, facts.RamModules)) / 1000:0} Go/s théoriques",
                                   $"barrettes prévues pour {facts.RamRated} MT/s : +{(facts.RamRated / (double)facts.RamConfigured - 1) * 100:0} % de débit");
                snap.Metrics["Mémoire Go/s"] = Math.Round(gbs);
                End(n, $"{gbs:0} Go/s" + (theory > 0 ? $" (théorique {theory:0} Go/s)" : ""));
            }

            // 7. Chaque cœur
            if (plan.Cores && cores.Count > 0 && !ct.IsCancellationRequested)
            {
                n = "Chaque cœur"; Begin(n);
                bool hybrid = cores.Select(c => c.Efficiency).Distinct().Count() > 1;
                int perf = cores.Max(c => c.Efficiency);
                var res = new List<(CpuCore core, double gflops, double mhz, long errors)>();
                foreach (var c in cores)
                {
                    if (ct.IsCancellationRequested) break;
                    var st = await Phase(plan.CoreSec, new CpuLoad(1, c.FirstLogical), null, $"Cœur {c.Index + 1}/{cores.Count}" + (hybrid ? (c.Efficiency == perf ? " (P)" : " (E)") : ""));
                    res.Add((c, PhaseStats.Steady(st.Cpu), st.CoreMHz.GetValueOrDefault(c.FirstLogical), st.Errors));
                }
                var table = res.Select(r => new[] { $"Cœur {r.core.Index + 1}", hybrid ? (r.core.Efficiency == perf ? "performance" : "efficacité") : "—", $"{r.core.FirstLogical}",
                                                   $"{r.gflops:0}", r.mhz > 0 ? $"{r.mhz:0}" : "—", r.errors == 0 ? "aucune" : r.errors.ToString() }).ToList();
                report.Tables.Add(("Test cœur par cœur", new[] { "Cœur", "Type", "Processeur logique", "GFLOPS", "MHz", "Erreurs" }, table));
                var bad = res.Where(r => r.errors > 0).ToList();
                var slow = new List<string>();
                foreach (var grp in res.Where(r => r.errors == 0 && r.gflops > 0).GroupBy(r => r.core.Efficiency))
                {
                    var l = grp.OrderBy(r => r.gflops).ToList();
                    if (l.Count < 2) continue;
                    double med = l[l.Count / 2].gflops;
                    slow.AddRange(l.Where(r => r.gflops < med * 0.85).Select(r => $"cœur {r.core.Index + 1} ({(1 - r.gflops / med) * 100:0} % plus lent)"));
                }
                if (bad.Count > 0)
                    report.Add(DeviceStatus.Problem, "Processeur", $"{bad.Count} cœur(s) font des erreurs de calcul quand ils travaillent seuls, à pleine vitesse.",
                        string.Join(", ", bad.Select(b => $"cœur {b.core.Index + 1} (logique {b.core.FirstLogical}) : {b.errors} erreur(s)")), "Réglage de tension par cœur trop agressif (undervolt, Curve Optimizer) ou processeur défectueux.");
                else if (slow.Count > 0)
                    report.Add(DeviceStatus.Warning, "Processeur", "Un ou plusieurs cœurs sont nettement plus lents que les autres.", string.Join(", ", slow), "Refaire le test ; si cela se confirme, vérifier les réglages du BIOS.");
                else if (res.Count == cores.Count)
                    report.Add(DeviceStatus.Ok, "Processeur", $"Les {res.Count} cœurs sont stables et homogènes.", string.Join(" · ", res.GroupBy(r => r.core.Efficiency).OrderByDescending(g => g.Key)
                        .Select(g => $"{(hybrid ? (g.Key == perf ? "P" : "E") : "cœurs")} : {g.Min(r => r.gflops):0}–{g.Max(r => r.gflops):0} GFLOPS, {g.Where(r => r.mhz > 0).Select(r => r.mhz).DefaultIfEmpty(0).Max():0} MHz max")));
                End(n, bad.Count > 0 ? $"{bad.Count} cœur(s) instable(s)" : slow.Count > 0 ? $"{slow.Count} cœur(s) plus lent(s)" : $"{res.Count} cœurs stables",
                    bad.Count > 0 ? DeviceStatus.Problem : slow.Count > 0 ? DeviceStatus.Warning : DeviceStatus.Ok);
            }

            // 8. Carte graphique
            if (plan.GpuStep && adapter != null && !ct.IsCancellationRequested)
            {
                n = "Carte graphique"; Begin(n);
                var gl = new GpuLoad(adapter);
                var st = await Phase(plan.GpuSec, null, gl, adapter.Name);
                double tf = PhaseStats.Steady(st.Gpu);
                report.Measure("Carte graphique", "Calcul", $"{tf:0.00} TFLOPS", adapter.Name);
                report.Measure("Carte graphique", "Mémoire vidéo", $"{gl.MemoryRate / 1e9:0} Go/s");
                if (st.Use.Count > 0) report.Measure("Carte graphique", "Charge moyenne", $"{PhaseStats.Avg(st.Use):0} %");
                if (PhaseStats.Max(st.Pow) is double pw) report.Measure("Carte graphique", "Consommation maximale", $"{pw:0} W");
                if (PhaseStats.Max(st.GpuT) is double gt) report.Measure("Carte graphique", "Température maximale (charge courte)", $"{gt:0} °C" + (PhaseStats.Max(st.Hot) is double hh ? $", point chaud {hh:0} °C" : ""));
                snap.Metrics["Carte graphique TFLOPS"] = Math.Round(tf, 2); snap.Metrics["Mémoire vidéo Go/s"] = Math.Round(gl.MemoryRate / 1e9);
                if (st.Errors > 0 || st.Aborted != null)
                    report.Add(DeviceStatus.Problem, "Carte graphique", "La carte graphique a fait des erreurs de calcul ou a cessé de répondre.", $"{st.Errors} erreur(s) ; {st.Aborted}",
                               "Revenir aux fréquences d'usine (logiciel du fabricant), mettre à jour le pilote, vérifier l'alimentation.");
                report.KeyFigures.Add(("Carte graphique", $"{tf:0.0} TFLOPS"));
                End(n, $"{tf:0.00} TFLOPS, mémoire {gl.MemoryRate / 1e9:0} Go/s" + (st.Use.Count > 0 ? $", chargée à {PhaseStats.Avg(st.Use):0} %" : ""), st.Errors > 0 ? DeviceStatus.Problem : DeviceStatus.Ok);
            }

            // 8 ter. Test de contrainte graphique (façon 3DMark : stabilité = pire boucle / meilleure boucle, réussi à 97 %)
            if (plan.Stress && adapter != null && !ct.IsCancellationRequested)
            {
                n = "Contrainte graphique"; Begin(n);
                int loopSec = plan.StressLoops >= 20 ? 60 : 30;
                var sr = await GraphicsStressAsync(adapter, plan.StressLoops, loopSec, sensors, (loop, fps, g) =>
                {
                    live.Text = $"Contrainte graphique · boucle {loop + 1}/{plan.StressLoops} · {fps:0.0} images/s" + (g?.Temp is double t ? $" · {t:0} °C" : "") + (g?.PowerW is double p ? $" · {p:0} W" : "");
                    temps.Add("Carte graphique", "#22C55E", g?.Temp);
                    temps.Redraw();
                    Progress();
                }, ct);
                if (sr.LoopFps.Count > 0)
                    report.Tables.Add(("Contrainte graphique, boucle par boucle", new[] { "Boucle", "Images par seconde", "% de la meilleure" },
                        sr.LoopFps.Select((f, i) => new[] { $"{i + 1}", $"{f:0.0}", $"{f / sr.LoopFps.Max() * 100:0.0} %" }).ToList()));
                report.Measure("Carte graphique", "Contrainte : stabilité", sr.LoopFps.Count >= 2 ? $"{sr.Stability:0.0} %" : "—", $"{sr.LoopFps.Count} boucles de {loopSec} s en 2560 × 1440 ; réussi à partir de 97 % (comme 3DMark)");
                if (sr.LoopFps.Count > 0) report.Measure("Carte graphique", "Contrainte : images par seconde", $"{sr.LoopFps.Average():0.0}", $"de {sr.LoopFps.Min():0.0} à {sr.LoopFps.Max():0.0}");
                if (sr.GpuTempMax is double sgt) report.Measure("Carte graphique", "Contrainte : température max", $"{sgt:0} °C" + (sr.HotspotMax is double sh ? $", point chaud {sh:0} °C" : ""));
                if (sr.PowerMax is double spw) report.Measure("Carte graphique", "Contrainte : consommation max", $"{spw:0} W", sr.ClockAvg is double sca ? $"fréquence moyenne {sca:0} MHz" : null);
                snap.Metrics["Contrainte graphique stabilité %"] = Math.Round(sr.Stability, 1);
                if (sr.LoopFps.Count > 0) snap.Metrics["Contrainte graphique images/s"] = Math.Round(sr.LoopFps.Average(), 1);
                DeviceStatus lvl = sr.Failure != null ? DeviceStatus.Problem : sr.LoopFps.Count < 2 ? DeviceStatus.Info : sr.Stability >= 97 ? DeviceStatus.Ok : sr.Stability >= 90 ? DeviceStatus.Warning : DeviceStatus.Problem;
                if (sr.Failure != null)
                    report.Add(DeviceStatus.Problem, "Carte graphique", $"Le test de contrainte graphique a échoué : {sr.Failure}.", $"{sr.LoopFps.Count} boucle(s) terminée(s).",
                               "Pilote graphique à réinstaller, alimentation ou refroidissement de la carte à vérifier.");
                else if (sr.LoopFps.Count >= 2)
                    report.Add(lvl, "Carte graphique", sr.Stability >= 97
                            ? $"Test de contrainte graphique réussi : stabilité {sr.Stability:0.0} % (seuil 97 %)."
                            : $"Test de contrainte graphique échoué : stabilité {sr.Stability:0.0} % (seuil 97 %) — la carte perd de la performance sous charge prolongée.",
                        $"{sr.LoopFps.Count} boucles × {loopSec} s : {sr.LoopFps.Min():0.0} à {sr.LoopFps.Max():0.0} images/s" + (sr.GpuTempMax is double t1 ? $", {t1:0} °C max" : "") + (sr.PowerMax is double p1 ? $", {p1:0} W max" : ""),
                        sr.Stability >= 97 ? null : "Cause habituelle : chaleur (flux d'air du boîtier, pâte thermique de la carte) ou limite de puissance.");
                report.KeyFigures.Add(("Contrainte graphique", sr.LoopFps.Count >= 2 ? $"{sr.Stability:0.0} %" : "—"));
                End(n, sr.LoopFps.Count >= 2 ? $"stabilité {sr.Stability:0.0} % sur {sr.LoopFps.Count} boucles" : sr.Failure ?? "interrompu", lvl);
            }

            // 8 bis. Stabilité mémoire + processeur (façon OCCT)
            if (plan.Mem && !ct.IsCancellationRequested)
            {
                n = "Stabilité mémoire + processeur"; Begin(n);
                int th = Environment.ProcessorCount;
                var mt = new MemTest(Math.Max(2, th / 2));
                var st = await Phase(plan.MemMin * 60, new CpuLoad(Math.Max(1, th - th / 2)), null, "Stabilité mémoire + processeur", minutes: true, mem: mt);
                double pct = facts.RamUsable > 0 ? mt.BytesTested * 100.0 / facts.RamUsable : 0;
                report.Measure("Stabilité", "Mémoire testée", $"{mt.BytesTested / 1073741824.0:0.#} Go", $"{pct:0} % de la mémoire (le reste est utilisé par Windows)");
                report.Measure("Stabilité", "Données écrites et vérifiées", $"{mt.Verified / 1e12:0.##} To", $"{mt.Passes} passage(s) complet(s), 4 motifs (adresse, aléatoire, inversé, damier)");
                report.Measure("Stabilité", "Durée", $"{st.Seconds / 60} min {st.Seconds % 60:00} s", $"objectif {plan.MemMin} min");
                if (st.Minutes.Count > 0) report.Tables.Add(("Stabilité mémoire + processeur, minute par minute", new[] { "Temps", "Processeur", "Fréquence", "GFLOPS", "—", "—", "—", "—" }, st.Minutes));
                snap.Metrics["Stabilité : erreurs"] = st.Errors;
                DeviceStatus lvl;
                if (st.Errors > 0 || st.Aborted != null)
                {
                    lvl = DeviceStatus.Problem;
                    bool memErr = mt.Errors > 0;
                    report.Add(DeviceStatus.Problem, "Stabilité", memErr ? $"La mémoire a fait des erreurs après {st.Seconds / 60} min de test : la machine n'est pas stable."
                                                                         : $"Le test de stabilité a échoué : {st.Aborted ?? "erreur de calcul"}.",
                        $"{mt.Errors} erreur(s) mémoire ({mt.FirstError}), {st.Errors - mt.Errors} erreur(s) de calcul ; {mt.BytesTested / 1073741824.0:0.#} Go testés.",
                        memErr ? "Désactiver le XMP / EXPO (ou baisser la fréquence de la mémoire) puis refaire le test ; si l'erreur persiste, tester les barrettes une par une."
                               : "Revenir aux réglages d'usine du BIOS, vérifier refroidissement et alimentation.");
                }
                else
                {
                    lvl = DeviceStatus.Ok;
                    report.Add(DeviceStatus.Ok, "Stabilité", $"Aucune erreur en {st.Seconds / 60} minutes de test mémoire + processeur ({mt.BytesTested / 1073741824.0:0} Go de mémoire vérifiés en boucle).",
                        $"{mt.Verified / 1e12:0.##} To vérifiés, {mt.Passes} passage(s), aucune erreur de calcul.");
                    if (plan.MemMin < 60)
                        report.Add(DeviceStatus.Info, "Stabilité", $"Test de {plan.MemMin} min : pour une validation complète (référence des techniciens), lancez-le pendant 1 heure.", "");
                }
                report.KeyFigures.Add(("Stabilité mémoire + processeur", st.Errors == 0 && st.Aborted == null ? $"0 erreur en {st.Seconds / 60} min" : "ÉCHEC"));
                Thermals(st);
                End(n, st.Errors == 0 && st.Aborted == null ? $"aucune erreur en {st.Seconds / 60} min, {mt.BytesTested / 1073741824.0:0} Go testés" : st.Aborted ?? $"{st.Errors} erreur(s)", lvl);
            }

            // 9. Endurance + 10. Refroidissement
            if (plan.Endurance && !ct.IsCancellationRequested)
            {
                n = $"Endurance ({plan.EnduranceMin} min)"; Begin(n);
                GpuLoad gl = adapter != null ? new GpuLoad(adapter) : null;
                var st = await Phase(plan.EnduranceMin * 60, new CpuLoad(Environment.ProcessorCount), gl, "Endurance processeur + carte graphique", minutes: true);
                Thermals(st);
                if (st.Minutes.Count > 0) report.Tables.Add(("Endurance minute par minute", new[] { "Temps", "Processeur", "Fréquence", "GFLOPS", "Carte graphique", "Point chaud", "Consommation", "TFLOPS" }, st.Minutes));
                var lvl = DeviceStatus.Ok;
                var tech = new List<string>();
                double cStart = st.Cpu.Take(Math.Max(1, st.Cpu.Count / 6)).DefaultIfEmpty(0).Average(), cEnd = st.Cpu.Skip(st.Cpu.Count * 5 / 6).DefaultIfEmpty(0).Average();
                double gStart = st.Gpu.Take(Math.Max(1, st.Gpu.Count / 6)).DefaultIfEmpty(0).Average(), gEnd = st.Gpu.Skip(st.Gpu.Count * 5 / 6).DefaultIfEmpty(0).Average();
                double cDrop = cStart > 0 ? (1 - cEnd / cStart) * 100 : 0, gDrop = gStart > 0 ? (1 - gEnd / gStart) * 100 : 0;
                double limitedPct = st.Seconds > 0 ? st.Limited * 100.0 / st.Seconds : 0;
                tech.Add($"durée {st.Seconds} s, processeur {cStart:0} → {cEnd:0} GFLOPS ({-cDrop:+0;-0;0} %), bridé {limitedPct:0} % du temps (limite min {st.LimitMin:0} %)");
                if (gl != null) tech.Add($"carte graphique {gStart:0.00} → {gEnd:0.00} TFLOPS ({-gDrop:+0;-0;0} %)");
                if (PhaseStats.Max(st.CpuT) is double ct1) { tech.Add($"processeur max {ct1:0} °C"); snap.Metrics["Processeur en charge °C"] = Math.Round(ct1); report.KeyFigures.Add(("Processeur en charge max", $"{ct1:0} °C")); }
                if (PhaseStats.Max(st.GpuT) is double gt1) { tech.Add($"carte graphique max {gt1:0} °C"); snap.Metrics["Carte graphique en charge °C"] = Math.Round(gt1); report.KeyFigures.Add(("Carte graphique en charge max", $"{gt1:0} °C")); }
                if (PhaseStats.Max(st.Hot) is double hs1) tech.Add($"point chaud max {hs1:0} °C");
                if (PhaseStats.Max(st.Pow) is double pw1) tech.Add($"carte graphique jusqu'à {pw1:0} W");
                foreach (var (fan, rpm) in st.FanMax) report.Measure("Endurance", $"Ventilateur {fan} (max)", $"{rpm:0} tr/min");
                bool amd = (facts.Cpu ?? "").Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
                double tj = amd ? 95 : 100;   // température maximale de fonctionnement (Ryzen 7000 / 9000 : 95 °C ; Intel : 100 °C)
                if (st.Aborted != null || st.Errors > 0)
                {
                    lvl = DeviceStatus.Problem;
                    report.Add(DeviceStatus.Problem, "Endurance", $"La machine n'a pas tenu la charge maximale : {st.Aborted ?? "erreur de calcul"}.", string.Join(" · ", tech),
                               "Instabilité ou surchauffe : vérifier refroidissement, alimentation et réglages du BIOS avant toute utilisation intensive.");
                }
                else
                {
                    if (PhaseStats.Max(st.CpuT) is double cmax && cmax >= tj - 5)
                    {
                        bool thermal = limitedPct > 20;
                        lvl = thermal ? DeviceStatus.Problem : DeviceStatus.Warning;
                        report.Add(lvl, "Refroidissement",
                            $"Le processeur monte à {cmax:0} °C en charge maximale, pour une limite de {tj:0} °C" + (thermal ? " : il se bride pour se protéger." : " : peu de marge, même s'il ne se bride pas encore."),
                            string.Join(" · ", tech),
                            thermal ? "Améliorer le refroidissement (ventirad plus performant, pâte thermique, flux d'air du boîtier)."
                                    : "Acceptable pour une charge extrême ; en usage réel (jeu) la température est plus basse. Pour plus de marge : pâte thermique, courbe des ventilateurs, ou limite de puissance dans le BIOS.");
                    }
                    if (limitedPct > 20 || cDrop > 10)
                    { lvl = DeviceStatus.Warning; report.Add(DeviceStatus.Warning, "Processeur", $"Le processeur se bride pendant l'effort (perte de {Math.Max(cDrop, 0):0} % de performance).", string.Join(" · ", tech),
                        PhaseStats.Max(st.CpuT) is double c2 && c2 >= 88 ? "Cause probable : la chaleur." : "Cause probable : une limite de puissance (BIOS, mode d'alimentation, portable sur batterie)."); }
                    if (PhaseStats.Max(st.Hot) is double hot && hot >= 105)
                    { lvl = DeviceStatus.Warning; report.Add(DeviceStatus.Warning, "Carte graphique", $"Le point chaud de la carte graphique atteint {hot:0} °C.", string.Join(" · ", tech), "Pâte thermique de la carte ou flux d'air à vérifier."); }
                    if (gl != null && gDrop > 10)
                    { lvl = DeviceStatus.Warning; report.Add(DeviceStatus.Warning, "Carte graphique", $"La carte graphique perd {gDrop:0} % de performance au fil de l'effort.", string.Join(" · ", tech), "Chaleur ou limite de puissance de la carte."); }
                    if (st.FanStopped.Count > 0)
                    { lvl = DeviceStatus.Problem; report.Add(DeviceStatus.Problem, "Ventilation", $"Ventilateur arrêté pendant l'effort : {string.Join(", ", st.FanStopped)}.", string.Join(" · ", tech), "Vérifier son branchement et sa courbe dans le BIOS."); }
                    if (lvl == DeviceStatus.Ok)
                        report.Add(DeviceStatus.Ok, "Endurance", $"La machine tient {st.Seconds / 60} minutes de charge maximale sans erreur, sans surchauffe ni perte de performance.", string.Join(" · ", tech));
                }
                End(n, lvl == DeviceStatus.Ok ? "tenue sans faiblesse" : st.Aborted ?? "points à surveiller", lvl);

                if (plan.Cooldown && !ct.IsCancellationRequested)
                {
                    n = "Refroidissement"; Begin(n);
                    var cool = await Phase(plan.CooldownSec, null, null, "Retour au calme");
                    if (cool.CpuT.Count > 1)
                    {
                        double from = st.CpuT.LastOrDefault(), to = cool.CpuT.Last();
                        report.Measure("Refroidissement", $"Processeur après {cool.Seconds} s de repos", $"{from:0} → {to:0} °C", $"baisse de {from - to:0} °C");
                        if (from - to < 10 && from > 75)
                            report.Add(DeviceStatus.Info, "Refroidissement", "Le processeur refroidit lentement après l'effort.", $"{from:0} → {to:0} °C en {cool.Seconds} s.", "Vérifier le flux d'air et la propreté des ventilateurs.");
                        End(n, $"processeur {from:0} → {to:0} °C en {cool.Seconds} s");
                    }
                    else End(n, "température du processeur non mesurée", DeviceStatus.Info);
                }
            }

            // 11. Disques
            if (plan.Disks && drives.Count > 0 && !ct.IsCancellationRequested)
            {
                n = drives.Count > 1 ? "Disques" : "Disque"; Begin(n);
                var parts = new List<string>();
                var diskRows = new List<string[]>();
                // Lien PCI Express des SSD NVMe : un SSD sur un lien réduit plafonne sous sa vitesse
                foreach (var l in await Task.Run(DiskMark.PcieLinks))
                {
                    report.Measure("Disques", $"Lien PCIe : {l.Disk}", $"PCIe {l.Gen}.0 ×{l.Width}", l.Degraded ? $"maximum possible PCIe {l.MaxGen}.0 ×{l.MaxWidth}" : "au maximum");
                    if (l.Degraded)
                        report.Add(DeviceStatus.Warning, "Disques", $"Le SSD {l.Disk} fonctionne en PCIe {l.Gen}.0 ×{l.Width} au lieu de PCIe {l.MaxGen}.0 ×{l.MaxWidth} : il ne peut pas atteindre sa vitesse.",
                                   $"DEVPKEY_PciDevice_CurrentLinkSpeed / Width = Gen{l.Gen} x{l.Width}, maximum Gen{l.MaxGen} x{l.MaxWidth}.",
                                   "Le placer sur l'emplacement M.2 relié au processeur (voir le manuel de la carte mère), vérifier qu'il est bien enfoncé et que l'emplacement n'est pas partagé.");
                }
                // Santé SMART (comme CrystalDiskInfo), relevée par la session HWiNFO en administrateur
                var health = _hwinfo?.DiskHealth() ?? new List<HwinfoSession.DiskHealthInfo>();
                if (health.Count > 0)
                {
                    report.Tables.Add(("Santé des disques (S.M.A.R.T.)", new[] { "Disque", "État", "Température", "Usure", "Heures d'utilisation", "Erreurs non corrigées" },
                        health.Select(dh => new[] { dh.Name, dh.Health ?? "—", dh.Temp is double t ? $"{t:0} °C" : "—", dh.Wear is double w ? $"{w:0} %" : "—",
                                                    dh.Hours is double hr ? $"{hr:0} h" : "—", $"{(dh.ReadErrors ?? 0) + (dh.WriteErrors ?? 0):0}" }).ToList()));
                    foreach (var dh in health)
                    {
                        if (dh.Wear is double w && w >= 70)
                            report.Add(w >= 90 ? DeviceStatus.Problem : DeviceStatus.Warning, "Disques", $"Le disque {dh.Name} est usé à {w:0} % de sa durée de vie prévue.",
                                       $"S.M.A.R.T. « Percentage Used » = {w:0} %, {dh.Hours:0} h d'utilisation.", "Prévoir son remplacement et sauvegarder régulièrement.");
                        if ((dh.ReadErrors ?? 0) + (dh.WriteErrors ?? 0) > 0)
                            report.Add(DeviceStatus.Problem, "Disques", $"Le disque {dh.Name} a enregistré des erreurs de lecture ou d'écriture non corrigées.",
                                       $"Erreurs non corrigées : lecture {dh.ReadErrors:0}, écriture {dh.WriteErrors:0}.", "Sauvegarder sans attendre et remplacer le disque.");
                    }
                    if (health.All(dh => (dh.Wear ?? 0) < 70 && (dh.ReadErrors ?? 0) + (dh.WriteErrors ?? 0) == 0))
                        report.Add(DeviceStatus.Ok, "Disques", $"Santé des disques bonne : usure maximale {health.Max(dh => dh.Wear ?? 0):0} %, aucune erreur non corrigée.",
                                   string.Join(" · ", health.Select(dh => $"{dh.Name} : {dh.Wear:0} % d'usure, {dh.Hours:0} h")));
                }
                foreach (var d in drives)
                {
                    if (ct.IsCancellationRequested) break;
                    var letter = d.Name.TrimEnd('\\');
                    var prog = new Progress<string>(t => live.Text = $"{letter} : {t}");
                    try
                    {
                        var r = await DiskMark.RunAsync(d.Name, 1L << 30, plan.Level == "Express" ? 3 : 5, prog, ct);
                        diskRows.Add(new[] { letter, $"{r.SeqReadMBs:0}", $"{r.SeqWriteMBs:0}", $"{r.RndQ32ReadMBs:0}", $"{r.RndQ32WriteMBs:0}", $"{r.RndQ1ReadMBs:0}", $"{r.RndQ1WriteMBs:0}" });
                        snap.Metrics[$"Disque {letter} lecture Mo/s"] = Math.Round(r.SeqReadMBs);
                        snap.Metrics[$"Disque {letter} 4K Q1 lecture Mo/s"] = Math.Round(r.RndQ1ReadMBs, 1);
                        parts.Add($"{letter} {r.SeqReadMBs:0} / {r.SeqWriteMBs:0} Mo/s");
                        report.KeyFigures.Add(($"Disque {letter} (lecture / écriture)", $"{r.SeqReadMBs:0} / {r.SeqWriteMBs:0} Mo/s"));
                        if (r.SeqReadMBs < 300 && string.Equals(d.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                            report.Add(DeviceStatus.Info, "Disques", "Windows est installé sur un disque lent : c'est souvent la première cause de lenteur.",
                                       $"{letter} : {r.SeqReadMBs:0} Mo/s en lecture séquentielle, {r.RndQ1ReadMBs:0.0} Mo/s en 4K Q1.", "Passer Windows sur un SSD change radicalement la réactivité.");
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { parts.Add($"{letter} : {ex.Message}"); }
                }
                if (diskRows.Count > 0)
                    report.Tables.Add(("Disques : mesures façon CrystalDiskMark (Mo/s)", new[] { "Lecteur", "SEQ1M Q8T1 lecture", "SEQ1M Q8T1 écriture", "RND4K Q32T1 lecture", "RND4K Q32T1 écriture", "RND4K Q1T1 lecture", "RND4K Q1T1 écriture" }, diskRows));
                End(n, string.Join(" · ", parts));
            }

            // 12. Internet
            if (plan.Net && !ct.IsCancellationRequested)
            {
                n = "Internet"; Begin(n);
                try
                {
                    var r = await DeviceTests.NetBenchAsync(new Progress<string>(t => live.Text = "Internet : " + t), ct);
                    report.Measure("Internet", "Latence / gigue", $"{r.PingMs:0} ms / {r.JitterMs:0} ms");
                    report.Measure("Internet", "Téléchargement / envoi", $"{r.DownMbps:0} / {r.UpMbps:0} Mbit/s");
                    snap.Metrics["Internet téléchargement Mbit/s"] = Math.Round(r.DownMbps);
                    report.KeyFigures.Add(("Internet", $"{r.DownMbps:0} / {r.UpMbps:0} Mbit/s"));
                    End(n, $"{r.DownMbps:0} Mbit/s descendant, {r.UpMbps:0} montant, {r.PingMs:0} ms");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { End(n, "test impossible : " + ex.Message, DeviceStatus.Info); }
            }
        }
        catch (Exception ex) { Log.Error("Bilan", ex); report.Op("Erreur", ex.Message); }
        finally
        {
            sensors?.Dispose();
            SetThreadExecutionState(0x80000000);
            _hwinfo?.Stop(); _hwinfo = null;
        }

        // Fin : instantané comparé au précédent, rapport enregistré
        report.Interrupted = ct.IsCancellationRequested;
        if (report.Interrupted) report.Op("Interruption", "Bilan arrêté avant la fin.");
        report.End = DateTime.Now;
        string htmlPath = null;
        try
        {
            snap.Findings = report.Findings.Select(f => $"{f.Level}|{f.Area} : {f.Client}").ToList();
            snap.Verdict = report.VerdictTitle;
            if (facts != null)
            {
                snap.CompareTo(MachineSnapshot.Latest(report.Machine), report);
                if (!report.Interrupted) report.Op("Instantané", "État de l'installation enregistré : " + Path.GetFileName(snap.Save()));
            }
            snap.Verdict = report.VerdictTitle;
            var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
            htmlPath = Path.Combine(MachineSnapshot.Folder, $"Bilan {report.Machine} {report.Start:yyyy-MM-dd HH-mm}.html");
            File.WriteAllText(htmlPath, report.Html(version), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex) { Log.Error("Rapport du bilan", ex); }
        if (_testKind == "checkup") ShowCheckupResult(report, htmlPath);
        else if (htmlPath != null) ShowToast("Bilan terminé : rapport enregistré dans Documents\\Centre PC\\Bilans.");
    }

    // ================================================================== résultat
    private void ShowCheckupResult(CheckupReport report, string htmlPath)
    {
        _testCleanup = null;
        DeviceTestHost.Children.Clear();
        var color = report.Worst switch { DeviceStatus.Problem => "#EF4444", DeviceStatus.Warning => "#F59E0B", _ => "#10B981" };
        DeviceTestHost.Children.Add(new TextBlock { Text = report.VerdictTitle, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = BrushOfStatic(color), Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
        DeviceTestHost.Children.Add(TestText($"Bilan {report.Level.ToLowerInvariant()} · {(report.End - report.Start).TotalMinutes:0} min · {report.Findings.Count(f => f.Level >= DeviceStatus.Warning)} point(s) à traiter", 12.5));
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        if (htmlPath != null)
        {
            buttons.Children.Add(TestButton("", "Ouvrir le rapport complet", () => OpenTarget(htmlPath), accent: true));
            buttons.Children.Add(TestButton("", "Dossier des bilans", () => OpenTarget(MachineSnapshot.Folder)));
        }
        buttons.Children.Add(TestButton("", "Copier le résumé client", () =>
        {
            try { Clipboard.SetText(report.ClientText()); ShowToast("Résumé copié : collez-le dans un e-mail ou un ticket."); } catch { ShowToast("Copie impossible."); }
        }));
        buttons.Children.Add(TestButton("", "Nouveau bilan", () => OpenDeviceTest("checkup")));
        DeviceTestHost.Children.Add(buttons);
        foreach (var f in report.Findings.OrderByDescending(f => f.Level).ThenBy(f => f.Area))
        {
            var (_, c, glyph) = StatusLook(f.Level);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), Foreground = BrushOfStatic(c), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"{f.Area} : {f.Client}", TextWrapping = TextWrapping.Wrap });
            if (f.Advice != null) sp.Children.Add(new TextBlock { Text = "→ " + f.Advice, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Subtle"), FontSize = 12.5 });
            row.Children.Add(sp);
            DeviceTestHost.Children.Add(row);
        }
    }
}
