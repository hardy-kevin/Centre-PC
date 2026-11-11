using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CentrePC.Core;
using Microsoft.Win32;

namespace CentrePC;

/// <summary>
/// Accès rapide : icône près de l'horloge + raccourci clavier ouvrant un petit lanceur d'applications.
/// Proposé une fois, désactivable dans Réglages. Fermer la fenêtre garde alors Centre PC disponible en arrière-plan ;
/// « Quitter » (clic droit sur l'icône) l'arrête vraiment.
/// </summary>
public partial class MainWindow
{
    private TrayHost _tray;
    private LauncherWindow _launcher;
    private bool _quitting;

    private static HotkeyCombo CurrentHotkey => HotkeyCombo.Deserialize(Settings.Current.QuickAccessHotkey);

    /// <summary>Met en place (ou retire) l'icône, le raccourci et le lanceur selon les réglages.</summary>
    private void ApplyQuickAccess()
    {
        var cfg = Settings.Current;
        if (cfg.QuickAccess)
        {
            try
            {
                _launcher ??= new LauncherWindow(() => _apps.Where(a => !Settings.IsHidden(a.Name)), Launch, ShowMainWindow) { Capture = CaptureFromLauncher };
                if (_tray == null)
                {
                    _tray = new TrayHost();
                    _tray.LeftClick += () => _launcher.Toggle(nearTray: true);
                    _tray.OpenLauncher += () => _launcher.ShowLauncher(nearTray: true);
                    _tray.Hotkey += () => _launcher.Toggle(nearTray: false);
                    _tray.OpenMain += ShowMainWindow;
                    _tray.Quit += QuitApp;
                    _tray.Add();
                }
                var hk = CurrentHotkey;
                bool ok = _tray.SetHotkey(hk);
                _launcher.HotkeyLabel = ok && !hk.IsNone ? hk.Label : null;
                if (!ok) ShowToast($"Le raccourci {hk.Label} est déjà utilisé par une autre application : choisissez-en un autre dans Réglages.");
            }
            catch (Exception ex) { Log.Error("Accès rapide", ex); }
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
            _launcher?.Close();
            _launcher = null;
        }
        ApplyStartWithWindows();
    }

