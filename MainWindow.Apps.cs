using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>Page Applications : liste par thème, recherche, tri en ligne, assistant de tri, tuiles.</summary>
public partial class MainWindow
{
    // =====================================================================================
    //  Page Applications
    // =====================================================================================
    private bool AppFilter(object o)
    {
        if (o is not AppItem a) return false;
        var q = SearchBox?.Text.Trim() ?? "";
        if (q.Length > 0)
        {
            var hay = a.Name + " " + a.Theme;
            return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => Cmp.IndexOf(hay, w, CmpOpts) >= 0);
        }
        return _chip == null || a.Theme == _chip;
    }

    private void BuildChips()
    {
        ChipsHost.Children.Clear();
        // Comptes identiques à ce qui est affiché : une suite (Git, LibreOffice…) compte pour une tuile
        var tiles = AppSuites.Group(_apps.Where(a => !Settings.IsHidden(a.Name))).ToList();
        AddChip(null, "Toutes", tiles.Count, null);
        foreach (var cat in Settings.AllCategoryNames)
        {
            int n = tiles.Count(a => a.Theme == cat);
            if (n > 0) AddChip(cat, cat, n, Settings.BrushOf(cat));
        }
        if (_chip != null && !_apps.Any(a => a.Theme == _chip)) { _chip = null; ((RadioButton)ChipsHost.Children[0]).IsChecked = true; }
    }

    private void AddChip(string key, string label, int count, Brush dot)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot != null) sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = count.ToString(), Margin = new Thickness(8, 0, 0, 0), Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
        var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = sp, Tag = key, IsChecked = _chip == key };
        rb.Checked += (s, _) =>
        {
            _chip = (string)((RadioButton)s).Tag;
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            RefreshApps();
            AppsScroll.ScrollToTop();
        };
        ChipsHost.Children.Add(rb);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshApps();
        AppsScroll.ScrollToTop();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var first = _appGroups.FirstOrDefault()?.Items.FirstOrDefault();
            if (first != null) Launch(first.IsSuite ? first.Members[0] : first);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; }
    }

    // Taper n'importe où sur la page Applications lance la recherche
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (PageApps.Visibility != Visibility.Visible || Keyboard.FocusedElement is TextBox) return;
        if (SorterOverlay.Visibility == Visibility.Visible || WelcomeOverlay.Visibility == Visibility.Visible
            || DocSorterOverlay.Visibility == Visibility.Visible || PromptOverlay.Visibility == Visibility.Visible
            || ManagerOverlay.Visibility == Visibility.Visible || DocPlanOverlay.Visibility == Visibility.Visible
            || DeviceTestOverlay.Visibility == Visibility.Visible) { e.Handled = true; return; }
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        SearchBox.Focus();
        SearchBox.AppendText(e.Text);
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    // ---------- Tri en ligne des applications restées dans « Autres »
    private bool _onlineRunning;
    private async void Online_Click(object sender, RoutedEventArgs e) => await RunOnlineAsync(silent: false);

    private async Task RunOnlineAsync(bool silent)
    {
        var todo = _apps.Where(a => a.Theme == Settings.Current.DefaultCategory && !Settings.IsHidden(a.Name)
                                    && !OnlineClassifier.AlreadySearched(a.Name)).ToList();
        if (todo.Count == 0 || _onlineRunning) { if (!silent) ShowToast("Aucune nouvelle application à identifier en ligne."); return; }
        if (!await Network.IsOnlineAsync()) { if (!silent) ShowToast("Pas de connexion Internet : le classement en ligne reprendra ultérieurement."); return; }
        _onlineRunning = true;
        UpdateAppsState();
        try
        {
            int found = await OnlineClassifier.ClassifyAsync(todo, null);
            ReclassifyAll();
            if (found > 0) ShowToast($"{found} application(s) classée(s) automatiquement grâce à Internet");
        }
        catch (Exception ex) { if (!silent) ShowToast("Recherche en ligne impossible : " + ex.Message); }
        finally
        {
            _onlineRunning = false;
            RefreshAll();
        }
    }

    // ---------- Assistant de tri
    // Une entrée par tuile : une suite (Git, LibreOffice…) se classe en une fois avec toutes ses applications.
    private List<AppItem> _sortQueue = new();
    private int _sortIndex;
    private readonly Stack<(int index, List<(string name, string prevOverride, bool prevHidden, bool prevSkipped)> states)> _sortHistory = new();

    private static IEnumerable<AppItem> MembersOf(AppItem a) => a.IsSuite ? a.Members : new List<AppItem> { a };

    private List<AppItem> SortCandidates(bool includeSkipped) => AppSuites.Group(_apps.Where(a => !Settings.IsHidden(a.Name)))
        .Where(a => a.Theme == Settings.Current.DefaultCategory && a.IsShortcut
                    && (includeSkipped || !MembersOf(a).All(m => Settings.IsSkipped(m.Name))))
        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void OpenSorter_Click(object sender, RoutedEventArgs e) => OpenSorter(includeSkipped: false);
    private void OpenSorterAll_Click(object sender, RoutedEventArgs e) => OpenSorter(includeSkipped: true);
    private void ReviewSort_Click(object sender, RoutedEventArgs e) => OpenSorter(includeSkipped: true, review: true);

    /// <summary>Mode « vérifier » : passe en revue les applications triées automatiquement (pas celles choisies à la main).</summary>
    private bool _sortReview;

    private void OpenSorter(bool includeSkipped, bool review = false)
    {
        _sortReview = review;
        _sortQueue = review
            ? AppSuites.Group(_apps.Where(a => !Settings.IsHidden(a.Name)))
                   .Where(a => a.IsShortcut && !MembersOf(a).All(m => Settings.Current.Overrides.ContainsKey(m.Name)))
                   .OrderBy(a => a.ThemeOrder).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : SortCandidates(includeSkipped);
        if (_sortQueue.Count == 0)
        {
            ShowToast(review ? "Toutes les applications ont déjà une catégorie définie manuellement." : "Toutes les applications sont déjà classées.");
            return;
        }
        SorterSkipText.Text = review ? "Catégorie correcte" : "Laisser dans Autres";
        SorterSkipGlyph.Text = review ? "\uE73E" : "\uE893";
        _sortIndex = 0;
        _sortHistory.Clear();
        BuildSorterThemes();
        ShowSorterItem();
        SorterOverlay.Visibility = Visibility.Visible;
        Keyboard.ClearFocus();
        Focus();
    }

    private void BuildSorterThemes()
    {
        SorterThemes.Children.Clear();
        int i = 1;
        foreach (var cat in Settings.Current.Categories.Select(c => c.Name))
        {
            var key = i <= 9 ? i.ToString() : "";
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var sq = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Settings.BrushOf(cat) };
            sq.Child = new TextBlock { Text = Settings.GlyphOf(cat), FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, Foreground = Brushes.White,
                                       HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(sq);
            sp.Children.Add(new TextBlock { Text = cat, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            if (key != "")
                sp.Children.Add(new TextBlock { Text = key, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Style = (Style)FindResource("BaseButton"), Content = sp, Tag = cat, Margin = new Thickness(0, 0, 10, 10),
                                   Width = 242, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8, 10, 8) };
            btn.Click += (s, _) => SorterAssign((string)((Button)s).Tag);
            SorterThemes.Children.Add(btn);
            i++;
        }
        // Aucune catégorie ne convient : l'utilisateur en crée une, et l'application y est rangée aussitôt
        var add = new StackPanel { Orientation = Orientation.Horizontal };
        add.Children.Add(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("Line"),
            Child = new TextBlock { Text = "\uE710", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13,
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        add.Children.Add(new TextBlock { Text = "Nouvelle catégorie…", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var addBtn = new Button { Style = (Style)FindResource("BaseButton"), Content = add, Margin = new Thickness(0, 0, 10, 10), Width = 242,
                                  HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8, 10, 8),
                                  ToolTip = "Créer une catégorie et y ranger cette application (touche N)" };
        addBtn.Click += async (_, _) => await SorterNewCategoryAsync();
        SorterThemes.Children.Add(addBtn);
    }

    /// <summary>Demande le nom d'une nouvelle catégorie. Renvoie son nom, ou null si l'utilisateur annule.</summary>
    private async Task<string> AskNewCategoryAsync(string forApp)
    {
        var name = await PromptAsync("Nouvelle catégorie",
            forApp == null ? "Nom de la catégorie." : $"« {forApp} » y sera rangée. Vous pourrez la renommer, changer sa couleur et ajouter des mots-clés dans Réglages.",
            "", Settings.CheckCategoryName);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Settings.CreateCategory(name).Name;
    }

    private async Task SorterNewCategoryAsync()
    {
        if (_sortIndex >= _sortQueue.Count) return;
        var name = await AskNewCategoryAsync(_sortQueue[_sortIndex].Name);
        if (name == null) { Focus(); return; }
        BuildSorterThemes();
        SorterAssign(name);
        Focus();
    }

    private void ShowSorterItem()
    {
        if (_sortIndex >= _sortQueue.Count) { CloseSorter(done: true); return; }
        var a = _sortQueue[_sortIndex];
        var main = MembersOf(a).First();
        var suite = a.IsSuite ? $"Suite de {a.Members.Count} applications : {string.Join(", ", a.Members.Take(5).Select(m => m.Name))}{(a.Members.Count > 5 ? "…" : "")}. " : "";
        SorterName.Text = a.Name;
        SorterHint.Text = suite + (_sortReview
            ? $"Actuellement : {a.Theme} — {Settings.Explain(main.Name, main.Target, main.Context, main.LooksLikeGame)}"
            : string.IsNullOrWhiteSpace(main.Context) ? "Aucune information sur cette application : choisissez sa catégorie, ou créez-en une."
                                                      : main.Context);
        SorterHint.ToolTip = SorterHint.Text;
        // Thème actuel mis en évidence
        foreach (var b in SorterThemes.Children.OfType<Button>())
        {
            if (_sortReview && b.Tag is string t && t == a.Theme)
            {
                b.BorderBrush = (Brush)FindResource("Accent");
                b.BorderThickness = new Thickness(2);
            }
            else { b.ClearValue(Control.BorderBrushProperty); b.ClearValue(Control.BorderThicknessProperty); }
        }
        SorterIcon.Source = a.Icon;
        SorterInitial.Text = a.Initial;
        SorterInitialBox.Visibility = a.Icon == null ? Visibility.Visible : Visibility.Collapsed;
        SorterProgress.Text = $"{_sortIndex + 1} / {_sortQueue.Count}";
        SorterBar.Maximum = _sortQueue.Count;
        SorterBar.Value = _sortIndex;
        SorterBackButton.IsEnabled = _sortHistory.Count > 0;
    }

    private void Remember(AppItem a)
    {
        var states = MembersOf(a).Select(m =>
        {
            Settings.Current.Overrides.TryGetValue(m.Name, out var prev);
            return (m.Name, prev, Settings.IsHidden(m.Name), Settings.IsSkipped(m.Name));
        }).ToList();
        _sortHistory.Push((_sortIndex, states));
    }

    private void Reclassify(string name)
    {
        foreach (var x in _apps.Concat(_desk).Where(x => x.IsShortcut && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
    }

    /// <summary>Après un changement de classement : déductions des applications voisines recalculées, bureau compris.</summary>
    private void ReclassifyAll()
    {
        Affinity.Compute(_apps);
        foreach (var d in _desk.Where(d => d.IsShortcut && !_apps.Contains(d)))
            d.Theme = Settings.Classify(d.Name, d.Target, d.Context, d.LooksLikeGame);
    }

    private void SorterAssign(string category)
    {
        if (_sortIndex >= _sortQueue.Count) return;
        var a = _sortQueue[_sortIndex];
        Remember(a);
        foreach (var m in MembersOf(a))
        {
            Settings.Current.Overrides[m.Name] = category;
            Settings.SetSkipped(m.Name, false);
            Reclassify(m.Name);
        }
        Settings.Save();
        _sortIndex++;
        ShowSorterItem();
    }

    private void SorterSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_sortIndex >= _sortQueue.Count) return;
        var a = _sortQueue[_sortIndex];
        Remember(a);
        if (!_sortReview) foreach (var m in MembersOf(a)) Settings.SetSkipped(m.Name, true);   // en vérification : le thème est bon
        _sortIndex++;
        ShowSorterItem();
    }

    private void SorterBack_Click(object sender, RoutedEventArgs e)
    {
        if (_sortHistory.Count == 0) return;
        var (index, states) = _sortHistory.Pop();
        foreach (var (name, prevOverride, prevHidden, prevSkipped) in states)
        {
            Settings.SetOverride(name, prevOverride);
            Settings.SetHidden(name, prevHidden);
            Settings.SetSkipped(name, prevSkipped);
            Reclassify(name);
        }
        _sortIndex = index;
        ShowSorterItem();
    }

    private void CloseSorter_Click(object sender, RoutedEventArgs e) => CloseSorter(done: false);

    private void CloseSorter(bool done)
    {
        SorterOverlay.Visibility = Visibility.Collapsed;
        int n = _sortHistory.Count;
        if (n > 0) ReclassifyAll();
        RefreshAll();
        RefreshSettingsPage();
        if (n > 0) ShowToast(done ? "Classement terminé et enregistré" : $"{n} application(s) classée(s) et enregistrée(s)");
        // En mode « dossiers », les raccourcis rangés suivent leur nouveau thème
        if (n > 0 && DesktopManager.FoldersMode)
        {
            DesktopManager.Refile(_desk.Where(x => x.Location == ItemLocation.Stored).ToList());
            _ = AfterTidyAsync();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (WelcomeOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key is Key.Enter or Key.Escape) { Welcome_Click(this, null); e.Handled = true; }
            return;
        }
        if (AppUpdateOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape && AppUpdateLaterButton.IsEnabled) { AppUpdateLater_Click(this, null); e.Handled = true; }
            return;
        }
        if (PromptOverlay.Visibility == Visibility.Visible) return;   // la zone de saisie gère Entrée / Échap
        if (DeviceTestOverlay.Visibility == Visibility.Visible)
        {
            if (DeviceTestKey(e, true)) e.Handled = true;
            return;
        }
        if (ManagerOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { ManagerOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
            return;
        }
        if (DocPlanOverlay.Visibility == Visibility.Visible)
        {
            if (DocPlanKey(e.Key)) e.Handled = true;
            return;
        }
        if (DocSorterOverlay.Visibility == Visibility.Visible)
        {
            if (DocSorterKey(e.Key)) e.Handled = true;
            return;
        }
        if (ThemeEditor.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { ThemeEditor.Visibility = Visibility.Collapsed; e.Handled = true; }
            return;
        }
        if (SorterOverlay.Visibility != Visibility.Visible) return;
        int digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 + 1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 + 1 : 0;
        if (digit > 0 && digit <= Settings.Current.Categories.Count) SorterAssign(Settings.Current.Categories[digit - 1].Name);
        else if (e.Key == Key.Space) SorterSkip_Click(this, null);
        else if (e.Key == Key.N) _ = SorterNewCategoryAsync();
        else if (e.Key == Key.Back) SorterBack_Click(this, null);
        else if (e.Key == Key.Escape) CloseSorter(done: false);
        else return;
        e.Handled = true;
    }


    // =====================================================================================
    //  Tuiles (communes aux pages)
    // =====================================================================================
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppItem a) return;
        if (DateTime.Now - _lastDrag < TimeSpan.FromMilliseconds(400)) return;   // fin d'un glisser-déposer, pas un clic
        if (a.IsSuite) ShowSuiteMenu((UIElement)sender, a, full: false);
        else Launch(a);
    }

    /// <summary>Menu d'une suite : ses applications (avec leur icône), puis les actions communes.</summary>
    private void ShowSuiteMenu(UIElement target, AppItem suite, bool full)
    {
        var menu = new ContextMenu();
        bool annexHeader = false;
        foreach (var m in suite.Members)
        {
            var app = m;
            // Éléments annexes (désinstalleur, aide, contenu bonus…) à la fin, sous un intitulé
            if (app.IsCompanion && !annexHeader)
            {
                annexHeader = true;
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Compléments", IsEnabled = false, FontSize = 11 });
            }
            var it = new MenuItem { Header = app.Label, FontWeight = app == suite.Members[0] ? FontWeights.SemiBold : FontWeights.Normal };
            if (app.Icon != null) it.Icon = new Image { Source = app.Icon, Width = 18, Height = 18 };
            it.ToolTip = app.IsObsolete ? app.ObsoleteTip : "Clic droit : plus d'options";
            it.Click += (_, _) => Launch(app);
            // Clic droit sur une application de la suite : son propre menu (comme pour une tuile)
            it.PreviewMouseRightButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                menu.IsOpen = false;
                var sub = BuildAppMenu(app);
                sub.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                sub.IsOpen = true;
            };
            menu.Items.Add(it);
        }
        if (full)
        {
            menu.Items.Add(new Separator());
            var themeMenu = MakeItem("Changer de catégorie", "\uE790", null);
            foreach (var cat in Settings.AllCategoryNames)
            {
                var c = cat;
                themeMenu.Items.Add(MakeItem(c, suite.Theme == c ? "\uE73E" : null, () => ChangeTheme(suite, c)));
            }
            themeMenu.Items.Add(new Separator());
            themeMenu.Items.Add(MakeItem("Nouvelle catégorie…", "\uE710", () => _ = NewCategoryForAsync(suite)));
            themeMenu.Items.Add(MakeItem("Automatique", "\uE895", () => ChangeTheme(suite, null)));
            menu.Items.Add(themeMenu);
            menu.Items.Add(MakeItem("Désinstaller…", "\uE74D", () => OpenManager(suite.Name, obsolete: false)));
            menu.Items.Add(new Separator());
            AddGroupActions(menu, suite);
        }
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private string _lastLaunchKey;
    private DateTime _lastLaunchAt;
    /// <summary>
    /// Un double clic (ou deux clics rapprochés) sur une tuile ne doit ouvrir l'application qu'une fois :
    /// un second lancement de la même application dans les 2,5 secondes est ignoré.
    /// </summary>
    private static readonly TimeSpan LaunchDebounce = TimeSpan.FromSeconds(2.5);

    private void Launch(AppItem a)
    {
        var key = a.FilePath ?? a.Aumid ?? a.Name;
        var now = DateTime.UtcNow;
        if (key == _lastLaunchKey && now - _lastLaunchAt < LaunchDebounce) return;
        _lastLaunchKey = key;
        _lastLaunchAt = now;
        RememberLaunch(a);
        try
        {
            if (a.FilePath != null)
                Process.Start(new ProcessStartInfo(a.FilePath) { UseShellExecute = true });
            else if (a.Aumid != null)
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + a.Aumid) { UseShellExecute = true });
            ShowToast($"Lancement de {a.Label}…");
        }
        catch (Exception ex) { ShowToast($"Impossible de lancer {a.Name} : {ex.Message}"); }
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppItem a) return;
        e.Handled = true;
        if (a.IsSuite) { ShowSuiteMenu((UIElement)sender, a, full: true); return; }
        var menu = BuildAppMenu(a);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    /// <summary>Chemin compris par l'Explorateur : le raccourci, sinon l'application du Store.</summary>
    private static string ShellPath(AppItem a) =>
        a.FilePath != null && (File.Exists(a.FilePath) || Directory.Exists(a.FilePath)) ? a.FilePath
        : a.Aumid != null ? @"shell:AppsFolder\" + a.Aumid : null;

    /// <summary>Programme lancé par le raccourci (pour « Ouvrir l'emplacement du fichier »), ou null.</summary>
    private static string TargetExe(AppItem a)
    {
        var t = a.FilePath != null && a.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Shortcuts.GetTargetPath(a.FilePath)
              : AppScanner.ResolveAppsFolderPath(a.Aumid);
        t = AppScanner.NormalizePath(t);
        return t != null && File.Exists(t) ? t : null;
    }

    /// <summary>Menu d'une application (tuile ou application d'une suite), proche de celui de Windows.</summary>
    private ContextMenu BuildAppMenu(AppItem a)
    {
        var menu = new ContextMenu();

        menu.Items.Add(MakeItem("Ouvrir", "\uE8A7", () => Launch(a)));
        if (a.FilePath != null && !a.FilePath.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && !AppKinds.IsHosted(a.Target))
            menu.Items.Add(MakeItem("Exécuter en tant qu'administrateur", "\uE7EF", () =>
            {
                try { Process.Start(new ProcessStartInfo(a.FilePath) { UseShellExecute = true, Verb = "runas" }); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
                catch (Exception ex) { ShowToast($"Impossible de lancer {a.Label} : {ex.Message}"); }
            }));
        var exe = TargetExe(a);
        if (exe != null)
            menu.Items.Add(MakeItem("Ouvrir l'emplacement du fichier", "\uE838", () => DocumentLibrary.ShowInExplorer(exe)));
        menu.Items.Add(new Separator());

        bool fav = Settings.Current.Favorites.Contains(a.Name, StringComparer.OrdinalIgnoreCase);
        menu.Items.Add(MakeItem(fav ? "Retirer des favoris" : "Ajouter aux favoris", fav ? "" : "", () =>
        {
            if (fav) Settings.Current.Favorites.RemoveAll(n => n.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
            else Settings.Current.Favorites.Add(a.Name);
            Settings.Save(); RefreshFavorites();
        }));

        if (a.IsShortcut)
            menu.Items.Add(MakeItem("Masquer cette application", "\uED1A", () =>
            {
                Settings.SetHidden(a.Name, true);
                RefreshAll();
                ShowToast($"{a.Name} masquée (réversible dans Réglages)");
            }));

        // Actions de bureau (si l'élément vient du bureau ou du rangement)
        var deskItem = _desk.FirstOrDefault(d => d.FilePath != null && d.FilePath == a.FilePath);
        if (deskItem != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem(deskItem.Keep ? "Ne plus conserver sur le bureau" : "Toujours conserver sur le bureau", "", () =>
            {
                var keep = !deskItem.Keep;
                Settings.Current.KeepOnDesktop.RemoveAll(n => n.Equals(deskItem.Name, StringComparison.OrdinalIgnoreCase));
                if (keep) Settings.Current.KeepOnDesktop.Add(deskItem.Name);
                Settings.Save();
                deskItem.Keep = keep;
                if (keep && deskItem.Location == ItemLocation.Stored) _ = RestoreAsync(new[] { deskItem });
            }));
            if (deskItem.OnDesktop)
                menu.Items.Add(MakeItem("Ranger maintenant", "", () => _ = StoreAsync(new[] { deskItem })));
            else
                menu.Items.Add(MakeItem("Remettre sur le bureau", "", () => _ = RestoreAsync(new[] { deskItem })));
        }

        // Changer de catégorie
        if (a.IsShortcut)
        {
            menu.Items.Add(new Separator());
            var themeMenu = MakeItem("Changer de catégorie", "", null);
            foreach (var cat in Settings.AllCategoryNames)
            {
                var c = cat;
                var it = MakeItem(c, a.Theme == c ? "" : null, () => ChangeTheme(a, c));
                themeMenu.Items.Add(it);
            }
            themeMenu.Items.Add(new Separator());
            themeMenu.Items.Add(MakeItem("Nouvelle catégorie…", "\uE710", () => _ = NewCategoryForAsync(a)));
            themeMenu.Items.Add(MakeItem("Automatique", "", () => ChangeTheme(a, null)));
            menu.Items.Add(themeMenu);
        }

        // Désinstallation : depuis le gestionnaire d'applications (programme officiel de l'éditeur)
        if (a.IsShortcut && _apps.Contains(a))
            menu.Items.Add(MakeItem("Désinstaller…", "\uE74D", () => OpenManager(a.Name, obsolete: false)));

        menu.Items.Add(new Separator());
        if (a.FilePath != null)
            menu.Items.Add(MakeItem("Afficher le raccourci dans l'Explorateur", "\uE71B", () => DocumentLibrary.ShowInExplorer(a.FilePath)));
        var path = exe ?? ShellPath(a);
        if (path != null)
            menu.Items.Add(MakeItem("Copier le chemin d'accès", "\uE8C8", () =>
            {
                try { Clipboard.SetText(path); ShowToast("Chemin copié."); } catch { }
            }));
        if (a.FilePath != null && !(_desk.FirstOrDefault(d => d.FilePath == a.FilePath)?.OnDesktop ?? false)
            && System.IO.Path.GetExtension(a.FilePath).ToLowerInvariant() is ".lnk" or ".url")
            menu.Items.Add(MakeItem("Créer un raccourci sur le bureau", "\uE8E5", () =>
            {
                try
                {
                    var dest = DesktopManager.UniquePath(AppScanner.UserDesktop, System.IO.Path.GetFileName(a.FilePath));
                    File.Copy(a.FilePath, dest);
                    ShowToast($"Raccourci de {a.Label} ajouté au bureau.");
                    _ = ReloadAsync();
                }
                catch (Exception ex) { ShowToast("Impossible : " + ex.Message); }
            }));
        // Menu complet de Windows : Propriétés, Épingler, Envoyer vers, options ajoutées par les logiciels…
        var shell = ShellPath(a);
        if (shell != null)
            menu.Items.Add(MakeItem("Plus d'options (menu de Windows)", "\uE712", () =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (!ShellMenu.Show(shell, hwnd)) ShowToast("Windows ne propose pas d'autres options pour cet élément.");
            }));
        return menu;
    }

    private static MenuItem MakeItem(string header, string glyph, Action action)
    {
        var mi = new MenuItem { Header = header };
        if (glyph != null)
            mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 14 };
        if (action != null) mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Crée une catégorie depuis le menu d'une tuile et y range l'application (ou la suite).</summary>
    private async Task NewCategoryForAsync(AppItem a)
    {
        var name = await AskNewCategoryAsync(a.Name);
        if (name != null) ChangeTheme(a, name);
    }

    private void ChangeTheme(AppItem a, string category)
    {
        // Suite : le choix s'applique à toutes ses applications
        if (a.IsSuite)
        {
            foreach (var m in a.Members) Settings.Current.Overrides.Remove(m.Name);
            foreach (var m in a.Members.Where(_ => category != null)) Settings.Current.Overrides[m.Name] = category;
            Settings.Save();
            ReclassifyAll();
            RefreshAll();
            ShowToast(category == null ? $"{a.Name} : catégorie automatique" : $"{a.Name} → {category}");
            return;
        }
        Settings.SetOverride(a.Name, category);
        ReclassifyAll();
        RefreshAll();
        ShowToast(category == null ? $"{a.Name} : catégorie automatique" : $"{a.Name} → {category}");
        // En mode « dossiers », le raccourci rangé suit son nouveau thème
        if (DesktopManager.FoldersMode)
        {
            var moved = _desk.Where(x => x.Location == ItemLocation.Stored && x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (moved.Count > 0) { DesktopManager.Refile(moved); _ = AfterTidyAsync(); }
        }
    }
}
