using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Accueil : au premier lancement, ou quand les conditions d'utilisation changent, l'utilisateur les accepte
/// explicitement (ou quitte), puis règle en un écran ce qui rend Centre PC utile au quotidien : le lanceur au raccourci
/// clavier, le démarrage avec Windows en arrière-plan et les retours anonymes. Les deux premiers sont cochés d'avance
/// (recommandés, sans enjeu de confidentialité) ; pour les retours, rien n'est coché : il faut répondre oui ou non,
/// et refuser ne retire aucune fonction.
/// </summary>
public partial class MainWindow
{
    /// <summary>Présentation rouverte depuis Réglages : simple lecture, les conditions sont déjà acceptées.</summary>
    private bool _welcomeReplay;

    /// <summary>Au démarrage : conditions à accepter (jamais acceptées ou nouvelle version) → accueil.</summary>
    private void ShowWelcomeIfNeeded()
    {
        var cfg = Settings.Current;
        if (!Terms.NeedsAcceptance(cfg)) return;
        OpenWelcome(update: Terms.IsUpdate(cfg), replay: false);
        _launchScreen = true;
    }

    /// <summary>
    /// Premier lancement : présentation, puis conditions. Conditions modifiées : directement les conditions, avec ce qui
    /// change. Présentation rouverte depuis Réglages : présentation seule.
    /// </summary>
    private void OpenWelcome(bool update, bool replay)
    {
        _welcomeReplay = replay;
        WelcomeSetupPanel.Visibility = Visibility.Collapsed;
        WelcomeOverlay.Visibility = Visibility.Visible;
        if (update && !replay) { ShowTerms(update: true); return; }
        WelcomeTermsPanel.Visibility = Visibility.Collapsed;
        WelcomeIntroPanel.Visibility = Visibility.Visible;
        WelcomeNextButton.Content = replay ? "Fermer" : "Continuer";
        WelcomeNextButton.Focus();
    }

    private void ShowWelcome_Click(object sender, RoutedEventArgs e) => OpenWelcome(update: false, replay: true);

    private void WelcomeNext_Click(object sender, RoutedEventArgs e)
    {
        if (_welcomeReplay)
        {
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            FocusStart();
            return;
        }
        ShowTerms(update: false);
    }

    // ------------------------------------------------------------------ étape 2 : conditions, en entier
    private bool _termsRead;

