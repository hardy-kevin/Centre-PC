using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Page Périphériques : état de chaque famille de périphériques, diagnostic primaire et corrections en un clic ;
/// pour aller plus loin, les paramètres de Windows et le logiciel du fabricant s'il est installé.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, DeviceCard> _deviceCards = new();
    private readonly HashSet<string> _devicePending = new();
    private bool _devicesScanning;
    private readonly Dictionary<string, (DeviceCard card, FrameworkElement view)> _deviceViews = new();
    private DateTime _devicesScannedAt;

    /// <summary>Première analyse discrète quelques secondes après le démarrage (pour la pastille du menu).</summary>
    private async Task ScanDevicesLaterAsync()
    {
        await Task.Delay(8000);
        await ScanDevicesAsync();
    }

    private void DevicesScan_Click(object sender, RoutedEventArgs e) => _ = ScanDevicesAsync();

    /// <summary>Toutes les familles analysées en parallèle ; chaque carte s'affiche dès qu'elle est prête.</summary>
    private async Task ScanDevicesAsync()
    {
        if (_devicesScanning) return;
        _devicesScanning = true;
        DevicesScanButton.IsEnabled = false;
        DeviceHealth.InvalidateHardware();
        try { await ResetFingerprintsAsync(); } catch (Exception ex) { Log.Error("Périphériques en direct", ex); }
        _deviceCards.Clear();
        _devicePending.Clear();
        foreach (var k in DeviceHealth.Order) _devicePending.Add(k);
        RenderDevices();
        var tasks = DeviceHealth.Order.ToDictionary(k => k, k => SafeDeviceScan(k));
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks.Values);
            var key = tasks.First(kv => kv.Value == done).Key;
            tasks.Remove(key);
            _devicePending.Remove(key);
            var card = await done;
            if (card != null) _deviceCards[key] = card;
            RenderDevices();
        }
        _devicesScannedAt = DateTime.Now;
        _devicesScanning = false;
        DevicesScanButton.IsEnabled = true;
    }

    private static async Task<DeviceCard> SafeDeviceScan(string key)
    {
        try { return await DeviceHealth.Collector(key)(); }
        catch (Exception ex) { Log.Error("Périphériques : " + key, ex); return null; }
    }

    /// <summary>Après une correction : seule la carte concernée est réanalysée.</summary>
    private async Task RescanDeviceAsync(string key)
    {
        var card = await SafeDeviceScan(key);
        if (card != null) _deviceCards[key] = card; else _deviceCards.Remove(key);
        RenderDevices();
    }

    // ------------------------------------------------------------------ affichage
    private static (string text, string color, string glyph) StatusLook(DeviceStatus s) => s switch
    {
        DeviceStatus.Problem => ("Problème", "#EF4444", ""),
        DeviceStatus.Warning => ("À vérifier", "#F59E0B", ""),
        DeviceStatus.Info => ("À savoir", "#3B82F6", ""),
        _ => ("Fonctionne", "#10B981", "")
    };

    private void RenderDevices()
    {
        DevicesPanel.Children.Clear();
        // Ce qui pose problème d'abord, puis l'ordre habituel
        var cards = _deviceCards.Values
            .OrderByDescending(c => c.Status is DeviceStatus.Problem or DeviceStatus.Warning ? (int)c.Status : 0)
            .ThenBy(c => Array.IndexOf(DeviceHealth.Order, c.Key)).ToList();
        // Vue reconstruite seulement pour les cartes réanalysées (les autres sont réutilisées telles quelles)
        foreach (var c in cards)
        {
            if (!_deviceViews.TryGetValue(c.Key, out var v) || v.card != c) _deviceViews[c.Key] = v = (c, DeviceCardView(c));
            DevicesPanel.Children.Add(v.view);
        }
        if (_devicePending.Count > 0)
            DevicesPanel.Children.Add(new TextBlock { Text = "Analyse en cours…", Style = (Style)FindResource("Subtle"), Margin = new Thickness(4, 6, 0, 0) });

        int problems = cards.Sum(c => c.Issues.Count(i => i.Level == DeviceStatus.Problem));
        int warnings = cards.Sum(c => c.Issues.Count(i => i.Level == DeviceStatus.Warning));
        var worst = problems > 0 ? DeviceStatus.Problem : warnings > 0 ? DeviceStatus.Warning : DeviceStatus.Ok;
        var (_, color, glyph) = StatusLook(worst);
        DevicesSummaryIcon.Background = BrushOf(color);
        DevicesSummaryGlyph.Text = _devicePending.Count > 0 && cards.Count == 0 ? "" : glyph;
        DevicesSummaryTitle.Text = _devicePending.Count > 0 && cards.Count == 0 ? "Analyse des périphériques…"
            : problems + warnings == 0 ? (_devicePending.Count > 0 ? "Rien à signaler pour l'instant…" : "Tout fonctionne")
            : (problems + warnings == 1 ? "1 point à vérifier" : $"{problems + warnings} points à vérifier");
        DevicesSummaryText.Text = _devicePending.Count > 0
            ? "Son, réseau, écrans, disques, mémoire, imprimantes, Bluetooth… quelques secondes."
            : problems + warnings == 0 ? "Aucun problème détecté sur vos périphériques."
            : "Chaque point propose une correction ou indique où aller : commencez par le haut de la liste.";
        UpdateDevicesBadge(problems + warnings);
        UpdateTestTiles();
    }

    private void UpdateDevicesBadge(int count)
    {
        DevicesBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesBadgeText.Text = count.ToString();
    }

    private FrameworkElement DeviceCardView(DeviceCard c)
    {
        var panel = new StackPanel();

        // En-tête : symbole, nom, résumé, état
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Glyph(c.Glyph, c.Color, 34));
        var title = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = c.Title, Style = (Style)FindResource("SectionTitle"), FontSize = 16 });
        if (!string.IsNullOrEmpty(c.Headline)) title.Children.Add(new TextBlock { Text = c.Headline, Style = (Style)FindResource("Subtle"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(title, 1); head.Children.Add(title);
        var (text, color, _) = StatusLook(c.Status);
        var pill = new Border
        {
            Background = BrushOf(color + "").Clone(), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
        };
        pill.Background.Opacity = 0.85;
        Grid.SetColumn(pill, 2); head.Children.Add(pill);
        panel.Children.Add(head);

        // Informations
        if (c.Lines.Count > 0)
        {
            var lines = new StackPanel { Margin = new Thickness(48, 10, 0, 0) };
            foreach (var l in c.Lines) lines.Children.Add(new TextBlock { Text = l, Style = (Style)FindResource("Subtle"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            panel.Children.Add(lines);
        }

        // Appareils en tuiles visuelles (disques et leurs lecteurs, batterie)
        if (c.Groups.Count > 0)
        {
            var tiles = new WrapPanel { Margin = new Thickness(48, 12, 0, 0) };
            foreach (var g in c.Groups) tiles.Children.Add(DeviceGroupView(g, c.Color));
            panel.Children.Add(tiles);
        }

        // Points à signaler, chacun avec sa correction
        foreach (var i in c.Issues)
        {
            var (_, ic, ig) = StatusLook(i.Level);
            var row = new Grid { Margin = new Thickness(48, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = ig, Style = (Style)FindResource("Glyph"), FontSize = 14, Foreground = BrushOf(ic), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) });
            var t = new TextBlock { Text = i.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 1); row.Children.Add(t);
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, -3, 0, 0) };
            foreach (var a in i.Actions) acts.Children.Add(DeviceActionButton(c.Key, a));
            Grid.SetColumn(acts, 2); row.Children.Add(acts);
            panel.Children.Add(row);
        }

        // Aller plus loin : actions de la carte, paramètres de Windows, logiciel du fabricant
        var more = new WrapPanel { Margin = new Thickness(40, 14, 0, 0) };
        foreach (var a in c.Actions) more.Children.Add(DeviceActionButton(c.Key, a));
        if (!string.IsNullOrEmpty(c.SettingsUri))
            more.Children.Add(DeviceActionButton(c.Key, new DeviceAction { Label = "Paramètres Windows", Glyph = "", Uri = c.SettingsUri }));
        foreach (var app in VendorApps(c.RelevantSoftware().ToArray()))
        {
            var target = app;
            var b = SmallButton("", "Ouvrir " + app.Label, (_, _) => Launch(target), tip: "Logiciel du fabricant : réglages avancés");
            b.Margin = new Thickness(8, 0, 0, 8);
            more.Children.Add(b);
        }
        if (more.Children.Count > 0) panel.Children.Add(more);

        return new Border
        {
            Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(20, 16, 20, 10), Child = panel,
            BorderBrush = c.Status is DeviceStatus.Problem or DeviceStatus.Warning ? BrushOf(color) : (Brush)FindResource("Line"), BorderThickness = new Thickness(1)
        };
    }

    private static string GaugeColor(DeviceStatus level, string ok) => level switch
    {
        DeviceStatus.Problem => "#EF4444", DeviceStatus.Warning => "#F59E0B", _ => ok
    };

    /// <summary>Tuile d'un appareil : symbole, nom, type, état, puis une jauge par lecteur (place utilisée) ou par mesure.</summary>
    private FrameworkElement DeviceGroupView(DeviceGroup g, string color)
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = g.Glyph, Style = (Style)FindResource("Glyph"), FontSize = 22, Foreground = BrushOf(color), VerticalAlignment = VerticalAlignment.Center });
        var t = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = g.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = g.Title });
        if (!string.IsNullOrEmpty(g.Subtitle)) t.Children.Add(new TextBlock { Text = g.Subtitle, Style = (Style)FindResource("Subtle"), FontSize = 12 });
        Grid.SetColumn(t, 1); head.Children.Add(t);
        if (!string.IsNullOrEmpty(g.StatusText))
        {
            var sc = GaugeColor(g.Level, g.Level == DeviceStatus.Info ? "#64748B" : "#10B981");
            var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top,
                                    Background = BrushOf(sc).Clone(), Child = new TextBlock { Text = g.StatusText, FontSize = 11, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold } };
            pill.Background.Opacity = 0.8;
            Grid.SetColumn(pill, 2); head.Children.Add(pill);
        }
        sp.Children.Add(head);
        if (g.Gauges.Count == 0 && g.IsDisk)
            sp.Children.Add(new TextBlock { Text = "Aucun lecteur avec une lettre", Style = (Style)FindResource("Subtle"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });
        foreach (var x in g.Gauges)
        {
            var row = new StackPanel { Margin = new Thickness(-6, 6, -6, 0) };
            var line = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = x.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var d = new TextBlock { Text = x.Detail, Style = (Style)FindResource("Subtle"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(d, 1); line.Children.Add(d);
            row.Children.Add(line);
            double f = Math.Clamp(x.Fraction, 0, 1);
            var track = new Grid { Height = 8 };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
            var back = new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) };
            Grid.SetColumnSpan(back, 2); track.Children.Add(back);
            track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = BrushOf(GaugeColor(x.Level, color)) });
            row.Children.Add(track);
            if (x.Path == null) { row.Margin = new Thickness(0, 12, 0, 0); sp.Children.Add(row); continue; }
            // Lecteur : clic pour l'ouvrir, clic droit pour ses options ; survol visible
            var hit = new Border { Child = row, Padding = new Thickness(6, 6, 6, 8), Margin = new Thickness(-6, 4, -6, 0), CornerRadius = new CornerRadius(8),
                                   Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Clic : ouvrir le lecteur · clic droit : plus d'options",
                                   ContextMenu = DriveMenu(x) };
            row.Margin = new Thickness(0);
            hit.MouseEnter += (_, _) => hit.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x80, 0x80, 0x80));
            hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
            var path = x.Path;
            hit.MouseLeftButtonUp += (_, _) => OpenDrive(path);
            sp.Children.Add(hit);
        }
        var tile = new Border
        {
            Width = 330, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 12, 14, 14), CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("Surface2"), BorderThickness = new Thickness(1),
            BorderBrush = g.Level is DeviceStatus.Problem or DeviceStatus.Warning ? BrushOf(GaugeColor(g.Level, color)) : (Brush)FindResource("Line"),
            Child = sp
        };
        if (g.IsDisk) { tile.ContextMenu = DiskMenu(g); tile.ToolTip = "Clic droit : options du disque"; }
        return tile;
    }

    private void OpenDrive(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("Impossible d'ouvrir le lecteur : " + ex.Message); }
    }

    /// <summary>Options d'un lecteur : ouvrir, faire de la place, nettoyer, optimiser, vérifier, propriétés, éjecter, menu de Windows.</summary>
    private ContextMenu DriveMenu(DeviceGauge x)
    {
        var letter = x.Path.TrimEnd('\\');
        var menu = new ContextMenu();
        var open = MakeItem("Ouvrir", "\uE8B7", () => OpenDrive(x.Path));
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        if (x.IsSystem)
            menu.Items.Add(MakeItem("Voir ce qui prend de la place", "\uEDA2", () => OpenTarget("ms-settings:storagesense")));
        menu.Items.Add(MakeItem("Nettoyer les fichiers inutiles", "\uE74D", () => OpenTarget($"cleanmgr.exe /d {letter[0]}")));
        menu.Items.Add(MakeItem("Optimiser le lecteur", "\uE9D9", () => OpenTarget("dfrgui.exe")));
        menu.Items.Add(MakeItem("Rechercher des erreurs", "\uE9F5", () => _ = CheckDriveAsync(letter)));
        menu.Items.Add(MakeItem("Tester la vitesse", "\uE916", () => OpenDeviceTest("disk")));
        if (x.External)
            menu.Items.Add(MakeItem("Éjecter en toute sécurité", "\uE7E8", () => EjectDrive(x.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Propriétés", "\uE946", () =>
        {
            if (!SHObjectProperties(new System.Windows.Interop.WindowInteropHelper(this).Handle, 2, x.Path, null)) ShowToast("Impossible d'ouvrir les propriétés.");
        }));
        menu.Items.Add(MakeItem("Plus d'options (menu de Windows)", "\uE712", () =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (!ShellMenu.Show(x.Path, hwnd)) ShowToast("Windows ne propose pas d'autres options pour ce lecteur.");
        }));
        return menu;
    }

    /// <summary>Options d'un disque physique : ses lecteurs, gestion des disques, vitesse.</summary>
    private ContextMenu DiskMenu(DeviceGroup g)
    {
        var menu = new ContextMenu();
        foreach (var x in g.Gauges.Where(x => x.Path != null))
        {
            var p = x.Path;
            menu.Items.Add(MakeItem("Ouvrir " + x.Label.Replace(" · Windows", ""), "\uE8B7", () => OpenDrive(p)));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Tester la vitesse", "\uE916", () => OpenDeviceTest("disk")));
        menu.Items.Add(MakeItem("Gestion des disques (Windows)", "\uEDA2", () => OpenTarget("diskmgmt.msc")));
        if (g.External && g.Gauges.FirstOrDefault(x => x.Path != null) is { } first)
            menu.Items.Add(MakeItem("Éjecter en toute sécurité", "\uE7E8", () => EjectDrive(first.Path)));
        return menu;
    }

    /// <summary>Vérification du système de fichiers en lecture seule (chkdsk /scan, autorisation administrateur), dans une fenêtre visible.</summary>
    private async Task CheckDriveAsync(string letter)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c chkdsk {letter} /scan & echo. & pause") { UseShellExecute = true, Verb = "runas" });
            ShowToast($"Vérification de {letter} en cours dans la fenêtre ouverte : elle ne modifie rien.");
            if (p != null) await p.WaitForExitAsync();
            await RescanDeviceAsync("storage");
        }
        catch (System.ComponentModel.Win32Exception) { ShowToast("Vérification annulée : Windows demande l'autorisation administrateur."); }
        catch (Exception ex) { ShowToast("Impossible : " + ex.Message); }
    }

    /// <summary>Éjection propre (comme « Éjecter » dans l'Explorateur), puis mise à jour de la carte.</summary>
    private void EjectDrive(string path)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic item = shell.NameSpace(17).ParseName(path.TrimEnd('\\'));   // 17 : Ce PC
            if (item == null) { ShowToast("Ce lecteur ne peut pas être éjecté d'ici : utilisez l'icône près de l'horloge."); return; }
            item.InvokeVerb("Eject");
            ShowToast("Éjection demandée : attendez que Windows confirme avant de débrancher.");
            _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.Invoke(() => _ = RescanDeviceAsync("storage")));
        }
        catch (Exception ex) { ShowToast("Impossible d'éjecter : " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string page);

    /// <summary>Logiciels du fabricant installés parmi les noms proposés (3 au plus).</summary>
    private List<AppItem> VendorApps(string[] names) =>
        names == null || names.Length == 0 ? new()
        : _apps.Where(a => !a.IsCompanion && names.Any(n => a.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
               .GroupBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First()).Take(3).ToList();

    private Button DeviceActionButton(string cardKey, DeviceAction a)
    {
        Button b;
        if (a.Primary)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = a.Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = a.Label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            b = new Button { Style = (Style)FindResource("AccentButton"), Content = sp, Padding = new Thickness(12, 6, 12, 6), ToolTip = a.Tip };
        }
        else b = SmallButton(a.Glyph, a.Label, null, tip: a.Tip);
        b.Margin = new Thickness(8, 0, 0, 8);
        b.Click += async (_, _) =>
        {
            if (a.Goto == "updates") { ShowUpdatesPage(); return; }
            if (a.Goto?.StartsWith("test:") == true) { OpenDeviceTest(a.Goto[5..]); return; }
            if (a.Uri != null) { OpenTarget(a.Uri); return; }
            if (a.Run == null) return;
            b.IsEnabled = false;
            string msg;
            try { msg = await a.Run(); }
            catch (Exception ex) { msg = "Impossible : " + ex.Message; Log.Error("Périphériques : " + a.Label, ex); }
            if (!string.IsNullOrEmpty(msg)) ShowToast(msg);
            DeviceHealth.InvalidateHardware();
            await RescanDeviceAsync(cardKey);
        };
        return b;
    }

    /// <summary>Ouvre un lien de Windows (« ms-settings:… »), un outil (« joy.cpl ») ou « programme.exe arguments ».</summary>
    private void OpenTarget(string target)
    {
        try
        {
            var m = Regex.Match(target, @"^(\S+\.exe)\s+(.+)$", RegexOptions.IgnoreCase);
            var psi = m.Success ? new ProcessStartInfo(m.Groups[1].Value, m.Groups[2].Value) : new ProcessStartInfo(target);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex) { ShowToast("Impossible d'ouvrir : " + ex.Message); }
    }
}
