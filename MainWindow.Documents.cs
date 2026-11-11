using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CentrePC.Core;

namespace CentrePC;

/// <summary>Page Documents : arborescence de rangement dans les dossiers de Windows et assistant pour ranger les fichiers du bureau.</summary>
public partial class MainWindow
{
    // =====================================================================================
    //  Page Documents
    // =====================================================================================
    private void RefreshDocuments()
    {
        // Affichage immédiat avec la dernière analyse ; le total définitif arrive avec le plan (analyse en arrière-plan)
        if (_plan.Count > 0 || _planManual.Count > 0 || _docLeftover.Count > 0) UpdateDocHero(_plan.Sum(f => f.Items.Count));
        else
        {
            var pending = DocumentLibrary.Pending();
            DocPendingCount.Text = pending.Count.ToString();
            DocPendingLabel.Text = pending.Count == 1 ? "élément à ranger" : "éléments à ranger";
            DocPendingDetail.Text = "Analyse en cours…";
            DocSortButton.IsEnabled = pending.Count > 0;
        }

        var folders = DocumentLibrary.Folders(withCounts: true);
        // Plan de rangement et vérification (analyse en arrière-plan)
        UpdateDocPlanCard();

        DocTreePanel.Children.Clear();
        foreach (var group in folders.GroupBy(f => f.Root.Key))
        {
            DocTreePanel.Children.Add(BuildRootSection(group.First().Root, group.ToList()));
        }

        var recent = DocumentLibrary.Recent();
        BuildRecent(recent);
        DocRecentBox.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDocsBadge(int? count = null)
    {
        // Même total que le bandeau de la page Documents dès que l'analyse a eu lieu
        int n = count ?? (_plan.Count > 0 || _planManual.Count > 0 || _docLeftover.Count > 0
                          ? _plan.Sum(f => f.Items.Count) + _planManual.Count + _docLeftover.Count : DocumentLibrary.Pending().Count);
        DocsBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        DocsBadgeText.Text = n.ToString();
    }

    private static Brush BrushOf(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

    private FrameworkElement Glyph(string glyph, string color, double size = 30)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 3.4), Background = BrushOf(color) };
        b.Child = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = size * 0.45, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        return b;
    }

    private Button SmallButton(string glyph, string text, RoutedEventHandler click, object tag = null, string tip = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)FindResource("BaseButton"), Content = sp, Tag = tag, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0), ToolTip = tip };
        if (click != null) b.Click += click;   // certains boutons reçoivent leur action après coup (menu ancré sur le bouton)
        return b;
    }

    /// <summary>
    /// Un grand dossier (Documents, Images…) dans sa carte : en-tête sobre (le chemin complet est en infobulle), une tuile
    /// par dossier principal, ses sous-dossiers à l'intérieur (la hiérarchie se lit d'un coup d'œil).
    /// </summary>
    private FrameworkElement BuildRootSection(DocRoot root, List<DocFolder> folders)
    {
        var panel = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Glyph(root.Glyph, root.Color));
        var title = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = string.IsNullOrEmpty(root.Path) ? null : root.Path };
        title.Children.Add(new TextBlock { Text = root.Name, Style = (Style)FindResource("SectionTitle"), FontSize = 16 });
        var rootFolder = folders.FirstOrDefault(f => f.Depth == 0);
        var subs = folders.Count(f => f.Depth > 0);
        var info = new List<string>();
        if (string.IsNullOrEmpty(root.Path)) info.Add("Dossiers ajoutés depuis un autre endroit");
        if (subs > 0) info.Add(Plural(subs, "dossier", "dossiers"));
        if (rootFolder != null && rootFolder.FileCount > 0) info.Add($"{Plural(rootFolder.FileCount, "fichier", "fichiers")} hors dossier");
        if (info.Count > 0) title.Children.Add(new TextBlock { Text = string.Join(" · ", info), Style = (Style)FindResource("Subtle"), FontSize = 12 });
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(root.Path))
        {
            actions.Children.Add(SmallButton("\uE8F4", "Nouveau dossier", async (s, _) => await NewFolderAsync(root.Path, root.Name), tip: $"Créer un dossier dans {root.Name}"));
            actions.Children.Add(SmallButton("\uE838", null, (s, _) => DocumentLibrary.ShowInExplorer(root.Path), tip: $"Ouvrir {root.Name} dans l'Explorateur"));
        }
        Grid.SetColumn(actions, 2);
        head.Children.Add(actions);
        panel.Children.Add(head);

        // Dossiers principaux (sans parent connu de Centre PC) et leurs sous-dossiers
        var inner = folders.Where(f => f.Depth > 0).ToList();
        bool HasParent(DocFolder f) => inner.Any(x => x != f && DocumentLibrary.IsUnder(f.Path, x.Path));
        var wrap = new WrapPanel();
        foreach (var top in inner.Where(f => !HasParent(f)))
            wrap.Children.Add(BuildFolderTile(top, inner.Where(c => DocumentLibrary.IsUnder(c.Path, top.Path)).ToList()));
        if (wrap.Children.Count == 0)
            wrap.Children.Add(new TextBlock
            {
                Text = "Aucun dossier pour l'instant : « Nouveau dossier » pour classer par sujet.",
                Style = (Style)FindResource("Subtle"), FontSize = 12.5, Margin = new Thickness(42, -4, 0, 0)
            });
        panel.Children.Add(wrap);
        return new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(18, 16, 18, 8), Child = panel };
    }

    /// <summary>Tuile d'un dossier : nom, contenu, et ses sous-dossiers en petites étiquettes cliquables.</summary>
    private FrameworkElement BuildFolderTile(DocFolder f, List<DocFolder> children)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = "\uE8B7", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20, Foreground = BrushOf(f.Root.Color),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
        });
        var txt = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        txt.Children.Add(new TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var parts = new List<string>();
        if (f.FileCount > 0 || children.Count == 0) parts.Add(Plural(f.FileCount, "fichier", "fichiers"));
        if (children.Count > 0) parts.Add(Plural(children.Count, "sous-dossier", "sous-dossiers"));
        txt.Children.Add(new TextBlock { Text = string.Join(" · ", parts), Style = (Style)FindResource("Subtle"), FontSize = 12 });
        if (children.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var c in children)
            {
                var rel = System.IO.Path.GetRelativePath(f.Path, c.Path).Replace("\\", " › ");
                var chip = new Button
                {
                    Style = (Style)FindResource("BaseButton"), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), FontSize = 12,
                    Content = c.FileCount > 0 ? $"{rel} · {c.FileCount}" : rel, ToolTip = c.Path + "\n(clic droit : plus d'options)", Cursor = Cursors.Hand
                };
                var folder = c;
                chip.Click += (_, e) => { DocumentLibrary.ShowInExplorer(folder.Path); e.Handled = true; };
                chip.ContextMenu = FolderMenu(folder);
                chips.Children.Add(chip);
            }
            txt.Children.Add(chips);
        }
        Grid.SetColumn(txt, 1);
        grid.Children.Add(txt);

        var hover = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        var tile = new Border
        {
            Background = (Brush)FindResource("Surface2"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 12, children.Count > 0 ? 6 : 12),
            Width = children.Count > 0 ? 340 : 230, MinHeight = 62, Margin = new Thickness(0, 0, 10, 10), Child = grid, Cursor = Cursors.Hand,
            BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            ToolTip = f.Path + "\n(clic : ouvrir · clic droit : plus d'options)", ContextMenu = FolderMenu(f)
        };
        tile.MouseEnter += (_, _) => tile.BorderBrush = BrushOf(f.Root.Color);
        tile.MouseLeave += (_, _) => tile.BorderBrush = (Brush)FindResource("Line");
        tile.MouseLeftButtonUp += (_, e) => { if (!e.Handled) DocumentLibrary.ShowInExplorer(f.Path); };
        return tile;
    }

    private ContextMenu FolderMenu(DocFolder f)
    {
        var menu = new ContextMenu();
        void Item(string text, Action act) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => act(); menu.Items.Add(mi); }
        Item("Ouvrir", () => DocumentLibrary.ShowInExplorer(f.Path));
        Item("Nouveau sous-dossier…", async () => await NewFolderAsync(f.Path, f.Label));
        Item("Renommer…", async () => await RenameFolderAsync(f));
        Item("Retirer de Centre PC (le dossier reste sur le disque)", () => RemoveFolder(f));
        return menu;
    }

    private bool _recentAll;
    private readonly HashSet<string> _recentOpen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rangés récemment : une rubrique par dossier de destination (les 4 dernières d'abord). Un clic sur la rubrique
    /// l'ouvre et détaille chaque fichier, avec « Ouvrir » et « Afficher l'emplacement ».
    /// </summary>
    private void BuildRecent(List<string> recent)
    {
        DocRecentPanel.Children.Clear();
        var groups = recent.GroupBy(p => System.IO.Path.GetDirectoryName(p), StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var g in _recentAll ? groups : groups.Take(4))
        {
            var key = g.Key;
            bool open = _recentOpen.Contains(key);
            var names = g.Select(System.IO.Path.GetFileName).ToList();

            var head = new Grid { Margin = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                                  ToolTip = open ? "Replier" : "Voir le détail" };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = open ? "\uE70D" : "\uE76C", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11, Opacity = 0.7,
                                              VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 10, 0) });
            var icon = new TextBlock { Text = "\uE8B7", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16, Opacity = 0.7,
                                       VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
            Grid.SetColumn(icon, 1); head.Children.Add(icon);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"{FriendlyFolder(key)}  ·  {Plural(names.Count, "élément", "éléments")}", FontWeight = FontWeights.SemiBold });
            if (!open)
                sp.Children.Add(new TextBlock
                {
                    Text = string.Join(", ", names.Take(6)) + (names.Count > 6 ? (names.Count == 7 ? " et 1 autre" : $" et {names.Count - 6} autres") : ""),
                    Style = (Style)FindResource("Subtle"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis
                });
            Grid.SetColumn(sp, 2); head.Children.Add(sp);
            var show = SmallButton("\uE838", "Afficher l'emplacement", (_, e) => { DocumentLibrary.ShowInExplorer(g.First()); e.Handled = true; },
                                   tip: "Ouvrir ce dossier dans l'Explorateur");
            show.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(show, 3); head.Children.Add(show);
            head.MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled) return;
                if (!_recentOpen.Remove(key)) _recentOpen.Add(key);
                BuildRecent(recent);
            };
            DocRecentPanel.Children.Add(head);

            if (open)
            {
                // Détail : chaque fichier rangé dans ce dossier
                var list = new StackPanel { Margin = new Thickness(54, 0, 8, 8) };
                foreach (var path in g)
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    bool isDir = Directory.Exists(path);
                    row.Children.Add(new TextBlock { Text = isDir ? "\uE8B7" : "\uE8A5", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, Opacity = 0.6,
                                                     VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                    var name = new TextBlock { Text = System.IO.Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center,
                                               TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path };
                    Grid.SetColumn(name, 1); row.Children.Add(name);
                    var acts = new StackPanel { Orientation = Orientation.Horizontal };
                    var item = path;
                    acts.Children.Add(SmallButton("\uE8A7", "Ouvrir", (_, _) => Updates.Open(item), tip: isDir ? "Ouvrir le dossier" : "Ouvrir avec l'application habituelle"));
                    acts.Children.Add(SmallButton("\uE838", null, (_, _) => DocumentLibrary.ShowInExplorer(item), tip: "Afficher l'emplacement"));
                    Grid.SetColumn(acts, 2); row.Children.Add(acts);
                    list.Children.Add(row);
                }
                DocRecentPanel.Children.Add(list);
            }
        }
        if (groups.Count > 4)
        {
            var more = new Button { Style = (Style)FindResource("BaseButton"), Content = _recentAll ? "Afficher moins" : $"Tout afficher ({groups.Count} dossiers)",
                                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 4, 0, 6), Padding = new Thickness(12, 5, 12, 5) };
            more.Click += (_, _) => { _recentAll = !_recentAll; BuildRecent(recent); };
            DocRecentPanel.Children.Add(more);
        }
    }

    /// <summary>« C:\Users\…\Documents\Jeux » → « Documents › Jeux ».</summary>
    private static string FriendlyFolder(string dir)
    {
        foreach (var r in DocumentLibrary.Roots)
        {
            if (string.Equals(dir?.TrimEnd('\\'), r.Path?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return r.Name;
            if (dir != null && DocumentLibrary.IsUnder(dir, r.Path))
                return r.Name + " › " + System.IO.Path.GetRelativePath(r.Path, dir).Replace("\\", " › ");
        }
        return dir;
    }

    private void DocStarter_Click(object sender, RoutedEventArgs e)
    {
        try { DocumentLibrary.CreateStarter(); ShowToast("Dossiers créés : vous pouvez les renommer ou en ajouter."); }
        catch (Exception ex) { ShowToast("Impossible de créer les dossiers : " + ex.Message); }
        RefreshDocuments();
    }

    private void DocStarterSkip_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.DocStarterDismissed = true;
        Settings.Save();
        RefreshDocuments();
    }

    private void DocAddExisting_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choisissez un dossier à ajouter à l'arborescence",
            InitialDirectory = DocumentLibrary.Roots[0].Path
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName;
        if (DocumentLibrary.Roots.Any(r => string.Equals(r.Path?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Ce dossier est déjà affiché.");
            return;
        }
        DocumentLibrary.AddFolder(path);
        RefreshDocuments();
        ShowToast($"« {System.IO.Path.GetFileName(path)} » ajouté.");
    }

    private async Task<string> NewFolderAsync(string parent, string parentLabel)
    {
        var name = await PromptAsync("Nouveau dossier", $"Dans {parentLabel}", "", DocumentLibrary.CheckName);
        if (name == null) return null;
        try
        {
            var path = DocumentLibrary.CreateFolder(parent, name);
            RefreshDocuments();
            ShowToast($"Dossier « {name.Trim()} » créé.");
            return path;
        }
        catch (Exception ex) { ShowToast("Impossible de créer le dossier : " + ex.Message); return null; }
    }

    private async Task RenameFolderAsync(DocFolder f)
    {
        var name = await PromptAsync("Renommer le dossier", f.Label, f.Name, n =>
            DocumentLibrary.CheckName(n) ?? (Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path), n.Trim()))
                && !n.Trim().Equals(f.Name, StringComparison.OrdinalIgnoreCase) ? "Un dossier porte déjà ce nom." : null));
        if (name == null || name.Trim() == f.Name) return;
        try { DocumentLibrary.RenameFolder(f.Path, name); RefreshDocuments(); }
        catch (Exception ex) { ShowToast("Renommage impossible (un fichier du dossier est peut-être ouvert) : " + ex.Message); }
    }

    private void RemoveFolder(DocFolder f)
    {
        bool deleted = DocumentLibrary.RemoveFolder(f.Path);
        RefreshDocuments();
        ShowToast(deleted ? $"« {f.Name} » était vide : supprimé." : $"« {f.Name} » n'est plus affiché. Son contenu reste dans {FriendlyFolder(System.IO.Path.GetDirectoryName(f.Path))}.");
    }

    // =====================================================================================
    //  Saisie d'un nom (petite fenêtre intégrée)
    // =====================================================================================
    private TaskCompletionSource<string> _prompt;
    private Func<string, string> _promptCheck;

    private Task<string> PromptAsync(string title, string hint, string initial, Func<string, string> check)
    {
        _prompt?.TrySetResult(null);
        _prompt = new TaskCompletionSource<string>();
        _promptCheck = check;
        PromptTitle.Text = title;
        PromptHint.Text = hint;
        PromptBox.Text = initial ?? "";
        PromptError.Visibility = Visibility.Collapsed;
        PromptOverlay.Visibility = Visibility.Visible;
        _ = Dispatcher.InvokeAsync(() => { PromptBox.Focus(); PromptBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        return _prompt.Task;
    }

    private void PromptOk_Click(object sender, RoutedEventArgs e)
    {
        var err = _promptCheck?.Invoke(PromptBox.Text);
        if (err != null) { PromptError.Text = err; PromptError.Visibility = Visibility.Visible; return; }
        PromptOverlay.Visibility = Visibility.Collapsed;
        _prompt?.TrySetResult(PromptBox.Text.Trim());
    }

    private void PromptCancel_Click(object sender, RoutedEventArgs e)
    {
        PromptOverlay.Visibility = Visibility.Collapsed;
        _prompt?.TrySetResult(null);
    }

    private void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { PromptOk_Click(sender, null); e.Handled = true; }
        else if (e.Key == Key.Escape) { PromptCancel_Click(sender, null); e.Handled = true; }
    }

    // =====================================================================================
    //  Assistant « Ranger mes fichiers »
    // =====================================================================================
    private List<DocItem> _docQueue = new();
    private int _docIndex;
    private int _docDone;
    /// <summary>Historique pour « Annuler la dernière action » : élément, chemin d'origine, nouveau chemin (null = rien déplacé), garde ajoutée.</summary>
    private readonly Stack<(int index, DocItem item, string movedTo, string keptName, bool counted)> _docHistory = new();
    private List<DocFolder> _docTargets = new();

    /// <summary>
    /// Point d'entrée unique du rangement : le plan (dossiers proposés, réversible) s'il contient des éléments,
    /// sinon le tri un par un des éléments que le plan ne sait pas classer.
    /// </summary>
    private void DocSort_Click(object sender, RoutedEventArgs e)
    {
        if (HasPlanWork) ShowPlanOverlay();
        else OpenDocSorter(_docLeftover.Count > 0 ? _docLeftover : DocumentLibrary.Pending());
    }

    private void OpenDocSorter(List<DocItem> items)
    {
        _docQueue = items;
        if (_docQueue.Count == 0) { ShowToast("Aucun fichier à ranger."); return; }
        _docIndex = 0; _docDone = 0;
        _docHistory.Clear();
        ShowDocItem();
        DocSorterOverlay.Visibility = Visibility.Visible;
        Keyboard.ClearFocus();
        Focus();
    }

    private void ShowDocItem()
    {
        if (_docIndex >= _docQueue.Count) { CloseDocSorter(); return; }
        var it = _docQueue[_docIndex];
        if (!File.Exists(it.Path) && !Directory.Exists(it.Path)) { _docIndex++; ShowDocItem(); return; }   // déplacé entre-temps

        DocSorterName.Text = it.Name;
        string size = "";
        DateTime when = default;
        try
        {
            if (it.IsFolder)
            {
                var di = new DirectoryInfo(it.Path);
                when = di.LastWriteTime;
                size = $"{di.EnumerateFiles("*", SearchOption.AllDirectories).Count()} fichier(s)";
            }
            else
            {
                var fi = new FileInfo(it.Path);
                when = fi.LastWriteTime;
                size = fi.Length >= 1 << 20 ? $"{fi.Length / 1048576.0:0.#} Mo" : $"{Math.Max(1, fi.Length / 1024)} Ko";
            }
        }
        catch { }
        DocSorterInfo.Text = $"{DocumentLibrary.KindOf(it.Path, it.IsFolder)} · {size} · modifié le {when:dd/MM/yyyy} · {it.Origin}";
        DocSorterIcon.Source = null;
        var path = it.Path;
        _ = Native.RunSta(() => Native.GetIcon(path, 96)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && _docIndex < _docQueue.Count && _docQueue[_docIndex].Path == path)
                DocSorterIcon.Source = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());

        // Destinations : le grand dossier conseillé et ses sous-dossiers d'abord, puis les autres
        var suggest = DocumentLibrary.SuggestRoot(it.Path, it.IsFolder);
        var all = DocumentLibrary.Folders();
        _docTargets = all.Where(f => f.Root.Key == suggest).Concat(all.Where(f => f.Root.Key != suggest)).ToList();
        var suggestName = DocumentLibrary.Roots.First(r => r.Key == suggest).Name;
        DocSorterSuggest.Text = $"Conseillé : {suggestName}" + (_docTargets.Any(f => f.Root.Key == suggest && f.Depth > 0) ? " ou un de ses sous-dossiers" : "");

        DocSorterTargets.Children.Clear();
        int n = 1;
        foreach (var f in _docTargets)
        {
            var key = n <= 9 ? n.ToString() : "";
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = f.Depth == 0 ? f.Root.Glyph : "\uE8B7", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15,
                Foreground = BrushOf(f.Root.Color), VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = f.Label, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200,
                FontWeight = f.Root.Key == suggest ? FontWeights.SemiBold : FontWeights.Normal
            });
            if (key != "") sp.Children.Add(new TextBlock { Text = key, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button
            {
                Style = (Style)FindResource("BaseButton"), Content = sp, Tag = f, Width = 270, Margin = new Thickness(0, 0, 10, 10),
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 9, 12, 9), ToolTip = f.Path
            };
            if (f.Root.Key == suggest && f.Depth == 0) { btn.BorderBrush = (Brush)FindResource("Accent"); btn.BorderThickness = new Thickness(2); }
            btn.Click += (s, _) => DocMoveTo((DocFolder)((Button)s).Tag);
            DocSorterTargets.Children.Add(btn);
            n++;
        }

        DocSorterProgress.Text = $"{_docIndex + 1} / {_docQueue.Count}";
        DocSorterBar.Maximum = _docQueue.Count;
        DocSorterBar.Value = _docIndex;
        DocSorterBackButton.IsEnabled = _docHistory.Count > 0;
    }

    private void DocMoveTo(DocFolder target)
    {
        if (_docIndex >= _docQueue.Count) return;
        var it = _docQueue[_docIndex];
        try
        {
            var dest = DocumentLibrary.Move(it, target.Path);
            _docHistory.Push((_docIndex, it, dest, null, true));
            _docDone++;
            _docIndex++;
            ShowDocItem();
        }
        catch (Exception ex) { Log.Error("Rangement de " + it.Path, ex); ShowToast($"Impossible de déplacer « {it.Name} » (fichier ouvert dans une autre application ?) : {ex.Message}"); }
    }

    private void DocSorterLater_Click(object sender, RoutedEventArgs e)
    {
        if (_docIndex >= _docQueue.Count) return;
        _docHistory.Push((_docIndex, _docQueue[_docIndex], null, null, false));
        _docIndex++;
        ShowDocItem();
    }

    /// <summary>Garder sur le bureau : n'est plus proposé (un fichier de l'ancien rangement revient sur le bureau).</summary>
    private void DocSorterKeep_Click(object sender, RoutedEventArgs e)
    {
        if (_docIndex >= _docQueue.Count) return;
        var it = _docQueue[_docIndex];
        try
        {
            string moved = null;
            if (!it.Origin.StartsWith("sur le bureau")) moved = DocumentLibrary.Move(it, AppScanner.UserDesktop);
            var name = System.IO.Path.GetFileName(moved ?? it.Path);
            if (!Settings.Current.KeepOnDesktop.Contains(name, StringComparer.OrdinalIgnoreCase)) Settings.Current.KeepOnDesktop.Add(name);
            Settings.Save();
            _docHistory.Push((_docIndex, it, moved, name, false));
            _docIndex++;
            ShowDocItem();
        }
        catch (Exception ex) { ShowToast("Impossible : " + ex.Message); }
    }

    private void DocSorterRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (_docIndex >= _docQueue.Count) return;
        var it = _docQueue[_docIndex];
        try
        {
            DocumentLibrary.Recycle(it);
            _docHistory.Push((_docIndex, it, "", null, true));   // "" = Corbeille (récupérable depuis la Corbeille de Windows)
            _docDone++;
            _docIndex++;
            ShowToast($"« {it.Name} » envoyé à la Corbeille.");
            ShowDocItem();
        }
        catch (Exception ex) { ShowToast($"« {it.Name} » : {ex.Message}"); }
    }

    private async void DocSorterNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_docIndex >= _docQueue.Count) return;
        var it = _docQueue[_docIndex];
        var root = DocumentLibrary.Roots.First(r => r.Key == DocumentLibrary.SuggestRoot(it.Path, it.IsFolder));
        var path = await NewFolderAsync(root.Path, root.Name + " (saisissez « Jeux\\Dofus » pour créer un sous-dossier)");
        if (path == null) { ShowDocItem(); return; }
        var target = DocumentLibrary.Folders().FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (target != null) DocMoveTo(target); else ShowDocItem();
    }

    private void DocSorterOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_docIndex < _docQueue.Count) Updates.Open(_docQueue[_docIndex].Path);
    }

    private void DocSorterBack_Click(object sender, RoutedEventArgs e)
    {
        if (_docHistory.Count == 0) return;
        var (index, item, movedTo, keptName, counted) = _docHistory.Pop();
        try
        {
            if (movedTo == "") ShowToast($"« {item.Name} » se trouve dans la Corbeille : restaurez-le depuis la Corbeille de Windows.");
            else if (movedTo != null) DocumentLibrary.Undo(movedTo, item.Path, item.IsFolder);
            if (keptName != null) { Settings.Current.KeepOnDesktop.RemoveAll(k => k.Equals(keptName, StringComparison.OrdinalIgnoreCase)); Settings.Save(); }
            if (counted) _docDone--;
        }
        catch (Exception ex) { ShowToast("Retour impossible : " + ex.Message); }
        _docIndex = index;
        ShowDocItem();
    }

    private void DocSorterClose_Click(object sender, RoutedEventArgs e) => CloseDocSorter();

    private async void CloseDocSorter()
    {
        DocSorterOverlay.Visibility = Visibility.Collapsed;
        var n = _docDone;
        _docHistory.Clear();
        RefreshDocuments();
        await ReloadAsync();
        if (n > 0) ShowToast(_docIndex >= _docQueue.Count ? $"Rangement terminé : {n} élément(s) rangé(s)." : $"{n} élément(s) rangé(s). Les autres éléments restent sur le bureau.");
    }

    /// <summary>Clavier de l'assistant. Renvoie vrai si la touche a été utilisée.</summary>
    private bool DocSorterKey(Key key)
    {
        int digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 + 1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 + 1 : 0;
        if (digit > 0 && digit <= _docTargets.Count) DocMoveTo(_docTargets[digit - 1]);
        else if (key == Key.Space) DocSorterLater_Click(this, null);
        else if (key == Key.G) DocSorterKeep_Click(this, null);
        else if (key == Key.N) DocSorterNewFolder_Click(this, null);
        else if (key == Key.O) DocSorterOpen_Click(this, null);
        else if (key == Key.Delete) DocSorterRecycle_Click(this, null);
        else if (key == Key.Back) DocSorterBack_Click(this, null);
        else if (key == Key.Escape) CloseDocSorter();
        else return false;
        return true;
    }
}
