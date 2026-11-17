using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Accessibilité, pour toute l'application d'un coup (la plupart des éléments sont construits dans le code) :
/// <list type="bullet">
/// <item>chaque bouton, case à cocher et champ de saisie reçoit un nom lisible par les lecteurs d'écran, tiré de ses textes
/// visibles (sans les caractères d'icône) ou, pour un bouton qui n'est qu'une icône, de son infobulle ;</item>
/// <item>le nom est recalculé au survol de la souris et à l'arrivée du focus clavier (libellés qui changent : « Désactiver » / « Réactiver ») :
/// il est donc annoncé correctement avant tout clic ;</item>
/// <item>un nom donné explicitement (AutomationProperties.Name) n'est jamais remplacé.</item>
/// </list>
/// </summary>
public static class Accessibility
{
    /// <summary>Marque les noms posés ici (pour pouvoir les mettre à jour, et seulement eux).</summary>
    private static readonly DependencyProperty AutoNamedProperty =
        DependencyProperty.RegisterAttached("AutoNamed", typeof(bool), typeof(Accessibility), new PropertyMetadata(false));

    private static bool _errorLogged;

    /// <summary>
    /// Passe sur toute la fenêtre : nomme chaque élément, et retire de la lecture les icônes décoratives (caractères de la
    /// police d'icônes), qui seraient annoncées comme des symboles incompréhensibles. Relancée quand l'affichage change
    /// (au plus une fois par seconde), pour couvrir les éléments construits dans le code.
    /// </summary>
    public static void Watch(Window w)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) => { timer.Stop(); Sweep(w); };
        w.LayoutUpdated += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
        timer.Start();
    }

    public static void Sweep(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            switch (d)
            {
                case ButtonBase or TextBox or ComboBox: Apply((FrameworkElement)d); break;
                // Liste d'une catégorie : porte son nom (« Jeux, 15 application(s) »)
                case ItemsControl ic when ic is not ComboBox && string.IsNullOrEmpty(AutomationProperties.GetName(ic)) && ic.DataContext is ThemeGroup g:
                    AutomationProperties.SetName(ic, g.ToString());
                    break;
                // Icône d'application dans une tuile : le nom de la tuile suffit, l'image n'est pas annoncée à part
                case Image img when string.IsNullOrEmpty(AutomationProperties.GetName(img)) && IsInsideClickable(img):
                    if (AutomationProperties.GetIsOffscreenBehavior(img) != IsOffscreenBehavior.Offscreen)
                        AutomationProperties.SetIsOffscreenBehavior(img, IsOffscreenBehavior.Offscreen);
                    break;
                case TextBlock tb when tb.Text.Length > 0 && tb.Text.All(c => AccessibleText.IsIcon(c) || char.IsWhiteSpace(c)):
                    if (AutomationProperties.GetIsOffscreenBehavior(tb) != IsOffscreenBehavior.Offscreen)
                        AutomationProperties.SetIsOffscreenBehavior(tb, IsOffscreenBehavior.Offscreen);
                    break;
            }
            int n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    // ------------------------------------------------------------------ lecture au survol
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    /// <summary>Un lecteur d'écran est en marche (Narrateur, NVDA, JAWS signalent leur présence à Windows).</summary>
    public static bool ScreenReaderRunning
    {
        get
        {
            bool on = false;
            try { SystemParametersInfo(0x0046 /* SPI_GETSCREENREADER */, 0, ref on, 0); } catch { }
            return on;
        }
    }

    /// <summary>
    /// Lecture au survol : quand un lecteur d'écran est actif, l'élément sous la souris est annoncé dès que la souris s'y
    /// arrête (un tiers de seconde), sans clic, donc sans rien ouvrir ni lancer. L'annonce passe par le lecteur d'écran
    /// lui-même (notification d'accessibilité de Windows) : sa voix, sa vitesse, ses réglages. Désactivable dans Réglages
    /// (utile si la lecture à la souris du Narrateur est déjà activée, pour éviter une double lecture).
    /// </summary>
    private static bool _hoverFocusing;

    /// <summary>Remis à faux par toute touche du clavier, à vrai par un mouvement de souris : la souris ne reprend la main
    /// que si elle bouge.</summary>
    private static bool _mouseMovedSinceKeyboard = true;

    public static void SpeakOnHover(Window w)
    {
        NarratorMouse.Attach(w);
        w.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) => { if (_hoverFocusing) e.Handled = true; }), true);
        UIElement candidate = null, spoken = null;
        int stable = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        bool? reader = null;
        DateTime checkedAt = default;
        Point lastMouse = new(double.NaN, double.NaN);
        timer.Tick += (_, _) =>
        {
            if (!Settings.Current.HoverSpeech || !w.IsVisible || InputLocked) return;
            // Seul un vrai mouvement de la souris compte : pendant la navigation au clavier, la page défile sous une souris
            // immobile, qui ne doit pas reprendre le focus
            Point pos;
            try { pos = w.PointToScreen(Mouse.GetPosition(w)); } catch { return; }
            bool moved = Math.Abs(pos.X - lastMouse.X) > 2 || Math.Abs(pos.Y - lastMouse.Y) > 2 || double.IsNaN(lastMouse.X);
            if (moved) { lastMouse = pos; candidate = null; stable = 0; _mouseMovedSinceKeyboard = true; return; }   // en mouvement : on attend l'arrêt
            if (reader == null || (DateTime.Now - checkedAt).TotalSeconds > 5) { reader = ScreenReaderRunning; checkedAt = DateTime.Now; }
            if (reader != true) return;
            var target = Mouse.DirectlyOver is DependencyObject d && IsIn(w, d) ? Target(d) : null;
            if (target != candidate)
            {
                // La souris a quitté l'élément lu : on coupe aussitôt la lecture en cours, sans attendre le suivant
                if (spoken != null && target != spoken) spoken = null;
                candidate = target; stable = 0;
                return;
            }
            if (target == null || target == spoken || ++stable < 2) return;
            if (!_mouseMovedSinceKeyboard) return;
            spoken = target;
            // Le focus clavier suit la souris : le lecteur d'écran coupe ce qu'il disait et lit le nouvel élément
            // (c'est le seul signal que tous les lecteurs d'écran interrompent à coup sûr). Rien n'est cliqué ni lancé.
            if (!FocusForReading(target)) Speak(target);
        };
        w.MouseLeave += (_, _) => { candidate = null; spoken = null; };
        w.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => { _mouseMovedSinceKeyboard = false; spoken = null; }), true);
        timer.Start();
    }

    private static bool IsIn(Window w, DependencyObject d)
    {
        try { return Window.GetWindow(d) == w; } catch { return false; }
    }

    /// <summary>Élément à annoncer sous la souris : le bouton ou la ligne cliquable qui le contient, sinon le texte lui-même.</summary>
    private static UIElement Target(DependencyObject d)
    {
        UIElement text = null;
        for (var p = d; p != null; p = p is Visual || p is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
        {
            if (p is ButtonBase or ClickBorder or TextBox or ComboBox) return (UIElement)p;
            if (text == null && p is TextBlock tb && AccessibleText.Clean(tb.Text).Length > 0) text = tb;
            if (p is Window) break;
        }
        // Texte faisant partie d'un petit bloc de textes (chiffre + libellé, titre + détail) : le bloc entier est lu
        if (text is TextBlock t && VisualTreeHelper.GetParent(t) is Panel panel && TextGroup(panel) != null) return panel;
        return text;
    }

    /// <summary>
    /// Textes d'un petit bloc composé uniquement de textes (« 14 » + « sur le bureau », titre + phrase d'explication),
    /// lus d'un seul tenant ; null si le bloc contient autre chose (bouton, champ…) ou trop de textes.
    /// </summary>
    private static List<string> TextGroup(Panel panel)
    {
        var texts = new List<string>();
        foreach (UIElement c in panel.Children)
        {
            if (c.Visibility != Visibility.Visible) continue;
            if (c is TextBlock tb) { var t = AccessibleText.Clean(tb.Text); if (t.Length > 0) texts.Add(t); continue; }
            if (c is Panel inner && TextGroup(inner) is { } sub) { texts.AddRange(sub); continue; }
            if (c is Border { Child: TextBlock bt }) { var t = AccessibleText.Clean(bt.Text); if (t.Length > 0) texts.Add(t); continue; }   // icône dans une pastille
            return null;
        }
        return texts.Count is >= 2 and <= 4 ? texts : null;
    }

    /// <summary>
    /// Place le focus sur l'élément survolé. Un texte (ou un bloc de textes) devient le temps du survol un élément
    /// focalisable hors de l'ordre de tabulation, nommé avec ce qu'il faut lire (bloc entier, titre et sa précision).
    /// </summary>
    private static bool FocusForReading(UIElement target)
    {
        try
        {
            UIElement focus = target;
            if (target is Panel or TextBlock)
            {
                var text = Describe(target);
                var tb = target as TextBlock ?? FirstText((Panel)target);
                if (tb == null || string.IsNullOrEmpty(text)) return false;
                if (!tb.Focusable) { tb.Focusable = true; KeyboardNavigation.SetIsTabStop(tb, false); }
                AutomationProperties.SetName(tb, text);
                focus = tb;
            }
            if (!focus.Focusable || !focus.IsEnabled) return false;
            if (focus.IsKeyboardFocused)
            {
                // Déjà focalisé (retour sur le même élément) : on le relit
                System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(focus)?
                    .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.AutomationFocusChanged);
                return true;
            }
            // Pas de défilement : l'élément est sous la souris, donc déjà visible
            _hoverFocusing = true;
            try { return focus.Focus(); } finally { _hoverFocusing = false; }
        }
        catch { return false; }
    }

    private static TextBlock FirstText(Panel panel)
    {
        foreach (UIElement c in panel.Children)
        {
            if (c.Visibility != Visibility.Visible) continue;
            if (c is TextBlock tb && AccessibleText.Clean(tb.Text).Length > 0) return tb;
            if (c is Panel inner && FirstText(inner) is { } t) return t;
        }
        return null;
    }

    /// <summary>Texte annoncé : nom, nature et état (« Jeux, 15, onglet, sélectionné »), puis l'aide éventuelle.</summary>
    internal static string Describe(UIElement el)
    {
        // Bloc de textes : lu d'un tenant ; un chiffre suivi de son libellé se lit naturellement (« 14 sur le bureau »)
        if (el is Panel panel && TextGroup(panel) is { } group)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < group.Count; i++)
            {
                if (i > 0) sb.Append(group[i - 1].All(char.IsDigit) ? " " : ". ");
                sb.Append(group[i]);
            }
            return sb.ToString();
        }
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(el);
        // Texte : son contenu réel (son nom d'accessibilité peut déjà être le bloc complet posé au survol précédent)
        string name = el is TextBlock tb ? AccessibleText.Clean(tb.Text) : peer?.GetName() ?? "";
        if (string.IsNullOrEmpty(name)) return null;
        // Titre suivi de sa phrase d'explication : les deux ensemble (« Bureau, titre. Le bureau est net… »)
        if (el is TextBlock heading && AutomationProperties.GetHeadingLevel(heading) != AutomationHeadingLevel.None
            && VisualTreeHelper.GetParent(heading) is Panel hp)
        {
            int k = hp.Children.IndexOf(heading);
            if (k >= 0 && k + 1 < hp.Children.Count && hp.Children[k + 1] is TextBlock next && next.Visibility == Visibility.Visible
                && AutomationProperties.GetHeadingLevel(next) == AutomationHeadingLevel.None && AccessibleText.Clean(next.Text) is { Length: > 0 } detail)
                return $"Titre {name}. {detail}";
        }
        // Le type d'abord, puis le nom, puis l'état (« Case à cocher Démarrer avec Windows, cochée »)
        var (role, state) = el switch
        {
            CheckBox c => ("Case à cocher", c.IsChecked == true ? "cochée" : "non cochée"),
            RadioButton r => ("Option", r.IsChecked == true ? "sélectionnée" : ""),
            ToggleButton t => ("Interrupteur", t.IsChecked == true ? "activé" : "désactivé"),
            ButtonBase or ClickBorder => ("Bouton", ""),
            TextBox => ("Zone de saisie", ""),
            ComboBox => ("Liste déroulante", ""),
            TextBlock t2 when AutomationProperties.GetHeadingLevel(t2) != AutomationHeadingLevel.None => ("Titre", ""),
            _ => ("", "")
        };
        var help = peer?.GetHelpText();
        var parts = new List<string> { role.Length > 0 ? $"{role} {name}" : name };
        if (state.Length > 0) parts.Add(state);
        if (!el.IsEnabled) parts.Add("indisponible");
        if (!string.IsNullOrEmpty(help) && help != name) parts.Add(help);
        return string.Join(", ", parts);
    }

    private static void Speak(UIElement el)
    {
        try
        {
            var text = Describe(el);
            if (string.IsNullOrEmpty(text)) return;
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(el)
                       ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(Window.GetWindow(el));
            peer?.RaiseNotificationEvent(System.Windows.Automation.AutomationNotificationKind.Other,
                                         System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, text, "CentrePC.Survol");
        }
        catch (Exception ex)
        {
            if (!_errorLogged) { _errorLogged = true; Log.Info("Accessibilité (survol) : " + ex.GetType().Name + " " + ex.Message); }
        }
    }

    /// <summary>
    /// Fait lire un message par le lecteur d'écran (sans déplacer le focus). <paramref name="interrupt"/> : remplace ce
    /// qu'il était en train de dire (touche pressée, bouton détecté) ; sinon le message attend son tour.
    /// </summary>
    private static DateTime _instructionsUntil;

    /// <summary>Consignes en cours de lecture : les annonces d'événements (touche, bouton) ne les coupent pas.</summary>
    public static bool InstructionsPlaying => DateTime.Now < _instructionsUntil;

    /// <summary>
    /// Consignes importantes (comment se servir d'un test) : lues en entier, sans être coupées par les annonces qui suivent
    /// (touches, boutons détectés pendant la lecture sont comptés mais pas annoncés). <see cref="StopInstructions"/> les arrête.
    /// </summary>
    public static TimeSpan AnnounceInstructions(UIElement anchor, string text)
    {
        var spoken = AccessibleText.Clean(text);
        if (spoken.Length == 0) return TimeSpan.Zero;
        // Durée de lecture estimée : environ 2,6 mots par seconde au débit habituel des lecteurs d'écran
        int words = spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        _instructionsUntil = DateTime.Now.AddSeconds(Math.Max(3, words / 2.6));
        Raise(anchor, spoken, System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, "CentrePC.Consignes");
        return _instructionsUntil - DateTime.Now;
    }

    /// <summary>Clavier et souris bloqués (explication de départ en cours) : le survol ne lit rien.</summary>
    public static bool InputLocked { get; set; }

    /// <summary>On a quitté l'écran : les consignes s'arrêtent, remplacées par un message bref (« Test du clavier fermé »).</summary>
    public static void StopInstructions(UIElement anchor, string closing)
    {
        if (!InstructionsPlaying) return;
        _instructionsUntil = DateTime.MinValue;
        Raise(anchor, AccessibleText.Clean(closing), System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, "CentrePC.Consignes");
    }

    private static void Raise(UIElement anchor, string spoken, System.Windows.Automation.AutomationNotificationProcessing processing, string activity)
    {
        try
        {
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(anchor)
                       ?? (Window.GetWindow(anchor) is { } w ? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(w) : null);
            peer?.RaiseNotificationEvent(System.Windows.Automation.AutomationNotificationKind.Other, processing, spoken, activity);
        }
        catch { }
    }

    public static void Announce(UIElement anchor, string text, bool interrupt = true)
    {
        try
        {
            if (anchor == null || string.IsNullOrWhiteSpace(text)) return;
            if (InstructionsPlaying) return;   // les consignes d'abord, en entier
            var spoken = AccessibleText.Clean(text);
            if (spoken.Length == 0) return;
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(anchor)
                       ?? (Window.GetWindow(anchor) is { } w ? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(w) : null);
            peer?.RaiseNotificationEvent(System.Windows.Automation.AutomationNotificationKind.Other,
                interrupt ? System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent : System.Windows.Automation.AutomationNotificationProcessing.ImportantAll,
                spoken, interrupt ? "CentrePC.Annonce" : "CentrePC.Suite");
        }
        catch { }
    }

    /// <summary>
    /// Texte « en direct » : chaque changement est lu (résultat d'un test, appareil détecté, étape terminée).
    /// <paramref name="minSeconds"/> espace les lectures d'un texte qui change souvent (fréquence, niveau).
    /// </summary>
    /// <summary>Personne qui se sert du clavier (ou d'un lecteur d'écran) : on peut lui déplacer le focus vers le suivi.</summary>
    public static bool KeyboardUser => ScreenReaderRunning || InputManager.Current.MostRecentInputDevice is KeyboardDevice;

    /// <summary>
    /// Après une action longue (« Tout mettre à jour »), le focus va sur le texte de suivi au lieu de se perdre : le
    /// lecteur d'écran lit <paramref name="spoken"/>, puis l'avancement est annoncé sans avoir à se déplacer.
    /// </summary>
    public static void FollowText(TextBlock tb, string spoken)
    {
        if (tb == null || !KeyboardUser) return;
        if (!tb.Focusable) { tb.Focusable = true; KeyboardNavigation.SetIsTabStop(tb, false); }
        AutomationProperties.SetName(tb, AccessibleText.Clean(spoken ?? tb.Text));
        tb.BringIntoView();
        if (tb.IsKeyboardFocused)
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(tb)?
                .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.AutomationFocusChanged);
        else tb.Focus();
    }

    public static TextBlock Live(TextBlock tb, double minSeconds = 0, bool interrupt = true)
    {
        var last = DateTime.MinValue;
        var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler handler = null;
        handler = (_, _) =>
        {
            if (PresentationSource.FromVisual(tb) == null) { dpd.RemoveValueChanged(tb, handler); return; }   // écran refermé
            if (!tb.IsVisible || (DateTime.Now - last).TotalSeconds < minSeconds) return;
            last = DateTime.Now;
            Announce(tb, tb.Text, interrupt);
        };
        dpd.AddValueChanged(tb, handler);
        return tb;
    }

    private static bool IsInsideClickable(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ButtonBase or ClickBorder) return true;
        return false;
    }

    public static void Install()
    {
        foreach (var type in new[] { typeof(ButtonBase), typeof(TextBox), typeof(ComboBox) })
        {
            EventManager.RegisterClassHandler(type, FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply(s as FrameworkElement)));
            EventManager.RegisterClassHandler(type, UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, _) => Apply(s as FrameworkElement)));
            // Survol : le nom est à jour avant que le lecteur d'écran l'annonce sous la souris (sans attendre un clic)
            EventManager.RegisterClassHandler(type, UIElement.MouseEnterEvent, new MouseEventHandler((s, _) => Apply(s as FrameworkElement)));
        }
    }

    /// <summary>
    /// Diagnostic : si le fichier « diagnostic-accessibilite » existe dans le dossier des réglages, écrit dans Journaux ce
    /// qu'un lecteur d'écran voit de la fenêtre (type, nom, aide, titre), puis supprime le repère. Rien n'est envoyé.
    /// </summary>
    public static async void DumpIfRequested(Window w)
    {
        try
        {
            var flag = Path.Combine(Settings.DataDir, "diagnostic-accessibilite");
            if (!File.Exists(flag)) return;
            File.Delete(flag);
            await Task.Delay(6000);   // laisser la liste des applications et les icônes se charger
            Sweep(w);
            var sb = new System.Text.StringBuilder();
            void Walk(System.Windows.Automation.Peers.AutomationPeer p, int depth)
            {
                if (p == null || depth > 40) return;
                string name = "", type = "", help = "", heading = "";
                try { name = p.GetName(); type = p.GetAutomationControlType().ToString(); help = p.GetHelpText(); } catch { }
                try { if (p is System.Windows.Automation.Peers.UIElementAutomationPeer u && u.Owner != null) heading = AutomationProperties.GetHeadingLevel(u.Owner).ToString(); } catch { }
                bool offscreen = false; try { offscreen = p.IsOffscreen(); } catch { }
                sb.Append(' ', depth * 2).Append(type).Append(" « ").Append(name).Append(" »");
                if (!string.IsNullOrEmpty(help)) sb.Append(" aide « ").Append(help).Append(" »");
                if (heading is not ("" or "None")) sb.Append(" [").Append(heading).Append(']');
                if (offscreen) sb.Append(" (hors écran)");
                try { if (p.IsKeyboardFocusable()) sb.Append(" [tab]"); } catch { }
                sb.AppendLine();
                List<System.Windows.Automation.Peers.AutomationPeer> kids = null;
                try { kids = p.GetChildren(); } catch { }
                if (kids != null) foreach (var k in kids) Walk(k, depth + 1);
            }
            Walk(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(w), 0);
            var file = Path.Combine(Settings.DataDir, "Journaux", "accessibilite.txt");
            File.WriteAllText(file, sb.ToString());
            Log.Info("Accessibilité : relevé écrit (" + sb.Length + " caractères)");
        }
        catch (Exception ex) { Log.Error("Relevé d'accessibilité", ex); }
    }

    private static void Apply(FrameworkElement el)
    {
        if (el == null) return;
        try
        {
            var current = AutomationProperties.GetName(el);
            if (!string.IsNullOrEmpty(current) && !(bool)el.GetValue(AutoNamedProperty)) return;   // nom explicite : on n'y touche pas
            string name = el switch
            {
                TextBox t => AccessibleText.Clean(t.Tag as string ?? t.ToolTip as string),
                ComboBox c => AccessibleText.Clean(c.ToolTip as string),
                ContentControl c when c.Content is string s && !s.Any(AccessibleText.IsIcon) => "",   // texte simple : déjà lu par Windows
                _ => AccessibleText.Name(VisibleTexts(el), el.ToolTip as string)
            };
            // Bouton d'action générique dans une ligne : on dit sur quoi il porte (« Ouvrir, Récépissé.odt »)
            if (el is ButtonBase && name.Length > 0 && AccessibleText.IsGenericAction(name) && RowContext(el) is { Length: > 0 } ctx)
                name = $"{name}, {ctx}";
            if (name.Length == 0 || name == current) return;
            AutomationProperties.SetName(el, name);
            el.SetValue(AutoNamedProperty, true);
            // Infobulle en complément (« aide »), quand elle ajoute quelque chose au nom
            if (el.ToolTip is string tip && string.IsNullOrEmpty(AutomationProperties.GetHelpText(el)) && AccessibleText.Clean(tip) is { Length: > 0 } help && help != name)
                AutomationProperties.SetHelpText(el, help);
        }
        catch (Exception ex)
        {
            // L'accessibilité ne doit jamais gêner l'affichage ; la première erreur est notée pour pouvoir la corriger
            if (!_errorLogged) { _errorLogged = true; Log.Info("Accessibilité : " + ex.GetType().Name + " " + ex.Message); }
        }
    }

    /// <summary>
    /// Ce que désigne la ligne où se trouve le bouton : les premiers textes du plus proche conteneur qui en a (hors boutons).
    /// </summary>
    private static string RowContext(FrameworkElement button)
    {
        DependencyObject p = VisualTreeHelper.GetParent(button);
        for (int level = 0; p != null && level < 4; level++, p = VisualTreeHelper.GetParent(p))
        {
            if (p is ScrollViewer or Window) break;
            var texts = VisibleTexts(p).Select(AccessibleText.Clean).Where(t => t.Length > 1).Distinct().Take(2).ToList();
            if (texts.Count > 0)
            {
                var ctx = string.Join(", ", texts);
                return ctx.Length > 160 ? ctx[..160] + "…" : ctx;
            }
        }
        return null;
    }

    /// <summary>Textes visibles à l'intérieur de l'élément (pas ceux d'un autre bouton imbriqué).</summary>
    internal static IEnumerable<string> VisibleTexts(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        var found = new List<string>();
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d != root && (d is ButtonBase || d is ClickBorder)) continue;
            if (d is UIElement u && d != root && u.Visibility != Visibility.Visible) continue;
            if (d is TextBlock tb) { found.Add(tb.Text); continue; }
            if (d is AccessText at) { found.Add(at.Text); continue; }
            int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            // Ordre d'affichage : les enfants sont empilés à l'envers
            for (int i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return found;
    }
}

