using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Outils de test des périphériques (fenêtre par-dessus la page Périphériques) : clavier, souris, écran,
/// haut-parleurs, micro, manette, vitesse du disque, vitesse d'Internet.
/// </summary>
public partial class MainWindow
{
    private string _testKind;
    private Action _testCleanup;

    private static readonly (string key, string title, string glyph, string color, string text)[] TestTools =
    {
        ("checkup", "Bilan complet du PC", "\uE9D9", "#7C5CFF", "Santé, performances, stabilité, rapport"),
        ("keyboard", "Clavier", "", "#64748B", "Chaque touche, et plusieurs à la fois"),
        ("mouse", "Souris", "", "#64748B", "Tous les boutons, molette, double clics involontaires, fréquence"),
        ("screen", "Écran", "", "#8B5CF6", "Pixels morts et fluidité, en plein écran"),
        ("speakers", "Haut-parleurs", "", "#EC4899", "Gauche, droite, graves et aigus"),
        ("mic", "Micro", "", "#EC4899", "Niveau en direct, enregistrement et réécoute"),
        ("gamepad", "Manette", "", "#64748B", "Boutons, sticks, gâchettes, vibrations"),
        ("bench", "Performances et endurance", "\uE950", "#8B5CF6", "Processeur, carte graphique, températures"),
        ("disk", "Vitesse du disque", "", "#10B981", "Lecture et écriture réelles"),
        ("net", "Vitesse d'Internet", "", "#3B82F6", "Latence, téléchargement, envoi"),
    };

    /// <summary>Tuiles « Outils de test » en tête de la page Périphériques.</summary>
    private readonly Dictionary<string, Button> _testTiles = new();

