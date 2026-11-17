using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Navigation au clavier en trois niveaux, la même partout (pensée pour le lecteur d'écran, utile à tous) :
/// <list type="number">
/// <item><b>Menu</b> : les flèches passent d'une page à l'autre (sans l'ouvrir) ; Entrée ouvre la page et y entre.</item>
/// <item><b>Sections</b> de la page (blocs, cartes, catégories) : les flèches passent de l'une à l'autre ; Entrée y entre ;
/// Échap revient au menu.</item>
/// <item><b>Éléments</b> de la section : les flèches passent de l'un à l'autre ; Entrée active (coche, sélectionne, lance)
/// en gardant la place ; touche Menu pour les options ; Échap revient à la section.</item>
/// </list>
/// Dans une fenêtre ouverte par-dessus (bilan, test, outil), les sections sont celles de la fenêtre, et Échap au niveau des
/// sections la ferme comme avant. Tab et les raccourcis existants continuent de fonctionner.
/// </summary>
public partial class MainWindow
{
    /// <summary>Deux curseurs de section, utilisés à tour de rôle : chaque changement de section est un vrai changement de
    /// focus, que le lecteur d'écran annonce en coupant ce qu'il disait.</summary>
    private SectionCursor _cursorA, _cursorB;

    private SectionCursor CurrentCursor => Keyboard.FocusedElement as SectionCursor;

    private void InitKeyboardNav()
    {
        if (Root.Child is not Panel host) return;
        _cursorA = new SectionCursor(); _cursorB = new SectionCursor();
        foreach (var c in new[] { _cursorA, _cursorB })
        {
            if (host is Grid) { Grid.SetColumnSpan(c, 99); Grid.SetRowSpan(c, 99); }
            host.Children.Add(c);
        }
        InitNavGuide();
    }

    // ------------------------------------------------------------------ explication de départ (lecteur d'écran)
    private const string NavGuideText =
        "Bienvenue dans Centre PC. L'application est rangée en niveaux : le menu, puis les catégories et parties de chaque page, puis leurs éléments. " +
        "Les flèches, ou Tab, passent d'un élément à l'autre dans le niveau ; Majuscule Tab revient en arrière. " +
        "Entrée entre dans le niveau suivant ou active l'élément. Échap revient au niveau précédent. " +
        "Contrôle et un chiffre de 1 à 6 ouvrent une page. La souris lit l'élément survolé sans l'ouvrir. " +
        "Pour réécouter cette explication : Réglages, Accessibilité.";

    private bool _navGuideGiven, _navGuidePlaying;
    private DateTime _readerCheckedAt;
    private bool _readerOn;

    /// <summary>
    /// Lecteur d'écran actif : à la première action dans la fenêtre (touche, clic ou souris), la façon de se déplacer est
    /// expliquée en entier. Clavier et souris restent sans effet pendant l'explication, puis le focus va sur le premier
    /// menu. Le lanceur rapide n'a pas cette explication.
    /// </summary>
    private void InitNavGuide()
    {
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => { if (NavGuideGate()) e.Handled = true; }), true);
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) => { if (NavGuideGate()) e.Handled = true; }), true);
        PreviewMouseMove += (_, _) => NavGuideGate();
    }

    /// <summary>Vrai si l'action doit être ignorée (explication en cours ou qui commence).</summary>
    internal bool NavGuideGate()
    {
        if (_navGuidePlaying) return true;
        if (_navGuideGiven || Settings.Current.NavGuideHeard) return false;
        if ((DateTime.Now - _readerCheckedAt).TotalSeconds > 2) { _readerOn = Accessibility.ScreenReaderRunning; _readerCheckedAt = DateTime.Now; }
        if (!_readerOn) return false;
        _navGuideGiven = true;
        Settings.Current.NavGuideHeard = true;   // une seule fois ; Réglages > Accessibilité pour la réécouter
        Settings.Save();
        PlayNavGuide();
        return true;
    }

    /// <summary>Lit l'explication en entier, clavier et souris bloqués, puis place sur le premier menu.</summary>
    private void PlayNavGuide()
    {
        if (_navGuidePlaying) return;
        _navGuidePlaying = true;
        Accessibility.InputLocked = true;
        var duration = Accessibility.AnnounceInstructions(this, NavGuideText);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration + TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _navGuidePlaying = false;
            Accessibility.InputLocked = false;
            HideSectionMark();
            NavPages[0].Focus();   // annoncé par le lecteur d'écran : « Applications »
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ contexte : page ou fenêtre ouverte
    /// <summary>Zone parcourue : la fenêtre ouverte par-dessus si elle s'y prête, sinon la page affichée ; null si les
    /// touches appartiennent à la fenêtre ouverte (tri, test du clavier ou de la souris…).</summary>
    private FrameworkElement NavRoot(out bool overlay)
    {
        overlay = false;
        var top = Overlays.Where(o => o.Visibility == Visibility.Visible).OrderBy(o => Panel.GetZIndex(o)).LastOrDefault();
        if (top == null) return Pages.FirstOrDefault(p => p.Visibility == Visibility.Visible);
        overlay = true;
        if (top == ManagerOverlay || top == ThemeEditor) return top;
        if (top == DeviceTestOverlay && _testKind is "checkup" or "bench" or "startup" or "space" or "speakers" or "mic" or "net" or "disk") return top;
        return null;
    }

    private RadioButton[] NavPages => new[] { NavApps, NavDesktop, NavDocuments, NavUpdates, NavDevices, NavSettings };

    // ------------------------------------------------------------------ sections
    /// <summary>Sections de la zone : on descend dans les conteneurs de mise en page (page, défilement, carte de fenêtre),
    /// les listes de cartes et les listes de catégories, jusqu'aux blocs que l'on voit comme un tout.</summary>
    private static List<FrameworkElement> Sections(FrameworkElement root)
    {
        var list = new List<FrameworkElement>();
        void Collect(FrameworkElement e, bool top)
        {
            if (e == null || !e.IsVisible) return;
            switch (e)
            {
                case ScrollViewer sv when sv.Content is FrameworkElement c: Collect(c, true); return;
                case ItemsControl ic when ic is not ComboBox && ic.Items.Count > 0 && IsGroupList(ic):
                    for (int i = 0; i < ic.Items.Count; i++) if (ic.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement it) Collect(it, false);
                    return;
                case ContentPresenter cp when VisualTreeHelper.GetChildrenCount(cp) == 1 && VisualTreeHelper.GetChild(cp, 0) is FrameworkElement only && only is Panel:
                    list.Add(cp); return;
            }
            if (e is Panel p && (top || ContainsScroll(e, 3) || IsCardList(p) || IsTitledStack(p)))
            {
                foreach (UIElement c in p.Children) if (c is FrameworkElement fe) Collect(fe, false);
                return;
            }
            if (e is Border { Child: FrameworkElement child } b && b is not ClickBorder && ContainsScroll(b, 3)) { Collect(child, true); return; }
            list.Add(e);
        }
        Collect(root, true);
        // Les éléments vides (séparateurs, décor) ne sont pas des sections
        return list.Where(s => Items(s).Count > 0 || SectionText(s).Length > 0).ToList();
    }

    private static bool ContainsScroll(DependencyObject d, int depth)
    {
        if (depth < 0) return false;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer || ContainsScroll(c, depth - 1)) return true;
        }
        return false;
    }

    /// <summary>Liste dont chaque élément est lui-même un groupe (catégories d'applications).</summary>
    private static bool IsGroupList(ItemsControl ic) =>
        ic.ItemContainerGenerator.ContainerFromIndex(0) is DependencyObject c && HasDescendant<ItemsControl>(c, 6);

    /// <summary>Pile de cartes (périphériques…) : chaque carte devient une section.</summary>
    private static bool IsCardList(Panel p)
    {
        var visible = p.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).ToList();
        return p is StackPanel && visible.Count >= 2 && visible.All(c => c is Border and not ClickBorder || c is Panel);
    }

    /// <summary>Pile de petits titres et de blocs (« Saisie et affichage » puis ses tuiles…) : chaque bloc devient une partie.</summary>
    private static bool IsTitledStack(Panel p)
    {
        if (p is not StackPanel) return false;
        var visible = p.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).ToList();
        return visible.Count >= 3 && visible.Any(c => c is TextBlock && IsSubLabel(c)) && visible.Count(c => c is Panel && Items(c).Count > 0) >= 2;
    }

    private static bool HasDescendant<T>(DependencyObject d, int depth) where T : class
    {
        if (depth < 0) return false;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T || HasDescendant<T>(c, depth - 1)) return true;
        }
        return false;
    }

    /// <summary>Éléments activables d'une section, dans l'ordre d'affichage.</summary>
    private static List<UIElement> Items(DependencyObject section)
    {
        var found = new List<UIElement>();
        void Walk(DependencyObject d)
        {
            if (d is UIElement u && !u.IsVisible) return;
            if (d is ButtonBase or ClickBorder or TextBox or ComboBox)
            {
                var e = (UIElement)d;
                if (e.Focusable && e.IsEnabled && KeyboardNavigation.GetIsTabStop(e)) found.Add(e);
                if (d is not ClickBorder) return;   // pas d'éléments dans un bouton ; une ligne cliquable peut contenir une case
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(section);
        return found;
    }

    /// <summary>Textes de la section, sans ceux des boutons (pour la nommer et lire les blocs de texte).</summary>
    private static string SectionText(DependencyObject section, int max = 4)
    {
        var texts = new List<string>();
        var headings = new List<string>();
        void Walk(DependencyObject d)
        {
            if (texts.Count >= max && headings.Count > 0) return;
            if (d is UIElement u && !u.IsVisible) return;
            if (d is ButtonBase or TextBox or ComboBox) return;
            if (d is TextBlock tb)
            {
                var t = AccessibleText.Clean(tb.Text);
                if (t.Length > 0)
                {
                    if (AutomationProperties.GetHeadingLevel(tb) != AutomationHeadingLevel.None || tb.FontWeight >= FontWeights.SemiBold) headings.Add(t);
                    texts.Add(t);
                }
                return;
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(section);
        // Nom propre donné dans le XAML (liste nommée) en priorité
        if (section is FrameworkElement fe && AutomationProperties.GetName(fe) is { Length: > 0 } explicitName) return explicitName;
        var parts = headings.Count > 0 ? new List<string> { headings[0] } : new List<string>();
        foreach (var t in texts) if (parts.Count < max && !parts.Contains(t)) parts.Add(t);
        var text = string.Join(". ", parts);
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    // ------------------------------------------------------------------ touches
    /// <summary>
    /// Renvoie vrai si la touche a servi à la navigation. Les quatre flèches se déplacent dans le niveau courant (menu,
    /// sections ou éléments) ; Entrée entre (ou active l'élément) ; Échap revient d'un niveau.
    /// </summary>
    private bool KeyboardNav(KeyEventArgs e)
    {
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter or Key.Escape or Key.Tab)) return false;
        var root = NavRoot(out bool overlay);
        if (root == null || _cursorA == null) return false;
        var focused = Keyboard.FocusedElement as DependencyObject;
        // Menu d'options ou liste déroulante ouverts (fenêtres surgissantes) : leurs flèches, Entrée et Échap leur appartiennent
        if (focused != null && Window.GetWindow(focused) != this) return false;
        if (focused is MenuItem or System.Windows.Controls.ContextMenu) return false;
        // Tab / Maj+Tab font comme les flèches, dans le niveau où l'on est (menu, catégories, parties, éléments), en
        // bouclant : au bout, on repart du premier
        bool tab = key == Key.Tab;
        bool arrow = tab || key is Key.Up or Key.Down or Key.Left or Key.Right;
        int step = tab ? (Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1) : key is Key.Down or Key.Right ? 1 : -1;
        int Next(int i, int count) => tab ? ((i + step) % count + count) % count : Math.Clamp(i + step, 0, count - 1);
        // Zone de saisie : gauche / droite déplacent le curseur dans le texte ; Échap remonte seulement si elle est vide
        // Zone de saisie : gauche / droite déplacent le curseur dans le texte, mais sortent de la zone quand le curseur est
        // déjà au début (gauche) ou à la fin (droite) — une zone vide se traverse donc comme un bouton
        if (focused is TextBox tbx)
        {
            if (key == Key.Enter || key == Key.Escape && tbx.Text.Length > 0) return false;
            if (key == Key.Left && (tbx.CaretIndex > 0 || tbx.SelectionLength > 0)) return false;
            if (key == Key.Right && (tbx.CaretIndex < tbx.Text.Length || tbx.SelectionLength > 0)) return false;
            if (tbx.AcceptsReturn && key is Key.Up or Key.Down)
            {
                // Texte sur plusieurs lignes : haut / bas changent de ligne, et sortent de la zone depuis la première
                // (haut) ou la dernière ligne (bas)
                int line = tbx.GetLineIndexFromCharacterIndex(tbx.CaretIndex);
                int last = Math.Max(0, tbx.LineCount - 1);
                if (key == Key.Up && line > 0 || key == Key.Down && line >= 0 && line < last) return false;
            }
        }
        // Liste déroulante ouverte : ses touches lui appartiennent
        if (focused is ComboBox { IsDropDownOpen: true } || focused is ComboBoxItem) return false;

        var nav = NavPages.Cast<UIElement>().Append(NavScreenshot).ToList();
        bool inMenu = focused is UIElement fu && nav.Contains(fu);

        // ---------- menu
        if (!overlay && (inMenu || focused == null || !IsWithin(focused, root) && CurrentCursor == null))
        {
            int i = inMenu ? nav.IndexOf((UIElement)focused) : Math.Max(0, Array.FindIndex(NavPages, n => n.IsChecked == true));
            if (arrow) { nav[Next(i, nav.Count)].Focus(); return true; }   // on écoute, on ne change pas encore de page
            if (key != Key.Enter) return false;
            if (!inMenu) return EnterSection(root, 0);
            if (nav[i] is RadioButton r)
            {
                r.IsChecked = true;
                // La page vient d'être affichée : ses éléments existent au rendu suivant
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (NavRoot(out _) is { } page) EnterSection(page, 0); });
                return true;
            }
            Click(nav[i]);
            return true;
        }

        var top = Tree(root);
        if (top.Count == 0) return false;

        // ---------- catégories et parties
        if (CurrentCursor is { } cursor)
        {
            var node = FindNode(top, cursor.Section) ?? top[0];
            var siblings = node.Parent?.Children ?? top;
            int i = Math.Max(0, siblings.IndexOf(node));
            if (arrow) { ShowNode(siblings[Next(i, siblings.Count)], top); return true; }
            if (key == Key.Enter)
            {
                if (node.Category) { ShowNode(node.Children[0], top); return true; }
                var items = Items(node.Element);
                if (items.Count == 0) ShowNode(node, top);   // texte seul : relu
                else items[0].Focus();
                return true;
            }
            if (node.Parent != null) { ShowNode(node.Parent, top); return true; }   // Échap : remonte à la catégorie
            if (overlay) return false;   // Échap dans une fenêtre ouverte : elle se ferme comme avant
            BackToMenu();
            return true;
        }

        // ---------- éléments
        var leaves = Leaves(top);
        var leaf = leaves.FirstOrDefault(l => IsWithin(focused, l.Element) || l.Labels.Any(x => IsWithin(focused, x)));
        if (leaf == null)
        {
            // Titre de catégorie lu au survol : les touches reprennent depuis cette catégorie
            if (focused is FrameworkElement fl && FindNode(top, fl) is { } titled && (arrow || key == Key.Enter)) { ShowNode(titled, top); return true; }
            return arrow && EnterSection(root, 0);
        }
        var list = Items(leaf.Element);
        // L'élément lui-même d'abord : une tuile de dossier contient ses sous-dossiers, qui sont aussi des éléments ;
        // sinon l'élément qui le contient (texte survolé dans une tuile), le plus proche
        int k = focused is UIElement fe0 ? list.IndexOf(fe0) : -1;
        if (k < 0) k = list.FindLastIndex(x => IsWithin(focused, x));
        if (key == Key.Escape) { ShowNode(leaf, top); return true; }
        if (arrow)
        {
            if (list.Count == 0) return true;
            int next = k < 0 ? 0 : Next(k, list.Count);
            list[next].Focus();
            (list[next] as FrameworkElement)?.BringIntoView();
            return true;
        }
        // Entrée : active l'élément comme un clic (case cochée / décochée, choix sélectionné…) puis garde la place
        if (k < 0) return false;
        ActivateAndStay(list[k], root, leaves.IndexOf(leaf), k);
        return true;
    }

    // ------------------------------------------------------------------ arbre : catégories > parties
    /// <summary>Une catégorie (titre comme « État des périphériques » et ce qui le suit) ou une partie (bloc, carte).</summary>
    private sealed class NavNode
    {
        public FrameworkElement Element;
        public string Text;
        public bool Category;
        public NavNode Parent;
        public List<NavNode> Children = new();
        /// <summary>Titres lus avec la partie (« Saisie et affichage » au-dessus de ses tuiles) : encadrés avec elle.</summary>
        public List<FrameworkElement> Labels = new();

        /// <summary>Ce qui est encadré : titres, partie, et pour une catégorie tout ce qu'elle contient.</summary>
        public IEnumerable<FrameworkElement> Marks => Labels.Append(Element).Concat(Children.SelectMany(c => c.Marks));
    }

    /// <summary>
    /// Parties de la zone, regroupées sous les titres de catégorie qui les précèdent (« ÉTAT DES PÉRIPHÉRIQUES » puis
    /// chaque carte de périphérique) : la catégorie se choisit avec son titre et contient ses parties.
    /// </summary>
    private static List<NavNode> Tree(FrameworkElement root)
    {
        var top = new List<NavNode>();
        NavNode cat = null;
        var flat = Sections(root);
        for (int i = 0; i < flat.Count; i++)
        {
            var e = flat[i];
            if (IsCategoryLabel(e)) { cat = new NavNode { Element = e, Text = Speakable(SectionText(e)), Category = true }; top.Add(cat); continue; }
            var n = new NavNode { Element = e, Text = SectionText(e) };
            // Petit titre suivi de son contenu (« Saisie et affichage » puis ses tuiles) : une seule partie, lue avec son titre
            if (i + 1 < flat.Count && IsSubLabel(e) && Items(flat[i + 1]).Count > 0 && !IsCategoryLabel(flat[i + 1]))
            {
                var next = flat[++i];
                n = new NavNode { Element = next, Text = Join(Speakable(SectionText(e)), SectionText(next)), Labels = { e } };
            }
            if (cat != null) { n.Parent = cat; cat.Children.Add(n); } else top.Add(n);
        }
        for (int i = 0; i < top.Count; i++)
        {
            var c = top[i];
            if (!c.Category) continue;
            if (c.Children.Count == 0) { c.Category = false; continue; }   // titre seul : simple texte
            if (c.Children.Count == 1)
            {
                // Un titre et un seul bloc : c'est une seule partie, lue et encadrée avec son titre
                var only = c.Children[0];
                only.Parent = null;
                only.Text = Join(c.Text, only.Text);
                only.Labels.Insert(0, c.Element);
                top[i] = only;
            }
        }
        return top;
    }

    private static string Join(string a, string b) =>
        string.IsNullOrEmpty(b) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase) ? (string.IsNullOrEmpty(b) ? a : b)
        : string.IsNullOrEmpty(a) ? b : a + ", " + b;

    /// <summary>Petit titre : un texte seul et court, au-dessus d'un bloc.</summary>
    private static bool IsSubLabel(FrameworkElement e) =>
        e is TextBlock tb && Items(e).Count == 0 && AccessibleText.Clean(tb.Text) is { Length: > 0 and <= 80 };

    private static List<NavNode> Leaves(List<NavNode> top) => top.SelectMany(n => n.Category ? n.Children : new List<NavNode> { n }).ToList();

    private static NavNode FindNode(List<NavNode> top, FrameworkElement element) =>
        element == null ? null : top.Concat(top.SelectMany(t => t.Children)).FirstOrDefault(n => n.Element == element || n.Labels.Contains(element));

    /// <summary>Titre de catégorie : un texte seul, en majuscules (« OUTILS DE TEST ») ou marqué comme titre de section.</summary>
    private static bool IsCategoryLabel(FrameworkElement e)
    {
        if (Items(e).Count > 0) return false;
        TextBlock tb = e as TextBlock;
        if (tb == null && e is Panel p)
        {
            var texts = p.Children.OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible && AccessibleText.Clean(t.Text).Length > 0).ToList();
            tb = texts.FirstOrDefault(t => AutomationProperties.GetHeadingLevel(t) is AutomationHeadingLevel.Level2 or AutomationHeadingLevel.Level3);
            if (tb == null || texts.Count > 3) return false;
            return true;
        }
        if (tb == null) return false;
        var text = AccessibleText.Clean(tb.Text);
        if (AutomationProperties.GetHeadingLevel(tb) is AutomationHeadingLevel.Level2 or AutomationHeadingLevel.Level3) return true;
        var letters = text.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.All(char.IsUpper);
    }

    /// <summary>« ÉTAT DES PÉRIPHÉRIQUES » → « État des périphériques » (les majuscules sont épelées par certains lecteurs d'écran).</summary>
    private static string Speakable(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 4 || !letters.All(char.IsUpper)) return text;
        var lower = text.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    /// <summary>Même effet qu'un clic de souris sur l'élément.</summary>
    private static void Click(UIElement el)
    {
        switch (el)
        {
            case ButtonBase b:
                // OnClick : coche / décoche, sélectionne, puis déclenche l'action — exactement comme un clic
                typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(b, null);
                break;
            case ClickBorder cb: cb.Activate(); break;
            case ComboBox c: c.Focus(); c.IsDropDownOpen = true; break;
        }
    }

    /// <summary>
    /// Active l'élément, puis, si l'écran a été reconstruit (un choix qui redessine la fenêtre), remet le focus au même
    /// endroit : même section, même rang. On ne se perd pas.
    /// </summary>
    private void ActivateAndStay(UIElement el, FrameworkElement root, int sectionIndex, int itemIndex)
    {
        // Action longue (le bouton se désactive le temps du travail) : le focus ne doit pas se perdre. S'il n'est allé nulle
        // part (l'action peut le placer elle-même sur son suivi), on reste sur la partie, sans revenir en arrière
        if (el is UIElement ue)
        {
            DependencyPropertyChangedEventHandler h = null;
            h = (_, _) =>
            {
                ue.IsEnabledChanged -= h;
                if (ue.IsEnabled) return;
                DispatcherTimerOnce(TimeSpan.FromSeconds(2), () =>
                {
                    var f = Keyboard.FocusedElement as DependencyObject;
                    if (f != null && f is not MainWindow && (f as UIElement)?.IsVisible == true && (f as UIElement)?.IsEnabled == true) return;
                    if (NavRoot(out _) is not { } r || !IsWithin(ue, r)) return;
                    var t = Tree(r);
                    var leaf = Leaves(t).FirstOrDefault(l => IsWithin(ue, l.Element));
                    if (leaf != null) ShowNode(leaf, t);
                });
            };
            ue.IsEnabledChanged += h;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                DispatcherTimerOnce(TimeSpan.FromSeconds(15), () => ue.IsEnabledChanged -= h));
        }
        Click(el);
        if (el is ComboBox) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            var now = Keyboard.FocusedElement as DependencyObject;
            // Menu contextuel, autre fenêtre, application lancée : on ne touche à rien
            if (now != null && Window.GetWindow(now) is { } w && w != this) return;
            if (now is DependencyObject n && n is not MainWindow && IsWithinWindow(n) && (n as UIElement)?.IsVisible == true && n != root) return;   // le focus est déjà quelque part de sensé
            var newRoot = NavRoot(out _);
            if (newRoot == null) return;
            if (newRoot != root) { EnterSection(newRoot, 0); return; }   // une fenêtre s'est ouverte ou fermée
            if (el.IsVisible && IsWithin(el, root)) { el.Focus(); return; }
            var top = Tree(root);
            var leaves = Leaves(top);
            if (leaves.Count == 0) return;
            var leaf = leaves[Math.Clamp(sectionIndex, 0, leaves.Count - 1)];
            var items = Items(leaf.Element);
            if (items.Count == 0) { ShowNode(leaf, top); return; }
            items[Math.Clamp(itemIndex, 0, items.Count - 1)].Focus();
        });
    }

    private void DispatcherTimerOnce(TimeSpan delay, Action action)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }

    private bool IsWithinWindow(DependencyObject d) => IsWithin(d, this);

    private bool EnterSection(FrameworkElement root, int index)
    {
        var top = Tree(root);
        if (top.Count == 0) return false;
        ShowNode(top[Math.Clamp(index, 0, top.Count - 1)], top);
        return true;
    }

    private void BackToMenu()
    {
        var page = NavPages.FirstOrDefault(n => n.IsChecked == true) ?? NavApps;
        HideSectionMark();
        page.Focus();
    }

    /// <summary>
    /// Annonce une catégorie (« État des périphériques, catégorie, 6 parties, 3 sur 4 ») ou une partie (« Clavier et souris,
    /// 5 éléments, 2 sur 6 ») et l'encadre ; une catégorie est encadrée avec tout ce qu'elle contient.
    /// </summary>
    private void ShowNode(NavNode node, List<NavNode> top)
    {
        var siblings = node.Parent?.Children ?? top;
        int pos = siblings.IndexOf(node) + 1;
        var cursor = CurrentCursor == _cursorA ? _cursorB : _cursorA;
        cursor.Section = node.Element;
        string text = node.Text.Length > 0 ? node.Text : "Partie";
        if (node.Category)
        {
            cursor.Kind = "catégorie";
            cursor.Label = $"Catégorie {text}, {pos} sur {siblings.Count}, {node.Children.Count} partie{(node.Children.Count > 1 ? "s" : "")}";
        }
        else
        {
            int items = Items(node.Element).Count;
            cursor.Kind = "partie";
            cursor.Label = $"Partie {text}, {pos} sur {siblings.Count}" + (items > 0 ? $", {items} élément{(items > 1 ? "s" : "")}" : "");
        }
        node.Element.BringIntoView();
        MarkSection(node.Marks);
        cursor.Focus();
    }

    // ------------------------------------------------------------------ repère visuel de la section
    private readonly List<SectionAdorner> _marks = new();

    private void MarkSection(IEnumerable<FrameworkElement> parts)
    {
        HideSectionMark();
        foreach (var part in parts)
        {
            var layer = AdornerLayer.GetAdornerLayer(part);
            if (layer == null) continue;
            var m = new SectionAdorner(part);
            layer.Add(m);
            _marks.Add(m);
        }
    }

    internal void HideSectionMarkPublic() => HideSectionMark();

    private void HideSectionMark()
    {
        foreach (var m in _marks) AdornerLayer.GetAdornerLayer(m.AdornedElement)?.Remove(m);
        _marks.Clear();
    }

    private static bool IsWithin(DependencyObject d, DependencyObject container)
    {
        for (var p = d; p != null; p = p is Visual || p is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p == container) return true;
        return false;
    }

    private sealed class SectionAdorner : Adorner
    {
        public SectionAdorner(UIElement e) : base(e) { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext dc)
        {
            var r = new Rect(AdornedElement.RenderSize);
            r.Inflate(5, 5);
            // Repère bien visible, sur fond clair comme sombre : voile léger, liseré sombre et contour jaune épais
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xD6, 0x0A)), new Pen(Brushes.Black, 5), r, 12, 12);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)), 3), r, 12, 12);
        }
    }
}

