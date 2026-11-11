using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Test « Processeur et carte graphique » : mesure des performances (1 minute) ou test d'endurance (5, 15 ou 30 minutes)
/// en charge maximale, avec températures, ventilateurs, fréquences et bridage en direct, vérification des calculs,
/// arrêt automatique en cas de danger, verdict clair et historique. Aucun pilote ajouté par Centre PC : la température
/// du processeur et les ventilateurs viennent de LibreHardwareMonitor ou HWiNFO si l'utilisateur les a (installables d'ici).
/// </summary>
public partial class MainWindow
{
    private int _benchMinutes = 15;
    private bool _benchCpu = true, _benchGpu = true;
    private long _benchGpuLuid;
    private DispatcherTimer _monitorWatch;

    // ================================================================== préparation
    private void BuildBenchTest()
    {
        DeviceTestHost.Children.Add(TestText("Met le processeur et la carte graphique à 100 % pour mesurer leurs performances ou valider une installation "
            + "en conditions extrêmes (nouveau PC, nouveau refroidissement, overclocking, XMP). Chaque calcul est vérifié : une seule erreur signale un PC instable. "
            + "Le test s'arrête tout seul si une température devient dangereuse ; Échap l'arrête à tout moment."));

        var measures = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DeviceTestHost.Children.Add(Section("Ce qui sera mesuré"));
        DeviceTestHost.Children.Add(measures);
        var gpus = Gpu.Adapters();
        if (_benchGpuLuid == 0 || gpus.All(g => g.Luid != _benchGpuLuid)) _benchGpuLuid = gpus.FirstOrDefault()?.Luid ?? 0;
        _ = FillMeasuresAsync(measures, gpus);

        // Carte graphique testée (si plusieurs : portable avec puce intégrée et carte dédiée…)
        if (gpus.Count > 1)
        {
            DeviceTestHost.Children.Add(Section("Carte graphique à tester"));
            DeviceTestHost.Children.Add(Choices(gpus.Select(g => (g.Name + (g.DedicatedMemory > 512L << 20 ? $" · {g.DedicatedMemory / 1073741824.0:0} Go" : " · intégrée"), (object)g.Luid)).ToList(),
                                                _benchGpuLuid, v => _benchGpuLuid = (long)v));
        }

        DeviceTestHost.Children.Add(Section("Mesurer les performances"));
        DeviceTestHost.Children.Add(TestText("Environ 1 minute : processeur sur 1 cœur, puis sur tous les cœurs, puis carte graphique. Pour comparer avant / après un réglage."));
        DeviceTestHost.Children.Add(TestButton("", "Mesurer les performances", () => StartBench(endurance: false), accent: true));

        DeviceTestHost.Children.Add(Section("Test d'endurance"));
        DeviceTestHost.Children.Add(TestText("Charge maximale continue : vérifie la stabilité, les températures, les ventilateurs et l'absence de bridage dans la durée."));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)> { ("5 minutes", 5), ("15 minutes", 15), ("30 minutes", 30) }, _benchMinutes, v => _benchMinutes = (int)v));
        var what = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        what.Children.Add(Check("Processeur", _benchCpu, v => _benchCpu = v));
        if (gpus.Count > 0) what.Children.Add(Check("Carte graphique", _benchGpu, v => _benchGpu = v));
        DeviceTestHost.Children.Add(what);
        DeviceTestHost.Children.Add(TestButton("", "Lancer le test d'endurance", () => StartBench(endurance: true), accent: true));

        DeviceTestHost.Children.Add(Section("Test cœur par cœur"));
        DeviceTestHost.Children.Add(TestText("Charge chaque cœur seul, l'un après l'autre, à sa fréquence maximale : repère un cœur instable ou plus faible que les autres, "
            + "que la charge sur tous les cœurs masque (réglages de tension par cœur, overclocking, Curve Optimizer…)."));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)> { ("10 s par cœur", 10), ("30 s par cœur", 30), ("1 min par cœur", 60) }, _coreSeconds, v => _coreSeconds = (int)v));
        DeviceTestHost.Children.Add(TestButton("\uE950", "Tester cœur par cœur", StartCoreTest, accent: true));

        DeviceTestHost.Children.Add(Section("Test de contrainte graphique (façon 3DMark)"));
        DeviceTestHost.Children.Add(TestText("Rendu 3D lourd en boucle (2560 × 1440). Chaque boucle est mesurée : la stabilité compare la pire à la meilleure. "
            + "Comme 3DMark, 97 % ou plus = la carte graphique et son refroidissement tiennent la charge sans perdre de performance."));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)> { ("10 boucles × 30 s", 10), ("20 boucles × 60 s (comme 3DMark)", 20) }, _stressLoops, v => _stressLoops = (int)v));
        DeviceTestHost.Children.Add(TestButton("\uE7F4", "Lancer le test de contrainte", StartGraphicsStress, accent: true));

        DeviceTestHost.Children.Add(Section("Test de stabilité mémoire et processeur (façon OCCT)"));
        DeviceTestHost.Children.Add(TestText("Remplit la mémoire libre de motifs, la relit et la vérifie en boucle, pendant que le processeur calcule à fond : "
            + "aucune erreur pendant 1 heure = installation stable (référence des techniciens). Indispensable après l'activation du XMP / EXPO."));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)> { ("15 min", 15), ("30 min", 30), ("1 heure", 60), ("2 heures", 120) }, _stabMinutes, v => _stabMinutes = (int)v));
        DeviceTestHost.Children.Add(Choices(new List<(string, object)> { ("Mémoire + processeur", "both"), ("Mémoire seule", "mem"), ("Processeur seul", "cpu") }, _stabMode, v => _stabMode = (string)v));
        DeviceTestHost.Children.Add(TestButton("\uE9F5", "Lancer le test de stabilité", StartStabilityTest, accent: true));

        DeviceTestHost.Children.Add(Section("Outils d'expert (en cas de doute)"));
        DeviceTestHost.Children.Add(TestText("Les logiciels de référence des techniciens, ouverts le temps de l'utilisation : installés si besoin (paquet officiel), "
            + "puis désinstallés complètement dès que vous les fermez. Déjà installés, ils sont simplement ouverts et laissés en place.", 12.5));
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var t in ToolSession.Tools)
        {
            var tool = t;
            var b = TestButton("\uE8A7", t.Name, () =>
            {
                var err = ToolSession.Open(tool);
                ShowToast(err ?? $"{tool.Name} s'ouvre (acceptez l'autorisation de Windows). S'il n'était pas installé, il sera retiré à sa fermeture.");
            });
            b.ToolTip = t.Purpose;
            tools.Children.Add(b);
        }
        DeviceTestHost.Children.Add(tools);

        var history = Settings.Current.BenchHistory;
        if (history.Any(h => h.Method == BenchResult.CurrentMethod))
        {
            DeviceTestHost.Children.Add(Section("Résultats précédents"));
            foreach (var r in history.Where(h => h.Method == BenchResult.CurrentMethod).Reverse().Take(6))
                DeviceTestHost.Children.Add(TestText($"{r.Date:d MMM yyyy HH:mm} · {r.Kind} · " + Summary(r), 12.5));
        }
    }

    private static string Summary(BenchResult r)
    {
        var parts = new List<string>();
        if (r.CpuSingleGflops > 0) parts.Add($"1 cœur {r.CpuSingleGflops:0} GFLOPS");
        if (r.CpuAllGflops > 0) parts.Add($"tous les cœurs {r.CpuAllGflops:0} GFLOPS");
        if (r.GpuTflops > 0) parts.Add($"carte graphique {r.GpuTflops:0.0} TFLOPS");
        if (r.CpuTempMax != null) parts.Add($"processeur max {r.CpuTempMax:0} °C");
        if (r.GpuTempMax != null) parts.Add($"carte graphique max {r.GpuTempMax:0} °C");
        parts.Add(r.Errors == 0 ? "stable" : $"{r.Errors} erreur(s)");
        if (!string.IsNullOrEmpty(r.Verdict)) parts.Add(r.Verdict);
        return string.Join(" · ", parts);
    }

    /// <summary>Liste « ✓ disponible / – non disponible », puis les boutons pour ajouter la température du processeur et les ventilateurs.</summary>
    private async Task FillMeasuresAsync(StackPanel panel, List<Gpu.Adapter> gpus)
    {
        panel.Children.Clear();
        panel.Children.Add(TestText("Lecture des capteurs…", 12.5));
        SensorSnapshot s = null;
        string monitor = null;
        await Task.Run(() =>
        {
            try
            {
                using var sensors = new Sensors();
                sensors.Read();
                Thread.Sleep(600);   // deux lectures : les compteurs de Windows ont besoin d'un intervalle
                s = sensors.Read();
                monitor = Sensors.MonitorRunning();
            }
            catch (Exception ex) { Log.Error("Capteurs", ex); }
        });
        if (_testKind != "bench" || s == null) { panel.Children.Clear(); return; }
        panel.Children.Clear();
        void Row(bool ok, string text) => panel.Children.Add(new TextBlock
        {
            Text = (ok ? "✓  " : "–  ") + text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
            Foreground = ok ? BrushOfStatic("#10B981") : (Brush)FindResource("Text"), Opacity = ok ? 1 : 0.75
        });
        Row(true, $"{Bench.CpuName()} : utilisation, fréquence réelle et bridage (chaleur ou puissance), calculs vérifiés");
        foreach (var g in s.Gpus)
        {
            var got = new List<string>();
            if (g.Temp != null) got.Add("température");
            if (g.FanRpm != null || g.FanPercent != null) got.Add("ventilateur");
            if (g.PowerW != null || g.PowerPercent != null) got.Add("consommation");
            if (g.ClockMHz != null) got.Add("fréquence");
            Row(got.Count > 0, $"{g.Name} : " + (got.Count > 0 ? string.Join(", ", got) : "performances seulement (le pilote ne donne pas de capteurs)"));
        }
        if (s.CpuTemp != null)
            Row(true, $"Température du processeur et ventilateurs ({s.Fans.Count}) : lus dans {monitor ?? s.CpuTempSource}");
        else if (monitor == "HWiNFO")
        {
            Row(false, "Température du processeur et ventilateurs : HWiNFO est ouvert mais ne partage pas ses mesures.");
            panel.Children.Add(MonitorHelp(panel, gpus, monitor));
        }
        else if (monitor == null)
        {
            // HWiNFO géré par Centre PC le temps du test
            bool installed = Bench.FindMonitor("hwinfo") != null;
            Row(_benchManaged, "Température du processeur et ventilateurs : " + (_benchManaged
                ? (installed ? "HWiNFO sera ouvert par Centre PC pendant le test, puis refermé." : "HWiNFO sera installé le temps du test, puis désinstallé complètement.")
                : "non mesurées (HWiNFO désactivé ci-dessous)."));
            var box = new StackPanel { Margin = new Thickness(18, 4, 0, 4) };
            box.Children.Add(Check("Utiliser HWiNFO pendant le test (Centre PC s'en occupe)", _benchManaged, v => { _benchManaged = v; _ = FillMeasuresAsync(panel, gpus); }));
            box.Children.Add(TestText("Windows demande une autorisation au lancement du test. Vos réglages HWiNFO sont remis à l'identique ensuite ; "
                + "son pilote n'est actif que pendant le test" + (installed ? "." : ", et rien ne reste sur le PC après."), 11.5));
            panel.Children.Add(box);
        }
        else Row(false, $"Température du processeur : non trouvée dans les mesures de {monitor}.");
    }

    /// <summary>
    /// Ajouter la température du processeur et les ventilateurs : ouvrir le programme de surveillance installé,
    /// ou l'installer (winget). Ces programmes utilisent leur propre pilote : c'est le choix de l'utilisateur, pas de Centre PC.
    /// </summary>
    private FrameworkElement MonitorHelp(StackPanel measures, List<Gpu.Adapter> gpus, string running)
    {
        var box = new StackPanel { Margin = new Thickness(18, 6, 0, 4) };
        var lhm = Bench.FindMonitor("lhm");
        var hw = Bench.FindMonitor("hwinfo");
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var status = TestText("", 12.5);
        if (running == "HWiNFO")
            box.Children.Add(TestText("HWiNFO est ouvert mais ne partage pas ses mesures : dans ses Réglages (clic droit sur son icône près de l'horloge), onglet « Général / Interface utilisateur », "
                + "cochez « Prise en charge de la mémoire partagée » (« Shared Memory Support »), puis OK. Version gratuite : à recocher toutes les 12 heures.", 12.5));
        if (lhm != null)
            buttons.Children.Add(TestButton("", "Ouvrir LibreHardwareMonitor", () => OpenMonitor(lhm, "LibreHardwareMonitor", measures, gpus, status)));
        if (hw != null)
            buttons.Children.Add(TestButton("", "Ouvrir HWiNFO", () => OpenMonitor(hw, "HWiNFO", measures, gpus, status)));
        if (lhm == null && hw == null)
        {
            box.Children.Add(TestText("Ces mesures demandent un pilote système, que Centre PC n'installe pas lui-même (sécurité, anti-triche des jeux). "
                + "Vous pouvez installer l'un de ces programmes de surveillance reconnus ; Centre PC lira ses mesures pendant le test :", 12.5));
            buttons.Children.Add(TestButton("", "Installer LibreHardwareMonitor", () => _ = InstallMonitorAsync("LibreHardwareMonitor.LibreHardwareMonitor", "lhm", measures, gpus, status)));
            buttons.Children.Add(TestButton("", "Installer HWiNFO", () => _ = InstallMonitorAsync("REALiX.HWiNFO", "hwinfo", measures, gpus, status)));
            box.Children.Add(buttons);
            box.Children.Add(TestText("LibreHardwareMonitor : libre et gratuit, mesures partagées automatiquement ; il utilise le pilote PawnIO, refusé par l'anti-triche FACEIT "
                + "(à désinstaller avant d'y jouer). HWiNFO : gratuit pour un usage personnel ; dans ses Réglages, cochez « Prise en charge de la mémoire partagée » (à recocher toutes les 12 heures en version gratuite) ; laissez « Pilote persistant » décoché : son pilote est alors retiré quand HWiNFO est fermé.", 11.5));
        }
        else box.Children.Add(buttons);
        box.Children.Add(status);
        return box;
    }

    private async Task InstallMonitorAsync(string id, string which, StackPanel measures, List<Gpu.Adapter> gpus, TextBlock status)
    {
        status.Text = "Installation en cours (Windows peut demander une autorisation)…";
        status.Style = null;
        int code;
        try
        {
            var p = Process.Start(new ProcessStartInfo("winget", $"install --id {id} -e --accept-package-agreements --accept-source-agreements --disable-interactivity")
            { UseShellExecute = false, CreateNoWindow = true });
            await p.WaitForExitAsync();
            code = p.ExitCode;
        }
        catch (Exception ex) { status.Text = "Installation impossible : " + ex.Message; return; }
        if (_testKind != "bench") return;
        var exe = Bench.FindMonitor(which);
        if (exe == null)
        {
            status.Text = code == 0 ? "Installé. Ouvrez-le depuis le menu Démarrer, puis revenez ici." : $"L'installation n'a pas abouti (code {code}).";
            return;
        }
        OpenMonitor(exe, which == "hwinfo" ? "HWiNFO" : "LibreHardwareMonitor", measures, gpus, status);
    }

    /// <summary>Ouvre le programme (Windows demande l'autorisation administrateur), puis attend que ses mesures soient lisibles.</summary>
    private void OpenMonitor(string exe, string name, StackPanel measures, List<Gpu.Adapter> gpus, TextBlock status)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { status.Text = "Ouverture annulée."; return; }
        catch (Exception ex) { status.Text = "Impossible d'ouvrir : " + ex.Message; return; }
        status.Style = null;
        status.Text = name == "HWiNFO"
            ? "HWiNFO s'ouvre : cliquez sur « Réglages », onglet « Général / Interface utilisateur », cochez « Prise en charge de la mémoire partagée », OK, puis « Capteurs uniquement ». "
            + "Centre PC détecte ses mesures automatiquement…"
            : "LibreHardwareMonitor s'ouvre (acceptez l'installation de PawnIO s'il la demande). Centre PC détecte ses mesures automatiquement…";
        _monitorWatch?.Stop();
        int tries = 0;
        _monitorWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitorWatch.Tick += async (_, _) =>
        {
            if (_testKind != "bench" || ++tries > 90) { _monitorWatch.Stop(); return; }
            bool ready = await Task.Run(() =>
            {
                try { using var sn = new Sensors(); return sn.Read().CpuTemp != null; } catch { return false; }
            });
            if (!ready) return;
            _monitorWatch.Stop();
            ShowToast($"Mesures de {name} détectées : température du processeur et ventilateurs inclus. Vous pouvez réduire sa fenêtre.");
            await FillMeasuresAsync(measures, gpus);
        };
        _monitorWatch.Start();
    }

    // ================================================================== test en cours
    private sealed class BenchRun
    {
        public bool Endurance;
        public CpuLoad Cpu;
        public GpuLoad Gpu;
        public Sensors Sensors;
        public DateTime Start = DateTime.Now, PhaseStart = DateTime.Now;
        public int Phase;   // performances : 0 = 1 cœur, 1 = tous les cœurs, 2 = carte graphique
        public TimeSpan Duration;
        public long LastCpuFlops, LastGpuFlops;
        public double LastComputeSeconds;
        public List<double> GpuUsage = new();
        public double? GpuHotspotMax, GpuPowerMax;
        public double LastFmaSeconds;
        public DateTime LastTick = DateTime.Now;
        public List<double> CpuRates = new(), GpuRates = new(), CpuMHz = new();
        public double? CpuTempMax, GpuTempMax, LimitMin;
        public int LimitedSeconds, Seconds;
        public Dictionary<string, double> FanMax = new();
        public HashSet<string> FanStopped = new();
        public BenchResult Result = new();
        public string Gpuname;
        public double? GpuTempLimit;
        public string GpuThrottle;
        public bool Busy;
    }

    private BenchRun _bench;

    private bool _benchManaged = true;
    private HwinfoSession _hwinfo;

    private void StartBench(bool endurance)
    {
        if (endurance && !_benchCpu && !_benchGpu) { ShowToast("Choisissez au moins le processeur ou la carte graphique."); return; }
        _monitorWatch?.Stop();
        if (_benchManaged && _hwinfo == null && Sensors.MonitorRunning() == null) { _ = PrepareHwinfoAsync(() => BeginBench(endurance)); return; }
        BeginBench(endurance);
    }

    /// <summary>HWiNFO géré : installation éventuelle, ouverture, puis le test démarre dès que ses mesures arrivent.</summary>
    private async Task PrepareHwinfoAsync(Action begin)
    {
        bool go = false;
        var kind = _testKind;   // test en cours (« bench », « checkup »…) : l'attente s'arrête s'il est fermé
        void Go() { if (go) return; go = true; begin(); }
        var session = HwinfoSession.Start(out var error);
        if (session == null) { ShowToast(error); Go(); return; }
        _hwinfo = session;
        DeviceTestHost.Children.Clear();
        var title = TestText("Préparation des capteurs…", 15, subtle: false);
        var detail = TestText("Acceptez l'autorisation demandée par Windows.", 13);
        DeviceTestHost.Children.Add(title);
        DeviceTestHost.Children.Add(detail);
        DeviceTestHost.Children.Add(TestButton("\uE768", "Lancer sans HWiNFO", () => { if (_hwinfo == session) { session.Stop(); _hwinfo = null; } Go(); }));
        _testCleanup = () => { if (_hwinfo == session) { session.Stop(); _hwinfo = null; } };
        var started = DateTime.Now;
        while (_hwinfo == session && _testKind == kind && !go)
        {
            var (state, message) = session.Status();
            if (!string.IsNullOrEmpty(message)) detail.Text = message;
            if (state == "error") { ShowToast(message); session.Stop(); _hwinfo = null; Go(); return; }
            // HWiNFO refermé avant que ses mesures arrivent : le test démarre sans lui (le script remet tout en ordre)
            if (state is "close" or "uninstall" or "done")
            {
                Log.Info("HWiNFO géré : fermé avant le test (" + state + ")");
                ShowToast("HWiNFO s'est fermé avant de fournir ses mesures : le test démarre sans la température du processeur.");
                _hwinfo = null;
                Go();
                return;
            }
            if (state is "run" or "open")
            {
                bool ready = await Task.Run(() => { try { using var sn = new Sensors(); return sn.Read().CpuTemp != null; } catch { return false; } });
                if (ready && _hwinfo == session) { Go(); return; }
            }
            // Installation comprise : au plus 3 minutes, puis le test démarre quand même
            if (DateTime.Now - started > TimeSpan.FromMinutes(3))
            {
                ShowToast("HWiNFO ne répond pas : le test démarre sans la température du processeur.");
                Go();
                return;
            }
            await Task.Delay(1000);
        }
    }

    private void BeginBench(bool endurance)
    {
        if (_bench != null) return;
        var gpu = Gpu.Adapters().FirstOrDefault(g => g.Luid == _benchGpuLuid);
        var run = new BenchRun { Endurance = endurance, Duration = endurance ? TimeSpan.FromMinutes(_benchMinutes) : TimeSpan.FromSeconds(12 + 15 + (gpu != null ? 15 : 0)) };
        run.Result.Date = DateTime.Now;
        run.Result.Method = BenchResult.CurrentMethod;
        run.Result.Kind = endurance ? $"Endurance {_benchMinutes} min" : "Performances";
        run.Result.Cpu = Bench.CpuName();
        run.Result.Gpu = gpu?.Name;
        run.Gpuname = gpu?.Name;
        try { run.Sensors = new Sensors(); run.Sensors.Read(); } catch (Exception ex) { Log.Error("Capteurs", ex); }
        if (endurance)
        {
            if (_benchCpu) run.Cpu = new CpuLoad(Environment.ProcessorCount);
            if (_benchGpu && gpu != null) run.Gpu = new GpuLoad(gpu);
        }
        else run.Cpu = new CpuLoad(1);
        _bench = run;
        SetThreadExecutionState(0x80000000 | 0x1 | 0x2);   // pas de mise en veille pendant le test
        BuildBenchRunning(run);
    }

    private void StopBench()
    {
        var run = _bench;
        _bench = null;
        if (run == null) return;
        run.Cpu?.Dispose();
        run.Gpu?.Dispose();
        run.Sensors?.Dispose();
        SetThreadExecutionState(0x80000000);
        // HWiNFO géré : fermé, réglages remis, désinstallé si Centre PC l'avait installé (en arrière-plan)
        _hwinfo?.Stop();
        _hwinfo = null;
    }

    private void BuildBenchRunning(BenchRun run)
    {
        DeviceTestHost.Children.Clear();
        var phase = TestText("", 14, subtle: false);
        DeviceTestHost.Children.Add(phase);
        DeviceTestHost.Children.Add(Bar(out var progress, "#7C5CFF", 8));
        var tiles = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        DeviceTestHost.Children.Add(tiles);
        TextBlock Tile(string title, string glyph)
        {
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 14, Margin = new Thickness(0, 1, 8, 0) });
            head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(head);
            var v = new TextBlock { Text = "…", Style = (Style)FindResource("Subtle"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(v);
            tiles.Children.Add(new Border { Width = 260, MinHeight = 96, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12),
                                            Background = (Brush)FindResource("Surface2"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Child = sp });
            return v;
        }
        var cpuTile = Tile("Processeur", "");
        var gpuTile = run.Gpuname != null ? Tile("Carte graphique", "") : null;
        var fanTile = Tile("Ventilateurs", "");
        var stableTile = Tile("Stabilité", "");

        var temps = new LiveChart("Températures (°C)") { Height = 150, Margin = new Thickness(0, 4, 0, 10) };
        var perf = new LiveChart("Performance (% du début du test)") { Height = 120, Margin = new Thickness(0, 0, 0, 10) };
        DeviceTestHost.Children.Add(temps);
        DeviceTestHost.Children.Add(perf);
        DeviceTestHost.Children.Add(TestButton("", "Arrêter le test", () => FinishBench(run, "Arrêté par vous")));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        double cpuRef = 0, gpuRef = 0;
        timer.Tick += async (_, _) =>
        {
            if (_bench != run) { timer.Stop(); return; }
            if (run.Busy) return;
            run.Busy = true;
            try
            {
                var now = DateTime.Now;
                double dt = Math.Max(0.2, (now - run.LastTick).TotalSeconds);
                run.LastTick = now;
                run.Seconds++;
                var s = run.Sensors != null ? await Task.Run(() => { try { return run.Sensors.Read(); } catch { return null; } }) : null;
                if (_bench != run) return;

                // Débits instantanés
                double cpuRate = 0, gpuRate = 0;
                if (run.Cpu != null)
                {
                    // Débit mesuré sur le temps réellement passé à calculer (le test alterne calcul, hachage et compression)
                    var f = run.Cpu.Flops; var fs = run.Cpu.FmaSeconds;
                    cpuRate = fs > run.LastFmaSeconds ? (f - run.LastCpuFlops) / (fs - run.LastFmaSeconds) * run.Cpu.Threads / 1e9 : 0;
                    run.LastCpuFlops = f; run.LastFmaSeconds = fs;
                }
                if (run.Gpu != null)
                {
                    // Débit mesuré sur le temps de calcul (le test alterne calcul et passes de mémoire vidéo)
                    var f = run.Gpu.Flops; var cs = run.Gpu.ComputeSeconds;
                    gpuRate = cs > run.LastComputeSeconds ? (f - run.LastGpuFlops) / (cs - run.LastComputeSeconds) / 1e12 : 0;
                    run.LastGpuFlops = f; run.LastComputeSeconds = cs;
                }
                double phaseSec = (now - run.PhaseStart).TotalSeconds;
                if (run.Cpu != null && phaseSec > 3) run.CpuRates.Add(cpuRate);
                if (run.Gpu != null && phaseSec > 3 && gpuRate > 0) run.GpuRates.Add(gpuRate);

                // Mesures
                var g = s?.Gpus.FirstOrDefault(x => x.Name == run.Gpuname) ?? s?.Gpus.FirstOrDefault();
                if (s?.CpuTemp is double ct) run.CpuTempMax = Math.Max(run.CpuTempMax ?? 0, ct);
                if (g?.Temp is double gt && run.Gpu != null) run.GpuTempMax = Math.Max(run.GpuTempMax ?? 0, gt);
                if (run.Gpu != null && phaseSec > 3)
                {
                    if (g?.Usage is double gUse) run.GpuUsage.Add(gUse);
                    if (g?.Hotspot is double gHot) run.GpuHotspotMax = Math.Max(run.GpuHotspotMax ?? 0, gHot);
                    if (g?.PowerW is double gPow) run.GpuPowerMax = Math.Max(run.GpuPowerMax ?? 0, gPow);
                }
                if (g?.TempMax != null) run.GpuTempLimit = g.TempMax;
                if (run.Gpu != null && g?.Throttle != null) run.GpuThrottle = g.Throttle;
                if (s?.CpuMHz is double mhz && run.Cpu != null && run.Cpu.Threads > 1) run.CpuMHz.Add(mhz);
                if (s?.CpuLimit is double lim && run.Cpu != null && run.Cpu.Threads > 1)
                {
                    run.LimitMin = Math.Min(run.LimitMin ?? 100, lim);
                    if (lim < 99) run.LimitedSeconds++;
                }
                if (s != null)
                    foreach (var f in s.Fans)
                    {
                        if (f.Rpm > run.FanMax.GetValueOrDefault(f.Name)) run.FanMax[f.Name] = f.Rpm;
                        if (f.Rpm < 1 && run.FanMax.GetValueOrDefault(f.Name) > 200) run.FanStopped.Add(f.Name);
                    }

                // Affichage
                cpuTile.Text = string.Join("\n", new[]
                {
                    s?.CpuUsage is double u ? $"Charge {u:0} %" : null,
                    s?.CpuMHz is double m ? $"Fréquence {m:0} MHz" + (s.CpuBaseMHz is double b ? $" (base {b:0})" : "") : null,
                    s?.CpuTemp is double t ? $"Température {t:0} °C" : "Température : non disponible",
                    s?.CpuLimit is double l ? (l < 99 ? $"Bridé à {l:0} % de ses capacités" : "Aucun bridage") : null,
                    s?.CpuPowerW is double w ? $"Consommation {w:0} W" : null,
                    run.Cpu != null ? $"{cpuRate:0} GFLOPS" : null
                }.Where(x => x != null));
                if (gpuTile != null)
                    gpuTile.Text = string.Join("\n", new[]
                    {
                        g?.Usage is double gu ? $"Charge {gu:0} %" : null,
                        g?.Temp is double gtNow ? $"Température {gtNow:0} °C" + (g.Hotspot is double hs ? $" · point chaud {hs:0} °C" : "") + (g.TempMax is double mx ? $" (limite {mx:0})" : "") : "Température : non fournie par le pilote",
                        g?.FanRpm is double r ? $"Ventilateur {r:0} tr/min" : g?.FanPercent is double fp ? $"Ventilateur {fp:0} %" : null,
                        g?.PowerW is double pw ? $"Consommation {pw:0} W" : g?.PowerPercent is double pp ? $"Puissance {pp:0} %" : null,
                        g?.ClockMHz is double c ? $"Fréquence {c:0} MHz" : null,
                        g?.Throttle != null && run.Gpu != null ? $"Bridée : {g.Throttle}" : null,
                        run.Gpu != null ? $"{gpuRate:0.00} TFLOPS" + (run.Gpu.MemoryRate > 0 ? $" · mémoire {run.Gpu.MemoryRate / 1e9:0} Go/s" : "") : "En attente"
                    }.Where(x => x != null));
                fanTile.Text = s == null || s.Fans.Count == 0
                    ? (s?.CpuTemp == null ? "Non disponibles (programme de surveillance non ouvert)" : "Aucun ventilateur signalé")
                    : string.Join("\n", s.Fans.OrderByDescending(f => f.Rpm).Take(6).Select(f => $"{f.Name} : {f.Rpm:0} tr/min"));
                long errors = (run.Cpu?.Errors ?? 0) + (run.Gpu?.Errors ?? 0);
                stableTile.Text = errors == 0 ? "Aucune erreur de calcul" : $"{errors} erreur(s) de calcul !";
                stableTile.Foreground = errors == 0 ? BrushOfStatic("#10B981") : BrushOfStatic("#EF4444");

                // Courbes
                temps.Add("Processeur", "#F59E0B", s?.CpuTemp);
                if (run.Gpuname != null) temps.Add("Carte graphique", "#22C55E", g?.Temp);
                if (s?.SystemTemp != null) temps.Add("Système", "#64748B", s.SystemTemp);
                if (cpuRef == 0 && run.CpuRates.Count >= 3 && (run.Endurance || run.Phase == 1)) cpuRef = run.CpuRates.Take(5).Average();
                if (gpuRef == 0 && run.GpuRates.Count >= 3) gpuRef = run.GpuRates.Take(5).Average();
                if (run.Cpu != null && cpuRef > 0) perf.Add("Processeur", "#F59E0B", cpuRate / cpuRef * 100);
                if (run.Gpu != null && gpuRef > 0) perf.Add("Carte graphique", "#22C55E", gpuRate / gpuRef * 100);
                temps.Redraw(); perf.Redraw();

                // Avancement et phases
                var elapsed = now - run.Start;
                progress.Width = Math.Min(1, elapsed.TotalSeconds / run.Duration.TotalSeconds) * Math.Max(0, ((FrameworkElement)progress.Parent).ActualWidth);
                var left = run.Duration - elapsed;
                if (run.Endurance)
                    phase.Text = $"Test d'endurance en cours · {elapsed:mm\\:ss} écoulées · encore {Math.Max(0, left.TotalMinutes):0} min {Math.Max(0, left.Seconds)} s";
                else
                {
                    phase.Text = run.Phase switch { 0 => "Processeur, 1 cœur…", 1 => $"Processeur, tous les cœurs ({Environment.ProcessorCount})…", _ => "Carte graphique…" };
                    if (run.Phase == 0 && phaseSec >= 12)
                    {
                        run.Result.CpuSingleGflops = Steady(run.CpuRates);
                        run.Cpu.Dispose();
                        run.Cpu = new CpuLoad(Environment.ProcessorCount);
                        run.LastCpuFlops = 0; run.LastFmaSeconds = 0; run.CpuRates.Clear(); run.Phase = 1; run.PhaseStart = now;
                    }
                    else if (run.Phase == 1 && phaseSec >= 15)
                    {
                        run.Result.CpuAllGflops = Steady(run.CpuRates);
                        run.Result.CpuHashGBs = run.Cpu.HashRate / 1e9;
                        run.Result.CpuCompMBs = run.Cpu.CompressRate / 1e6;
                        run.Result.Errors += run.Cpu.Errors;
                        run.Cpu.Dispose(); run.Cpu = null;
                        var gpu = Gpu.Adapters().FirstOrDefault(x => x.Luid == _benchGpuLuid);
                        if (gpu == null) { FinishBench(run, null); return; }
                        run.Gpu = new GpuLoad(gpu); run.Phase = 2; run.PhaseStart = now;
                    }
                    else if (run.Phase == 2 && phaseSec >= 15) { FinishBench(run, null); return; }
                }

                // Sécurité : arrêt immédiat en cas de danger ou d'erreur
                if (run.Gpu?.Failure != null) { FinishBench(run, "Carte graphique : " + run.Gpu.Failure); return; }
                if (errors > 0 && run.Endurance) { FinishBench(run, "Erreur de calcul détectée : le PC n'est pas stable"); return; }
                if (s?.CpuTemp >= 98) { FinishBench(run, $"Arrêt de sécurité : processeur à {s.CpuTemp:0} °C"); return; }
                if (g?.Temp is double gtemp && run.Gpu != null && gtemp >= (g.TempMax is double gmax ? gmax - 2 : 95)) { FinishBench(run, $"Arrêt de sécurité : carte graphique à {gtemp:0} °C"); return; }
                if (run.Endurance && elapsed >= run.Duration) { FinishBench(run, null); return; }
            }
            catch (Exception ex) { Log.Error("Test processeur / carte graphique", ex); }
            finally { run.Busy = false; }
        };
        timer.Start();
        _testCleanup = () => { timer.Stop(); _monitorWatch?.Stop(); StopBench(); };
    }

    /// <summary>Débit stable : moyenne après la montée en charge (premier quart ignoré).</summary>
    private static double Steady(List<double> rates) => rates.Count == 0 ? 0 : rates.Skip(rates.Count / 4).DefaultIfEmpty(0).Average();

    // ================================================================== verdict
    private void FinishBench(BenchRun run, string stopReason)
    {
        if (_bench != run) return;
        var r = run.Result;
        if (run.Endurance)
        {
            r.CpuAllGflops = Steady(run.CpuRates);
            if (run.Cpu != null) { r.CpuHashGBs = run.Cpu.HashRate / 1e9; r.CpuCompMBs = run.Cpu.CompressRate / 1e6; }
        }
        if (run.Gpu != null) { r.GpuTflops = Steady(run.GpuRates); r.GpuMemGBs = run.Gpu.MemoryRate / 1e9; }
        if (run.GpuUsage.Count > 0) r.GpuUsageAvg = run.GpuUsage.Average();
        r.Errors += (run.Cpu?.Errors ?? 0) + (run.Gpu?.Errors ?? 0);
        string firstError = run.Cpu?.FirstError;
        r.CpuTempMax = run.CpuTempMax;
        r.GpuTempMax = run.GpuTempMax;
        r.CpuLimitMin = run.LimitMin;
        StopBench();

        // Points du verdict : (niveau, texte)
        var points = new List<(DeviceStatus level, string text)>();
        bool partial = stopReason == "Arrêté par vous";
        if (stopReason != null && !partial) points.Add((DeviceStatus.Problem, stopReason + "."));
        if (partial) points.Add((DeviceStatus.Info, "Test arrêté avant la fin : les résultats ne portent que sur la partie effectuée."));
        points.Add(r.Errors == 0
            ? (DeviceStatus.Ok, "Stable : aucune erreur de calcul.")
            : (DeviceStatus.Problem, $"{r.Errors} erreur(s) de calcul{(firstError != null ? $" ({firstError})" : "")} : le PC n'est pas stable. Causes fréquentes : overclocking ou XMP / EXPO trop poussé, "
                                   + "tension insuffisante, RAM défectueuse (testez-la depuis la carte Mémoire vive), alimentation faible."));

        // Processeur : bridage et baisse de performance
        if (run.CpuMHz.Count >= 10 || run.LimitMin != null)
        {
            double start = run.CpuMHz.Take(Math.Max(1, run.CpuMHz.Count / 6)).DefaultIfEmpty(0).Average();
            double end = run.CpuMHz.Skip(run.CpuMHz.Count * 5 / 6).DefaultIfEmpty(0).Average();
            double drop = start > 0 ? (1 - end / start) * 100 : 0;
            double limitedPct = run.Seconds > 0 ? run.LimitedSeconds * 100.0 / run.Seconds : 0;
            if (run.CpuTempMax is double ct && ct >= 95)
                points.Add((DeviceStatus.Problem, $"Processeur : {ct:0} °C, à la limite. Il se bride pour se protéger : refroidissement insuffisant (ventirad, pâte thermique, flux d'air du boîtier)."));
            else if (limitedPct > 20 || drop > 10)
                points.Add((DeviceStatus.Warning, $"Processeur bridé {limitedPct:0} % du temps" + (drop > 5 ? $", fréquence en baisse de {drop:0} % au fil du test" : "")
                    + (run.CpuTempMax is double t2 ? (t2 >= 88 ? $" : la chaleur ({t2:0} °C) en est probablement la cause." : $" alors que la température reste correcte ({t2:0} °C) : limite de puissance (réglages du BIOS, mode d'alimentation, portable sur batterie).")
                                                   : " : chaleur ou limite de puissance (température du processeur non mesurée).")));
            else
                points.Add((DeviceStatus.Ok, "Processeur : aucun bridage notable" + (start > 0 ? $", {end:0} MHz en fin de test" : "") + "."));
        }
        if (run.CpuTempMax is double cmax && cmax < 95)
            points.Add((cmax >= 90 ? DeviceStatus.Warning : DeviceStatus.Ok, $"Température maximale du processeur : {cmax:0} °C" + (cmax >= 90 ? " : élevée, peu de marge." : cmax >= 80 ? " : normale en charge maximale." : " : excellente.")));
        else if (run.CpuTempMax == null && (r.CpuAllGflops > 0))
            points.Add((DeviceStatus.Info, (Sensors.MonitorRunning() is string mon
                ? $"Température du processeur non trouvée dans les mesures de {mon}"
                : "Température du processeur non mesurée (aucun programme de surveillance ouvert)") + " : le bridage a servi d'indicateur."));

        // Carte graphique
        if (r.GpuTflops > 0)
        {
            double start = run.GpuRates.Take(Math.Max(1, run.GpuRates.Count / 6)).DefaultIfEmpty(0).Average();
            double end = run.GpuRates.Skip(run.GpuRates.Count * 5 / 6).DefaultIfEmpty(0).Average();
            double drop = start > 0 ? (1 - end / start) * 100 : 0;
            if (run.GpuTempMax is double gt)
            {
                double limit = run.GpuTempLimit ?? 95;
                points.Add((gt >= limit - 5 ? DeviceStatus.Warning : DeviceStatus.Ok,
                    $"Température maximale de la carte graphique : {gt:0} °C (limite {limit:0} °C)"
                    + (run.GpuHotspotMax is double hot ? $", point chaud {hot:0} °C" + (hot >= 105 ? " (élevé : pâte thermique ou refroidissement à vérifier)" : "") : "")
                    + (gt >= limit - 5 ? " : très peu de marge, vérifiez son refroidissement." : ".")));
            }
            if (run.GpuUsage.Count >= 5 && run.GpuUsage.Average() < 80)
                points.Add((DeviceStatus.Info, $"La carte graphique n'a été chargée qu'à {run.GpuUsage.Average():0} % en moyenne : une autre application l'utilisait peut-être."));
            if (!run.Endurance)
                points.Add((DeviceStatus.Info, "Mesure courte (15 secondes) : la carte n'a pas le temps de chauffer. Pour vérifier son refroidissement, lancez le test d'endurance."));
            if (run.Endurance && drop > 10)
                points.Add((DeviceStatus.Warning, $"La carte graphique a perdu {drop:0} % de performance au fil du test" + (run.GpuThrottle != null ? $" (bridée : {run.GpuThrottle})." : " (chaleur ou limite de puissance).")));
            else if (run.Endurance)
                points.Add((DeviceStatus.Ok, "Carte graphique : performance constante du début à la fin."));
        }

        // Ventilateurs
        if (run.FanStopped.Count > 0)
            points.Add((DeviceStatus.Problem, $"Ventilateur arrêté pendant le test : {string.Join(", ", run.FanStopped)}. Vérifiez son branchement et sa courbe dans le BIOS."));
        else if (run.FanMax.Count > 0)
        {
            var silent = run.FanMax.Where(f => f.Value < 1).Select(f => f.Key).ToList();
            var spinning = run.FanMax.Where(f => f.Value >= 1).OrderByDescending(f => f.Value).Select(f => $"{f.Key} {f.Value:0} tr/min").Take(5);
            points.Add((DeviceStatus.Ok, "Ventilateurs : " + string.Join(", ", spinning) + "."));
            if (silent.Count > 0)
                points.Add((DeviceStatus.Info, $"Restés à 0 tr/min : {string.Join(", ", silent)} — normal si rien n'est branché sur ces prises, sinon vérifiez le branchement."));
        }

        var worst = points.Max(p => p.level);
        r.Verdict = worst switch { DeviceStatus.Problem => "problème détecté", DeviceStatus.Warning => "à surveiller", _ => partial ? "partiel" : "validé" };
        var history = Settings.Current.BenchHistory;
        // Comparaison avec le dernier test du même type et de la même méthode de mesure (même carte graphique)
        var previous = history.LastOrDefault(h => h.Kind == r.Kind && h.Method == r.Method && h.Gpu == r.Gpu);
        history.Add(r);
        while (history.Count > 30) history.RemoveAt(0);
        Settings.Save();

        // Affichage du verdict
        DeviceTestHost.Children.Clear();
        var (title, color) = worst switch
        {
            DeviceStatus.Problem => ("Problème détecté", "#EF4444"),
            DeviceStatus.Warning => ("Fonctionne, avec des points à surveiller", "#F59E0B"),
            _ => (partial ? "Test partiel" : run.Endurance ? "Installation validée" : "Mesure terminée", "#10B981")
        };
        DeviceTestHost.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = BrushOfStatic(color), Margin = new Thickness(0, 0, 0, 12) });
        foreach (var (level, text) in points)
        {
            var (_, c, glyph) = StatusLook(level);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), Foreground = BrushOfStatic(c), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            DeviceTestHost.Children.Add(row);
        }
        DeviceTestHost.Children.Add(Section("Performances"));
        string Cmp(double now, double? before, string unit, string format) =>
            $"{now.ToString(format, CultureInfo.CurrentCulture)} {unit}" + (before is double b && b > 0 ? $" ({(now / b - 1) * 100:+0;-0;±0} % par rapport à la dernière fois)" : "");
        if (r.CpuSingleGflops > 0) DeviceTestHost.Children.Add(TestText("Processeur, 1 cœur : " + Cmp(r.CpuSingleGflops, previous?.CpuSingleGflops, "GFLOPS", "0"), 13, subtle: false));
        if (r.CpuAllGflops > 0) DeviceTestHost.Children.Add(TestText($"Processeur, {Environment.ProcessorCount} cœurs logiques : " + Cmp(r.CpuAllGflops, previous?.CpuAllGflops, "GFLOPS", "0")
            + $" · hachage {r.CpuHashGBs:0.0} Go/s · compression {r.CpuCompMBs:0} Mo/s", 13, subtle: false));
        if (r.GpuTflops > 0) DeviceTestHost.Children.Add(TestText($"{r.Gpu} : " + Cmp(r.GpuTflops, previous?.GpuTflops, "TFLOPS", "0.00")
            + (r.GpuMemGBs > 0 ? $" · mémoire vidéo {r.GpuMemGBs:0} Go/s" : "") + (r.GpuUsageAvg is double ua ? $" · chargée à {ua:0} % en moyenne" : "")
            + (run.GpuPowerMax is double pm ? $" · jusqu'à {pm:0} W" : ""), 13, subtle: false));
        DeviceTestHost.Children.Add(TestText("GFLOPS / TFLOPS : milliards / billions de calculs par seconde, mesurés par Centre PC. Utiles pour comparer vos propres mesures "
            + "(avant / après un réglage, un nettoyage, un nouveau pilote) ; d'autres logiciels de test utilisent d'autres calculs.", 11.5));
        var again = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        again.Children.Add(TestButton("", "Refaire un test", () => OpenDeviceTest("bench"), accent: true));
        DeviceTestHost.Children.Add(again);
        _testCleanup = null;
    }

    // ================================================================== petits éléments
    private TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Opacity = 0.7, Margin = new Thickness(0, 14, 0, 8)
    };

    /// <summary>Choix exclusif sous forme de pastilles.</summary>
    private WrapPanel Choices(List<(string label, object value)> items, object current, Action<object> pick)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new List<(Button b, object v)>();
        void Paint() { foreach (var (b, v) in buttons) b.BorderBrush = Equals(v, current) ? (Brush)FindResource("Accent") : (Brush)FindResource("Line"); }
        foreach (var (label, value) in items)
        {
            var b = SmallButton(null, label, null);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.BorderThickness = new Thickness(1.5);
            var v = value;
            b.Click += (_, _) => { current = v; pick(v); Paint(); };
            buttons.Add((b, value));
            panel.Children.Add(b);
        }
        Paint();
        return panel;
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 18, 0) };
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        return c;
    }

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}

