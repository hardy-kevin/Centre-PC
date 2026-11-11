using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Test de stabilité mémoire et processeur (même usage qu'OCCT) : mémoire vive vérifiée en boucle et/ou calcul processeur vérifié,
/// pendant 15 min à 2 h, avec températures (processeur, VRM, chipset), consommation et limites de puissance. Une seule erreur = instable.
/// </summary>
public partial class MainWindow
{
    private int _stabMinutes = 60;
    private string _stabMode = "both";
    private Action _stabStop;

    private void StartStabilityTest()
    {
        _monitorWatch?.Stop();
        int minutes = _stabMinutes; string mode = _stabMode;
        if (_benchManaged && _hwinfo == null && Sensors.MonitorRunning() == null) { _ = PrepareHwinfoAsync(() => BeginStabilityTest(minutes, mode)); return; }
        BeginStabilityTest(minutes, mode);
    }

    private void BeginStabilityTest(int minutes, string mode)
    {
        DeviceTestHost.Children.Clear();
        var phase = TestText("Réservation de la mémoire…", 14, subtle: false);
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
        var memTile = mode != "cpu" ? Tile("Mémoire vive", "") : null;
        var cpuTile = Tile("Processeur", "");
        var boardTile = Tile("Carte mère et disques", "");
        var stableTile = Tile("Stabilité", "");
        var temps = new LiveChart("Températures (°C)") { Height = 150, Margin = new Thickness(0, 4, 0, 10) };
        DeviceTestHost.Children.Add(temps);
        Button stopButton = null;
        stopButton = TestButton("", "Arrêter le test", () => _stabStop?.Invoke());
        DeviceTestHost.Children.Add(stopButton);

        // Mémoire d'abord (réservation), puis processeur sur les fils restants
        int n = Environment.ProcessorCount;
        MemTest mem = null; CpuLoad cpu = null; Sensors sensors = null;
        try
        {
            if (mode != "cpu") mem = new MemTest(mode == "mem" ? n : Math.Max(2, n / 2));
            if (mode != "mem") cpu = new CpuLoad(mode == "cpu" ? n : Math.Max(1, n - n / 2));
            sensors = new Sensors(); sensors.Read();
        }
        catch (Exception ex) { Log.Error("Test de stabilité", ex); }
        SetThreadExecutionState(0x80000000 | 0x1 | 0x2);
        var start = DateTime.Now;
        double? cpuMax = null, vrmMax = null, chipMax = null, powMax = null, pl1 = null, pl2 = null;
        var driveMax = new Dictionary<string, double>();
        bool busy = false, finished = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        void Finish(string reason)
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            long memErr = mem?.Errors ?? 0, cpuErr = cpu?.Errors ?? 0;
            string memFirst = mem?.FirstError, cpuFirst = cpu?.FirstError;
            long verified = mem?.Verified ?? 0, tested = mem?.BytesTested ?? 0, passes = mem?.Passes ?? 0;
            mem?.Dispose(); cpu?.Dispose(); sensors?.Dispose();
            SetThreadExecutionState(0x80000000);
            _hwinfo?.Stop(); _hwinfo = null;
            _stabStop = null; _testCleanup = null;
            var elapsed = DateTime.Now - start;
            bool complete = reason == null;
            var points = new List<(DeviceStatus level, string text)>();
            if (reason != null && reason != "Arrêté par vous") points.Add((DeviceStatus.Problem, reason + "."));
            if (reason == "Arrêté par vous") points.Add((DeviceStatus.Info, $"Test arrêté après {elapsed.TotalMinutes:0} min sur {minutes} : la validation porte sur cette durée."));
            if (mode != "cpu")
                points.Add(memErr == 0
                    ? (DeviceStatus.Ok, $"Mémoire : aucune erreur — {tested / 1073741824.0:0.#} Go testés, {verified / 1e12:0.##} To écrits et vérifiés, {passes} passage(s) complet(s).")
                    : (DeviceStatus.Problem, $"Mémoire : {memErr} erreur(s) ({memFirst}). La mémoire n'est pas stable : désactivez le XMP / EXPO ou baissez sa fréquence ; si l'erreur persiste aux réglages d'usine, testez les barrettes une par une."));
            if (mode != "mem")
                points.Add(cpuErr == 0 ? (DeviceStatus.Ok, "Processeur : aucune erreur de calcul.")
                                       : (DeviceStatus.Problem, $"Processeur : {cpuErr} erreur(s) de calcul ({cpuFirst}) : réglages du BIOS (tensions, overclocking) à revoir."));
            if (cpuMax is double c) points.Add((c >= 95 ? DeviceStatus.Warning : DeviceStatus.Ok, $"Processeur : {c:0} °C au maximum" + (powMax is double p ? $", {p:0} W" + (pl1 is double l1 ? $" (limites PL1 {l1:0} W / PL2 {pl2:0} W)" : "") : "") + "."));
            if (vrmMax is double v) points.Add((v >= 110 ? DeviceStatus.Problem : v >= 95 ? DeviceStatus.Warning : DeviceStatus.Ok, $"Étage d'alimentation (VRM) de la carte mère : {v:0} °C au maximum" + (v >= 95 ? " : élevé, améliorer le flux d'air autour du processeur." : ".")));
            if (chipMax is double ch) points.Add((ch >= 85 ? DeviceStatus.Warning : DeviceStatus.Ok, $"Chipset : {ch:0} °C au maximum."));
            foreach (var (d, t) in driveMax) points.Add((t >= 80 ? DeviceStatus.Problem : t >= 70 ? DeviceStatus.Warning : DeviceStatus.Ok, $"Disque {d} : {t:0} °C au maximum."));
            if (memErr == 0 && cpuErr == 0 && complete && minutes >= 60) points.Insert(0, (DeviceStatus.Ok, $"Installation stable : {minutes} minutes sans aucune erreur."));

            var worst = points.Max(p => p.level);
            var history = Settings.Current.BenchHistory;
            history.Add(new BenchResult { Date = DateTime.Now, Kind = $"Stabilité {minutes} min ({(mode == "both" ? "mémoire + processeur" : mode == "mem" ? "mémoire" : "processeur")})", Method = BenchResult.CurrentMethod,
                                          Cpu = Bench.CpuName(), Errors = memErr + cpuErr, CpuTempMax = cpuMax,
                                          Verdict = worst == DeviceStatus.Problem ? "instable" : complete ? "stable" : "partiel" });
            while (history.Count > 30) history.RemoveAt(0);
            Settings.Save();

            DeviceTestHost.Children.Clear();
            var (title, color) = worst switch
            {
                DeviceStatus.Problem => ("Installation instable", "#EF4444"),
                DeviceStatus.Warning => ("Stable, avec des points à surveiller", "#F59E0B"),
                _ => (complete ? "Installation stable" : "Aucune erreur (test partiel)", "#10B981")
            };
            DeviceTestHost.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = BrushOfStatic(color), Margin = new Thickness(0, 0, 0, 12) });
            foreach (var (level, text) in points)
            {
                var (_, cl, glyph) = StatusLook(level);
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), Foreground = BrushOfStatic(cl), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
                DeviceTestHost.Children.Add(row);
            }
            DeviceTestHost.Children.Add(TestButton("", "Refaire un test", () => OpenDeviceTest("bench"), accent: true));
        }

        _stabStop = () => Finish("Arrêté par vous");
        _testCleanup = () => { if (!finished) { finished = true; timer.Stop(); mem?.Dispose(); cpu?.Dispose(); sensors?.Dispose(); SetThreadExecutionState(0x80000000); _hwinfo?.Stop(); _hwinfo = null; } };

        long lastVerified = 0;
        timer.Tick += async (_, _) =>
        {
            if (busy || finished) return;
            busy = true;
            try
            {
                var s = sensors != null ? await Task.Run(() => { try { return sensors.Read(); } catch { return null; } }) : null;
                if (finished) return;
                var el = DateTime.Now - start;
                progress.Width = Math.Min(1, el.TotalMinutes / minutes) * Math.Max(0, ((FrameworkElement)progress.Parent).ActualWidth);
                var left = TimeSpan.FromMinutes(minutes) - el;
                phase.Text = $"Test de stabilité · {(int)el.TotalMinutes} min {el.Seconds:00} s écoulées · encore {(int)Math.Max(0, left.TotalMinutes)} min";
                if (s?.CpuTemp is double t) cpuMax = Math.Max(cpuMax ?? 0, t);
                if (s?.VrmTemp is double v) vrmMax = Math.Max(vrmMax ?? 0, v);
                if (s?.ChipsetTemp is double ch) chipMax = Math.Max(chipMax ?? 0, ch);
                if (s?.CpuPowerW is double p) powMax = Math.Max(powMax ?? 0, p);
                pl1 ??= s?.CpuPl1; pl2 ??= s?.CpuPl2;
                if (s != null) foreach (var (d, dt) in s.DriveTemps) driveMax[d] = Math.Max(driveMax.GetValueOrDefault(d), dt);
                if (mem != null && memTile != null)
                {
                    var ver = mem.Verified;
                    memTile.Text = $"{mem.BytesTested / 1073741824.0:0.#} Go en test ({mem.Threads} fils)\n{(ver - lastVerified) / 1e9:0.0} Go/s vérifiés · {ver / 1e12:0.##} To au total\n{mem.Passes} passage(s) complet(s)";
                    lastVerified = ver;
                }
                cpuTile.Text = string.Join("\n", new[]
                {
                    s?.CpuUsage is double u ? $"Charge {u:0} %" : null,
                    s?.CpuMHz is double m ? $"Fréquence {m:0} MHz" : null,
                    s?.CpuTemp is double t2 ? $"Température {t2:0} °C" : "Température : non disponible",
                    s?.CpuPowerW is double p2 ? $"Consommation {p2:0} W" + (s.CpuPl1 is double l ? $" (PL1 {l:0} W)" : "") : null,
                    s?.CpuLimit is double lim ? (lim < 99 ? $"Bridé à {lim:0} %" : "Aucun bridage") : null
                }.Where(x => x != null));
                boardTile.Text = string.Join("\n", new[]
                {
                    s?.VrmTemp is double v2 ? $"VRM {v2:0} °C" : null,
                    s?.ChipsetTemp is double c2 ? $"Chipset {c2:0} °C" : null
                }.Concat(s?.DriveTemps.Select(d => $"{d.Key} {d.Value:0} °C") ?? Enumerable.Empty<string>()).Where(x => x != null).DefaultIfEmpty("Non disponibles (HWiNFO non utilisé)"));
                long errs = (mem?.Errors ?? 0) + (cpu?.Errors ?? 0);
                stableTile.Text = errs == 0 ? "Aucune erreur" : $"{errs} erreur(s) !";
                stableTile.Foreground = BrushOfStatic(errs == 0 ? "#10B981" : "#EF4444");
                temps.Add("Processeur", "#F59E0B", s?.CpuTemp);
                if (s?.VrmTemp != null) temps.Add("VRM", "#EC4899", s.VrmTemp);
                temps.Redraw();

                if (errs > 0) { Finish("Erreur détectée : arrêt immédiat"); return; }
                if (s?.CpuTemp >= 98) { Finish($"Arrêt de sécurité : processeur à {s.CpuTemp:0} °C"); return; }
                if (s?.VrmTemp >= 120) { Finish($"Arrêt de sécurité : VRM à {s.VrmTemp:0} °C"); return; }
                if (el.TotalMinutes >= minutes) Finish(null);
            }
            catch (Exception ex) { Log.Error("Test de stabilité", ex); }
            finally { busy = false; }
        };
        phase.Text = mem != null ? $"Mémoire réservée : {mem.BytesTested / 1073741824.0:0.#} Go" : "Démarrage…";
        timer.Start();
    }
}
