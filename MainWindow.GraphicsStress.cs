using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Test de contrainte graphique (même méthode que les « Stress Tests » de 3DMark) : la même scène 3D rendue en boucles successives ;
/// stabilité = images par seconde de la pire boucle ÷ meilleure boucle. Réussi à partir de 97 %.
/// </summary>
public partial class MainWindow
{
    private int _stressLoops = 20;

    /// <summary>Résultat d'un test de contrainte (aussi utilisé par le bilan).</summary>
    public sealed class StressResult
    {
        public List<double> LoopFps = new();
        public double? GpuTempMax, HotspotMax, PowerMax, ClockAvg;
        public string Failure;
        public double Stability => LoopFps.Count < 2 ? 0 : LoopFps.Min() / LoopFps.Max() * 100;
        public bool Passed => LoopFps.Count >= 2 && Stability >= 97 && Failure == null;
    }

    private void StartGraphicsStress()
    {
        _monitorWatch?.Stop();
        int loops = _stressLoops;
        if (_benchManaged && _hwinfo == null && Sensors.MonitorRunning() == null) { _ = PrepareHwinfoAsync(() => _ = RunGraphicsStressUiAsync(loops)); return; }
        _ = RunGraphicsStressUiAsync(loops);
    }

    /// <summary>
    /// Boucles de rendu mesurées une par une. <paramref name="tick"/> reçoit chaque seconde : boucle en cours, images/s, mesures.
    /// </summary>
    private async Task<StressResult> GraphicsStressAsync(Gpu.Adapter adapter, int loops, int loopSeconds, Sensors sensors,
                                                         Action<int, double, GpuReading> tick, CancellationToken ct)
    {
        var res = new StressResult();
        var clocks = new List<double>();
        using var load = new GpuRenderLoad(adapter);
        await Task.Delay(3000, ct).ContinueWith(_ => { });   // préchauffage : compilation et montée en fréquence
        for (int loop = 0; loop < loops && !ct.IsCancellationRequested; loop++)
        {
            long f0 = load.Frames; double s0 = load.Seconds;
            for (int sec = 0; sec < loopSeconds && !ct.IsCancellationRequested; sec++)
            {
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
                var s = sensors != null ? await Task.Run(() => { try { return sensors.Read(); } catch { return null; } }) : null;
                var g = s?.Gpus.FirstOrDefault(x => x.Luid == adapter.Luid) ?? s?.Gpus.FirstOrDefault();
                if (g?.Temp is double t) res.GpuTempMax = Math.Max(res.GpuTempMax ?? 0, t);
                if (g?.Hotspot is double h) res.HotspotMax = Math.Max(res.HotspotMax ?? 0, h);
                if (g?.PowerW is double p) res.PowerMax = Math.Max(res.PowerMax ?? 0, p);
                if (g?.ClockMHz is double c) clocks.Add(c);
                double elapsed = load.Seconds - s0;
                tick(loop, elapsed > 0 ? (load.Frames - f0) / elapsed : 0, g);
                if (load.Failure != null) { res.Failure = load.Failure; break; }
                if (g?.Temp is double gt && gt >= (g.TempMax is double gm ? gm - 2 : 95)) { res.Failure = $"arrêt de sécurité, carte graphique à {gt:0} °C"; break; }
            }
            if (res.Failure != null || ct.IsCancellationRequested) break;
            double dt = load.Seconds - s0;
            if (dt > 0) res.LoopFps.Add((load.Frames - f0) / dt);
        }
        if (clocks.Count > 0) res.ClockAvg = clocks.Average();
        return res;
    }