/// <summary>Courbe en direct (plusieurs séries, 10 dernières minutes), dessinée sans bibliothèque.</summary>
public sealed class LiveChart : FrameworkElement
{
    private readonly string _title;
    private readonly Dictionary<string, (Brush brush, List<double?> values)> _series = new();
    private const int Max = 600;

    public LiveChart(string title) { _title = title; }

    public void Add(string name, string color, double? value)
    {
        if (!_series.TryGetValue(name, out var s))
            _series[name] = s = ((Brush)new BrushConverter().ConvertFromString(color), new List<double?>());
        s.values.Add(value);
        if (s.values.Count > Max) s.values.RemoveAt(0);
    }

    public void Redraw() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 50 || h < 40) return;
        var text = (Brush)Application.Current.FindResource("Text");
        var line = (Brush)Application.Current.FindResource("Line");
        dc.DrawRoundedRectangle((Brush)Application.Current.FindResource("Surface2"), new Pen(line, 1), new Rect(0, 0, w, h), 10, 10);
        var tf = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawText(new FormattedText(_title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11.5, text, dpi) { }, new Point(12, 8));
        var all = _series.Values.SelectMany(s => s.values).Where(v => v != null).Select(v => v.Value).ToList();
        if (all.Count == 0)
        {
            dc.DrawText(new FormattedText("Pas de mesure", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11, text, dpi), new Point(12, h / 2));
            return;
        }
        double lo = Math.Floor(Math.Min(all.Min(), all.Max() - 10) / 10) * 10, hi = Math.Ceiling((all.Max() + 2) / 10) * 10;
        if (hi - lo < 10) hi = lo + 10;
        var area = new Rect(40, 28, w - 52, h - 40);
        for (int i = 0; i <= 2; i++)
        {
            double v = lo + (hi - lo) * i / 2, y = area.Bottom - area.Height * i / 2;
            dc.DrawLine(new Pen(line, 1), new Point(area.Left, y), new Point(area.Right, y));
            dc.DrawText(new FormattedText(v.ToString("0"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 10, text, dpi), new Point(8, y - 7));
        }
        int n = Math.Max(60, _series.Values.Max(s => s.values.Count));
        double lx = area.Right;
        foreach (var (name, (brush, values)) in _series.Reverse())
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i] is not double v) { open = false; continue; }
                    var p = new Point(area.Left + area.Width * i / (n - 1), area.Bottom - (v - lo) / (hi - lo) * area.Height);
                    if (!open) { g.BeginFigure(p, false, false); open = true; } else g.LineTo(p, true, true);
                }
            }
            dc.DrawGeometry(null, new Pen(brush, 2), geo);
            var last = values.LastOrDefault(v => v != null);
            var label = new FormattedText(name + (last is double lv ? $" {lv:0}" : ""), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11, brush, dpi);
            lx -= label.Width + 14;
            dc.DrawText(label, new Point(lx + 10, 8));
        }
    }
}