    /// <summary>
    /// Tuiles adaptées à ce qui est branché : la manette n'apparaît que si une manette est détectée, le micro et les
    /// haut-parleurs que s'il y en a (tant que l'analyse n'est pas finie, tout reste affiché).
    /// </summary>
    private void UpdateTestTiles()
    {
        bool Has(string card, string what) => !_deviceCards.TryGetValue(card, out var c) || c.Present.Contains(what);
        if (_testTiles.TryGetValue("gamepad", out var g)) g.Visibility = Has("input", "gamepad") ? Visibility.Visible : Visibility.Collapsed;
        if (_testTiles.TryGetValue("mic", out var m)) m.Visibility = Has("audio", "mic") ? Visibility.Visible : Visibility.Collapsed;
        if (_testTiles.TryGetValue("speakers", out var sp)) sp.Visibility = Has("audio", "speakers") ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildTestTiles()
    {
        DeviceTestTiles.Children.Clear();
        _testTiles.Clear();
        foreach (var (key, title, glyph, color, text) in TestTools)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Glyph(glyph, color, 30));
            var sp = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Subtle"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            var b = new Button { Style = (Style)FindResource("BaseButton"), Content = g, Width = 252, Height = 66, Margin = new Thickness(0, 0, 10, 10),
                                 HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8) };
            var k = key;
            b.Click += (_, _) => OpenDeviceTest(k);
            _testTiles[k] = b;
            DeviceTestTiles.Children.Add(b);
        }
    }

    private void OpenDeviceTest(string kind)
    {
        if (kind == "screen") { new ScreenTestWindow(this).Show(); return; }
        CloseDeviceTest();
        _testKind = kind;
        DeviceTestHost.Children.Clear();
        var tool = TestTools.First(t => t.key == kind);
        DeviceTestTitle.Text = "Test : " + tool.title;
        switch (kind)
        {
            case "keyboard": BuildKeyboardTest(); break;
            case "mouse": BuildMouseTest(); break;
            case "speakers": BuildSpeakersTest(); break;
            case "mic": BuildMicTest(); break;
            case "gamepad": BuildGamepadTest(); break;
            case "disk": BuildDiskTest(); break;
            case "net": BuildNetTest(); break;
            case "bench": BuildBenchTest(); break;
            case "checkup": BuildCheckupTest(); break;
        }
        DeviceTestOverlay.Visibility = Visibility.Visible;
        Focus();
    }

    private void CloseDeviceTest()
    {
        _testCleanup?.Invoke();
        _testCleanup = null;
        _testKind = null;
        DeviceTestOverlay.Visibility = Visibility.Collapsed;
    }

    private void DeviceTestClose_Click(object sender, RoutedEventArgs e) => CloseDeviceTest();

    /// <summary>Clavier : le test du clavier reçoit toutes les touches (Échap compris) ; ailleurs Échap ferme.</summary>
    private bool DeviceTestKey(KeyEventArgs e, bool down)
    {
        if (_testKind == "keyboard") { KeyboardTestKey(e, down); e.Handled = true; return true; }
        if (down && e.Key == Key.Escape) { CloseDeviceTest(); return true; }
        if (_testKind == "mouse" && down && _mouseKey != null) { _mouseKey(e); e.Handled = true; return true; }
        return false;
    }

    private TextBlock TestText(string text, double size = 13, bool subtle = true) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Style = subtle ? (Style)FindResource("Subtle") : null, Margin = new Thickness(0, 0, 0, 10)
    };

    private Button TestButton(string glyph, string text, Action click, bool accent = false)
    {
        Button b;
        if (accent)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0) });
            b = new Button { Style = (Style)FindResource("AccentButton"), Content = sp, Padding = new Thickness(16, 8, 16, 8) };
        }
        else b = SmallButton(glyph, text, null);
        b.Margin = new Thickness(0, 0, 10, 10);
        b.Click += (_, _) => click();
        return b;
    }

    private static Border Bar(out Border fill, string color, double height = 14)
    {
        fill = new Border { Background = BrushOfStatic(color), CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        return new Border { Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(height / 2), Height = height, Child = fill };
    }

    private static Brush BrushOfStatic(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

    // ================================================================== clavier
    private readonly Dictionary<int, Border> _keyViews = new();
    private readonly HashSet<int> _keysDown = new(), _keysSeen = new();
    private int _rollover;
    private TextBlock _keyStatus;

    // Disposition physique (codes de position des touches, valables pour tous les claviers : AZERTY, QWERTY…)
    private static readonly int[][] KeyRows =
    {
        new[] { 0x01, 0, 0x3B, 0x3C, 0x3D, 0x3E, 0, 0x3F, 0x40, 0x41, 0x42, 0, 0x43, 0x44, 0x57, 0x58 },
        new[] { 0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E },
        new[] { 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C },
        new[] { 0x3A, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2B },
        new[] { 0x2A, 0x56, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36 },
        new[] { 0x1D, 0xE05B, 0x38, 0x39, 0xE038, 0xE05D, 0xE01D, 0, 0xE04B, 0xE048, 0xE050, 0xE04D },
    };

    private static double KeyWidth(int scan) => scan switch
    {
        0x0E => 92, 0x0F => 66, 0x1C => 66, 0x3A => 78, 0x2A => 56, 0x36 => 118, 0x39 => 300, 0x1D or 0xE01D => 66, 0 => 22, _ => 44
    };

    private void BuildKeyboardTest()
    {
        _keyViews.Clear(); _keysDown.Clear(); _keysSeen.Clear(); _rollover = 0;
        DeviceTestHost.Children.Add(TestText("Appuyez sur chaque touche : elle s'allume tant qu'elle est enfoncée, puis reste verte une fois testée. "
            + "Appuyez sur plusieurs touches en même temps pour mesurer combien sont reconnues ensemble (utile pour les jeux). "
            + "Certaines combinaisons (touche Windows, Impr. écran) sont gardées par Windows. Fermez avec le bouton en haut à droite."));
        var board = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
        foreach (var row in KeyRows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var scan in row)
            {
                if (scan == 0) { line.Children.Add(new Border { Width = KeyWidth(0) }); continue; }
                var view = new Border
                {
                    Width = KeyWidth(scan), Height = 40, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(7),
                    Background = (Brush)FindResource("Surface2"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1, 1, 1, 3),
                    Child = new TextBlock { Text = KeyName(scan), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 0, 3, 0) }
                };
                _keyViews[scan] = view;
                line.Children.Add(view);
            }
            board.Children.Add(line);
        }
        DeviceTestHost.Children.Add(new Viewbox { Child = board, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        _keyStatus = TestText("Appuyez sur une touche…", 14, subtle: false);
        DeviceTestHost.Children.Add(_keyStatus);
        DeviceTestHost.Children.Add(TestButton("", "Recommencer", () => OpenDeviceTest("keyboard")));
    }

    private static string KeyName(int scan)
    {
        var sb = new System.Text.StringBuilder(32);
        int lParam = ((scan & 0xFF) << 16) | ((scan & 0xE000) != 0 ? 1 << 24 : 0);
        var name = GetKeyNameText(lParam, sb, 32) > 0 ? sb.ToString() : "?";
        return name.Length > 9 ? name[..1].ToUpperInvariant() + name[1..].ToLowerInvariant() : name;
    }

    private void KeyboardTestKey(KeyEventArgs e, bool down)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        int scan = (int)MapVirtualKey((uint)vk, 4);   // MAPVK_VK_TO_VSC_EX : préfixe E0 pour les touches étendues
        if ((scan & 0xFF00) == 0xE000) scan = 0xE000 | (scan & 0xFF);
        // Touches de droite que Windows confond avec celles de gauche
        if (key == Key.RightCtrl) scan = 0xE01D;
        if (key == Key.RightAlt) scan = 0xE038;
        if (key == Key.RightShift) scan = 0x36;
        if (down)
        {
            if (!_keysDown.Add(scan)) return;   // répétition automatique
            _keysSeen.Add(scan);
            _rollover = Math.Max(_rollover, _keysDown.Count);
        }
        else _keysDown.Remove(scan);
        if (_keyViews.TryGetValue(scan, out var view))
        {
            view.Background = down ? (Brush)FindResource("Accent") : BrushOfStatic("#2A10B981");
            view.BorderBrush = down ? (Brush)FindResource("Accent") : BrushOfStatic("#10B981");
        }
        _keyStatus.Text = $"Dernière touche : {KeyName(scan)} · {_keysSeen.Count} touche(s) testée(s) · jusqu'à {_rollover} touche(s) reconnue(s) en même temps"
            + (_rollover >= 6 ? " (très bien pour les jeux)" : _rollover >= 3 && _keysDown.Count == 0 ? "" : "");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetKeyNameText(int lParam, System.Text.StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);

    // ================================================================== souris
    /// <summary>Touche reçue pendant le test de la souris (boutons supplémentaires programmés comme raccourcis clavier).</summary>
    private Action<KeyEventArgs> _mouseKey;

    private void BuildMouseTest()
    {
        DeviceTestHost.Children.Add(TestText("Cliquez dans la zone avec chaque bouton, faites tourner la molette, puis bougez la souris rapidement en cercles pour mesurer sa fréquence. "
            + "Un double clic qui apparaît alors que vous n'avez cliqué qu'une fois signale un bouton usé (rebond)."));
        var labels = new Dictionary<MouseButton, (string name, Border view, TextBlock count)>();
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var counts = new Dictionary<MouseButton, int>();
        var last = new Dictionary<MouseButton, DateTime>();
        int bounces = 0, wheelUp = 0, wheelDown = 0;
        foreach (var (b, n) in new[] { (MouseButton.Left, "Gauche"), (MouseButton.Right, "Droit"), (MouseButton.Middle, "Molette (clic)"), (MouseButton.XButton1, "Précédent"), (MouseButton.XButton2, "Suivant") })
        {
            var count = new TextBlock { Text = "non testé", Style = (Style)FindResource("Subtle"), FontSize = 12 };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = n, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(count);
            var v = new Border { Width = 150, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10),
                                 Background = (Brush)FindResource("Surface2"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Child = sp };
            labels[b] = (n, v, count);
            buttons.Children.Add(v);
        }
        // Molette inclinée (défilement horizontal), sur les souris qui l'ont
        var tilt = new Dictionary<int, (Border view, TextBlock count)>();
        var tiltCounts = new Dictionary<int, int>();
        foreach (var (dir, n) in new[] { (-1, "Molette à gauche"), (1, "Molette à droite") })
        {
            var count = new TextBlock { Text = "si votre souris l'a", Style = (Style)FindResource("Subtle"), FontSize = 12 };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = n, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(count);
            var v = new Border { Width = 150, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10),
                                 Background = (Brush)FindResource("Surface2"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Child = sp };
            tilt[dir] = (v, count);
            buttons.Children.Add(v);
        }
        DeviceTestHost.Children.Add(buttons);
        // Boutons au-delà de 5 : la souris (ou son logiciel) les transforme en touches du clavier ; on affiche ce qu'ils envoient
        var extra = new List<string>();
        var keysText = TestText("Autres boutons (côté, DPI, profil…) : appuyez dessus. S'ils envoient une touche ou un raccourci, il s'affiche ici. "
            + "Un bouton qui ne montre rien peut agir directement dans la souris (le bouton DPI change la vitesse du pointeur) : bougez-la pour voir la différence.", 12.5);
        _mouseKey = e =>
        {
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            var mods = Keyboard.Modifiers;
            var name = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Maj+" : "")
                     + (mods.HasFlag(ModifierKeys.Windows) ? "Windows+" : "") + KeyLabel(k);
            extra.Remove(name);
            extra.Insert(0, name);
            keysText.Text = "Reçu d'un bouton ou du clavier : " + string.Join(" · ", extra.Take(8)) + "  (le plus récent en premier)";
            keysText.Style = null;
        };
        var info = TestText("Molette : —", 13, subtle: false);
        var bounceText = TestText("Double clics involontaires : aucun", 13, subtle: false);
        var rateText = TestText("Fréquence : bougez la souris dans la zone…", 13, subtle: false);
        var zone = new Border
        {
            Height = 200, CornerRadius = new CornerRadius(14), Background = (Brush)FindResource("Surface2"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.Cross,
            Child = new TextBlock { Text = "Zone de test : cliquez, faites défiler, bougez", Style = (Style)FindResource("Subtle"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        zone.PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            if (!labels.TryGetValue(e.ChangedButton, out var l)) return;
            var now = DateTime.UtcNow;
            // Deux appuis à moins de 60 ms : impossible à la main, c'est le contact du bouton qui rebondit
            if (last.TryGetValue(e.ChangedButton, out var prev) && (now - prev).TotalMilliseconds < 60)
            {
                bounces++;
                bounceText.Text = $"Double clics involontaires : {bounces} (bouton {l.name.ToLowerInvariant()}) · le bouton est probablement usé";
                bounceText.Foreground = BrushOfStatic("#F59E0B");
            }
            last[e.ChangedButton] = now;
            counts[e.ChangedButton] = counts.GetValueOrDefault(e.ChangedButton) + 1;
            l.count.Text = $"{counts[e.ChangedButton]} clic(s)";
            l.view.BorderBrush = BrushOfStatic("#10B981");
            l.view.Background = BrushOfStatic("#2A10B981");
        };
        zone.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (e.Delta > 0) wheelUp++; else wheelDown++;
            info.Text = $"Molette : {wheelUp} cran(s) vers le haut · {wheelDown} vers le bas" + (wheelUp > 0 && wheelDown > 0 ? " ✓" : "");
        };
        zone.ContextMenuOpening += (_, e) => e.Handled = true;
        DeviceTestHost.Children.Add(zone);
        DeviceTestHost.Children.Add(info);
        DeviceTestHost.Children.Add(bounceText);
        DeviceTestHost.Children.Add(rateText);
        DeviceTestHost.Children.Add(keysText);

        // Fréquence réelle : messages bruts de la souris par seconde (125, 500, 1000 Hz…)
        var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        int events = 0, best = 0;
        var dev = new RAWINPUTDEVICE[] { new() { UsagePage = 1, Usage = 2, Flags = 0, Target = src.Handle } };
        RegisterRawInputDevices(dev, 1, Marshal.SizeOf<RAWINPUTDEVICE>());
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x00FF) events++;
            else if (msg == 0x020E)   // WM_MOUSEHWHEEL : molette inclinée
            {
                int dir = (short)((w.ToInt64() >> 16) & 0xFFFF) > 0 ? 1 : -1;
                tiltCounts[dir] = tiltCounts.GetValueOrDefault(dir) + 1;
                var (view, count) = tilt[dir];
                count.Text = $"{tiltCounts[dir]} fois";
                view.BorderBrush = BrushOfStatic("#10B981");
                view.Background = BrushOfStatic("#2A10B981");
                handled = true;
            }
            return IntPtr.Zero;
        };
        src.AddHook(hook);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            int rate = events * 4;
            events = 0;
            if (rate < 40) return;
            best = Math.Max(best, rate);
            int nominal = new[] { 125, 250, 500, 1000, 2000, 4000, 8000 }.OrderBy(x => Math.Abs(x - best)).First();
            rateText.Text = $"Fréquence : {rate} Hz maintenant · {best} Hz au maximum (souris réglée sur environ {nominal} Hz)";
        };
        timer.Start();
        _testCleanup = () =>
        {
            timer.Stop();
            _mouseKey = null;
            src.RemoveHook(hook);
            var off = new RAWINPUTDEVICE[] { new() { UsagePage = 1, Usage = 2, Flags = 0x1, Target = IntPtr.Zero } };   // RIDEV_REMOVE
            RegisterRawInputDevices(off, 1, Marshal.SizeOf<RAWINPUTDEVICE>());
        };
    }

    /// <summary>Nom lisible d'une touche (« Précédent du navigateur », « F13 », « A »).</summary>
    private static string KeyLabel(Key k) => k switch
    {
        Key.BrowserBack => "Précédent (navigateur)", Key.BrowserForward => "Suivant (navigateur)", Key.VolumeUp => "Volume +", Key.VolumeDown => "Volume −",
        Key.VolumeMute => "Couper le son", Key.MediaPlayPause => "Lecture/Pause", Key.MediaNextTrack => "Piste suivante", Key.MediaPreviousTrack => "Piste précédente",
        Key.Space => "Espace", Key.Enter => "Entrée", Key.Tab => "Tab", Key.Back => "Retour arrière", Key.Delete => "Suppr",
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        _ => k.ToString()
    };

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, int size);

    // ================================================================== haut-parleurs
    /// <summary>Ligne d'un périphérique audio : nom, « utilisé actuellement », et boutons.</summary>
    private (Border row, StackPanel right, TextBlock state) AudioRow(AudioDevice d, UIElement extra = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = d.Render ? "" : "", Style = (Style)FindResource("Glyph"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var state = new TextBlock { Text = d.IsDefault ? "Utilisé actuellement par Windows" : "", Style = (Style)FindResource("Subtle"), FontSize = 12 };
        mid.Children.Add(state);
        if (extra != null) mid.Children.Add(extra);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(right, 2); g.Children.Add(right);
        var row = new Border
        {
            Padding = new Thickness(14, 10, 10, 2), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(12), Child = g,
            Background = (Brush)FindResource("Surface2"), BorderThickness = new Thickness(1),
            BorderBrush = d.IsDefault ? (Brush)FindResource("Accent") : (Brush)FindResource("Line")
        };
        return (row, right, state);
    }

    /// <summary>« Utiliser » : ce périphérique devient celui de Windows (son ou micro), puis le test est rafraîchi.</summary>
    private Button UseButton(AudioDevice d, string kind) => TestButton("", "Utiliser", () =>
    {
        if (AudioDevices.SetDefault(d))
        {
            ShowToast($"{d.Name} est maintenant {(d.Render ? "la sortie" : "le micro")} de Windows.");
            OpenDeviceTest(kind);
            _ = RescanDeviceAsync("audio");
        }
        else ShowToast("Windows a refusé ce changement : faites-le dans les paramètres du son.");
    });

    private void BuildSpeakersTest()
    {
        DeviceTestHost.Children.Add(TestText("Toutes vos sorties audio branchées (casques, enceintes, écrans HDMI…). « Tester » joue un son sur cette sortie seulement : "
            + "celle que vous entendez est la bonne. « Utiliser » la choisit pour Windows. « Tester l'une après l'autre » les essaie toutes à la suite."));
        var outputs = AudioDevices.List(render: true);
        if (outputs.Count == 0)
        {
            DeviceTestHost.Children.Add(TestText("Aucune sortie audio active : branchez un casque ou des enceintes, ou vérifiez le pilote audio.", 14, subtle: false));
            DeviceTestHost.Children.Add(TestButton("", "Paramètres du son", () => OpenTarget("ms-settings:sound")));
            return;
        }
        var rows = new List<(AudioDevice d, TextBlock state)>();
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var d in outputs)
        {
            var (row, right, state) = AudioRow(d);
            var dev = d;
            right.Children.Add(TestButton("", "Tester", async () =>
            {
                state.Text = "Son en cours sur cette sortie…";
                await AudioDevices.PlayOnAsync(dev, DeviceTests.Tone(523, 1.2, true, true));
                state.Text = dev.IsDefault ? "Utilisé actuellement par Windows" : "";
            }));
            if (!d.IsDefault) right.Children.Add(UseButton(d, "speakers"));
            rows.Add((d, state));
            list.Children.Add(row);
        }
        DeviceTestHost.Children.Add(list);
        Button all = null;
        all = TestButton("", "Tester l'une après l'autre", async () =>
        {
            all.IsEnabled = false;
            foreach (var (d, state) in rows)
            {
                state.Text = "▶ Son en cours sur cette sortie : l'entendez-vous ?";
                state.Foreground = (Brush)FindResource("Accent");
                await AudioDevices.PlayOnAsync(d, DeviceTests.Tone(523, 1.5, true, true));
                await Task.Delay(500);
                state.Text = d.IsDefault ? "Utilisé actuellement par Windows" : "";
                state.Foreground = (Brush)FindResource("Text2");
            }
            all.IsEnabled = true;
        }, accent: true);
        DeviceTestHost.Children.Add(all);

        // Sur la sortie actuelle : gauche / droite, graves / aigus
        DeviceTestHost.Children.Add(new TextBlock { Text = "SUR LA SORTIE ACTUELLE", Foreground = (Brush)FindResource("Text2"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 8) });
        DeviceTestHost.Children.Add(TestText("Vérifiez que le son sort du côté annoncé (sinon : casque à l'envers ou enceintes inversées), et qu'aucun haut-parleur ne grésille."));
        var row1 = new WrapPanel();
        row1.Children.Add(TestButton("", "Gauche", () => _ = DeviceTests.PlayAsync(DeviceTests.Tone(440, 1.2, true, false))));
        row1.Children.Add(TestButton("", "Les deux", () => _ = DeviceTests.PlayAsync(DeviceTests.Tone(440, 1.2, true, true))));
        row1.Children.Add(TestButton("", "Droite", () => _ = DeviceTests.PlayAsync(DeviceTests.Tone(440, 1.2, false, true))));
        row1.Children.Add(TestButton("", "Grave", () => _ = DeviceTests.PlayAsync(DeviceTests.Tone(80, 1.5, true, true))));
        row1.Children.Add(TestButton("", "Aigu", () => _ = DeviceTests.PlayAsync(DeviceTests.Tone(8000, 1.2, true, true))));
        row1.Children.Add(TestButton("", "Balayage", () => _ = Task.Run(async () =>
        {
            foreach (var hz in new[] { 60, 120, 250, 500, 1000, 2000, 4000, 8000, 12000 }) await DeviceTests.PlayAsync(DeviceTests.Tone(hz, 0.45, true, true));
        })));
        DeviceTestHost.Children.Add(row1);
    }

    // ================================================================== micro
    /// <summary>
    /// Tous les micros écoutés en même temps : celui dont la barre bouge quand on parle reçoit la voix.
    /// Centre PC le signale et propose de l'utiliser ; enregistrement et réécoute sur le micro actuel.
    /// </summary>
    private void BuildMicTest()
    {
        DeviceTestHost.Children.Add(TestText("Parlez normalement : tous vos micros sont écoutés en même temps. Celui dont la barre bouge reçoit votre voix ; "
            + "Centre PC le repère et vous propose de l'utiliser. Rien n'est enregistré sans que vous le demandiez."));
        var mics = AudioDevices.List(render: false);
        if (mics.Count == 0)
        {
            DeviceTestHost.Children.Add(TestText("Aucun micro actif : branchez-le, ou vérifiez qu'il n'est pas désactivé dans les paramètres du son.", 14, subtle: false));
            DeviceTestHost.Children.Add(TestButton("", "Paramètres du son", () => OpenTarget("ms-settings:sound")));
            return;
        }
        var detected = TestText("En attente de votre voix…", 14, subtle: false);
        var useDetected = new WrapPanel();
        var captures = new List<MicCapture>();
        var peaks = new Dictionary<AudioDevice, float>();
        AudioDevice shown = null;
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var d in mics)
        {
            var bar = Bar(out var fill, "#EC4899", 10);
            bar.Margin = new Thickness(0, 6, 0, 8);
            bar.Width = 360;
            bar.HorizontalAlignment = HorizontalAlignment.Left;
            var (row, right, state) = AudioRow(d, bar);
            if (!d.IsDefault) right.Children.Add(UseButton(d, "mic"));
            list.Children.Add(row);
            peaks[d] = 0;
            var cap = new MicCapture(d.WaveIndex);
            var dev = d;
            cap.Level += level => Dispatcher.InvokeAsync(() =>
            {
                fill.Width = 360 * Math.Min(1, level * 1.6);
                peaks[dev] = Math.Max(peaks[dev] * 0.97f, level);
                // Le micro qui capte nettement le plus fort est celui qui reçoit la voix
                var best = peaks.OrderByDescending(kv => kv.Value).First();
                if (best.Value < 0.06f || best.Key == shown) return;
                var others = peaks.Where(kv => kv.Key != best.Key).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
                if (best.Value < others * 1.5f) return;
                shown = best.Key;
                detected.Text = best.Key.IsDefault ? $"Votre voix arrive sur {best.Key.Name}, le micro utilisé par Windows : tout est bon."
                                                   : $"Votre voix arrive sur {best.Key.Name}, mais Windows utilise un autre micro.";
                detected.Foreground = best.Key.IsDefault ? BrushOfStatic("#10B981") : BrushOfStatic("#F59E0B");
                useDetected.Children.Clear();
                if (!best.Key.IsDefault)
                {
                    var b = UseButton(best.Key, "mic");
                    ((StackPanel)b.Content).Children.OfType<TextBlock>().Last().Text = $"Utiliser {best.Key.Name}";
                    useDetected.Children.Add(b);
                }
            });
            if (d.WaveIndex < 0 || !cap.Start()) state.Text = "Impossible d'écouter ce micro (utilisé par une autre application ou bloqué).";
            else captures.Add(cap);
        }
        DeviceTestHost.Children.Add(detected);
        DeviceTestHost.Children.Add(useDetected);
        DeviceTestHost.Children.Add(list);

        // Réécoute sur le micro actuel
        var current = mics.FirstOrDefault(m => m.IsDefault) ?? mics[0];
        DeviceTestHost.Children.Add(new TextBlock { Text = "ÉCOUTER LA QUALITÉ", Foreground = (Brush)FindResource("Text2"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 8) });
        DeviceTestHost.Children.Add(TestText($"Enregistre 5 secondes avec {current.Name} puis les rejoue : souffle, grésillement, voix trop faible ?"));
        var mainCap = new MicCapture(current.WaveIndex);
        bool mainOk = current.WaveIndex >= 0 && mainCap.Start();
        var row2 = new WrapPanel();
        Button rec = null;
        rec = TestButton("", "Enregistrer 5 secondes", async () =>
        {
            if (!mainOk) { ShowToast("Ce micro ne peut pas être écouté pour l'instant."); return; }
            var label = ((StackPanel)rec.Content).Children.OfType<TextBlock>().Last();
            rec.IsEnabled = false;
            label.Text = "Enregistrement… parlez";
            var wav = await mainCap.RecordAsync(5);
            label.Text = "Réécoute…";
            await DeviceTests.PlayAsync(wav);
            label.Text = "Enregistrer 5 secondes";
            rec.IsEnabled = true;
        }, accent: true);
        row2.Children.Add(rec);
        row2.Children.Add(TestButton("", "Confidentialité du micro", () => OpenTarget("ms-settings:privacy-microphone")));
        row2.Children.Add(TestButton("", "Paramètres du son", () => OpenTarget("ms-settings:sound")));
        DeviceTestHost.Children.Add(row2);
        _testCleanup = () => { foreach (var c in captures) c.Dispose(); mainCap.Dispose(); };
    }

    // ================================================================== manette
    private void BuildGamepadTest()
    {
        DeviceTestHost.Children.Add(TestText("Manettes Xbox et compatibles (la plupart des manettes PC) : appuyez sur les boutons, bougez les sticks et les gâchettes. "
            + "Sticks relâchés, la valeur doit revenir près de 0 : sinon le stick « dérive » (personnage qui avance seul). "
            + "Manette PlayStation ou autre : utilisez « Contrôleurs de jeu de Windows »."));
        var status = TestText("Recherche d'une manette…", 14, subtle: false);
        DeviceTestHost.Children.Add(status);
        var pad = new Grid { Margin = new Thickness(0, 6, 0, 10) };
        pad.ColumnDefinitions.Add(new ColumnDefinition());
        pad.ColumnDefinitions.Add(new ColumnDefinition());
        pad.ColumnDefinitions.Add(new ColumnDefinition());

        (Canvas canvas, Ellipse dot, TextBlock value) Stick(string name)
        {
            var c = new Canvas { Width = 120, Height = 120 };
            c.Children.Add(new Ellipse { Width = 120, Height = 120, Stroke = (Brush)FindResource("Line"), StrokeThickness = 2 });
            var d = new Ellipse { Width = 22, Height = 22, Fill = (Brush)FindResource("Accent") };
            Canvas.SetLeft(d, 49); Canvas.SetTop(d, 49);
            c.Children.Add(d);
            return (c, d, new TextBlock { Text = name, Style = (Style)FindResource("Subtle"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        }
        var left = Stick("Stick gauche");
        var right = Stick("Stick droit");
        StackPanel Col(params UIElement[] els) { var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center }; foreach (var e in els) s.Children.Add(e); return s; }
        var ltBar = Bar(out var ltFill, "#8B5CF6", 12); ltBar.Width = 140;
        var rtBar = Bar(out var rtFill, "#8B5CF6", 12); rtBar.Width = 140;
        var lc = Col(new TextBlock { Text = "Gâchette gauche (LT)", Style = (Style)FindResource("Subtle"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) }, ltBar, new Border { Height = 12 }, left.canvas, left.value);
        var rc = Col(new TextBlock { Text = "Gâchette droite (RT)", Style = (Style)FindResource("Subtle"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) }, rtBar, new Border { Height = 12 }, right.canvas, right.value);
        var names = new (ushort bit, string label)[] { (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"), (0x0100, "LB"), (0x0200, "RB"), (0x0020, "Back"), (0x0010, "Start"),
                                                       (0x0040, "L3"), (0x0080, "R3"), (0x0001, "↑"), (0x0002, "↓"), (0x0004, "←"), (0x0008, "→") };
        var btnViews = new Dictionary<ushort, Border>();
        var btns = new WrapPanel { Width = 230, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (bit, label) in names)
        {
            var v = new Border { Width = 46, Height = 36, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("Surface2"),
                                 BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
                                 Child = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold } };
            btnViews[bit] = v;
            btns.Children.Add(v);
        }
        Grid.SetColumn(btns, 1);
        pad.Children.Add(lc);
        pad.Children.Add(btns);
        Grid.SetColumn(rc, 2); pad.Children.Add(rc);
        DeviceTestHost.Children.Add(pad);
        int index = -1;
        var row = new WrapPanel();
        row.Children.Add(TestButton("", "Vibrer", async () =>
        {
            if (index < 0) return;
            DeviceTests.Vibrate(index, 1, 0); await Task.Delay(500);
            DeviceTests.Vibrate(index, 0, 1); await Task.Delay(500);
            DeviceTests.Vibrate(index, 0, 0);
        }, accent: true));
        row.Children.Add(TestButton("", "Contrôleurs de jeu de Windows", () => OpenTarget("joy.cpl")));
        DeviceTestHost.Children.Add(row);

        var seen = new HashSet<ushort>();
        static double Norm(short v) => Math.Max(-1, v / 32767.0);
        void Place(Ellipse dot, TextBlock text, string name, short x, short y)
        {
            double nx = Norm(x), ny = Norm(y);
            Canvas.SetLeft(dot, 49 + nx * 49); Canvas.SetTop(dot, 49 - ny * 49);
            double mag = Math.Sqrt(nx * nx + ny * ny);
            text.Text = $"{name} : {mag * 100:0} %";
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            DeviceTests.Gamepad? s = null;
            if (index >= 0) s = DeviceTests.Pad(index);
            if (s == null) { index = -1; for (int i = 0; i < 4 && s == null; i++) { s = DeviceTests.Pad(i); if (s != null) index = i; } }
            if (s == null) { status.Text = "Aucune manette compatible détectée : branchez-la (ou associez-la en Bluetooth), puis appuyez sur un bouton."; return; }
            var p = s.Value;
            status.Text = $"Manette {index + 1} connectée · {seen.Count} / {names.Length} boutons testés";
            foreach (var (bit, _) in names)
            {
                bool on = (p.Buttons & bit) != 0;
                if (on) seen.Add(bit);
                btnViews[bit].Background = on ? (Brush)FindResource("Accent") : seen.Contains(bit) ? BrushOfStatic("#2A10B981") : (Brush)FindResource("Surface2");
                btnViews[bit].BorderBrush = seen.Contains(bit) ? BrushOfStatic("#10B981") : (Brush)FindResource("Line");
            }
            Place(left.dot, left.value, "Stick gauche", p.LX, p.LY);
            Place(right.dot, right.value, "Stick droit", p.RX, p.RY);
            ltFill.Width = 140 * p.LeftTrigger / 255.0;
            rtFill.Width = 140 * p.RightTrigger / 255.0;
        };
        timer.Start();
        _testCleanup = () => { timer.Stop(); if (index >= 0) DeviceTests.Vibrate(index, 0, 0); };
    }

    // ================================================================== vitesse du disque
    private void BuildDiskTest()
    {
        DeviceTestHost.Children.Add(TestText("Mesure la vitesse réelle du disque choisi avec les tests de référence de CrystalDiskMark : séquentiel 1 Mio (8 accès en parallèle), "
            + "aléatoire 4 Ko (32 en parallèle, puis un seul), en lecture et en écriture, sans le cache de Windows. Fichier temporaire de 1 Go supprimé à la fin ; environ 45 secondes. "
            + "Le lien PCI Express des SSD NVMe est aussi vérifié."));
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.AvailableFreeSpace > 1L << 30).ToList();
        var pick = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        string chosen = drives.FirstOrDefault()?.Name;
        var chips = new List<RadioButton>();
        foreach (var d in drives)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "BenchDrive", IsChecked = d.Name == chosen,
                                       Content = $"{d.Name.TrimEnd('\\')} {(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel)}".Trim() };
            var name = d.Name;
            rb.Checked += (_, _) => chosen = name;
            pick.Children.Add(rb);
        }
        DeviceTestHost.Children.Add(pick);
        var status = TestText("", 13, subtle: false);
        var results = new StackPanel();
        var cts = new CancellationTokenSource();
        Button go = null;
        go = TestButton("", "Lancer la mesure", async () =>
        {
            if (chosen == null) return;
            go.IsEnabled = false;
            results.Children.Clear();
            try
            {
                var r = await DiskMark.RunAsync(chosen, 1L << 30, 5, new Progress<string>(s => status.Text = s), cts.Token);
                status.Text = "Mesure terminée (valeurs en Mo/s, comme CrystalDiskMark).";
                results.Children.Add(ResultLine("Lecture séquentielle (SEQ1M Q8T1)", $"{r.SeqReadMBs:0} Mo/s", r.SeqReadMBs, 7000));
                results.Children.Add(ResultLine("Écriture séquentielle (SEQ1M Q8T1)", $"{r.SeqWriteMBs:0} Mo/s", r.SeqWriteMBs, 7000));
                results.Children.Add(ResultLine("Lecture 4K Q32 (RND4K Q32T1)", $"{r.RndQ32ReadMBs:0} Mo/s · {r.RndQ32ReadIops:0} IOPS", r.RndQ32ReadMBs, 800));
                results.Children.Add(ResultLine("Écriture 4K Q32 (RND4K Q32T1)", $"{r.RndQ32WriteMBs:0} Mo/s", r.RndQ32WriteMBs, 800));
                results.Children.Add(ResultLine("Lecture 4K Q1 (RND4K Q1T1)", $"{r.RndQ1ReadMBs:0} Mo/s · {r.RndQ1ReadIops:0} IOPS", r.RndQ1ReadMBs, 100));
                results.Children.Add(ResultLine("Écriture 4K Q1 (RND4K Q1T1)", $"{r.RndQ1WriteMBs:0} Mo/s", r.RndQ1WriteMBs, 300));
                var links = await Task.Run(DiskMark.PcieLinks);
                foreach (var l in links)
                    results.Children.Add(TestText($"{(l.Degraded ? "⚠ " : "✓ ")}{l.Disk} : {l.Text}" + (l.Degraded ? " — le SSD ne peut pas atteindre sa vitesse : emplacement M.2 partagé ou limité, voir le manuel de la carte mère." : ""), 12.5, subtle: !l.Degraded));
            }
            catch (OperationCanceledException) { status.Text = "Mesure annulée."; }
            catch (UnauthorizedAccessException) { status.Text = "Ce disque n'accepte pas l'écriture du fichier de test (droits insuffisants)."; }
            catch (Exception ex) { status.Text = "Mesure impossible : " + ex.Message; }
            go.IsEnabled = true;
        }, accent: true);
        if (drives.Count == 0) status.Text = "Aucun disque avec au moins 1 Go libre.";
        DeviceTestHost.Children.Add(go);
        DeviceTestHost.Children.Add(status);
        DeviceTestHost.Children.Add(results);
        _testCleanup = () => cts.Cancel();
    }

    /// <summary>Ligne de résultat : libellé, valeur, barre proportionnelle à une valeur de référence élevée.</summary>
    private FrameworkElement ResultLine(string label, string value, double v, double max)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        g.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var bar = Bar(out var fill, "#10B981", 12);
        bar.VerticalAlignment = VerticalAlignment.Center;
        bar.Loaded += (_, _) => fill.Width = bar.ActualWidth * Math.Min(1, Math.Log10(1 + v) / Math.Log10(1 + max));   // échelle logarithmique : lisible de 1 à 10 000
        Grid.SetColumn(bar, 1); g.Children.Add(bar);
        var t = new TextBlock { Text = value, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 2); g.Children.Add(t);
        return g;
    }

    // ================================================================== vitesse d'Internet
    private void BuildNetTest()
    {
        DeviceTestHost.Children.Add(TestText("Mesure la latence (réactivité, importante pour les jeux et les appels), le débit de téléchargement et d'envoi, "
            + "avec les serveurs de test publics de Cloudflare. Environ 20 secondes ; environ 45 Mo sont échangés."));
        var status = TestText("", 13, subtle: false);
        var results = new StackPanel();
        var cts = new CancellationTokenSource();
        Button go = null;
        go = TestButton("", "Lancer la mesure", async () =>
        {
            go.IsEnabled = false;
            results.Children.Clear();
            try
            {
                var r = await DeviceTests.NetBenchAsync(new Progress<string>(s => status.Text = s), cts.Token);
                status.Text = (r.DownMbps < 8 ? "Débit faible : vidéo en HD difficile. " : r.DownMbps < 50 ? "Débit correct pour un usage courant. " : "Très bon débit. ")
                            + (r.PingMs > 80 ? "Latence élevée : les jeux en ligne et les appels peuvent saccader (Wi-Fi éloigné ?)." : r.JitterMs > 20 ? "Latence irrégulière : le Wi-Fi peut être perturbé." : "Latence bonne pour les jeux et les appels.");
                results.Children.Add(ResultLine("Latence", $"{r.PingMs:0} ms (variation {r.JitterMs:0} ms)", Math.Max(1, 200 - r.PingMs), 200));
                results.Children.Add(ResultLine("Téléchargement", $"{r.DownMbps:0.#} Mbit/s", r.DownMbps, 2000));
                results.Children.Add(ResultLine("Envoi", $"{r.UpMbps:0.#} Mbit/s", r.UpMbps, 1000));
            }
            catch (OperationCanceledException) { status.Text = "Mesure annulée."; }
            catch (Exception ex) { status.Text = "Mesure impossible (pas de connexion ?) : " + ex.Message; }
            go.IsEnabled = true;
        }, accent: true);
        DeviceTestHost.Children.Add(go);
        DeviceTestHost.Children.Add(status);
        DeviceTestHost.Children.Add(results);
        _testCleanup = () => cts.Cancel();
    }
}

