using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Test cœur par cœur : chaque cœur physique est chargé seul (un fil de calcul fixé dessus), l'un après l'autre, à sa fréquence
/// maximale. Les calculs sont vérifiés ; chaque cœur est comparé aux autres cœurs du même type (performance / efficacité).
/// Repère un cœur instable ou faible, invisible quand tous les cœurs travaillent ensemble.
/// </summary>
public partial class MainWindow
{
    private int _coreSeconds = 10;

    private sealed class CoreResult
    {
        public CpuCore Core;
        public double Gflops, MHz;
        public double? TempMax;
        public long Errors;
        public bool Done;
    }

    private void StartCoreTest()
    {
        _monitorWatch?.Stop();
        if (_benchManaged && _hwinfo == null && Sensors.MonitorRunning() == null) { _ = PrepareHwinfoAsync(() => BeginCoreTest(_coreSeconds)); return; }
        BeginCoreTest(_coreSeconds);
    }

    private void BeginCoreTest(int seconds)
    {
        var cores = CpuTopology.Cores();
        if (cores.Count == 0) { ShowToast("Windows ne donne pas la liste des cœurs de ce processeur."); return; }
        bool hybrid = cores.Select(c => c.Efficiency).Distinct().Count() > 1;
        int perf = cores.Max(c => c.Efficiency);
        string Kind(CpuCore c) => !hybrid ? "" : c.Efficiency == perf ? "performance" : "efficacité";
        string Short(CpuCore c) => !hybrid ? "" : c.Efficiency == perf ? " · P" : " · E";

        DeviceTestHost.Children.Clear();
        var phase = TestText("", 14, subtle: false);
        DeviceTestHost.Children.Add(phase);
        DeviceTestHost.Children.Add(Bar(out var progress, "#7C5CFF", 8));
        var live = TestText("", 13, subtle: false);
        live.Margin = new Thickness(0, 10, 0, 6);
        DeviceTestHost.Children.Add(live);
        DeviceTestHost.Children.Add(TestText(hybrid
            ? $"{cores.Count(c => c.Efficiency == perf)} cœurs de performance (P) et {cores.Count(c => c.Efficiency != perf)} cœurs d'efficacité (E) : chacun est comparé aux cœurs de son type."
            : $"{cores.Count} cœurs : chacun est comparé aux autres.", 12));

        var grid = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
        DeviceTestHost.Children.Add(grid);
        var results = cores.Select(c => new CoreResult { Core = c }).ToList();
        var views = new List<(Border box, TextBlock text)>();
        foreach (var r in results)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"Cœur {r.Core.Index + 1}{Short(r.Core)}", FontWeight = FontWeights.SemiBold });
            var t = new TextBlock { Text = "en attente", Style = (Style)FindResource("Subtle"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(t);
            var box = new Border { Width = 150, MinHeight = 58, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10),
                                   Background = (Brush)FindResource("Surface2"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Child = sp };
            grid.Children.Add(box);
            views.Add((box, t));
        }
        DeviceTestHost.Children.Add(TestButton("", "Arrêter le test", () => FinishCoreTest(results, hybrid, perf, Kind, "Arrêté par vous")));

        Sensors sensors = null;
        try { sensors = new Sensors(); sensors.Read(); } catch (Exception ex) { Log.Error("Capteurs", ex); }
        SetThreadExecutionState(0x80000000 | 0x1 | 0x2);
        int index = 0;
        CpuLoad load = null;
        DateTime slotStart = DateTime.Now, testStart = DateTime.Now;
        long lastFlops = 0; double lastSec = 0;
        var rates = new List<double>(); var mhz = new List<double>();
        bool busy = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _coreTestStop = () => { timer.Stop(); load?.Dispose(); load = null; sensors?.Dispose(); sensors = null; SetThreadExecutionState(0x80000000); _hwinfo?.Stop(); _hwinfo = null; };
        _testCleanup = () => _coreTestStop?.Invoke();

        timer.Tick += async (_, _) =>
        {
            if (busy || _testKind != "bench" || index >= results.Count) return;
            busy = true;
            try
            {
                var r = results[index];
                if (load == null)
                {
                    load = new CpuLoad(1, r.Core.FirstLogical);
                    slotStart = DateTime.Now; lastFlops = 0; lastSec = 0; rates.Clear(); mhz.Clear();
                    views[index].box.BorderBrush = (Brush)FindResource("Accent");
                    views[index].text.Text = "en cours…";
                }
                var s = sensors != null ? await Task.Run(() => { try { return sensors.Read(); } catch { return null; } }) : null;
                if (load == null) return;   // arrêté pendant la lecture
                var f = load.Flops; var fs = load.FmaSeconds;
                double rate = fs > lastSec ? (f - lastFlops) / (fs - lastSec) / 1e9 : 0;
                lastFlops = f; lastSec = fs;
                double el = (DateTime.Now - slotStart).TotalSeconds;
                if (el > 2 && rate > 0) rates.Add(rate);
                if (s != null && s.CoreMHz.TryGetValue(r.Core.FirstLogical, out var m) && el > 2) mhz.Add(m);
                if (s?.CpuTemp is double t) r.TempMax = Math.Max(r.TempMax ?? 0, t);
                r.Errors = load.Errors;
                views[index].text.Text = $"{rate:0} GFLOPS" + (mhz.Count > 0 ? $"\n{mhz[^1]:0} MHz" : "") + (s?.CpuTemp is double tt ? $" · {tt:0} °C" : "");

                double total = results.Count * (double)seconds;
                progress.Width = Math.Min(1, (index * seconds + Math.Min(el, seconds)) / total) * Math.Max(0, ((FrameworkElement)progress.Parent).ActualWidth);
                phase.Text = $"Cœur {r.Core.Index + 1} sur {results.Count}" + (hybrid ? $" (cœur {Kind(r.Core)})" : "") + $" · encore {Math.Max(0, total - (DateTime.Now - testStart).TotalSeconds) / 60:0} min {Math.Max(0, total - (DateTime.Now - testStart).TotalSeconds) % 60:00} s";
                live.Text = $"Processeur logique n° {r.Core.FirstLogical} · {rate:0} GFLOPS" + (mhz.Count > 0 ? $" · {mhz[^1]:0} MHz" : "") + (s?.CpuTemp is double t2 ? $" · {t2:0} °C" : "")
                          + (r.Errors > 0 ? $" · {r.Errors} erreur(s) !" : " · aucune erreur");

                if (s?.CpuTemp >= 98) { FinishCoreTest(results, hybrid, perf, Kind, $"Arrêt de sécurité : processeur à {s.CpuTemp:0} °C"); return; }
                if (el >= seconds || r.Errors > 0)
                {
                    r.Gflops = rates.Count > 0 ? rates.Skip(rates.Count / 4).Average() : 0;
                    r.MHz = mhz.Count > 0 ? mhz.Average() : 0;
                    r.Done = true;
                    load.Dispose(); load = null;
                    var (box, text) = views[index];
                    box.BorderBrush = BrushOfStatic(r.Errors > 0 ? "#EF4444" : "#10B981");
                    text.Text = (r.Errors > 0 ? $"{r.Errors} erreur(s) !\n" : "") + $"{r.Gflops:0} GFLOPS" + (r.MHz > 0 ? $"\n{r.MHz:0} MHz" : "");
                    index++;
                    if (index >= results.Count) FinishCoreTest(results, hybrid, perf, Kind, null);
                }
            }
            catch (Exception ex) { Log.Error("Test cœur par cœur", ex); }
            finally { busy = false; }
        };
        timer.Start();
    }

    private Action _coreTestStop;

    private void FinishCoreTest(List<CoreResult> results, bool hybrid, int perf, Func<CpuCore, string> kind, string stopReason)
    {
        if (_coreTestStop == null) return;
        _coreTestStop();
        _coreTestStop = null;
        _testCleanup = null;
        var done = results.Where(r => r.Done).ToList();

        var points = new List<(DeviceStatus level, string text)>();
        bool partial = stopReason == "Arrêté par vous" || done.Count < results.Count;
        if (stopReason != null && stopReason != "Arrêté par vous") points.Add((DeviceStatus.Problem, stopReason + "."));
        if (partial) points.Add((DeviceStatus.Info, $"Test arrêté avant la fin : {done.Count} cœur(s) sur {results.Count} testé(s)."));
        foreach (var r in done.Where(r => r.Errors > 0))
            points.Add((DeviceStatus.Problem, $"Cœur {r.Core.Index + 1} : {r.Errors} erreur(s) de calcul. Ce cœur est instable : réglage de tension ou overclocking par cœur (Curve Optimizer, offset) "
                                            + "trop poussé, ou processeur défectueux s'il est réglé d'usine."));
        // Comparaison avec les cœurs du même type : un cœur nettement plus lent est bridé ou n'atteint pas sa fréquence
        var slow = new List<CoreResult>();
        foreach (var group in done.Where(r => r.Errors == 0 && r.Gflops > 0).GroupBy(r => r.Core.Efficiency))
        {
            var list = group.OrderBy(r => r.Gflops).ToList();
            if (list.Count < 2) continue;
            double median = list[list.Count / 2].Gflops;
            double medianMhz = list.Where(r => r.MHz > 0).Select(r => r.MHz).OrderBy(x => x).Skip(list.Count(r => r.MHz > 0) / 2).FirstOrDefault();
            foreach (var r in list.Where(r => r.Gflops < median * 0.85))
            {
                slow.Add(r);
                points.Add((DeviceStatus.Warning, $"Cœur {r.Core.Index + 1} : {(1 - r.Gflops / median) * 100:0} % plus lent que les autres cœurs {(hybrid ? kind(r.Core) : "")}".TrimEnd()
                    + (r.MHz > 0 && medianMhz > 0 ? $" ({r.MHz:0} MHz contre {medianMhz:0})" : "") + ". Bridage de ce cœur (réglage du BIOS, chaleur) ou programme qui l'occupait pendant son test : refaites le test pour confirmer."));
            }
        }
        if (done.Count > 0 && done.All(r => r.Errors == 0))
            points.Insert(0, (DeviceStatus.Ok, $"Tous les cœurs testés sont stables ({done.Count}) : aucune erreur de calcul."));
        if (done.Count > 1 && slow.Count == 0 && done.Any(r => r.Gflops > 0))
            foreach (var group in done.Where(r => r.Gflops > 0).GroupBy(r => r.Core.Efficiency).OrderByDescending(g => g.Key))
            {
                var label = hybrid ? $"Cœurs {kind(group.First().Core)}" : "Cœurs";
                var withMhz = group.Where(r => r.MHz > 0).ToList();
                points.Add((DeviceStatus.Ok, $"{label} homogènes : {group.Min(r => r.Gflops):0} à {group.Max(r => r.Gflops):0} GFLOPS"
                    + (withMhz.Count > 0 ? $", {withMhz.Min(r => r.MHz):0} à {withMhz.Max(r => r.MHz):0} MHz" : "") + "."));
            }
        double? tempMax = done.Where(r => r.TempMax != null).Select(r => r.TempMax).DefaultIfEmpty(null).Max();
        if (tempMax is double tm) points.Add((tm >= 95 ? DeviceStatus.Warning : DeviceStatus.Ok, $"Température maximale du processeur : {tm:0} °C."));

        var worst = points.Count > 0 ? points.Max(p => p.level) : DeviceStatus.Ok;
        var history = Settings.Current.BenchHistory;
        history.Add(new BenchResult
        {
            Date = DateTime.Now, Kind = "Cœur par cœur", Method = BenchResult.CurrentMethod, Cpu = Bench.CpuName(),
            CpuSingleGflops = done.Count > 0 ? done.Max(r => r.Gflops) : 0, Errors = done.Sum(r => r.Errors), CpuTempMax = tempMax,
            Verdict = worst switch { DeviceStatus.Problem => "cœur instable", DeviceStatus.Warning => "cœur plus lent", _ => partial ? "partiel" : "validé" }
        });
        while (history.Count > 30) history.RemoveAt(0);
        Settings.Save();

        // Affichage : le verdict au-dessus de la grille des cœurs (gardée pour la vue d'ensemble)
        var (title, color) = worst switch
        {
            DeviceStatus.Problem => ("Problème détecté", "#EF4444"),
            DeviceStatus.Warning => ("Un cœur se démarque", "#F59E0B"),
            _ => (partial ? "Test partiel" : "Tous les cœurs sont validés", "#10B981")
        };
        var summary = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        summary.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = BrushOfStatic(color), Margin = new Thickness(0, 0, 0, 10) });
        foreach (var (level, text) in points)
        {
            var (_, c, glyph) = StatusLook(level);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), Foreground = BrushOfStatic(c), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            summary.Children.Add(row);
        }
        // On retire l'en-tête du test (phase, barre, ligne en direct, bouton) et on garde la grille
        var grid = DeviceTestHost.Children.OfType<WrapPanel>().FirstOrDefault();
        DeviceTestHost.Children.Clear();
        DeviceTestHost.Children.Add(summary);
        if (grid != null)
        {
            foreach (var (r, i) in results.Select((r, i) => (r, i)))
                if (slow.Contains(r) && grid.Children[i] is Border b) b.BorderBrush = BrushOfStatic("#F59E0B");
            DeviceTestHost.Children.Add(grid);
        }
        DeviceTestHost.Children.Add(TestButton("", "Refaire un test", () => OpenDeviceTest("bench"), accent: true));
    }
}