/// <summary>
/// Zone cliquable construite avec une bordure (ligne de liste, tuile, pastille de couleur) : atteignable au clavier
/// (Tab, puis Entrée ou Espace), et présentée aux lecteurs d'écran comme un bouton, avec un nom lu au survol comme au focus.
/// Le clavier et le lecteur d'écran déclenchent exactement le même code que le clic (MouseLeftButtonUp).
/// </summary>
public class ClickBorder : Border
{
    public ClickBorder()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this) && e.Key is Key.Enter or Key.Space) { Activate(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    /// <summary>Même effet qu'un clic gauche.</summary>
    internal void Activate() =>
        RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent, Source = this });

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ClickBorderPeer(this);

    private sealed class ClickBorderPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer, System.Windows.Automation.Provider.IInvokeProvider
    {
        public ClickBorderPeer(ClickBorder owner) : base(owner) { }
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Button;
        protected override string GetClassNameCore() => "ClickBorder";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        /// <summary>Nom explicite s'il y en a un, sinon les textes visibles du moment (toujours à jour, au survol comme au focus).</summary>
        protected override string GetNameCore()
        {
            var explicitName = base.GetNameCore();
            if (!string.IsNullOrEmpty(explicitName)) return explicitName;
            var el = (FrameworkElement)Owner;
            return AccessibleText.Name(Accessibility.VisibleTexts(el), el.ToolTip as string);
        }
        public override object GetPattern(System.Windows.Automation.Peers.PatternInterface p) =>
            p == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : base.GetPattern(p);
        public void Invoke() => ((ClickBorder)Owner).Activate();
    }
}