/// <summary>
/// Test de l'écran en plein écran : couleurs unies pour repérer les pixels morts ou bloqués, puis un test de fluidité
/// (carré en mouvement, images par seconde et images sautées). Clic ou → : suivant · ← : précédent · Échap : quitter.
/// </summary>
public sealed class ScreenTestWindow : Window
{
    private static readonly (Color color, string name)[] Steps =
    {
        (Colors.Black, "Noir : un point lumineux est un pixel bloqué"), (Colors.White, "Blanc : un point noir est un pixel mort"),
        (Color.FromRgb(255, 0, 0), "Rouge"), (Color.FromRgb(0, 255, 0), "Vert"), (Color.FromRgb(0, 0, 255), "Bleu"),
        (Color.FromRgb(128, 128, 128), "Gris : taches ou zones plus claires (fuite de lumière)")
    };
    private int _step;
    private readonly Grid _root = new();
    private readonly TextBlock _hint = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40) };
    private readonly Canvas _motion = new() { Visibility = Visibility.Collapsed };
    private readonly Rectangle _square = new() { Width = 120, Height = 120, Fill = Brushes.White };
    private readonly TextBlock _fps = new() { FontSize = 18, Foreground = Brushes.White, Margin = new Thickness(30) };
    private TimeSpan _lastFrame;
    private int _frames, _skipped;
    private DateTime _fpsAt = DateTime.UtcNow;
    private double _x, _rate;

    public ScreenTestWindow(Window owner)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.None;
        // Sur l'écran de Centre PC, puis plein écran
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = owner.Left + 20; Top = owner.Top + 20; Width = 400; Height = 300;
        Loaded += (_, _) => WindowState = WindowState.Maximized;
        _motion.Children.Add(_square);
        _motion.Children.Add(_fps);
        _root.Children.Add(_motion);
        _root.Children.Add(_hint);
        Content = _root;
        MouseLeftButtonUp += (_, _) => Move(1);
        MouseRightButtonUp += (_, _) => Move(-1);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Left or Key.Back) Move(-1);
            else Move(1);
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
        Show(0);
    }

    private void Move(int d)
    {
        int next = _step + d;
        if (next < 0) return;
        if (next > Steps.Length) { Close(); return; }
        Show(next);
    }

    private void Show(int step)
    {
        _step = step;
        CompositionTarget.Rendering -= OnFrame;
        if (step < Steps.Length)
        {
            var (c, name) = Steps[step];
            _motion.Visibility = Visibility.Collapsed;
            Background = new SolidColorBrush(c);
            _hint.Foreground = c.R + c.G + c.B > 380 ? Brushes.Black : Brushes.White;
            _hint.Text = $"{step + 1}/{Steps.Length + 1} · {name}  —  clic ou → : suivant · ← : précédent · Échap : quitter";
        }
        else
        {
            Background = Brushes.Black;
            _motion.Visibility = Visibility.Visible;
            _hint.Foreground = Brushes.White;
            _hint.Text = "Fluidité : le carré doit glisser sans à-coups · Échap ou clic : terminer";
            _frames = _skipped = 0;
            _lastFrame = TimeSpan.Zero;
            _fpsAt = DateTime.UtcNow;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 0 : (t - _lastFrame).TotalSeconds;
        _lastFrame = t;
        _frames++;
        if (dt > 0)
        {
            _x = (_x + dt * ActualWidth * 0.5) % Math.Max(1, ActualWidth - _square.Width);
            Canvas.SetLeft(_square, _x);
            Canvas.SetTop(_square, ActualHeight / 2 - _square.Height / 2);
        }
        var elapsed = (DateTime.UtcNow - _fpsAt).TotalSeconds;
        // Une image qui arrive bien plus tard que prévu = une saccade visible
        if (_rate > 0 && dt > 1.6 / _rate) _skipped++;
        if (elapsed >= 1)
        {
            _rate = _frames / elapsed;
            _fps.Text = $"{_rate:0} images par seconde · {_skipped} saccade(s)";
            _frames = 0;
            _fpsAt = DateTime.UtcNow;
        }
    }
}
