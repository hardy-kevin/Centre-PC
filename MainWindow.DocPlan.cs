using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Plan de rangement des documents : arborescence proposée d'après le contenu du PC, rangement en un clic,
/// puis vérification élément par élément (garder, remettre à sa place, autre dossier). Tout est réversible.
/// </summary>
public partial class MainWindow
{
    private List<DocFolderPlan> _plan = new();
    private List<DocFolderPlan> _planSuggested = new();
    /// <summary>File « à ranger vous-même » : éléments sans cas prévu, aucune destination devinée.</summary>
    private List<DocProposal> _planManual = new();
    private bool HasPlanWork => _plan.Any(f => f.Items.Count > 0) || _planManual.Count > 0;
    private bool _planBusy;

    private const string Reassurance =
        "Rien n'est supprimé ni renommé : les éléments sont seulement déplacés, et chacun peut retrouver son emplacement d'origine en un clic, " +
        "maintenant ou plus tard (Bureau › Historique, 30 jours). Ce que vous refusez ne vous sera plus proposé. Vos propres dossiers sont réutilisés en priorité.";

    private List<string> InstalledNames() =>
        _apps.Select(a => a.Name).Concat(_apps.Select(a => a.Publisher).Where(p => !string.IsNullOrEmpty(p)))
             .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Cartes « à vérifier » et « plan de rangement » de la page Documents (analyse en arrière-plan).</summary>
    private async void UpdateDocPlanCard()
    {
        var cfg = Settings.Current;
        // Les éléments déplacés ou supprimés depuis par l'utilisateur sortent de la vérification
        if (cfg.DocReview.RemoveAll(e => !File.Exists(e.To) && !Directory.Exists(e.To)) > 0) Settings.Save();
        int toReview = cfg.DocReview.Count;
        DocReviewCard.Visibility = toReview > 0 ? Visibility.Visible : Visibility.Collapsed;
        DocReviewTitle.Text = toReview == 1 ? "1 rangement à vérifier" : $"{toReview} rangements à vérifier";

        if (_planBusy) return;
        _planBusy = true;
        try
        {
            var names = InstalledNames();
            var (plan, manual, suggested) = await Task.Run(() =>
            {
                var (p, m) = DocPlanner.BuildPlanAndManual(names);
                return (p, m, DocPlanner.SuggestedEmptyFolders(p, names));
            });
            // Fenêtre du plan ouverte : on garde le plan affiché (cases décochées, destinations changées)
            if (DocPlanOverlay.Visibility != Visibility.Visible) { _plan = plan; _planManual = manual; _planSuggested = suggested; }
        }
        catch (Exception ex) { Log.Error("Plan de rangement", ex); _plan = new(); _planManual = new(); _planSuggested = new(); }
        finally { _planBusy = false; }

        int items = _plan.Sum(f => f.Items.Count);
        var planned = new HashSet<string>(_plan.SelectMany(f => f.Items).Concat(_planManual).Select(p => p.Source.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        _docLeftover = DocumentLibrary.Pending().Where(p => !planned.Contains(p.Path.TrimEnd('\\'))).ToList();
        UpdateDocHero(items);

        // Le bandeau du haut porte le rangement ; cette carte ne sert plus qu'à préparer l'arborescence quand tout est rangé
        bool snoozed = cfg.DocPlanSnoozedUntil > DateTime.Now;
        bool hasOwn = DocumentLibrary.Folders().Any(f => f.Depth > 0);
        if (items == 0 && _planManual.Count == 0 && !hasOwn && !snoozed && _planSuggested.Count > 0)
        {
            DocPlanTitle.Text = "Préparez votre arborescence";
            DocPlanText.Text = "Rien n'est à ranger pour l'instant. Centre PC peut créer à l'avance des dossiers adaptés à ce que l'on garde "
                + "habituellement sur un PC (papiers administratifs, études, photos, captures d'écran, enregistrements…). Vous choisissez lesquels.";
            DocPlanButton.Content = "Choisir mes dossiers";
            DocPlanCard.Visibility = Visibility.Visible;
        }
        else DocPlanCard.Visibility = Visibility.Collapsed;
    }

    private List<DocItem> _docLeftover = new();

    /// <summary>
    /// Bandeau du haut : un seul chiffre (tout ce qui n'est pas rangé, d'où qu'il vienne), sa provenance,
    /// et la garantie de réversibilité. Même total que le badge du menu.
    /// </summary>
    private void UpdateDocHero(int planned)
    {
        int manual = _planManual.Count + _docLeftover.Count;
        int total = planned + manual;
        DocPendingCount.Text = total.ToString();
        DocPendingLabel.Text = total == 0 ? "rien à ranger" : total == 1 ? "élément à ranger" : "éléments à ranger";
        var parts = _plan.SelectMany(f => f.Items).Concat(_planManual).GroupBy(p => p.Origin)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key == "Bureau" ? $"{g.Count()} sur le bureau" : $"{g.Count()} dans {g.Key}").ToList();
        int folders = _plan.Count, existing = _plan.Count(f => f.Exists);
        var how = new List<string>();
        if (planned > 0) how.Add($"{planned} avec un dossier proposé ({Plural(folders, "dossier", "dossiers")}" + (existing > 0 ? $", dont {existing} déjà à vous)" : ")"));
        if (manual > 0) how.Add($"{manual} à ranger vous-même (aucun dossier évident)");
        DocPendingDetail.Text = total == 0
            ? "Le bureau et la racine de vos dossiers personnels sont en ordre."
            : string.Join(" · ", parts)
              + "\n" + string.Join(" · ", how) + ". Vos sous-dossiers ne sont pas touchés."
              + "\nRéversible : rien n'est supprimé ni renommé, chaque élément peut être remis à sa place.";
        DocSortButton.IsEnabled = total > 0;
        UpdateDocsBadge(total);
    }

    private void DocPlanLater_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.DocPlanSnoozedUntil = DateTime.Now.AddDays(7);
        Settings.Save();
        DocPlanCard.Visibility = Visibility.Collapsed;
        ShowToast("Plan de rangement remis à plus tard (il reviendra dans 7 jours).");
    }

    private void DocPlanOpen_Click(object sender, RoutedEventArgs e) => ShowPlanOverlay();
    private void DocReviewOpen_Click(object sender, RoutedEventArgs e) => ShowReviewOverlay();

    private void DocPlanClose_Click(object sender, RoutedEventArgs e) => CloseDocPlan();

    private void CloseDocPlan()
    {
        HideZoom();
        DocPlanOverlay.Visibility = Visibility.Collapsed;
        RefreshDocuments();
    }