/// <summary>
/// Entrée du menu des pages. Annoncée comme une entrée de menu (« Documents, menu »), avec « page affichée » pour la
/// page en cours, au lieu de « case d'option non sélectionnée » répété sur chaque entrée. Rôle « élément de menu » côté
/// UI Automation : on s'y déplace aux flèches, comme dans un menu.
/// </summary>
public class NavRadio : RadioButton
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new NavRadioPeer(this);

    private sealed class NavRadioPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer, System.Windows.Automation.Provider.IInvokeProvider
    {
        public NavRadioPeer(NavRadio owner) : base(owner) { }
        private NavRadio Nav => (NavRadio)Owner;
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.MenuItem;
        protected override string GetClassNameCore() => nameof(NavRadio);
        // Le type est dit en premier, dans le nom (« Menu Applications, 1 sur 6, page affichée ») : pas de type répété après
        protected override string GetLocalizedControlTypeCore() => "";
        protected override string GetNameCore()
        {
            // Libellé de la page, et pastille dite en entier (« 1 point à vérifier », pas « 1 ») : son nom est posé avec le nombre
            string name = "", badge = "";
            foreach (var tb in Texts(Nav))
            {
                bool inBadge = VisualTreeHelper.GetParent(tb) is Border;
                var t = AccessibleText.Clean(tb.Text);
                if (t.Length == 0 || t.Any(AccessibleText.IsIcon)) continue;
                if (inBadge) { if (badge.Length == 0) badge = AutomationProperties.GetName(tb) is { Length: > 0 } n ? n : t; }
                else if (name.Length == 0) name = t;
            }
            var all = (Nav.Parent as Panel)?.Children.OfType<NavRadio>().Where(n => n.Visibility == Visibility.Visible).ToList();
            var pos = all != null && all.Count > 1 ? $", {all.IndexOf(Nav) + 1} sur {all.Count}" : "";
            return $"Menu {name}{pos}" + (Nav.IsChecked == true ? ", page affichée" : "") + (badge.Length > 0 ? ", " + badge : "");
        }

        private static IEnumerable<TextBlock> Texts(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is UIElement u && u.Visibility != Visibility.Visible) continue;
                if (c is TextBlock tb) yield return tb;
                foreach (var t in Texts(c)) yield return t;
            }
        }
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        public override object GetPattern(System.Windows.Automation.Peers.PatternInterface p) =>
            p == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : base.GetPattern(p);
        public void Invoke() { if (Nav.IsEnabled) Nav.IsChecked = true; }
    }
}

