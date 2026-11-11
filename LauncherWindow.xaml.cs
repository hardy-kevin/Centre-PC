using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Lanceur rapide : petit panneau ouvert par l'icône près de l'horloge ou le raccourci clavier.
/// La recherche a le focus tout de suite ; il disparaît dès qu'une application est lancée, qu'on clique ailleurs ou Échap.
/// Il reste chargé (caché) pour s'afficher sans attendre.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly Func<IEnumerable<AppItem>> _apps;
    private readonly Action<AppItem> _launch;
    private readonly Action _openMain;
    private readonly List<(AppItem app, Border row)> _results = new();
    private int _sel;
    private DateTime _hiddenAt;

    private static readonly CompareInfo Cmp = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;
    private const CompareOptions CmpOpts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Capture d'écran (« zone », « delay », « full », « folder ») : le lanceur se cache d'abord.</summary>
    public Action<string> Capture { get; set; }

    private void Capture_Click(object sender, RoutedEventArgs e) => RunCapture("zone");

    private void RunCapture(string mode)
    {
        HideLauncher();
        Capture?.Invoke(mode);
    }

    private void BuildCaptureMenu()
    {
        var menu = new ContextMenu();
        foreach (var (label, mode) in new[] { ("Capturer une zone", "zone"), ("Capturer une zone dans 5 secondes", "delay"), ("Capturer l'écran entier", "full"), ("Ouvrir le dossier des captures", "folder") })
        {
            var m = mode;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => RunCapture(m);
            menu.Items.Add(item);
        }
        // Le menu ouvert ne doit pas fermer le lanceur (perte de focus) avant le choix
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        CaptureButton.ContextMenu = menu;
    }

    private bool _menuOpen;

    /// <summary>Raccourci affiché en bas (null : aucun).</summary>
    public string HotkeyLabel { get; set; }

    public LauncherWindow(Func<IEnumerable<AppItem>> apps, Action<AppItem> launch, Action openMain)
    {
        _apps = apps;
        _launch = launch;
        _openMain = openMain;
        InitializeComponent();
        Deactivated += (_, _) => { if (!_menuOpen) HideLauncher(); };
        BuildCaptureMenu();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (Query.Text.Length > 0) Query.Clear(); else HideLauncher(); e.Handled = true; } };
        new WindowInteropHelper(this).EnsureHandle();   // prêt à s'afficher instantanément
    }

    /// <summary>Ouvre ou referme le lanceur. Près de l'horloge depuis l'icône, au centre de l'écran depuis le raccourci.</summary>
    public void Toggle(bool nearTray)
    {
        if (IsVisible) { HideLauncher(); return; }
        // Le clic sur l'icône a d'abord fait perdre le focus au lanceur (donc l'a caché) : ce clic voulait le fermer
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300) return;
        ShowLauncher(nearTray);
    }

    public void ShowLauncher(bool nearTray)
    {
        Query.Text = "";
        Build();
        Place(nearTray);
        Show();
        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Query.Focus();
        Keyboard.Focus(Query);
    }

    public void HideLauncher()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    // ------------------------------------------------------------------ contenu
    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Build();

    private void Build()
    {
        Host.Children.Clear();
        _results.Clear();
        _sel = 0;
        var q = Query.Text.Trim();
        var apps = _apps().ToList();
        Hint.Text = q.Length > 0 ? "↑ ↓ pour choisir · Entrée pour ouvrir · Échap pour effacer"
                  : "Tapez le nom d'une application · Échap pour fermer" + (HotkeyLabel != null ? $" · {HotkeyLabel} pour rouvrir" : "");
        if (q.Length > 0) BuildResults(apps, q);
        else BuildBrowse(apps);
        Scroll.ScrollToTop();
    }

    /// <summary>Recherche : chaque application (même membre d'une suite), le début du nom d'abord, puis les plus utilisées.</summary>
    private void BuildResults(List<AppItem> apps, string q)
    {
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var recent = Settings.Current.RecentApps;
        var found = apps
            .Where(a => words.All(w => Cmp.IndexOf(a.Label + " " + a.Theme + " " + a.Publisher, w, CmpOpts) >= 0))
            .Select(a =>
            {
                int score = Cmp.IsPrefix(a.Label, q, CmpOpts) ? 0
                          : a.Label.Split(' ', '-', '_').Any(p => Cmp.IsPrefix(p, words[0], CmpOpts)) ? 1 : 2;
                int r = recent.FindIndex(n => n.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
                return (a, score, rank: r < 0 ? 999 : r);
            })
            .OrderBy(x => x.score).ThenBy(x => x.rank).ThenBy(x => x.a.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(40).Select(x => x.a).ToList();
        if (found.Count == 0)
        {
            Host.Children.Add(new TextBlock { Text = "Aucune application ne correspond.", Style = (Style)FindResource("Subtle"), Margin = new Thickness(4, 10, 0, 0) });
            return;
        }
        foreach (var a in found)
        {
            var row = ResultRow(a);
            _results.Add((a, row));
            Host.Children.Add(row);
        }
        Select(0);
    }

    private Border ResultRow(AppItem a)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(IconOf(a, 28));
        var name = new TextBlock { Text = a.Label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                                   TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var cat = new TextBlock { Text = a.Theme, Style = (Style)FindResource("Subtle"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(cat, 2); grid.Children.Add(cat);
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7), Background = Brushes.Transparent, Child = grid, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => Select(_results.FindIndex(r => r.row == row));
        row.MouseLeftButtonUp += (_, _) => Open(a, row);
        return row;
    }

    private void Select(int i)
    {
        if (_results.Count == 0) return;
        _sel = Math.Clamp(i, 0, _results.Count - 1);
        var on = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x5C, 0xFF));
        for (int k = 0; k < _results.Count; k++) _results[k].row.Background = k == _sel ? on : Brushes.Transparent;
        _results[_sel].row.BringIntoView();
    }

    /// <summary>Sans recherche : favoris, récents, puis toutes les applications par catégorie (suites regroupées).</summary>
    private void BuildBrowse(List<AppItem> apps)
    {
        var favs = apps.Where(a => Settings.Current.Favorites.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
                       .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (favs.Count > 0) Section("Favoris", "", (Brush)new BrushConverter().ConvertFromString("#F5B83D"), favs);

        var recent = Settings.Current.RecentApps
            .Select(n => apps.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Where(a => a != null && !favs.Contains(a)).Take(7).ToList();
        if (recent.Count > 0) Section("Récemment utilisées", "", (Brush)FindResource("Accent"), recent);

        foreach (var g in ThemeGroup.Build(AppSuites.Group(apps)))
            Section(g.Name, g.Glyph, g.Brush, g.Items);
    }

    private void Section(string title, string glyph, Brush brush, IEnumerable<AppItem> items)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, Host.Children.Count == 0 ? 2 : 14, 0, 6) };
        head.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13, Foreground = brush, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Host.Children.Add(head);
        var wrap = new WrapPanel();
        foreach (var a in items) wrap.Children.Add(Tile(a));
        Host.Children.Add(wrap);
    }

    private Border Tile(AppItem a)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = IconOf(a, 34);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(icon);
        sp.Children.Add(new TextBlock
        {
            Text = a.Label, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 32,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0)
        });
        var content = new Grid();
        content.Children.Add(sp);
        if (a.IsSuite)
        {
            // Petit badge : nombre d'applications de la suite
            content.Children.Add(new Border
            {
                Background = (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 0, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, 8, 0),
                Child = new TextBlock { Text = a.Members.Count.ToString(), FontSize = 10, Foreground = Brushes.White }
            });
        }
        var tile = new Border
        {
            Width = 98, Height = 88, CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 8, 6, 4), Margin = new Thickness(0, 0, 4, 4),
            Background = Brushes.Transparent, Child = content, Cursor = Cursors.Hand,
            ToolTip = a.IsSuite ? $"{a.Label} · {a.Members.Count} applications" : a.Label
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        tile.MouseEnter += (_, _) => tile.Background = hover;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) => Open(a, tile);
        return tile;
    }

    private FrameworkElement IconOf(AppItem a, double size)
    {
        if (a.Icon != null)
        {
            var img = new Image { Source = a.Icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = a.ThemeBrush, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = a.Initial, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = size / 2.2,
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    /// <summary>Application : lancée, et le lanceur disparaît. Suite : petit menu de ses applications.</summary>
    private void Open(AppItem a, FrameworkElement anchor)
    {
        if (!a.IsSuite) { Run(a); return; }
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var m in a.Members.OrderBy(m => m.IsCompanion).ThenBy(m => m.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var app = m;
            var it = new MenuItem { Header = m.Label };
            if (m.Icon != null) it.Icon = new Image { Source = m.Icon, Width = 18, Height = 18 };
            it.Click += (_, _) => Run(app);
            menu.Items.Add(it);
        }
        menu.IsOpen = true;
    }

    private void Run(AppItem a)
    {
        HideLauncher();
        _launch(a);
    }

    // ------------------------------------------------------------------ clavier
    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (_results.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down: Select(_sel + 1); e.Handled = true; break;
            case Key.Up: Select(_sel - 1); e.Handled = true; break;
            case Key.Enter: Run(_results[_sel].app); e.Handled = true; break;
        }
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        HideLauncher();
        _openMain();
    }

    // ------------------------------------------------------------------ position
    /// <summary>Sur l'écran où se trouve la souris : au-dessus de l'horloge (icône) ou en haut au centre (raccourci).</summary>
    private void Place(bool nearTray)
    {
        GetCursorPos(out var pt);
        var mon = MonitorFromPoint(pt, 2);   // MONITOR_DEFAULTTONEAREST
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double l = info.rcWork.Left / dpi.DpiScaleX, t = info.rcWork.Top / dpi.DpiScaleY,
               r = info.rcWork.Right / dpi.DpiScaleX, b = info.rcWork.Bottom / dpi.DpiScaleY;
        if (nearTray)
        {
            Left = r - Width;
            Top = b - Height;
        }
        else
        {
            Left = l + (r - l - Width) / 2;
            Top = t + Math.Max(0, (b - t - Height) * 0.3);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