    /// <summary>Lancement à l'ouverture de session (copie installée seulement), avec « --tray » : aucune fenêtre ne s'ouvre.</summary>
    private static void ApplyStartWithWindows()
    {
        if (Native.IsPackaged || !SelfInstall.IsInstalledCopy) return;
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (Settings.Current.QuickAccess && Settings.Current.StartWithWindows)
                run.SetValue("CentrePC", $"\"{Environment.ProcessPath}\" --tray");
            else if (run.GetValue("CentrePC") != null)
                run.DeleteValue("CentrePC", false);
        }
        catch (Exception ex) { Log.Error("Démarrage avec Windows", ex); }
    }

    /// <summary>Démarrage avec Windows : tout est chargé, mais seule l'icône près de l'horloge apparaît.</summary>
    public void StartInBackground()
    {
        new WindowInteropHelper(this).EnsureHandle();
        _ = StartupAsync();
    }

    private void ShowMainWindow()
    {
        _launcher?.HideLauncher();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;     // contourne la protection de Windows contre le vol du premier plan
        Topmost = false;
        Focus();
    }

    private void QuitApp()
    {
        _quitting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Accès rapide actif : fermer la fenêtre la cache, Centre PC reste disponible près de l'horloge
        if (!_quitting && _tray != null && Settings.Current.QuickAccess)
        {
            e.Cancel = true;
            Hide();
            if (!Settings.Current.TrayHintShown)
            {
                var hk = _launcher?.HotkeyLabel;
                _tray.ShowBalloon("Centre PC reste disponible ici",
                    "Cliquez sur cette icône" + (hk != null ? $" ou appuyez sur {hk}" : "") + " pour ouvrir vos applications. Clic droit › Quitter pour l'arrêter.");
                Settings.Current.TrayHintShown = true;
                Settings.Save();
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    /// <summary>Applications lancées récemment (affichées en tête du lanceur).</summary>
    private static void RememberLaunch(AppItem a)
    {
        var recent = Settings.Current.RecentApps;
        recent.RemoveAll(n => n.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, a.Name);
        if (recent.Count > 12) recent.RemoveRange(12, recent.Count - 12);
        Settings.Save();
    }

    // ------------------------------------------------------------------ proposition (une seule fois)
    /// <summary>
    /// Premier lancement depuis l'arrivée de l'accès rapide : on demande à l'utilisateur s'il le veut (une seule fois,
    /// après l'accueil, et seulement quand la fenêtre est affichée).
    /// </summary>
    private void MaybeOfferQuickAccess()
    {
        var cfg = Settings.Current;
        if (cfg.QuickAccessAsked || !cfg.OnboardingDone || !IsVisible || WelcomeOverlay.Visibility == Visibility.Visible) return;
        // Raccourci proposé : le premier vraiment libre (Ctrl + Alt + Espace est celui de Claude, Alt + Espace celui de ChatGPT…)
        try
        {
            var scanned = HotkeyConflicts.ScanKnownApps();
            var names = InstalledNames();
            var free = new[] { new HotkeyCombo(HotkeyCombo.Ctrl | HotkeyCombo.Alt, 0x20), new HotkeyCombo(HotkeyCombo.Ctrl | HotkeyCombo.Shift, 0x20),
                               new HotkeyCombo(HotkeyCombo.Alt | HotkeyCombo.Shift, 0x20), new HotkeyCombo(HotkeyCombo.Ctrl | HotkeyCombo.Alt | HotkeyCombo.Shift, 0x20) }
                .FirstOrDefault(c => HotkeyConflicts.Check(c, null, scanned, names).State is HotkeyState.Free or HotkeyState.Note);
            if (free != null) cfg.QuickAccessHotkey = free.Serialize();
        }
        catch (Exception ex) { Log.Error("Raccourci proposé", ex); }
        var hk = CurrentHotkey;
        QuickOfferText.Text = "Un petit lanceur s'ouvre d'un geste : vous tapez le nom, l'application s'ouvre, le lanceur disparaît. "
            + "Ouvrez-le avec l'icône près de l'horloge ou avec ce raccourci :";
        QuickOfferKeys.Children.Clear();
        foreach (var part in hk.Label.Split(" + "))
        {
            if (QuickOfferKeys.Children.Count > 0)
                QuickOfferKeys.Children.Add(new TextBlock { Text = "+", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            QuickOfferKeys.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("Surface2"), BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
                BorderThickness = new Thickness(1, 1, 1, 3), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6, 14, 6),
                Child = new TextBlock { Text = part, FontWeight = FontWeights.SemiBold, FontSize = 14 }
            });
        }
        QuickOfferStartup.Visibility = SelfInstall.IsInstalledCopy && !Native.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
        QuickOfferOverlay.Visibility = Visibility.Visible;
    }

    private void QuickOfferYes_Click(object sender, RoutedEventArgs e)
    {
        var cfg = Settings.Current;
        cfg.QuickAccess = true;
        cfg.QuickAccessAsked = true;
        cfg.StartWithWindows = QuickOfferStartup.Visibility == Visibility.Visible && QuickOfferStartup.IsChecked == true;
        Settings.Save();
        QuickOfferOverlay.Visibility = Visibility.Collapsed;
        ApplyQuickAccess();
        InitQuickAccessSettings();
        var hk = _launcher?.HotkeyLabel;
        ShowToast("Accès rapide activé" + (hk != null ? $" : essayez {hk}." : " : cliquez sur l'icône près de l'horloge."));
    }

    private void QuickOfferNo_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.QuickAccessAsked = true;
        Settings.Save();
        QuickOfferOverlay.Visibility = Visibility.Collapsed;
        ShowToast("D'accord. Vous pourrez l'activer à tout moment dans Réglages › Accès rapide.");
    }

    // ------------------------------------------------------------------ Réglages : raccourci, conflits
    private bool _quickInit;
    private bool _capturing;
    private Button _captureButton;

    private void InitQuickAccessSettings()
    {
        _quickInit = true;
        var cfg = Settings.Current;
        QuickAccessCheck.IsChecked = cfg.QuickAccess;
        bool canStartup = SelfInstall.IsInstalledCopy && !Native.IsPackaged;
        StartWithWindowsCheck.IsChecked = cfg.StartWithWindows;
        StartWithWindowsCheck.IsEnabled = cfg.QuickAccess && canStartup;
        StartWithWindowsCheck.ToolTip = canStartup ? null : "Disponible dans la version installée de Centre PC";
        QuickOptions.IsEnabled = cfg.QuickAccess;
        QuickOptions.Opacity = cfg.QuickAccess ? 1 : 0.5;
        _quickInit = false;
        BuildHotkeyList();
    }

    /// <summary>
    /// Liste des raccourcis avec leur état : vert disponible, orange utilisé par une application connue (libérable) ou
    /// probablement utilisé, rouge réservé ou pris. Plus un raccourci au choix, saisi au clavier.
    /// </summary>
    private void BuildHotkeyList()
    {
        QuickHotkeys.Children.Clear();
        if (!Settings.Current.QuickAccess) return;
        var current = CurrentHotkey;
        List<HotkeyOwner> scanned;
        try { scanned = HotkeyConflicts.ScanKnownApps(); } catch (Exception ex) { Log.Error("Raccourcis", ex); scanned = new(); }
        var names = InstalledNames();
        var list = HotkeyConflicts.Suggestions.ToList();
        if (!current.IsNone && !list.Contains(current)) list.Insert(0, current);   // raccourci personnalisé en cours
        foreach (var k in list.Append(HotkeyCombo.None))
            QuickHotkeys.Children.Add(HotkeyRow(k, HotkeyConflicts.Check(k, current, scanned, names), k == current));

        // Raccourci au choix
        var custom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        custom.ColumnDefinitions.Add(new ColumnDefinition());
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        custom.Children.Add(new TextBlock { Text = "Un autre raccourci ? Cliquez sur « Enregistrer… » puis appuyez sur la combinaison voulue : elle est vérifiée de la même façon.",
                                            Style = (Style)FindResource("Subtle"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        _captureButton = new Button { Style = (Style)FindResource("BaseButton"), Content = "Enregistrer…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(12, 0, 0, 0) };
        _captureButton.Click += (_, _) => StartCapture();
        _captureButton.PreviewKeyDown += Capture_KeyDown;
        _captureButton.LostKeyboardFocus += (_, _) => StopCapture();
        Grid.SetColumn(_captureButton, 1);
        custom.Children.Add(_captureButton);
        QuickHotkeys.Children.Add(custom);

        int freed = Settings.Current.HotkeyBackups.Count;
        if (freed > 0)
            QuickHotkeys.Children.Add(new TextBlock
            {
                Text = $"Réglages d'autres applications modifiés pour ce raccourci : {string.Join(", ", Settings.Current.HotkeyBackups.Select(b => b.App).Distinct())}. "
                     + "Ils seront remis à l'identique si vous changez de raccourci, désactivez l'accès rapide ou désinstallez Centre PC.",
                Style = (Style)FindResource("Subtle"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
    }

    private FrameworkElement HotkeyRow(HotkeyCombo k, HotkeyCheck check, bool selected)
    {
        var (color, glyph) = check.State switch
        {
            HotkeyState.Free => ("#10B981", ""),
            HotkeyState.Note => ("#10B981", ""),
            HotkeyState.Freeable => ("#F59E0B", ""),
            HotkeyState.LikelyUsed => ("#F59E0B", ""),
            _ => ("#EF4444", "")
        };
        var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color);
        bool blocked = check.State == HotkeyState.Blocked;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Pastille de sélection
        grid.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(selected ? 5 : 1.5), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = selected ? (System.Windows.Media.Brush)FindResource("Accent") : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x5A, 0x6E))
        });
        var name = new TextBlock { Text = k.Label, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var state = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 12, Foreground = brush, VerticalAlignment = VerticalAlignment.Center });
        var status = new TextBlock { Text = selected && check.State is HotkeyState.Free or HotkeyState.Note ? "Utilisé par Centre PC" + (check.State == HotkeyState.Note ? " · " + check.Text : "") : check.Text,
                                     Foreground = brush, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        state.Children.Add(status);
        Grid.SetColumn(state, 2); grid.Children.Add(state);
        if (check.UnknownOwner)
        {
            // Propriétaire inconnu : l'utilisateur peut le faire identifier en déclenchant le raccourci
            var identify = new Button { Style = (Style)FindResource("BaseButton"), Content = "Identifier…", Padding = new Thickness(12, 4, 12, 4), Cursor = Cursors.Hand,
                                        Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                        ToolTip = "Vérifie qui utilise ce raccourci en le déclenchant : l'action de l'autre application se produit normalement" };
            identify.Click += (_, _) => IdentifyHotkey(k, status, identify);
            Grid.SetColumn(identify, 3); grid.Children.Add(identify);
        }

        var row = new Border
        {
            Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 2), Child = grid,
            Background = selected ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x7C, 0x5C, 0xFF)) : System.Windows.Media.Brushes.Transparent,
            Cursor = blocked ? Cursors.No : Cursors.Hand, Opacity = blocked && !check.UnknownOwner ? 0.55 : 1,
            ToolTip = blocked ? check.Text + " : ce raccourci n'est pas proposé." : null
        };
        if (!blocked && !selected) row.MouseLeftButtonUp += (_, _) => ChooseHotkey(k);
        return row;
    }

    /// <summary>
    /// « Identifier… » : on prévient que la vérification passe par le déclenchement du raccourci (l'action de l'autre
    /// application aura lieu), puis on regarde quelle fenêtre réagit quand l'utilisateur appuie.
    /// </summary>
    private async void IdentifyHotkey(HotkeyCombo k, TextBlock status, Button button)
    {
        if (MessageBox.Show($"Pour savoir quelle application utilise {k.Label}, Centre PC vérifie en déclenchant ce raccourci.\n\n"
                            + $"Après « OK », appuyez sur {k.Label}. L'action de l'autre application va se produire normalement "
                            + "(ouverture d'une fenêtre, capture, micro coupé…). Centre PC regarde seulement quelle fenêtre réagit, sans rien modifier.",
                            "Centre PC · vérification du raccourci", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        button.IsEnabled = false;
        status.Text = $"Vérification en cours : appuyez maintenant sur {k.Label} (l'action va se déclencher)…";
        status.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        var (pressed, app) = await HotkeyProbe.IdentifyAsync(k);
        ShowMainWindow();   // l'autre application a pu passer devant : on revient montrer le résultat
        if (!pressed) ShowToast($"{k.Label} n'a pas été pressé : vérification annulée.");
        else if (app == null) ShowToast($"Aucune fenêtre n'a réagi à {k.Label} : l'application qui l'utilise agit en arrière-plan (micro, capture…) et ne peut pas être nommée.");
        else
        {
            Settings.Current.HotkeyIdentified[k.Serialize()] = app;
            Settings.Save();
            ShowToast($"{k.Label} est utilisé par {app}.");
        }
        BuildHotkeyList();
    }

    /// <summary>
    /// Choix d'un raccourci : les réglages libérés pour l'ancien sont remis, puis, si une application connue utilise
    /// le nouveau, Centre PC propose de le lui libérer (réglage d'origine sauvegardé).
    /// </summary>
    private void ChooseHotkey(HotkeyCombo k)
    {
        var current = CurrentHotkey;
        if (k == current) return;
        // L'ancien choix est abandonné : les autres applications retrouvent leur réglage d'origine
        int restored = HotkeyConflicts.RestoreAll(keep: k);
        var scanned = HotkeyConflicts.ScanKnownApps();
        var check = HotkeyConflicts.Check(k, current, scanned, InstalledNames());
        string done = null;
        switch (check.State)
        {
            case HotkeyState.Blocked:
                ShowToast($"{k.Label} : {check.Text}.");
                BuildHotkeyList();
                return;
            case HotkeyState.LikelyUsed:
                if (MessageBox.Show($"{k.Label} : {check.Text}.\n\nCentre PC ne peut pas modifier le réglage de cette application. Utiliser ce raccourci quand même ?",
                                    "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                { BuildHotkeyList(); return; }
                break;
            case HotkeyState.Freeable:
                var apps = string.Join(", ", check.Owners.Select(o => o.App).Distinct());
                bool restarts = check.Owners.Any(o => o.Processes.Length > 0);
                if (MessageBox.Show($"{k.Label} est utilisé par {apps}.\n\nLibérer ce raccourci pour Centre PC ? {apps} recevra un autre raccourci libre"
                                    + (restarts ? " et sera redémarré" : "") + ".\n\nSon réglage d'origine est sauvegardé : il sera remis automatiquement si vous "
                                    + "choisissez un autre raccourci, désactivez l'accès rapide ou désinstallez Centre PC.",
                                    "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                { BuildHotkeyList(); return; }
                var (changes, error) = HotkeyConflicts.Free(check, k, scanned);
                if (error != null) { ShowToast("Impossible de libérer ce raccourci : " + error); BuildHotkeyList(); return; }
                done = string.Join(" · ", changes.Select(c => $"{c.app} utilise maintenant {c.now.Label}"));
                break;
        }
        Settings.Current.QuickAccessHotkey = k.Serialize();
        Settings.Save();
        ApplyQuickAccess();
        BuildHotkeyList();
        // L'ancien raccourci est rendu : l'application à qui il revient ne le reprend qu'à son prochain démarrage
        var previousOwner = current.IsNone ? null : HotkeyConflicts.KnownDefaultOwner(current, InstalledNames());
        if (previousOwner != null)
            done = (done != null ? done + ". " : "") + $"{current.Label} est libre : redémarrez {previousOwner.Split(' ')[0]} pour qu'il le récupère";
        ShowToast((k.IsNone ? "Aucun raccourci : le lanceur s'ouvre depuis l'icône près de l'horloge" : $"Raccourci : {k.Label}")
                  + (done != null ? ". " + done : restored > 0 ? ". Réglage d'origine remis dans l'autre application." : ""));
    }

    // ------------------------------------------------------------------ saisie d'un raccourci au clavier
    private void StartCapture()
    {
        _capturing = true;
        _tray?.PauseHotkey(true, CurrentHotkey);   // le raccourci actuel ne doit pas ouvrir le lanceur pendant la saisie
        _captureButton.Content = "Appuyez sur la combinaison… (Échap : annuler)";
        _captureButton.Focus();
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        _tray?.PauseHotkey(false, CurrentHotkey);
        if (_captureButton != null) _captureButton.Content = "Enregistrer…";
    }

    private void Capture_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { StopCapture(); return; }
        var combo = HotkeyCombo.FromKeyboard(key, Keyboard.Modifiers);
        if (combo == null)
        {
            // Seules des touches de modification sont enfoncées : on les montre en attendant la touche
            var mods = new List<string>();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods.Add("Windows");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods.Add("Maj");
            _captureButton.Content = string.Join(" + ", mods) + " + …";
            return;
        }
        StopCapture();
        ChooseHotkey(combo);
    }

    private void QuickAccess_Changed(object sender, RoutedEventArgs e)
    {
        if (_quickInit) return;
        Settings.Current.QuickAccess = QuickAccessCheck.IsChecked == true;
        Settings.Current.QuickAccessAsked = true;
        Settings.Save();
        // Accès rapide désactivé : les autres applications retrouvent leurs raccourcis d'origine
        if (!Settings.Current.QuickAccess && HotkeyConflicts.RestoreAll() > 0)
            ShowToast("Réglages d'origine remis dans les applications dont le raccourci avait été libéré.");
        ApplyQuickAccess();
        InitQuickAccessSettings();
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_quickInit) return;
        Settings.Current.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        Settings.Save();
        ApplyStartWithWindows();
    }

    private void QuickTry_Click(object sender, RoutedEventArgs e) => _launcher?.ShowLauncher(nearTray: false);
}