/// <summary>
/// Pastille « i » placée au bout d'un texte court : le détail s'affiche au survol (ou avec Entrée au clavier) au lieu
/// d'encombrer l'écran. Le lecteur d'écran lit « Information : » suivi du détail.
/// </summary>
public class InfoTip : ClickBorder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoTip),
        new PropertyMetadata("", (d, e) => ((InfoTip)d).Update((string)e.NewValue)));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private readonly ToolTip _tip = new() { MaxWidth = 380 };

    public InfoTip()
    {
        Width = 18; Height = 18; CornerRadius = new CornerRadius(9);
        Margin = new Thickness(6, 0, 0, -3);
        Background = Brushes.Transparent;
        Cursor = Cursors.Help;
        VerticalAlignment = VerticalAlignment.Center;
        Child = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB0, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _tip.Content = new TextBlock { TextWrapping = TextWrapping.Wrap };
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, true);
        MouseLeftButtonUp += (_, e) => { _tip.PlacementTarget = this; _tip.IsOpen = !_tip.IsOpen; e.Handled = true; };
        LostKeyboardFocus += (_, _) => _tip.IsOpen = false;
    }

    private void Update(string text)
    {
        ((TextBlock)_tip.Content).Text = text;
        AutomationProperties.SetName(this, "Information : " + AccessibleText.Clean(text ?? ""));
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new InfoTipPeer(this);

    private sealed class InfoTipPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
    {
        public InfoTipPeer(InfoTip owner) : base(owner) { }
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(InfoTip);
        protected override string GetLocalizedControlTypeCore() => "";   // « Information : … » dit déjà ce que c'est
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