    private void ShowTerms(bool update)
    {
        WelcomeIntroPanel.Visibility = Visibility.Collapsed;
        WelcomeTermsPanel.Visibility = Visibility.Visible;
        TermsTitle.Text = update ? "Les conditions d'utilisation évoluent" : "Conditions d'utilisation";
        TermsSubtitle.Text = update
            ? "Merci d'utiliser Centre PC. Voici ce qui change ; le texte complet suit, confidentialité et usage professionnel compris."
            : "Lisez-les jusqu'en bas pour pouvoir les accepter. La confidentialité et l'usage professionnel en font partie.";
        WelcomeChanges.Children.Clear();
        WelcomeChanges.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        if (update)
            foreach (var change in Terms.Changes)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = "\uE73E", Style = (Style)FindResource("Glyph"), FontSize = 13, Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
                                                 VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0) });
                var text = new TextBlock { Text = change, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                WelcomeChanges.Children.Add(row);
            }
        // Plus le résumé est long, moins il reste de place au texte (la fenêtre doit tenir sur un petit écran)
        TermsScroll.MaxHeight = update ? 300 : 430;
        BuildTermsDocument();
        _termsRead = false;
        WelcomeAcceptButton.IsEnabled = false;
        TermsHint.Text = "Faites défiler le texte jusqu'en bas pour pouvoir accepter.";
        TermsScroll.ScrollToTop();
        // Lecteur d'écran : il lit le texte sans le faire défiler à l'écran, l'acceptation ne doit pas en dépendre
        if (Accessibility.ScreenReaderRunning) MarkTermsRead();
        // Texte entièrement visible (grand écran) : rien à faire défiler
        Dispatcher.InvokeAsync(() => { if (TermsScroll.ScrollableHeight <= 1) MarkTermsRead(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        TermsScroll.Focus();
    }

    private void TermsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_termsRead && TermsScroll.VerticalOffset >= TermsScroll.ScrollableHeight - 8) MarkTermsRead();
    }

    private void MarkTermsRead()
    {
        if (_termsRead) return;
        _termsRead = true;
        WelcomeAcceptButton.IsEnabled = true;
        TermsHint.Text = "Merci de les avoir lues. Gardez-les à portée de main : Réglages › Centre PC.";
    }

    /// <summary>
    /// Documents joints aux conditions : là où une section renvoie à l'un d'eux (lien Markdown vers le fichier), il est
    /// inséré en entier à la fin de cette section, pour que tout se lise dans l'appli, sans passer par GitHub.
    /// </summary>
    private static readonly (string File, string Title, string Anchor)[] TermsAnnexes =
    {
        ("CONFIDENTIALITE.md", "Confidentialité", "confidentialite"),
        ("USAGE-PROFESSIONNEL.md", "Usage professionnel", "pro"),
    };

    private void BuildTermsDocument() => BuildTermsDocument(TermsDoc);

    /// <summary>
    /// Texte des conditions intégré à Centre PC (le même fichier que le dépôt), avec la confidentialité et l'usage
    /// professionnel insérés là où les conditions y renvoient. Renvoie les repères (début de chaque document) pour
    /// ouvrir la lecture au bon endroit.
    /// </summary>
    private Dictionary<string, FrameworkElement> BuildTermsDocument(StackPanel target)
    {
        target.Children.Clear();
        var anchors = new Dictionary<string, FrameworkElement>();
        var conditions = MarkdownLite.Embedded("CONDITIONS.md");
        if (conditions == null)
        {
            // Ne devrait pas arriver (fichiers intégrés à la compilation)
            target.Children.Add(new TextBlock { Text = "Le texte des conditions n'a pas pu être chargé. Réinstallez Centre PC pour le retrouver.", TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
            if (target == TermsDoc) MarkTermsRead();
            return anchors;
        }
        var added = new HashSet<string>();
        var pending = new List<(string File, string Title, string Anchor)>();
        void AddAnnex((string File, string Title, string Anchor) annex)
        {
            if (!added.Add(annex.File)) return;
            var text = MarkdownLite.Embedded(annex.File);
            if (text == null) return;
            var box = new StackPanel();
            foreach (var b in MarkdownLite.Parse(text))
            {
                if (b.Kind == MdKind.Heading && b.Level == 1) { box.Children.Add(MdHeading(annex.Title, 2)); continue; }
                box.Children.Add(b.Kind == MdKind.Heading ? MdHeading(b.Text, Math.Min(3, b.Level + 1)) : MdElement(b));
            }
            var border = new Border
            {
                BorderBrush = (System.Windows.Media.Brush)FindResource("Accent"), BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(16, 2, 0, 2), Margin = new Thickness(0, 4, 0, 12), Child = box
            };
            anchors[annex.Anchor] = border;
            target.Children.Add(border);
        }
        void FlushPending() { foreach (var a in pending) AddAnnex(a); pending.Clear(); }
        foreach (var b in MarkdownLite.Parse(conditions))
        {
            if (b.Kind == MdKind.Heading)
            {
                FlushPending();
                var h = MdHeading(b.Text, b.Level);
                if (b.Level == 1) anchors["conditions"] = h;
                target.Children.Add(h);
                continue;
            }
            target.Children.Add(MdElement(b));
            foreach (var annex in TermsAnnexes)
                if (b.Text.Contains("(" + annex.File + ")") && !pending.Contains(annex)) pending.Add(annex);
        }
        FlushPending();
        foreach (var annex in TermsAnnexes) AddAnnex(annex);   // document jamais cité : lu quand même, à la fin
        return anchors;
    }

    // ------------------------------------------------------------------ lecture depuis Réglages ou un lien
    private Dictionary<string, FrameworkElement> _viewerAnchors;
    private bool _viewerInit;

    /// <summary>Ouvre les conditions dans l'appli, au début du document demandé (conditions, confidentialite, pro).</summary>
    private void OpenTermsViewer(string anchor)
    {
        _viewerAnchors = BuildTermsDocument(TermsViewerDoc);
        var cfg = Settings.Current;
        TermsViewerSubtitle.Text = $"Version du {DateTime.ParseExact(Terms.Version, "yyyy-MM-dd", null):d MMMM yyyy}"
            + (cfg.TermsAcceptedOn is DateTime d && cfg.TermsAccepted == Terms.Version ? $", acceptée le {d:d MMMM yyyy}" : "")
            + ". Confidentialité et usage professionnel en font partie.";
        TermsViewerOverlay.Visibility = Visibility.Visible;
        _viewerInit = true;
        (anchor switch { "confidentialite" => TermsTabPrivacy, "pro" => TermsTabPro, _ => TermsTabConditions }).IsChecked = true;
        _viewerInit = false;
        Dispatcher.InvokeAsync(() => ScrollViewerTo(anchor), System.Windows.Threading.DispatcherPriority.Loaded);
        TermsViewerScroll.Focus();
    }

    private void ScrollViewerTo(string anchor)
    {
        if (_viewerAnchors == null || !_viewerAnchors.TryGetValue(anchor, out var el)) { TermsViewerScroll.ScrollToTop(); return; }
        try
        {
            var y = el.TransformToAncestor(TermsViewerDoc).Transform(new Point(0, 0)).Y;
            TermsViewerScroll.ScrollToVerticalOffset(Math.Max(0, y - 8));
        }
        catch (InvalidOperationException) { TermsViewerScroll.ScrollToTop(); }   // pas encore affiché
    }

    private void TermsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewerInit || sender is not RadioButton { Tag: string anchor }) return;
        ScrollViewerTo(anchor);
    }

    private void TermsViewerClose_Click(object sender, RoutedEventArgs e)
    {
        TermsViewerOverlay.Visibility = Visibility.Collapsed;
        if (WelcomeOverlay.Visibility != Visibility.Visible) FocusStart();
    }

    private TextBlock MdHeading(string text, int level) => new()
    {
        Text = MarkdownLite.Plain(text), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
        FontSize = level switch { 1 => 21, 2 => 16, _ => 14.5 },
        Margin = new Thickness(0, level == 1 ? 0 : 16, 0, 6)
    };

    private FrameworkElement MdElement(MdBlock b)
    {
        switch (b.Kind)
        {
            case MdKind.Bullet:
                var row = new Grid { Margin = new Thickness(4, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = "•", FontSize = 13.5, Margin = new Thickness(0, 0, 10, 0) });
                var text = MdText(b.Text);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                return row;
            case MdKind.Table:
                // Une carte par ligne (lisible dans une fenêtre étroite) : la première colonne en titre, les autres
                // précédées de leur en-tête
                var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
                var header = b.Rows[0];
                foreach (var cells in b.Rows.Skip(1))
                {
                    var card = new StackPanel();
                    card.Children.Add(MdText("**" + cells.FirstOrDefault() + "**"));
                    for (int c = 1; c < cells.Length; c++)
                    {
                        if (string.IsNullOrWhiteSpace(cells[c]) || cells[c] == "—") continue;
                        var line = MdText((c < header.Length ? header[c] + " : " : "") + cells[c]);
                        line.Margin = new Thickness(0, 3, 0, 0);
                        card.Children.Add(line);
                    }
                    panel.Children.Add(new Border
                    {
                        Background = (System.Windows.Media.Brush)FindResource("Surface"), CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), Child = card
                    });
                }
                return panel;
            default:
                var p = MdText(b.Text);
                p.Margin = new Thickness(0, 0, 0, 8);
                return p;
        }
    }

    private TextBlock MdText(string markdown)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 };
        foreach (var span in MarkdownLite.Inline(markdown))
        {
            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeights.SemiBold;
            if (span.Italic) run.FontStyle = FontStyles.Italic;
            if (span.Code) { run.FontFamily = new System.Windows.Media.FontFamily("Consolas"); run.Foreground = (System.Windows.Media.Brush)FindResource("Accent"); }
            tb.Inlines.Add(run);
        }
        return tb;
    }

    /// <summary>« Accepter et continuer » : conditions acceptées (version et date gardées sur le PC), puis réglages.</summary>
    private void Welcome_Click(object sender, RoutedEventArgs e)
    {
        if (!WelcomeAcceptButton.IsEnabled) return;
        var cfg = Settings.Current;
        Terms.Accept(cfg, DateTime.Now);
        cfg.OnboardingDone = true;
        Settings.Save();
        Log.Info($"Conditions d'utilisation acceptées (version {Terms.Version})");
        ShowSetup();
    }

    /// <summary>Conditions refusées : Centre PC se ferme, rien n'est modifié.</summary>
    private void TermsDecline_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Centre PC ne peut pas être utilisé sans accepter ses conditions d'utilisation.\n\nQuitter Centre PC ?",
                            "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Log.Info("Conditions d'utilisation refusées : fermeture");
        QuitApp();
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ étape 2 : réglages recommandés
    private void ShowSetup()
    {
        var cfg = Settings.Current;
        WelcomeIntroPanel.Visibility = Visibility.Collapsed;
        WelcomeTermsPanel.Visibility = Visibility.Collapsed;
        WelcomeSetupPanel.Visibility = Visibility.Visible;
        // Lanceur pas encore utilisé : le premier raccourci libre est proposé (Alt + Espace d'abord)
        if (!cfg.QuickAccess) ProposeFreeHotkey();
        // Recommandés : cochés d'avance s'ils n'ont jamais été refusés ; un choix déjà fait est respecté
        SetupLauncher.IsChecked = cfg.QuickAccess || !cfg.QuickAccessAsked;
        SetupStartupRow.Visibility = CanStartWithWindows ? Visibility.Visible : Visibility.Collapsed;
        SetupStartup.IsChecked = cfg.StartWithWindows || !cfg.StartupAsked;
        // Retours : jamais présélectionnés ; une réponse déjà donnée est reprise
        SetupFeedbackYes.IsChecked = cfg.FeedbackConsent == true;
        SetupFeedbackNo.IsChecked = cfg.FeedbackConsent == false;
        RefreshSetupKeys();
        SetupLauncher_Changed(null, null);
        UpdateSetupDone();
        SetupLauncher.Focus();
    }

    private void RefreshSetupKeys()
    {
        var hk = CurrentHotkey;
        FillKeyCaps(SetupKeys, hk);
        string note = null;
        try
        {
            var check = HotkeyConflicts.Check(hk, null, HotkeyConflicts.ScanKnownApps(), InstalledNames());
            if (check.State is HotkeyState.Note or HotkeyState.Freeable or HotkeyState.LikelyUsed) note = check.Text;
        }
        catch (Exception ex) { Log.Error("Raccourci proposé", ex); }
        SetupKeysNote.Text = (note != null ? note + ". " : "") + "Un autre raccourci : Réglages › Accès rapide.";
    }

    /// <summary>Touches d'un raccourci dessinées comme des touches de clavier (« Alt » + « Espace »).</summary>
    private void FillKeyCaps(WrapPanel panel, HotkeyCombo hk)
    {
        panel.Children.Clear();
        if (hk == null || hk.IsNone) return;
        foreach (var part in hk.Label.Split(" + "))
        {
            if (panel.Children.Count > 0)
                panel.Children.Add(new TextBlock { Text = "+", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            panel.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("Surface2"), BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
                BorderThickness = new Thickness(1, 1, 1, 3), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6, 14, 6),
                Child = new TextBlock { Text = part, FontWeight = FontWeights.SemiBold, FontSize = 14 }
            });
        }
    }

    /// <summary>Le démarrage avec Windows n'a de sens qu'avec le lanceur (sinon rien n'est disponible en arrière-plan).</summary>
    private void SetupLauncher_Changed(object sender, RoutedEventArgs e)
    {
        if (SetupStartup == null) return;   // pendant le chargement de la fenêtre
        bool on = SetupLauncher.IsChecked == true;
        SetupStartup.IsEnabled = on;
        SetupStartup.Opacity = on ? 1 : 0.5;
        SetupKeys.Opacity = on ? 1 : 0.4;
    }

    private void SetupFeedback_Checked(object sender, RoutedEventArgs e) => UpdateSetupDone();

    private void UpdateSetupDone()
    {
        bool answered = SetupFeedbackYes.IsChecked == true || SetupFeedbackNo.IsChecked == true;
        SetupDoneButton.IsEnabled = answered;
        SetupHint.Text = answered ? "Modifiable à tout moment dans Réglages."
                                  : "Choisissez une réponse pour les retours anonymes : refuser ne retire aucune fonction.";
    }

    private void SetupDone_Click(object sender, RoutedEventArgs e)
    {
        if (!SetupDoneButton.IsEnabled) return;
        var cfg = Settings.Current;
        bool launcher = SetupLauncher.IsChecked == true;
        cfg.QuickAccess = launcher;
        cfg.QuickAccessAsked = true;
        cfg.StartWithWindows = CanStartWithWindows && launcher && SetupStartup.IsChecked == true;
        cfg.StartupAsked = true;
        cfg.SetupDone = true;
        bool feedback = SetupFeedbackYes.IsChecked == true;
        if (cfg.FeedbackConsent != feedback) Feedback.SetConsent(feedback);
        Settings.Save();
        Log.Info($"Accueil : lanceur {(launcher ? "oui" : "non")}, démarrage avec Windows {(cfg.StartWithWindows ? "oui" : "non")}, retours {(feedback ? "oui" : "non")}");

        WelcomeOverlay.Visibility = Visibility.Collapsed;
        if (!launcher && HotkeyConflicts.RestoreAll() > 0) Log.Info("Accueil : réglages d'origine des raccourcis remis");
        ApplyQuickAccess();
        ApplyStartWithWindows(fromUser: true);
        InitQuickAccessSettings();
        RefreshFeedbackUi();
        FocusStart();

        var hk = _launcher?.HotkeyLabel;
        if (launcher)
        {
            ShowToast("C'est prêt" + (hk != null ? $" : essayez {hk} pour ouvrir vos applications." : " : cliquez sur l'icône près de l'horloge pour ouvrir vos applications."));
            _tray?.ShowBalloon("Centre PC est ici", (hk != null ? $"{hk} ouvre le lanceur. " : "") + "Clic sur cette icône : lanceur ; clic droit : ouvrir Centre PC ou quitter.");
        }
        else ShowToast("C'est prêt. Le lanceur s'active à tout moment dans Réglages › Accès rapide.");
    }

    /// <summary>
    /// Clavier dans l'accueil : Entrée passe à l'étape suivante (les conditions seulement une fois lues) ; Échap ne
    /// ferme que la présentation rouverte depuis Réglages. Les flèches et Page suivante font défiler les conditions.
    /// </summary>
    private void WelcomeKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_welcomeReplay) WelcomeNext_Click(this, null);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || Keyboard.FocusedElement is Hyperlink or ButtonBase) return;   // un bouton ou une case garde son comportement
        if (WelcomeSetupPanel.Visibility == Visibility.Visible) SetupDone_Click(this, null);
        else if (WelcomeTermsPanel.Visibility == Visibility.Visible) Welcome_Click(this, null);
        else WelcomeNext_Click(this, null);
        e.Handled = true;
    }
}