    private async Task RunGraphicsStressUiAsync(int loops)
    {
        var adapter = Gpu.Adapters().FirstOrDefault(g => g.Luid == _benchGpuLuid) ?? Gpu.Adapters().FirstOrDefault();
        if (adapter == null) { ShowToast("Aucune carte graphique compatible Direct3D 11."); return; }
        int loopSeconds = loops >= 20 ? 60 : 30;
        var cts = new CancellationTokenSource();
        DeviceTestHost.Children.Clear();
        var phase = TestText("Préparation de la scène…", 14, subtle: false);
        DeviceTestHost.Children.Add(phase);
        DeviceTestHost.Children.Add(Bar(out var progress, "#7C5CFF", 8));
        var live = TestText("", 13, subtle: false);
        live.Margin = new Thickness(0, 10, 0, 6);
        DeviceTestHost.Children.Add(live);
        var bars = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        DeviceTestHost.Children.Add(bars);
        var chart = new LiveChart("Images par seconde") { Height = 140, Margin = new Thickness(0, 0, 0, 10) };
        DeviceTestHost.Children.Add(chart);
        DeviceTestHost.Children.Add(TestButton("", "Arrêter le test", () => cts.Cancel()));
        Sensors sensors = null;
        try { sensors = new Sensors(); sensors.Read(); } catch { }
        SetThreadExecutionState(0x80000000 | 0x1 | 0x2);
        _testCleanup = () => { cts.Cancel(); };
        var start = DateTime.Now;
        int lastLoop = -1;
        var res = await GraphicsStressAsync(adapter, loops, loopSeconds, sensors, (loop, fps, g) =>
        {
            if (loop != lastLoop && lastLoop >= 0)
                bars.Children.Add(new Border { Width = 26, Height = 26, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(6), Background = BrushOfStatic("#2A10B981"),
                                               Child = new TextBlock { Text = (lastLoop + 1).ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            lastLoop = loop;
            phase.Text = $"Boucle {loop + 1} sur {loops} · {adapter.Name}";
            live.Text = $"{fps:0.0} images/s" + (g?.Usage is double u ? $" · charge {u:0} %" : "") + (g?.Temp is double t ? $" · {t:0} °C" : "")
                      + (g?.Hotspot is double h ? $" (point chaud {h:0} °C)" : "") + (g?.PowerW is double p ? $" · {p:0} W" : "") + (g?.ClockMHz is double c ? $" · {c:0} MHz" : "");
            chart.Add("Images/s", "#22C55E", fps);
            chart.Redraw();
            progress.Width = Math.Min(1, (DateTime.Now - start).TotalSeconds / (loops * loopSeconds + 3)) * Math.Max(0, ((FrameworkElement)progress.Parent).ActualWidth);
        }, cts.Token);
        sensors?.Dispose();
        SetThreadExecutionState(0x80000000);
        _hwinfo?.Stop(); _hwinfo = null;
        _testCleanup = null;
        if (_testKind != "bench") return;

        // Verdict
        var points = new List<(DeviceStatus, string)>();
        bool partial = cts.IsCancellationRequested || res.LoopFps.Count < loops;
        if (res.Failure != null) points.Add((DeviceStatus.Problem, $"Test interrompu : {res.Failure}."));
        if (res.LoopFps.Count >= 2)
            points.Add((res.Stability >= 97 ? DeviceStatus.Ok : res.Stability >= 90 ? DeviceStatus.Warning : DeviceStatus.Problem,
                $"Stabilité : {res.Stability:0.0} % ({res.LoopFps.Min():0.0} à {res.LoopFps.Max():0.0} images/s sur {res.LoopFps.Count} boucles)" +
                (res.Stability >= 97 ? " — réussi (seuil 97 %)." : " — sous le seuil de 97 % : la carte perd de la performance en chauffant ou atteint une limite de puissance.")));
        if (res.GpuTempMax is double gt) points.Add((gt >= 88 ? DeviceStatus.Warning : DeviceStatus.Ok, $"Température maximale : {gt:0} °C" + (res.HotspotMax is double hs ? $", point chaud {hs:0} °C" : "") + "."));
        if (res.PowerMax is double pw) points.Add((DeviceStatus.Info, $"Consommation maximale : {pw:0} W" + (res.ClockAvg is double ca ? $", fréquence moyenne {ca:0} MHz." : ".")));
        if (partial && res.Failure == null) points.Add((DeviceStatus.Info, $"Test arrêté avant la fin ({res.LoopFps.Count} boucle(s) sur {loops})."));
        var worst = points.Count > 0 ? points.Max(p => p.Item1) : DeviceStatus.Info;
        var history = Settings.Current.BenchHistory;
        history.Add(new BenchResult { Date = DateTime.Now, Kind = $"Contrainte graphique {loops} boucles", Method = BenchResult.CurrentMethod, Gpu = adapter.Name, GpuTempMax = res.GpuTempMax,
                                      Verdict = res.LoopFps.Count >= 2 ? $"stabilité {res.Stability:0.0} %" : "incomplet" });
        while (history.Count > 30) history.RemoveAt(0);
        Settings.Save();

        DeviceTestHost.Children.Clear();
        var (title, color) = res.Passed && !partial ? ($"Réussi : stabilité {res.Stability:0.0} %", "#10B981")
                           : res.LoopFps.Count >= 2 && res.Stability >= 97 ? ($"Stabilité {res.Stability:0.0} % (test partiel)", "#10B981")
                           : res.LoopFps.Count >= 2 ? ($"Échec : stabilité {res.Stability:0.0} %", worst == DeviceStatus.Problem ? "#EF4444" : "#F59E0B")
                           : ("Test incomplet", "#F59E0B");
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
        var table = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
        double best = res.LoopFps.DefaultIfEmpty(1).Max();
        foreach (var (fps, i) in res.LoopFps.Select((f, i) => (f, i)))
            table.Children.Add(new Border { Width = 92, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(8),
                                            Background = (Brush)FindResource("Surface2"), BorderThickness = new Thickness(1),
                                            BorderBrush = BrushOfStatic(fps / best >= 0.97 ? "#10B981" : "#F59E0B"),
                                            Child = new TextBlock { Text = $"Boucle {i + 1}\n{fps:0.0} img/s", FontSize = 11.5 } });
        DeviceTestHost.Children.Add(table);
        DeviceTestHost.Children.Add(TestText("Score propre à Centre PC (scène et résolution différentes de 3DMark) ; la stabilité en % se lit comme celle de 3DMark.", 11.5));
        DeviceTestHost.Children.Add(TestButton("", "Refaire un test", () => OpenDeviceTest("bench"), accent: true));
    }
}