/// <summary>
/// Élément invisible qui porte le focus au niveau « section » : le lecteur d'écran l'annonce avec le nom de la section,
/// son nombre d'éléments et sa position, et encadre la section elle-même.
/// </summary>
public sealed class SectionCursor : FrameworkElement
{
    public FrameworkElement Section { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Type annoncé par le lecteur d'écran : « catégorie » ou « partie ».</summary>
    public string Kind { get; set; } = "partie";
    public string Help { get => AutomationProperties.GetHelpText(this); set => AutomationProperties.SetHelpText(this, value ?? ""); }

    public SectionCursor()
    {
        Width = 1; Height = 1;
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top;
        Focusable = true; IsHitTestVisible = false;
        FocusVisualStyle = null;   // la section elle-même est encadrée
        KeyboardNavigation.SetIsTabStop(this, false);
        LostKeyboardFocus += (_, _) =>
        {
            if (Window.GetWindow(this) is MainWindow w && Keyboard.FocusedElement is not SectionCursor) w.HideSectionMarkPublic();
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : FrameworkElementAutomationPeer
    {
        public Peer(SectionCursor owner) : base(owner) { }
        private SectionCursor C => (SectionCursor)Owner;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => "Section";
        protected override string GetLocalizedControlTypeCore() => "";   // le type est dit en premier, dans le nom
        protected override string GetNameCore() => C.Label;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsOffscreenCore() => false;
        /// <summary>Le lecteur d'écran encadre la section, pas ce petit élément.</summary>
        protected override Rect GetBoundingRectangleCore()
        {
            try
            {
                if (C.Section is { IsVisible: true } s && PresentationSource.FromVisual(s) != null)
                {
                    var tl = s.PointToScreen(new Point(0, 0));
                    var br = s.PointToScreen(new Point(s.ActualWidth, s.ActualHeight));
                    return new Rect(tl, br);
                }
            }
            catch { }
            return base.GetBoundingRectangleCore();
        }
    }
}