    // ------------------------------------------------------------------ plan
    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    private void ShowPlanOverlay()
    {
        bool hasItems = HasPlanWork;
        foreach (var f in _plan) { f.Selected = true; foreach (var i in f.Items) i.Selected = true; }
        foreach (var m in _planManual) { m.Selected = false; m.Keep = false; }
        int total = _plan.Sum(f => f.Items.Count) + _planManual.Count;
        DocPlanOverlayTitle.Text = hasItems ? $"Ranger {Plural(total, "élément", "éléments")}" : "Préparer mon arborescence";
        DocPlanOverlayText.Text = hasItems
            ? "Choisissez un élément à gauche : vous voyez d'où il vient et où il ira. Décochez ce qui doit rester où il est."
              + (_planManual.Count > 0 ? " Les éléments sans dossier évident attendent à la fin de la liste : c'est vous qui choisissez." : "")
            : "Cochez les dossiers à créer. Vous pourrez les renommer, en ajouter ou en retirer ensuite.";
        DocPlanReassurance.Text = hasItems
            ? "Réversible : rien n'est supprimé ni renommé, chaque élément peut retrouver sa place en un clic."
            : "Seuls des dossiers vides sont créés : rien n'est déplacé.";
        DocPlanReassurance.ToolTip = Reassurance;
        DocPlanSplit.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        DocPlanScroll.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        if (hasItems)
        {
            _planCurrent = null;
            BuildPlanItems();
            // Aperçus nets préparés en arrière-plan, dans l'ordre de la liste : l'agrandissement sera immédiat
            _ = DocPreview.PrewarmAsync(_planOrder.Where(x => !x.IsFolder).Select(x => x.Source).ToList());
        }
        else BuildPlanList();
        DocPlanOverlay.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ plan : liste + origine → destination
    private DocProposal _planCurrent;
    private readonly List<DocProposal> _planOrder = new();
    private readonly Dictionary<DocProposal, (Border row, CheckBox check, TextBlock dest)> _planRows = new();
    private readonly Dictionary<DocFolderPlan, CheckBox> _planGroupChecks = new();

    private DocFolderPlan FolderOf(DocProposal p) => _plan.FirstOrDefault(f => f.Items.Contains(p));

    /// <summary>Liste de gauche : éléments groupés par dossier de destination (vos dossiers d'abord).</summary>
    private void BuildPlanItems()
    {
        DocPlanOverlayTitle.Text = $"Ranger {Plural(_plan.Sum(f => f.Items.Count) + _planManual.Count, "élément", "éléments")}";
        DocPlanItems.Children.Clear();
        _planRows.Clear();
        _planGroupChecks.Clear();
        _planOrder.Clear();
        foreach (var f in _plan.Where(f => f.Items.Count > 0).OrderBy(f => f.Exists ? 0 : 1).ThenBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var folder = f;
            var (name, _) = SplitLabel(f.Label);
            var gcheck = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Tout cocher ou décocher pour ce dossier" };
            gcheck.Click += (_, _) =>
            {
                bool on = gcheck.IsChecked == true;
                foreach (var it in folder.Items) it.Selected = on;
                foreach (var it in folder.Items) RefreshPlanItem(it, rebuildDetail: false);
                SyncGroup(folder);
                BuildPlanDetail();
                UpdatePlanFooter();
            };
            _planGroupChecks[f] = gcheck;
            var head = new Grid { Margin = new Thickness(8, _planOrder.Count == 0 ? 6 : 14, 8, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Children.Add(gcheck);
            var g = new TextBlock { Text = "\uE8B7", Style = (Style)FindResource("Glyph"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                                    Margin = new Thickness(0, 0, 8, 0), Foreground = f.Exists ? Brushes.MediumSeaGreen : (Brush)FindResource("Accent") };
            Grid.SetColumn(g, 1); head.Children.Add(g);
            var t = new TextBlock { Text = $"{name}  ·  {f.Items.Count}", FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                    TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.Label + (f.Exists ? "" : " (nouveau dossier)") };
            Grid.SetColumn(t, 2); head.Children.Add(t);
            DocPlanItems.Children.Add(head);
            foreach (var it in f.Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _planOrder.Add(it);
                DocPlanItems.Children.Add(PlanItemRow(it));
            }
            SyncGroup(f);
        }
        if (_planManual.Count > 0)
        {
            // File « à ranger vous-même » : aucun cas prévu, Centre PC ne devine pas
            var head = new Grid { Margin = new Thickness(8, _planOrder.Count == 0 ? 6 : 18, 8, 4),
                                  ToolTip = "Aucun dossier évident : choisissez-en un pour chaque élément, ou laissez-le à sa place. Ce que vous ne traitez pas maintenant attend ici." };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            var g = new TextBlock { Text = "\uE70F", Style = (Style)FindResource("Glyph"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                                    Margin = new Thickness(30, 0, 8, 0), Foreground = Brushes.Orange };
            head.Children.Add(g);
            var t = new TextBlock { Text = $"À ranger vous-même  ·  {_planManual.Count}", FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 1); head.Children.Add(t);
            DocPlanItems.Children.Add(head);
            foreach (var it in _planManual)
            {
                _planOrder.Add(it);
                DocPlanItems.Children.Add(PlanItemRow(it));
            }
        }
        SelectPlanItem(_planCurrent != null && _planOrder.Contains(_planCurrent) ? _planCurrent : _planOrder.FirstOrDefault());
        UpdatePlanFooter();
    }

    private void SyncGroup(DocFolderPlan f)
    {
        if (!_planGroupChecks.TryGetValue(f, out var c)) return;
        int on = f.Items.Count(i => i.Selected);
        f.Selected = on > 0;
        c.IsChecked = on == f.Items.Count ? true : on == 0 ? false : null;
    }

    private FrameworkElement PlanItemRow(DocProposal p)
    {
        var check = new CheckBox { IsChecked = p.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                                   ToolTip = "Décochez pour laisser l'élément à sa place" };
        if (p.IsManual) { check.Visibility = Visibility.Hidden; check.IsHitTestVisible = false; }   // rien à cocher tant qu'aucun dossier n'est choisi
        var thumb = Thumb(p, 32, 48);
        thumb.Margin = new Thickness(0, 0, 10, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = p.Name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = p.Name });
        var dest = new TextBlock { Style = (Style)FindResource("Subtle"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(dest);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(check);
        Grid.SetColumn(thumb, 1); grid.Children.Add(thumb);
        Grid.SetColumn(text, 2); grid.Children.Add(text);
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2),
                               Background = Brushes.Transparent, Child = grid, Cursor = Cursors.Hand };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, check)) return;
            SelectPlanItem(p);
        };
        check.Click += (_, _) => { p.Selected = check.IsChecked == true; RefreshPlanItem(p); };
        _planRows[p] = (row, check, dest);
        RefreshPlanItem(p, rebuildDetail: false, footer: false);
        return row;
    }

    /// <summary>Met à jour la ligne d'un élément (case, destination, opacité), son groupe et le détail affiché.</summary>
    private void RefreshPlanItem(DocProposal p, bool rebuildDetail = true, bool footer = true)
    {
        if (!_planRows.TryGetValue(p, out var r)) return;
        r.check.IsChecked = p.Selected;
        if (p.Trash)
        {
            r.dest.Text = "→ Corbeille";
            r.row.Opacity = 0.55;
        }
        else if (p.IsManual)
        {
            r.dest.Text = p.Keep ? "Reste à sa place" : "Dossier à choisir";
            r.row.Opacity = p.Keep ? 0.55 : 1;
        }
        else
        {
            r.dest.Text = p.Selected ? "→ " + SplitLabel(p.DestinationLabel ?? DocPlanner.FriendlyDir(p.Destination)).name : "Reste à sa place";
            r.row.Opacity = p.Selected ? 1 : 0.55;
        }
        var f = FolderOf(p);
        if (f != null) SyncGroup(f);
        if (rebuildDetail && p == _planCurrent) BuildPlanDetail();
        if (footer) UpdatePlanFooter();
    }

    private void SelectPlanItem(DocProposal p)
    {
        _planCurrent = p;
        var highlight = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x5C, 0xFF));
        foreach (var (item, r) in _planRows) r.row.Background = item == p ? highlight : Brushes.Transparent;
        if (p != null && _planRows.TryGetValue(p, out var sel)) sel.row.BringIntoView();
        BuildPlanDetail();
    }

    /// <summary>Aperçu (miniature de l'Explorateur, sinon icône) avec un symbole en attendant.</summary>
    private Grid Thumb(DocProposal p, double box, int pixels)
    {
        var g = new Grid { Width = box, Height = box };
        var glyph = new TextBlock { Text = p.IsFolder ? "\uE8B7" : "\uE8A5", Style = (Style)FindResource("Glyph"), FontSize = box * 0.55, Opacity = 0.5,
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var img = new System.Windows.Controls.Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        g.Children.Add(glyph);
        g.Children.Add(img);
        _ = LoadThumbAsync(img, glyph, p.Source, pixels);
        return g;
    }

    private static async Task LoadThumbAsync(System.Windows.Controls.Image img, UIElement placeholder, string path, int pixels)
    {
        var src = await ShellThumbnail.GetAsync(path, pixels);
        if (src == null) return;
        img.Source = src;
        placeholder.Visibility = Visibility.Collapsed;
    }

    private static string SizeText(long bytes) =>
        bytes < 1024 ? $"{bytes} octets"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} Ko"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} Mo"
        : $"{bytes / 1024.0 / 1024 / 1024:0.##} Go";

    private static string DetailsOf(DocProposal p)
    {
        try
        {
            if (p.IsFolder)
            {
                int n = Directory.EnumerateFileSystemEntries(p.Source).Take(1000).Count();
                return (n == 0 ? "Dossier vide" : n >= 1000 ? "Dossier · plus de 1000 éléments" : $"Dossier · {Plural(n, "élément", "éléments")}")
                       + $" · modifié le {Directory.GetLastWriteTime(p.Source):d MMMM yyyy}";
            }
            var fi = new FileInfo(p.Source);
            var text = $"{SizeText(fi.Length)} · modifié le {fi.LastWriteTime:d MMMM yyyy}";
            // « fichier (1).exe » à côté de « fichier.exe », même taille : téléchargé deux fois
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(p.Source), @"^(.+?)\s*\(\d+\)$");
            if (m.Success)
            {
                var original = Path.Combine(fi.DirectoryName ?? "", m.Groups[1].Value + fi.Extension);
                if (File.Exists(original) && new FileInfo(original).Length == fi.Length)
                    text += $"\nSemble être une copie de « {Path.GetFileName(original)} » (même taille)";
            }
            return text;
        }
        catch { return null; }
    }

    private Border Square(string caption, UIElement visual, IEnumerable<UIElement> lines, bool dim = false)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = caption, Style = (Style)FindResource("Subtle"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(new Border { Height = 190, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                                     Padding = new Thickness(8), Child = visual, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var l in lines) if (l != null) sp.Children.Add(l);
        return new Border { Background = (Brush)FindResource("Surface2"), CornerRadius = new CornerRadius(14), Padding = new Thickness(16),
                            BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Child = sp, Opacity = dim ? 0.6 : 1 };
    }

    private TextBlock Line(string text, bool strong = false, string tip = null, double size = 12.5) => string.IsNullOrEmpty(text) ? null : new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = strong ? 15 : size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Style = strong ? null : (Style)FindResource("Subtle"), Margin = new Thickness(0, strong ? 0 : 3, 0, 0), ToolTip = tip, MaxHeight = strong ? 44 : double.PositiveInfinity,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    /// <summary>Partie droite : carré « Origine » (aperçu du fichier) → carré « Destination » (dossier proposé).</summary>
    private void BuildPlanDetail()
    {
        DocPlanDetail.Children.Clear();
        var p = _planCurrent;
        if (p == null) return;

        // Origine
        // Aperçu cliquable : loupe en bas à droite, clic ou Espace pour l'agrandir
        var preview = new Grid { Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "Agrandir l'aperçu (Espace)" };
        preview.Children.Add(Thumb(p, 170, 256));
        preview.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x16)), CornerRadius = new CornerRadius(8), Padding = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 14 }
        });
        preview.MouseLeftButtonUp += (_, _) => ShowZoom(p);
        var what = p.IsFolder ? "le dossier" : "le fichier";
        var origin = Square("ORIGINE", preview, new UIElement[]
        {
            Line(p.Name, strong: true, tip: p.Name),
            Line(p.Origin == "Bureau" ? "Sur le bureau" : $"Dans {p.Origin}", tip: p.Source),
            Line(DetailsOf(p), size: 12),
            Actions(SmallButton("", "Ouvrir", (_, _) => OpenPath(p.Source), tip: $"Ouvrir {what} avec l'application habituelle"),
                    SmallButton("", "Afficher l'emplacement", (_, _) => DocumentLibrary.ShowInExplorer(p.Source), tip: "Ouvrir son dossier dans l'Explorateur, l'élément sélectionné"),
                    SmallButton(p.Trash ? "" : "", p.Trash ? "Ne pas supprimer" : (p.IsFolder ? "Supprimer le dossier" : "Supprimer le fichier"),
                                (_, _) => ToggleTrash(p),
                                tip: p.Trash ? "Annule la suppression : l'élément reste dans le rangement"
                                             : "Envoyé à la Corbeille de Windows quand vous validez (récupérable) · touche Suppr"))
        });

        // Destination : l'état de l'élément et toutes les actions qui le concernent, au même endroit
        var (name, parent) = p.IsManual ? (null, null) : SplitLabel(p.DestinationLabel ?? DocPlanner.FriendlyDir(p.Destination));
        bool exists = !p.IsManual && Directory.Exists(p.Destination);
        TextBlock Big(string glyph, Brush brush, double size = 84, double opacity = 1) => new()
        {
            Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = size, Foreground = brush, Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        Button Act(string glyph, string text, Action click, string tip = null, bool accent = false)
        {
            var b = accent
                ? new Button { Style = (Style)FindResource("AccentButton"), Content = text, Padding = new Thickness(14, 6, 14, 6), ToolTip = tip }
                : SmallButton(glyph, text, null, tip: tip);
            b.Click += (_, _) => click();
            return b;
        }
        Button ChangeFolder(string text, bool accent)
        {
            var b = accent
                ? new Button { Style = (Style)FindResource("AccentButton"), Content = text, Padding = new Thickness(14, 6, 14, 6), ToolTip = "Vos dossiers, ceux du plan, ou un nouveau dossier" }
                : SmallButton("", text, null, tip: "Choisir un autre dossier pour cet élément");
            b.Click += (s, _) => ShowPlanDestinationMenu((Button)s, p);
            return b;
        }
        void Toggle(bool manual) { if (manual) p.Keep = !p.Keep; else p.Selected = !p.Selected; RefreshPlanItem(p); }
        var leave = "Ne sera pas déplacé et ne vous sera plus proposé";

        Border destination;
        if (p.Trash)
            destination = Square("DESTINATION", Big("", new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))), new UIElement[]
            {
                Line("Corbeille de Windows", strong: true),
                Line($"Envoyé à la Corbeille quand vous validez. Récupérable depuis la Corbeille tant qu'elle n'est pas vidée.", size: 12),
                Actions(Act("", "Ne pas supprimer", () => ToggleTrash(p)))
            });
        else if (p.IsManual && !p.Keep)
            destination = Square("DESTINATION", Big("", Brushes.Orange, 72, 0.85), new UIElement[]
            {
                Line("Aucun dossier évident", strong: true),
                Line("Centre PC ne devine pas quand ce n'est pas clair. Choisissez le dossier qui convient, ou laissez l'élément où il est.", size: 12),
                Actions(ChangeFolder("Choisir un dossier…", true), Act("", "Laisser à sa place", () => Toggle(true), leave))
            });
        else if (!p.IsManual && p.Selected)
            destination = Square("DESTINATION", Big("", exists ? Brushes.MediumSeaGreen : (Brush)FindResource("Accent")), new UIElement[]
            {
                Line(name, strong: true, tip: p.Destination),
                Line(parent),
                Line(exists ? "Votre dossier existant" : "Nouveau dossier, créé au moment du rangement", size: 12),
                Line(string.IsNullOrEmpty(p.Reason) ? null : "Pourquoi : " + p.Reason, size: 12),
                Actions(ChangeFolder("Changer de dossier…", false), Act("", "Laisser à sa place", () => Toggle(false), leave))
            });
        else
            destination = Square("DESTINATION", Big("", (Brush)FindResource("Text2"), 72, 0.6), new UIElement[]
            {
                Line("Reste à sa place", strong: true),
                Line(p.IsManual ? "Ne vous sera plus proposé." : "Ne sera pas déplacé et ne vous sera plus proposé.", size: 12),
                Actions(p.IsManual ? Act("", "Ranger plus tard", () => Toggle(true), "L'élément reste dans la file « à ranger vous-même »")
                                   : Act("", "Ranger cet élément", () => Toggle(false)))
            });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(origin);
        bool moving = !p.Trash && (p.IsManual ? !p.Keep : p.Selected);
        var arrow = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 24, Margin = new Thickness(14, 0, 14, 0),
                                    VerticalAlignment = VerticalAlignment.Center, Opacity = moving || p.Trash ? 0.9 : 0.3 };
        Grid.SetColumn(arrow, 1); grid.Children.Add(arrow);
        Grid.SetColumn(destination, 2); grid.Children.Add(destination);
        DocPlanDetail.Children.Add(grid);

        // Navigation seulement : les actions sur l'élément sont dans les carrés
        int index = _planOrder.IndexOf(p);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        nav.Children.Add(new TextBlock { Text = $"{index + 1} / {_planOrder.Count}", Style = (Style)FindResource("Subtle"), VerticalAlignment = VerticalAlignment.Center,
                                         Margin = new Thickness(0, 0, 12, 0), ToolTip = "Flèches ↑ ↓ du clavier pour passer d'un élément à l'autre" });
        var prev = new Button { Style = (Style)FindResource("BaseButton"), Content = "Précédent", IsEnabled = index > 0, Margin = new Thickness(0, 0, 8, 0) };
        prev.Click += (_, _) => MovePlanSelection(-1);
        var next = new Button { Style = (Style)FindResource("BaseButton"), Content = "Suivant", IsEnabled = index < _planOrder.Count - 1 };
        next.Click += (_, _) => MovePlanSelection(1);
        nav.Children.Add(prev);
        nav.Children.Add(next);
        DocPlanDetail.Children.Add(nav);
    }

    /// <summary>Aperçu en grand de l'élément (miniature haute définition de l'Explorateur).</summary>
    private PreviewHost _previewHost;
    private int _zoomToken;

    /// <summary>
    /// Aperçu en grand, le plus net possible : image en pleine résolution, texte, rendu de Windows (PDF, Office…),
    /// conversion LibreOffice pour les fichiers OpenDocument, et en dernier recours la miniature (jamais trop agrandie).
    /// </summary>
    private async void ShowZoom(DocProposal p)
    {
        if (p == null) return;
        int token = ++_zoomToken;
        bool Stale() => token != _zoomToken || DocZoom.Visibility != Visibility.Visible;
        DocZoomName.Text = p.Name;
        DocZoomStatus.Text = "Préparation de l'aperçu…";
        _previewHost?.Unload();
        DocZoomHostBox.Visibility = DocZoomTextBox.Visibility = Visibility.Collapsed;
        DocZoomImage.Source = null;
        DocZoomImage.Visibility = Visibility.Visible;
        DocZoomImage.MaxWidth = DocZoomImage.MaxHeight = double.PositiveInfinity;
        DocZoomGlyph.Text = p.IsFolder ? "" : "";
        DocZoomGlyph.Visibility = Visibility.Visible;
        DocZoom.Visibility = Visibility.Visible;

        var file = p.Source;
        // Affichage immédiat de la miniature (déjà en mémoire), remplacée dès que la version nette est prête
        if (!p.IsFolder && !DocPreview.IsText(file))
        {
            var quick = await ShellThumbnail.GetAsync(file, 256);
            if (Stale()) return;
            if (quick != null)
            {
                DocZoomImage.Source = quick;
                DocZoomGlyph.Visibility = Visibility.Collapsed;
                DocZoomStatus.Text = "Aperçu rapide · version nette en préparation…";
            }
        }
        if (!p.IsFolder && DocPreview.IsImage(file))
        {
            var full = await DocPreview.LoadImageAsync(file);
            if (Stale()) return;
            if (full != null) { ZoomImage(full, limit: false); DocZoomStatus.Text = ""; return; }
        }
        if (!p.IsFolder && DocPreview.IsText(file))
        {
            var text = await DocPreview.LoadTextAsync(file);
            if (Stale()) return;
            DocZoomText.Text = text;
            DocZoomText.ScrollToHome();
            DocZoomTextBox.Visibility = Visibility.Visible;
            DocZoomImage.Visibility = DocZoomGlyph.Visibility = Visibility.Collapsed;
            DocZoomStatus.Text = "Contenu du fichier (lecture seule)";
            return;
        }
        if (!p.IsFolder)
        {
            var handler = DocPreview.HandlerFor(file);
            if (handler == null)
            {
                // Fichier OpenDocument (LibreOffice) : la miniature intégrée est petite ; LibreOffice en fait un PDF temporaire
                // (souvent déjà prêt : préparé en arrière-plan à l'ouverture de la fenêtre, et gardé d'une fois sur l'autre)
                if (DocPreview.CachedPdf(file) == null) DocZoomStatus.Text = "Aperçu rapide · version nette en préparation par LibreOffice…";
                var pdf = await DocPreview.ToPdfAsync(file);
                if (Stale()) return;
                if (pdf != null) { file = pdf; handler = DocPreview.HandlerFor(pdf); }
            }
            if (handler != null && await ShowInHost(file, handler.Value, token))
            {
                DocZoomStatus.Text = file == p.Source ? "Aperçu de Windows · faites défiler pour voir la suite"
                                                      : "Aperçu réalisé par LibreOffice sur une copie temporaire · le fichier n'est pas modifié";
                return;
            }
            if (Stale()) return;
        }
        var thumb = await ShellThumbnail.GetAsync(file, 1600);
        if (Stale()) return;
        if (thumb is BitmapSource b)
        {
            ZoomImage(b, limit: true);
            DocZoomStatus.Text = b.PixelWidth < 600 ? "Aperçu réduit fourni par le fichier · « Ouvrir » pour le voir en entier" : "";
        }
        else DocZoomStatus.Text = "Pas d'aperçu disponible pour ce type de fichier · « Ouvrir » pour le consulter";
    }

    /// <summary>Image affichée ; une petite miniature n'est pas agrandie au-delà de 1,5 fois (sinon elle devient floue).</summary>
    private void ZoomImage(ImageSource img, bool limit)
    {
        DocZoomImage.Source = img;
        DocZoomImage.Visibility = Visibility.Visible;
        DocZoomGlyph.Visibility = Visibility.Collapsed;
        if (limit && img is BitmapSource b)
        {
            DocZoomImage.MaxWidth = b.PixelWidth * 1.5;
            DocZoomImage.MaxHeight = b.PixelHeight * 1.5;
        }
    }

    /// <summary>Rendu par le gestionnaire d'aperçu de Windows dans la zone dédiée ; faux s'il échoue.</summary>
    private async Task<bool> ShowInHost(string file, Guid handler, int token)
    {
        _previewHost ??= new PreviewHost();
        if (DocZoomHostBox.Child == null) DocZoomHostBox.Child = _previewHost;
        DocZoomHostBox.Visibility = Visibility.Visible;
        DocZoomImage.Visibility = DocZoomGlyph.Visibility = Visibility.Collapsed;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);   // zone créée et mesurée
        if (token != _zoomToken || DocZoom.Visibility != Visibility.Visible) return false;
        bool ok = _previewHost.Load(file, handler);
        if (!ok)
        {
            DocZoomHostBox.Visibility = Visibility.Collapsed;
            DocZoomImage.Visibility = DocZoomGlyph.Visibility = Visibility.Visible;
        }
        Focus();   // le clavier reste à Centre PC (Échap, flèches)
        return ok;
    }

    private void HideZoom()
    {
        _zoomToken++;
        DocZoom.Visibility = Visibility.Collapsed;
        DocZoomHostBox.Visibility = Visibility.Collapsed;
        _previewHost?.Unload();   // libère le fichier
    }

    /// <summary>
    /// Clic n'importe où en dehors de l'aperçu (fond, nom du fichier, marges) : retour. Dans l'aperçu (image, texte, document),
    /// le clic sert à regarder : sélection, défilement.
    /// </summary>
    private void DocZoom_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        if (IsInside(d, DocZoomImage) || IsInside(d, DocZoomTextBox) || IsInside(d, DocZoomHostBox)) return;
        HideZoom();
        e.Handled = true;
    }

    private void DocZoomClose_Click(object sender, RoutedEventArgs e) => HideZoom();

    private WrapPanel Actions(params Button[] buttons)
    {
        var sp = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 8, 6); sp.Children.Add(b); }
        return sp;
    }

    private bool _trashExplained;

    /// <summary>
    /// « Supprimer » : l'élément est marqué pour la Corbeille de Windows, sans confirmation à chaque fois. Rien n'est supprimé
    /// avant la validation ; la ligne et le carré Destination l'indiquent, et « Ne pas supprimer » annule. On passe au suivant.
    /// </summary>
    private void ToggleTrash(DocProposal p)
    {
        if (p == null) return;
        p.Trash = !p.Trash;
        RefreshPlanItem(p);
        if (!p.Trash) return;
        ShowToast(_trashExplained
            ? $"« {p.Name} » ira à la Corbeille"
            : $"« {p.Name} » ira à la Corbeille de Windows quand vous validerez (récupérable). Rien n'est supprimé d'ici là.");
        _trashExplained = true;
        if (_planOrder.IndexOf(p) < _planOrder.Count - 1) MovePlanSelection(1);
    }

    private void MovePlanSelection(int delta)
    {
        int i = _planOrder.IndexOf(_planCurrent) + delta;
        if (i >= 0 && i < _planOrder.Count) SelectPlanItem(_planOrder[i]);
    }

    private static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Ouverture " + path, ex); }
    }

    /// <summary>Autre destination : dossiers du plan, vos dossiers, ou un nouveau dossier.</summary>
    private void ShowPlanDestinationMenu(Button anchor, DocProposal p)
    {
        var menu = new ContextMenu();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!p.IsManual) seen.Add(p.Destination);
        foreach (var f in _plan.Where(f => seen.Add(f.Path)))
        {
            var target = f.Path;
            menu.Items.Add(MakeItem(f.Label, "\uE8B7", () => ChangeDestination(p, target)));
        }
        var mine = DocumentLibrary.Folders().Where(f => !string.IsNullOrEmpty(f.Path) && seen.Add(f.Path)).ToList();
        if (mine.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var f in mine)
            {
                var target = f.Path;
                menu.Items.Add(MakeItem(f.Label, f.Depth == 0 ? f.Root.Glyph : "\uE8B7", () => ChangeDestination(p, target)));
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Nouveau dossier…", "\uE8F4", async () =>
        {
            var root = DocumentLibrary.Roots.First(r => r.Key == DocumentLibrary.SuggestRoot(p.Source, p.IsFolder));
            var path = await NewFolderAsync(root.Path, root.Name + " (saisissez « Jeux\\Dofus » pour créer un sous-dossier)");
            if (path != null) ChangeDestination(p, path);
        }));
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void ChangeDestination(DocProposal p, string path)
    {
        var from = FolderOf(p);
        from?.Items.Remove(p);
        _planManual.Remove(p);          // choisi depuis la file « à ranger vous-même »
        p.Keep = false;
        if (from != null && from.Items.Count == 0) _plan.Remove(from);
        var to = _plan.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (to == null)
        {
            to = new DocFolderPlan { Path = path, Label = DocPlanner.FriendlyDir(path), Exists = Directory.Exists(path) };
            _plan.Add(to);
        }
        p.Destination = path;
        p.DestinationLabel = to.Label;
        p.DestinationExists = to.Exists;
        p.Reason = "choisi par vous";
        p.Selected = true;
        to.Items.Add(p);
        _planCurrent = p;
        BuildPlanItems();
    }

    private Border Row(UIElement content, Thickness? margin = null) => new()
    {
        Background = (Brush)FindResource("Surface2"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10),
        Margin = margin ?? new Thickness(0, 0, 0, 8), Child = content
    };

    private TextBlock Header(string text) => new()
    {
        Text = text, Style = (Style)FindResource("SectionTitle"), FontSize = 15, Margin = new Thickness(2, 10, 0, 10)
    };

    private void BuildPlanList()
    {
        DocPlanList.Children.Clear();
        if (_plan.Any(f => f.Items.Count > 0))
        {
            // Deux groupes lisibles : ce qui rejoint vos dossiers, ce qui demande un dossier nouveau
            var mine = _plan.Where(f => f.Exists).ToList();
            var fresh = _plan.Where(f => !f.Exists).ToList();
            if (mine.Count > 0)
            {
                DocPlanList.Children.Add(Header("Dans vos dossiers"));
                foreach (var f in mine) DocPlanList.Children.Add(PlanRow(f));
            }
            if (fresh.Count > 0)
            {
                DocPlanList.Children.Add(Header("Dans de nouveaux dossiers"));
                foreach (var f in fresh) DocPlanList.Children.Add(PlanRow(f));
            }
        }
        else foreach (var f in _planSuggested) DocPlanList.Children.Add(PlanRow(f));
        UpdatePlanFooter();
    }

    /// <summary>« Documents › Administratif › Factures » → (« Factures », « Documents › Administratif »).</summary>
    private static (string name, string parent) SplitLabel(string label)
    {
        int k = (label ?? "").LastIndexOf(" › ", StringComparison.Ordinal);
        return k < 0 ? (label, null) : (label.Substring(k + 3), label.Substring(0, k));
    }

    private FrameworkElement PlanRow(DocFolderPlan f)
    {
        bool withItems = f.Items.Count > 0;
        var (name, parent) = SplitLabel(f.Label);

        var check = new CheckBox { IsChecked = f.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = string.Join(" · ", new[] { parent, f.Description }.Where(x => !string.IsNullOrEmpty(x)));
        if (sub.Length > 0)
            titles.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Subtle"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
                                                TextTrimming = TextTrimming.CharacterEllipsis });

        var head = new Grid { Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(check);
        var icon = new TextBlock { Text = "\uE8B7", Style = (Style)FindResource("Glyph"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center,
                                   Margin = new Thickness(0, 0, 12, 0), Foreground = f.Exists ? Brushes.MediumSeaGreen : (Brush)FindResource("Accent") };
        Grid.SetColumn(icon, 1); head.Children.Add(icon);
        Grid.SetColumn(titles, 2); head.Children.Add(titles);

        var box = new StackPanel();
        box.Children.Add(head);
        if (!withItems)
        {
            check.Click += (_, _) => { f.Selected = check.IsChecked == true; UpdatePlanFooter(); };
            return Row(box);
        }

        // Compteur + chevron : la liste des éléments s'ouvre au clic sur la ligne
        var count = new TextBlock { Text = Plural(f.Items.Count, "élément", "éléments"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock { Text = "\uE70D", Style = (Style)FindResource("Glyph"), FontSize = 11, Margin = new Thickness(12, 0, 0, 0),
                                      VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(count); right.Children.Add(chevron);
        Grid.SetColumn(right, 3); head.Children.Add(right);
        head.Cursor = Cursors.Hand;
        head.ToolTip = "Afficher les éléments";

        var list = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(30, 8, 0, 0) };
        var itemChecks = new List<CheckBox>();
        void SyncFolder()
        {
            int on = f.Items.Count(i => i.Selected);
            f.Selected = on > 0;
            check.IsChecked = on == f.Items.Count ? true : on == 0 ? false : null;
            count.Text = on == f.Items.Count ? Plural(f.Items.Count, "élément", "éléments") : $"{on} sur {f.Items.Count}";
            UpdatePlanFooter();
        }
        foreach (var it in f.Items)
        {
            var ic = new CheckBox { IsChecked = it.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            ic.Click += (_, _) => { it.Selected = ic.IsChecked == true; SyncFolder(); };
            itemChecks.Add(ic);
            var line = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(ic);
            var g = new TextBlock { Text = it.IsFolder ? "\uE8B7" : "\uE8A5", Style = (Style)FindResource("Glyph"), FontSize = 13, Opacity = 0.7,
                                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(g, 1); line.Children.Add(g);
            var n = new TextBlock { Text = it.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = it.Source };
            Grid.SetColumn(n, 2); line.Children.Add(n);
            var from = new TextBlock { Text = it.Origin == "Bureau" ? "depuis le bureau" : $"depuis {it.Origin}", Style = (Style)FindResource("Subtle"),
                                       FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                       ToolTip = string.IsNullOrEmpty(it.Reason) ? null : "Pourquoi ce dossier : " + it.Reason };
            Grid.SetColumn(from, 3); line.Children.Add(from);
            list.Children.Add(line);
        }
        check.Click += (_, _) =>
        {
            bool on = check.IsChecked == true;
            foreach (var it in f.Items) it.Selected = on;
            foreach (var ic in itemChecks) ic.IsChecked = on;
            SyncFolder();
        };
        head.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, check)) return;
            bool open = list.Visibility != Visibility.Visible;
            list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "\uE70E" : "\uE70D";
            head.ToolTip = open ? "Masquer les éléments" : "Afficher les éléments";
        };
        box.Children.Add(list);
        return Row(box);
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == parent) return true;
        return false;
    }

    private void UpdatePlanFooter()
    {
        bool hasItems = HasPlanWork;
        int items = _plan.Sum(f => f.Items.Count(i => i.Selected && !i.Trash));
        int left = _plan.Sum(f => f.Items.Count(i => !i.Selected && !i.Trash)) + _planManual.Count(m => m.Keep && !m.Trash);
        int waiting = _planManual.Count(m => !m.Keep && !m.Trash);
        int trash = _plan.Sum(f => f.Items.Count(i => i.Trash)) + _planManual.Count(m => m.Trash);
        int folders = hasItems ? _plan.Count(f => f.Items.Any(i => i.Selected)) : _planSuggested.Count(f => f.Selected);
        var notes = new List<string>();
        if (left > 0) notes.Add(left == 1 ? "1 élément restera à sa place et ne vous sera plus proposé."
                                          : $"{left} éléments resteront à leur place et ne vous seront plus proposés.");
        if (waiting > 0) notes.Add(waiting == 1 ? "1 élément sans dossier reste dans la file « à ranger vous-même »."
                                                : $"{waiting} éléments sans dossier restent dans la file « à ranger vous-même ».");
        if (trash > 0) notes.Insert(0, trash == 1 ? "1 élément ira à la Corbeille de Windows (récupérable)."
                                                  : $"{trash} éléments iront à la Corbeille de Windows (récupérables).");
        if (notes.Count == 0) notes.Add("Vous pourrez vérifier chaque élément juste après.");
        DocPlanSummary.Text = hasItems
            ? string.Join(" ", notes)
            : folders > 0 ? $"{Plural(folders, "dossier sera créé", "dossiers seront créés")}." : "Aucun dossier choisi.";
        DocPlanButtons.Children.Clear();
        var cancel = new Button { Style = (Style)FindResource("BaseButton"), Content = "Annuler", Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => CloseDocPlan();
        DocPlanButtons.Children.Add(cancel);
        var go = new Button
        {
            Style = (Style)FindResource("AccentButton"),
            IsEnabled = hasItems ? items > 0 || left > 0 || trash > 0 : folders > 0,
            Content = hasItems
                ? (items > 0 ? $"Ranger {Plural(items, "élément", "éléments")}" + (trash > 0 ? $" · {trash} à la corbeille" : "")
                   : trash > 0 ? $"Mettre {Plural(trash, "élément", "éléments")} à la corbeille" : "Valider mes choix")
                : "Créer les dossiers"
        };
        go.Click += async (_, _) => await ApplyPlanAsync();
        DocPlanButtons.Children.Add(go);
    }

    private async Task ApplyPlanAsync()
    {
        bool hasItems = HasPlanWork;
        if (!hasItems)
        {
            try { DocPlanner.CreateFolders(_planSuggested.Where(f => f.Selected).ToList()); }
            catch (Exception ex) { ShowToast("Création des dossiers impossible : " + ex.Message); }
            CloseDocPlan();
            ShowToast("Dossiers créés : vous pouvez les renommer ou en ajouter.");
            return;
        }
        // Décoché = l'utilisateur le considère rangé : il reste en place et ne sera plus proposé
        // (file manuelle : seulement ce que l'utilisateur a choisi de laisser ; le reste attend la prochaine fois)
        // Corbeille d'abord (ce que l'utilisateur a marqué « Supprimer »), récupérable depuis la Corbeille de Windows
        var trashed = _plan.SelectMany(f => f.Items).Concat(_planManual).Where(i => i.Trash).ToList();
        int recycled = 0, trashErrors = 0;
        foreach (var t in trashed)
        {
            _previewHost?.Unload();
            try { DocumentLibrary.Recycle(new DocItem { Path = t.Source, IsFolder = t.IsFolder, Origin = t.Origin }); recycled++; }
            catch (Exception ex) { trashErrors++; Log.Error("Corbeille " + t.Source, ex); }
        }
        var declined = _plan.SelectMany(f => f.Items).Where(i => !i.Selected && !i.Trash).Concat(_planManual.Where(m => m.Keep && !m.Trash)).ToList();
        if (declined.Count > 0) DocPlanner.Decline(declined);
        var selected = _plan.Where(f => f.Items.Any(i => i.Selected && !i.Trash)).ToList();
        string trashNote = (recycled > 0 ? $"{Plural(recycled, "élément mis", "éléments mis")} à la Corbeille. " : "")
                         + (trashErrors > 0 ? $"{Plural(trashErrors, "élément n'a", "éléments n'ont")} pas pu aller à la Corbeille (fichier ouvert ?). " : "");
        if (selected.Count == 0)
        {
            CloseDocPlan();
            _ = ReloadAsync();
            ShowToast(trashNote + (declined.Count == 0 ? "" : declined.Count == 1 ? "1 élément laissé à sa place." : $"{declined.Count} éléments laissés à leur place."));
            return;
        }
        DocPlanButtons.IsEnabled = false;
        DocPlanSummary.Text = "Rangement en cours…";
        var (moved, errors) = await Task.Run(() => DocPlanner.Apply(selected));
        DocPlanButtons.IsEnabled = true;
        if (errors.Count > 0)
            ShowToast(errors.Count == 1 ? "1 élément n'a pas pu être déplacé (fichier ouvert ?) : il reste à sa place."
                                        : $"{errors.Count} éléments n'ont pas pu être déplacés (fichiers ouverts ?) : ils restent à leur place.");
        _ = ReloadAsync();
        ShowReviewOverlay();
        if (moved > 0) ShowToast(trashNote + (moved == 1 ? "1 élément rangé. Vérifiez-le : il peut être remis à sa place."
                                                         : $"{moved} éléments rangés. Vérifiez-les : chacun peut être remis à sa place."));
    }

    // ------------------------------------------------------------------ vérification
    private void ShowReviewOverlay()
    {
        DocPlanSplit.Visibility = Visibility.Collapsed;
        DocPlanScroll.Visibility = Visibility.Visible;
        DocPlanOverlayTitle.Text = "Vérifiez le rangement";
        DocPlanOverlayText.Text = "Chaque élément a été déplacé dans le dossier indiqué. Gardez ce qui vous convient ; « Remettre à sa place » le replace exactement où il était.";
        DocPlanReassurance.ToolTip = null;
        DocPlanReassurance.Text = "Aucune perte possible : « Remettre à sa place » annule le déplacement, et un élément remis à sa place ne vous sera plus proposé. "
            + "Vous pouvez aussi terminer la vérification plus tard : ces éléments restent listés sur la page Documents.";
        BuildReviewList();
        DocPlanOverlay.Visibility = Visibility.Visible;
    }

    private void BuildReviewList()
    {
        var review = Settings.Current.DocReview.ToList();
        if (review.Count == 0)
        {
            CloseDocPlan();
            ShowToast("Vérification terminée.");
            return;
        }
        DocPlanList.Children.Clear();
        foreach (var g in review.GroupBy(e => e.Folder))
        {
            var head = new Grid { Margin = new Thickness(2, 12, 0, 8) };
            head.Children.Add(new TextBlock { Text = $"{g.Key}  ·  {g.Count()}", Style = (Style)FindResource("SectionTitle"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            var acts = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var keepAll = SmallButton("", "Tout garder", (_, _) => { foreach (var x in g) DocPlanner.Accept(x); BuildReviewList(); });
            var backAll = SmallButton("", "Tout remettre", (_, _) => RejectMany(g.ToList()));
            backAll.Margin = new Thickness(8, 0, 0, 0);
            acts.Children.Add(keepAll);
            acts.Children.Add(backAll);
            head.Children.Add(acts);
            DocPlanList.Children.Add(head);
            foreach (var e in g) DocPlanList.Children.Add(ReviewRow(e));
        }
        DocPlanSummary.Text = review.Count == 1 ? "1 élément à vérifier." : $"{review.Count} éléments à vérifier.";
        DocPlanButtons.Children.Clear();
        var later = new Button { Style = (Style)FindResource("BaseButton"), Content = "Terminer plus tard", Margin = new Thickness(0, 0, 10, 0) };
        later.Click += (_, _) => CloseDocPlan();
        var back = new Button { Style = (Style)FindResource("BaseButton"), Content = "Tout remettre à sa place", Margin = new Thickness(0, 0, 10, 0) };
        back.Click += (_, _) => RejectMany(Settings.Current.DocReview.ToList());
        var ok = new Button { Style = (Style)FindResource("AccentButton"), Content = "Tout valider" };
        ok.Click += (_, _) => { foreach (var x in Settings.Current.DocReview.ToList()) DocPlanner.Accept(x); BuildReviewList(); };
        DocPlanButtons.Children.Add(later);
        DocPlanButtons.Children.Add(back);
        DocPlanButtons.Children.Add(ok);
    }

    private FrameworkElement ReviewRow(DocReviewEntry e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = e.To });
        text.Children.Add(new TextBlock
        {
            Text = $"depuis {e.OriginLabel}" + (string.IsNullOrEmpty(e.Reason) ? "" : $" · {e.Reason}"),
            Style = (Style)FindResource("Subtle"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis
        });
        grid.Children.Add(text);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var keep = SmallButton("", "Garder", (_, _) => { DocPlanner.Accept(e); BuildReviewList(); }, tip: "Le rangement vous convient");
        var back = SmallButton("", "Remettre à sa place", (_, _) => RejectMany(new List<DocReviewEntry> { e }),
                               tip: "Replace l'élément à son emplacement d'origine ; il ne vous sera plus proposé");
        var other = SmallButton("", "Autre dossier…", null, tip: "Choisir un autre dossier");
        other.Click += (s, _) => ShowMoveElsewhereMenu((Button)s, e);
        var open = SmallButton("\uE838", null, (_, _) => DocumentLibrary.ShowInExplorer(e.To), tip: "Voir dans l'Explorateur");
        foreach (var b in new[] { keep, back, other, open }) { b.Margin = new Thickness(6, 0, 0, 0); acts.Children.Add(b); }
        Grid.SetColumn(acts, 1);
        grid.Children.Add(acts);
        return Row(grid);
    }

    private void RejectMany(List<DocReviewEntry> entries)
    {
        int errors = 0;
        foreach (var e in entries)
            if (DocPlanner.Reject(e) != null) errors++;
        if (errors > 0) ShowToast($"{errors} élément(s) n'ont pas pu être remis à leur place (fichier ouvert ?). Réessayez plus tard.");
        else ShowToast(entries.Count == 1 ? $"« {entries[0].Name} » est revenu à sa place." : $"{entries.Count} éléments sont revenus à leur place.");
        _ = ReloadAsync();
        BuildReviewList();
    }

    private void ShowMoveElsewhereMenu(Button anchor, DocReviewEntry e)
    {
        var menu = new ContextMenu();
        foreach (var f in DocumentLibrary.Folders().Where(f => !string.IsNullOrEmpty(f.Path)
                     && !string.Equals(f.Path, System.IO.Path.GetDirectoryName(e.To), StringComparison.OrdinalIgnoreCase)))
        {
            var folder = f;
            menu.Items.Add(MakeItem(folder.Label, folder.Depth == 0 ? folder.Root.Glyph : "", () => MoveReviewed(e, folder.Path)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Nouveau dossier…", "", async () =>
        {
            var root = DocumentLibrary.Roots.First(r => r.Key == DocumentLibrary.SuggestRoot(e.To, e.IsFolder));
            var path = await NewFolderAsync(root.Path, root.Name + " (saisissez « Jeux\\Dofus » pour créer un sous-dossier)");
            if (path != null) MoveReviewed(e, path);
        }));
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void MoveReviewed(DocReviewEntry e, string folder)
    {
        var err = DocPlanner.MoveElsewhere(e, folder);
        if (err != null) ShowToast("Déplacement impossible : " + err);
        else ShowToast($"« {e.Name} » rangé dans {DocPlanner.FriendlyDir(folder)}.");
        BuildReviewList();
    }

    private void DocReviewAcceptAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var x in Settings.Current.DocReview.ToList()) DocPlanner.Accept(x);
        ShowToast("Rangement validé.");
        RefreshDocuments();
    }

    /// <summary>Clavier : Échap ferme la fenêtre du plan (la vérification reste disponible).</summary>
    private bool DocPlanKey(Key key)
    {
        if (DocZoom.Visibility == Visibility.Visible)
        {
            if (key is Key.Up or Key.Down or Key.Left or Key.Right)
            {
                MovePlanSelection(key is Key.Up or Key.Left ? -1 : 1);
                ShowZoom(_planCurrent);
                return true;
            }
            if (key is Key.Escape or Key.Space or Key.Enter) { HideZoom(); return true; }
            return false;
        }
        if (DocPlanSplit.Visibility == Visibility.Visible && key == Key.Space && _planCurrent != null)
        {
            ShowZoom(_planCurrent);
            return true;
        }
        if (DocPlanSplit.Visibility == Visibility.Visible && key is Key.Up or Key.Down)
        {
            MovePlanSelection(key == Key.Up ? -1 : 1);
            return true;
        }
        if (DocPlanSplit.Visibility == Visibility.Visible && key == Key.Delete && _planCurrent != null)
        {
            ToggleTrash(_planCurrent);
            return true;
        }
        if (key != Key.Escape) return false;
        CloseDocPlan();
        return true;
    }
}
