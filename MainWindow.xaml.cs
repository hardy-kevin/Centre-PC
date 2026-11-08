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

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AppItem> _apps = new();
    private readonly ObservableCollection<AppItem> _desk = new();
    private List<ThemeGroup> _appGroups = new();
    private string _chip;   // thème sélectionné (null = toutes)
    private bool _loading;

    private readonly ObservableCollection<WingetUpdate> _winget = new();
    private readonly ObservableCollection<WuUpdate> _wu = new();
    private readonly ObservableCollection<StepItem> _steps = new();
    private HardwareInfo _hw;
    private bool _wingetScanned;

    private static readonly CompareInfo Cmp = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;
    private const CompareOptions CmpOpts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.StyleWindow(this);
        StateChanged += (_, _) => UpdateMaxState();
        Loaded += async (_, _) => await StartupAsync();
        PreviewTextInput += Window_PreviewTextInput;
        PreviewKeyDown += Window_PreviewKeyDown;

        WingetList.ItemsSource = _winget;
        WuList.ItemsSource = _wu;
        StepsList.ItemsSource = _steps;
        TidyFilesCheck.IsChecked = Settings.Current.TidyFiles;
        _modeInit = true;
        (DesktopManager.FoldersMode ? ModeFoldersRadio : ModeAppRadio).IsChecked = true;
        _modeInit = false;
        StyleFoldersCheck.IsChecked = Settings.Current.StyleFolders;
        ArrowsCheck.IsChecked = IconStyler.ArrowsHidden();
        UpdateStyleStatus();
        UpdateLastCheck();
    }

    private async Task StartupAsync()
    {
        SearchBox.Focus();
        if (!Settings.Current.OnboardingDone) WelcomeOverlay.Visibility = Visibility.Visible;
        ApplyOnlineState();
        await ReloadAsync();
        RefreshSettingsPage();
        try { _hw = await Updates.ScanHardwareAsync(); ShowHardware(); } catch { }

        // Tout ce qui suit a besoin d'internet : sans connexion, l'appli reste pleinement utilisable (mode dégradé)
        _online = await Network.IsOnlineAsync(force: true);
        ApplyOnlineState();
        Network.Changed += () => Dispatcher.InvokeAsync(async () =>
        {
            _online = await Network.IsOnlineAsync(force: true);
            ApplyOnlineState();
            if (_online && Settings.Current.AutoOnline) await RunOnlineAsync(silent: true);
        });
        if (!_online) return;

        // Nouvelle version de Centre PC ?
        if (Settings.Current.AutoCheckAppUpdate) await CheckAppUpdateAsync(fromUser: false);
        // Tri en ligne des applis inconnues, sans action de l'utilisateur
        if (Settings.Current.AutoOnline) await RunOnlineAsync(silent: true);
        // Vérification des mises à jour en arrière-plan (au plus une fois toutes les 12 h) : une pastille prévient
        var last = Settings.Current.LastUpdateCheck;
        if (last == null || DateTime.Now - last > TimeSpan.FromHours(12)) Scan_Click(this, null);
    }

    // =====================================================================================
    //  Chargement
    // =====================================================================================
    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var desk = await Task.Run(() => AppScanner.ScanDesktop());
            var all = await AppScanner.ScanAllAsync(desk);

            _desk.Clear(); foreach (var d in desk) _desk.Add(d);
            _apps.Clear(); foreach (var a in all) _apps.Add(a);

            RefreshAll();
            IconLoader.Enqueue(desk);
            IconLoader.Enqueue(all);
        }
        finally { _loading = false; }
    }

    private void RefreshAll()
    {
        BuildChips();
        RefreshApps();
        RefreshFavorites();
        DeskList.ItemsSource = _desk.Where(d => d.OnDesktop)
            .OrderBy(d => d.ThemeOrder).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        StoredList.ItemsSource = ThemeGroup.Build(_desk.Where(d => d.Location == ItemLocation.Stored));

        int onDesk = _desk.Count(d => d.OnDesktop), stored = _desk.Count(d => d.Location == ItemLocation.Stored);
        DeskCount.Text = onDesk.ToString();
        StoredCount.Text = stored.ToString();
        DeskEmpty.Visibility = onDesk == 0 ? Visibility.Visible : Visibility.Collapsed;

        int tidyable = _desk.Count(d => d.OnDesktop && !d.Keep && (d.IsShortcut || Settings.Current.TidyFiles));
        DeskHint.Text = tidyable > 0
            ? $"{tidyable} icône(s) peuvent être rangées : clique sur « Ranger le bureau ». Tu pourras tout remettre comme avant."
            : "Ton bureau est rangé. Tes icônes restent accessibles ici, classées par thème.";
        StoredLabel.Text = DesktopManager.FoldersMode ? "rangées dans les dossiers de thème" : "rangées dans Centre PC";
        LegacyBanner.Visibility = Visibility.Collapsed;
    }

    private void RefreshApps()
    {
        _appGroups = ThemeGroup.Build(_apps.Where(a => !Settings.IsHidden(a.Name)).Where(AppFilter));
        AppsList.ItemsSource = _appGroups;
        UpdateAppsState();
    }

    private void RefreshFavorites()
    {
        FavList.ItemsSource = _apps.Where(a => Settings.Current.Favorites.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        UpdateAppsState();
    }

    private void UpdateAppsState()
    {
        bool searching = SearchBox.Text.Trim().Length > 0;
        bool hasFav = FavList.ItemsSource is System.Collections.ICollection c && c.Count > 0;
        FavSection.Visibility = !searching && _chip == null && hasFav ? Visibility.Visible : Visibility.Collapsed;
        AppsEmpty.Visibility = _appGroups.Count == 0 && _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Bandeau de guidage : recherche en ligne en cours, ou applis à trier à la main
        var toSort = SortCandidates(includeSkipped: false);
        if (_onlineRunning)
        {
            SortBanner.Visibility = Visibility.Visible;
            SortBannerTitle.Text = "Identification des applis inconnues…";
            SortBannerText.Text = "Centre PC cherche en ligne le type de chaque appli. Rien à faire.";
            SortBannerButton.Visibility = Visibility.Collapsed;
        }
        else if (toSort.Count > 0 && !searching && _chip == null)
        {
            SortBanner.Visibility = Visibility.Visible;
            SortBannerTitle.Text = toSort.Count == 1 ? "1 appli n'a pas pu être triée" : $"{toSort.Count} applis n'ont pas pu être triées";
            SortBannerText.Text = "Range-les en quelques clics : un clic par appli.";
            SortBannerButton.Visibility = Visibility.Visible;
        }
        else SortBanner.Visibility = Visibility.Collapsed;
        OnlineButton.Visibility = Visibility.Collapsed;
        AppsSubtitle.Text = _apps.Count == 0
            ? "Chargement de tes applications…"
            : $"{_apps.Count(a => !Settings.IsHidden(a.Name))} applications · tape un nom pour lancer";
    }

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
        AddChip(null, "Toutes", _apps.Count, null);
        foreach (var cat in Settings.AllCategoryNames)
        {
            int n = _apps.Count(a => a.Theme == cat);
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
            if (first != null) Launch(first);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; }
    }

    // Taper n'importe où sur la page Applications lance la recherche
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (PageApps.Visibility != Visibility.Visible || Keyboard.FocusedElement is TextBox) return;
        if (SorterOverlay.Visibility == Visibility.Visible || WelcomeOverlay.Visibility == Visibility.Visible) { e.Handled = true; return; }
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        SearchBox.Focus();
        SearchBox.AppendText(e.Text);
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    // ---------- Tri en ligne des applis restées dans « Autres »
    private bool _onlineRunning;
    private async void Online_Click(object sender, RoutedEventArgs e) => await RunOnlineAsync(silent: false);

    private async Task RunOnlineAsync(bool silent)
    {
        var todo = _apps.Where(a => a.Theme == Settings.Current.DefaultCategory && !Settings.IsHidden(a.Name)
                                    && !OnlineClassifier.AlreadySearched(a.Name)).ToList();
        if (todo.Count == 0 || _onlineRunning) { if (!silent) ShowToast("Rien de nouveau à chercher en ligne."); return; }
        if (!await Network.IsOnlineAsync()) { if (!silent) ShowToast("Pas de connexion internet : le tri en ligne reprendra plus tard."); return; }
        _onlineRunning = true;
        UpdateAppsState();
        try
        {
            int found = await OnlineClassifier.ClassifyAsync(todo, null);
            foreach (var x in _apps.Concat(_desk).Where(x => x.IsShortcut))
                x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
            if (found > 0) ShowToast($"{found} appli(s) triée(s) automatiquement grâce à Internet");
        }
        catch (Exception ex) { if (!silent) ShowToast("Recherche en ligne impossible : " + ex.Message); }
        finally
        {
            _onlineRunning = false;
            RefreshAll();
        }
    }

    // ---------- Assistant de tri
    private List<AppItem> _sortQueue = new();
    private int _sortIndex;
    private readonly Stack<(AppItem app, int index, string prevOverride, bool prevHidden, bool prevSkipped)> _sortHistory = new();

    private List<AppItem> SortCandidates(bool includeSkipped) => _apps
        .Where(a => a.Theme == Settings.Current.DefaultCategory && a.IsShortcut && !Settings.IsHidden(a.Name)
                    && (includeSkipped || !Settings.IsSkipped(a.Name)))
        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void OpenSorter_Click(object sender, RoutedEventArgs e) => OpenSorter(includeSkipped: false);
    private void OpenSorterAll_Click(object sender, RoutedEventArgs e) => OpenSorter(includeSkipped: true);

    private void OpenSorter(bool includeSkipped)
    {
        _sortQueue = SortCandidates(includeSkipped);
        if (_sortQueue.Count == 0) { ShowToast("Toutes tes applis sont déjà triées."); return; }
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
    }

    private void ShowSorterItem()
    {
        if (_sortIndex >= _sortQueue.Count) { CloseSorter(done: true); return; }
        var a = _sortQueue[_sortIndex];
        SorterName.Text = a.Name;
        SorterHint.Text = string.IsNullOrWhiteSpace(a.Context) ? (a.Target ?? "") : a.Context;
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
        Settings.Current.Overrides.TryGetValue(a.Name, out var prev);
        _sortHistory.Push((a, _sortIndex, prev, Settings.IsHidden(a.Name), Settings.IsSkipped(a.Name)));
    }

    private void Reclassify(string name)
    {
        foreach (var x in _apps.Concat(_desk).Where(x => x.IsShortcut && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
    }

    private void SorterAssign(string category)
    {
        if (_sortIndex >= _sortQueue.Count) return;
        var a = _sortQueue[_sortIndex];
        Remember(a);
        Settings.SetOverride(a.Name, category);
        Settings.SetSkipped(a.Name, false);
        Reclassify(a.Name);
        _sortIndex++;
        ShowSorterItem();
    }

    private void SorterSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_sortIndex >= _sortQueue.Count) return;
        var a = _sortQueue[_sortIndex];
        Remember(a);
        Settings.SetSkipped(a.Name, true);
        _sortIndex++;
        ShowSorterItem();
    }

    private void SorterHide_Click(object sender, RoutedEventArgs e)
    {
        if (_sortIndex >= _sortQueue.Count) return;
        var a = _sortQueue[_sortIndex];
        Remember(a);
        Settings.SetHidden(a.Name, true);
        _sortIndex++;
        ShowSorterItem();
    }

    private void SorterBack_Click(object sender, RoutedEventArgs e)
    {
        if (_sortHistory.Count == 0) return;
        var (a, index, prevOverride, prevHidden, prevSkipped) = _sortHistory.Pop();
        Settings.SetOverride(a.Name, prevOverride);
        Settings.SetHidden(a.Name, prevHidden);
        Settings.SetSkipped(a.Name, prevSkipped);
        Reclassify(a.Name);
        _sortIndex = index;
        ShowSorterItem();
    }

    private void CloseSorter_Click(object sender, RoutedEventArgs e) => CloseSorter(done: false);

    private void CloseSorter(bool done)
    {
        SorterOverlay.Visibility = Visibility.Collapsed;
        int n = _sortHistory.Count;
        RefreshAll();
        RefreshSettingsPage();
        if (n > 0) ShowToast(done ? "Tri terminé, c'est enregistré" : $"{n} appli(s) triée(s), c'est enregistré");
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
        if (ThemeEditor.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { ThemeEditor.Visibility = Visibility.Collapsed; e.Handled = true; }
            return;
        }
        if (SorterOverlay.Visibility != Visibility.Visible) return;
        int digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 + 1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 + 1 : 0;
        if (digit > 0 && digit <= Settings.Current.Categories.Count) SorterAssign(Settings.Current.Categories[digit - 1].Name);
        else if (e.Key == Key.Space) SorterSkip_Click(this, null);
        else if (e.Key == Key.M) SorterHide_Click(this, null);
        else if (e.Key == Key.Back) SorterBack_Click(this, null);
        else if (e.Key == Key.Escape) CloseSorter(done: false);
        else return;
        e.Handled = true;
    }

    // ---------- Présentation
    private void Welcome_Click(object sender, RoutedEventArgs e)
    {
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        Settings.Current.OnboardingDone = true;
        Settings.Save();
        SearchBox.Focus();
    }

    private void ShowWelcome_Click(object sender, RoutedEventArgs e) => WelcomeOverlay.Visibility = Visibility.Visible;

    // ---------- Page Réglages
    private void RefreshSettingsPage()
    {
        _settingsInit = true;
        AutoOnlineCheck.IsChecked = Settings.Current.AutoOnline;
        _settingsInit = false;
        int unsorted = SortCandidates(includeSkipped: true).Count;
        OnlineSummary.Text = unsorted == 0 ? "Toutes les applis sont triées." : $"{unsorted} appli(s) encore dans « Autres ».";

        var rows = new List<ChoiceRow>();
        foreach (var kv in Settings.Current.Overrides.OrderBy(k => k.Key, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new ChoiceRow { Name = kv.Key, Kind = "theme", Label = kv.Value, Brush = Settings.BrushOf(kv.Value) });
        foreach (var n in Settings.Current.HiddenApps.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new ChoiceRow { Name = n, Kind = "hidden", Label = "Masquée", Brush = Brushes.Gray });
        foreach (var n in Settings.Current.SortSkipped.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new ChoiceRow { Name = n, Kind = "skipped", Label = "Laissée dans Autres", Brush = Settings.BrushOf(Settings.Current.DefaultCategory) });
        ChoicesList.ItemsSource = rows;
        ChoicesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChoicesSummary.Text = rows.Count == 0 ? "Rien à réinitialiser." : $"{rows.Count} choix enregistré(s). « Annuler » rend la main au tri automatique.";
        ResetChoicesButton.IsEnabled = rows.Count > 0;
        ThemesList.ItemsSource = Settings.Current.Categories.Select(c => new ThemeRow
        {
            Name = c.Name, Glyph = Settings.GlyphOf(c.Name), Brush = Settings.BrushOf(c.Name),
            Detail = $"{c.Keywords?.Count ?? 0} mots-clés · {_apps.Count(a => a.Theme == c.Name)} applis"
        }).ToList();
        AppVersionText.Text = $"Version {AppUpdater.Current}";
        _settingsInit = true;
        AutoAppUpdateCheck.IsChecked = Settings.Current.AutoCheckAppUpdate;
        _settingsInit = false;
        if (string.IsNullOrEmpty(AppUpdateStatus.Text))
            AppUpdateStatus.Text = _online ? "Clique sur « Rechercher une mise à jour » pour vérifier." : "Hors connexion : la vérification se fera quand internet reviendra.";
        AboutText.Text = $"© 2026 Kevin Hardy · Licence PolyForm Noncommercial 1.0.0 · réglages enregistrés dans {Settings.DataDir}";
    }

    private bool _settingsInit;
    private async void AutoOnline_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsInit) return;
        Settings.Current.AutoOnline = AutoOnlineCheck.IsChecked == true;
        Settings.Save();
        if (Settings.Current.AutoOnline) await RunOnlineAsync(silent: true);
    }

    private async void RetryOnline_Click(object sender, RoutedEventArgs e)
    {
        OnlineClassifier.Forget();
        foreach (var x in _apps.Concat(_desk).Where(x => x.IsShortcut))
            x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
        RefreshAll();
        await RunOnlineAsync(silent: false);
        RefreshSettingsPage();
    }

    private void UndoChoice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChoiceRow r) return;
        switch (r.Kind)
        {
            case "theme": Settings.SetOverride(r.Name, null); break;
            case "hidden": Settings.SetHidden(r.Name, false); break;
            case "skipped": Settings.SetSkipped(r.Name, false); break;
        }
        Reclassify(r.Name);
        RefreshAll();
        RefreshSettingsPage();
        ShowToast($"{r.Name} : choix annulé");
    }

    private void ResetChoices_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Réinitialiser tous tes choix de tri ?\n\nLes thèmes choisis à la main, les applis masquées et celles laissées dans « Autres » reviendront au tri automatique.",
                "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Settings.ResetUserChoices();
        foreach (var x in _apps.Concat(_desk).Where(x => x.IsShortcut))
            x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
        RefreshAll();
        RefreshSettingsPage();
        ShowToast("Choix réinitialisés : tri automatique rétabli");
    }

    // ---------- Thèmes : éditeur, export / import
    private static readonly string[] Palette =
    {
        "#7C5CFF", "#C026D3", "#A78BFA", "#6366F1", "#3B82F6", "#0EA5E9", "#38BDF8", "#0891B2", "#14B8A6", "#10B981",
        "#65A30D", "#84CC16", "#EAB308", "#F59E0B", "#CA8A04", "#F97316", "#B45309", "#EF4444", "#E11D48", "#EC4899",
        "#FB7185", "#475569", "#6B7280"
    };
    private static readonly string[] GlyphChoices =
    {
        "\uE7FC", "\uE768", "\uE774", "\uE8BD", "\uE945", "\uE8D6", "\uE714", "\uE722", "\uE8A5", "\uE8EF", "\uE80A", "\uE9D2",
        "\uE7BE", "\uE943", "\uE968", "\uE72E", "\uE753", "\uE772", "\uE90F", "\uE770", "\uE8B7", "\uE734", "\uE80F", "\uE716",
        "\uE825", "\uE7C3", "\uE8F1", "\uE719"
    };
    private string _editingTheme;     // null = nouveau thème
    private string _pickedColor, _pickedGlyph;

    private void AddTheme_Click(object sender, RoutedEventArgs e) => OpenThemeEditor(null);
    private void EditTheme_Click(object sender, RoutedEventArgs e) => OpenThemeEditor((sender as FrameworkElement)?.Tag as string);

    private void OpenThemeEditor(string name)
    {
        _editingTheme = name;
        var c = name == null ? null : Settings.Find(name);
        ThemeEditorTitle.Text = c == null ? "Nouveau thème" : "Modifier le thème";
        ThemeNameBox.Text = c?.Name ?? "";
        ThemeKeywordsBox.Text = string.Join(Environment.NewLine, c?.Keywords ?? new List<string>());
        _pickedColor = c?.Color ?? Palette[new Random().Next(Palette.Length)];
        _pickedGlyph = c?.Glyph ?? GlyphChoices[0];
        DeleteThemeButton.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        BuildPickers();
        ThemeEditor.Visibility = Visibility.Visible;
        ThemeNameBox.Focus();
    }

    private void BuildPickers()
    {
        ThemeColors.Children.Clear();
        foreach (var hex in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            var b = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = brush, Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand, BorderBrush = Brushes.White, BorderThickness = new Thickness(hex == _pickedColor ? 3 : 0), Tag = hex
            };
            b.MouseLeftButtonUp += (s, _) => { _pickedColor = (string)((Border)s).Tag; BuildPickers(); };
            ThemeColors.Children.Add(b);
        }
        ThemeGlyphs.Children.Clear();
        var pickedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_pickedColor));
        foreach (var g in GlyphChoices)
        {
            var b = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Background = g == _pickedGlyph ? pickedBrush : (Brush)FindResource("Surface2"), Tag = g,
                Child = new TextBlock { Text = g, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Foreground = Brushes.White,
                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            b.MouseLeftButtonUp += (s, _) => { _pickedGlyph = (string)((Border)s).Tag; BuildPickers(); };
            ThemeGlyphs.Children.Add(b);
        }
    }

    private void CancelTheme_Click(object sender, RoutedEventArgs e) => ThemeEditor.Visibility = Visibility.Collapsed;

    private async void SaveTheme_Click(object sender, RoutedEventArgs e)
    {
        var name = Settings.Normalize(ThemeNameBox.Text);
        if (name.Length == 0) { ShowToast("Donne un nom au thème."); return; }
        var reserved = new[] { Settings.Current.DefaultCategory, Settings.FilesCategory, Settings.FavoritesCategory };
        if (reserved.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            Settings.Current.Categories.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && c.Name != _editingTheme))
        { ShowToast("Ce nom est déjà utilisé."); return; }
        var keywords = ThemeKeywordsBox.Text.Split('\n').Select(k => k.Trim()).Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var cat = _editingTheme == null ? null : Settings.Find(_editingTheme);
        if (cat == null)
        {
            cat = new Category();
            // Nouveau thème : placé avant « Outils Windows » / « Utilitaires » pour garder les thèmes larges en fin de liste
            var idx = Settings.Current.Categories.FindIndex(c => c.Name is "Utilitaires" or "Outils Windows");
            if (idx < 0) Settings.Current.Categories.Add(cat); else Settings.Current.Categories.Insert(idx, cat);
        }
        else if (cat.Name != name) RenameTheme(cat.Name, name);
        cat.Name = name; cat.Color = _pickedColor; cat.Glyph = _pickedGlyph; cat.Keywords = keywords;
        Settings.Rebuild();
        Settings.Save();
        ThemeEditor.Visibility = Visibility.Collapsed;
        await AfterThemesChangedAsync($"Thème « {name} » enregistré");
    }

    private void RenameTheme(string oldName, string newName)
    {
        foreach (var k in Settings.Current.Overrides.Where(kv => kv.Value == oldName).Select(kv => kv.Key).ToList())
            Settings.Current.Overrides[k] = newName;
        // Dossier de thème sur le bureau
        try
        {
            var oldDir = System.IO.Path.Combine(AppScanner.UserDesktop, oldName);
            var newDir = System.IO.Path.Combine(AppScanner.UserDesktop, newName);
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
            {
                Directory.Move(oldDir, newDir);
                foreach (var b in IconStyler.Backups.Where(b => b.Path.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase)))
                    b.Path = newDir + b.Path.Substring(oldDir.Length);
                foreach (var k in Settings.Current.Stored.Keys.Where(k => k.StartsWith(oldDir + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var v = Settings.Current.Stored[k];
                    Settings.Current.Stored.Remove(k);
                    Settings.Current.Stored[newDir + k.Substring(oldDir.Length)] = v;
                }
            }
        }
        catch { }
    }

    private async void DeleteTheme_Click(object sender, RoutedEventArgs e)
    {
        var cat = _editingTheme == null ? null : Settings.Find(_editingTheme);
        if (cat == null) return;
        if (MessageBox.Show($"Supprimer le thème « {cat.Name} » ?\n\nSes applis seront triées dans les autres thèmes.", "Centre PC",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Settings.Current.Categories.Remove(cat);
        foreach (var k in Settings.Current.Overrides.Where(kv => kv.Value == cat.Name).Select(kv => kv.Key).ToList())
            Settings.Current.Overrides.Remove(k);
        Settings.Rebuild();
        Settings.Save();
        ThemeEditor.Visibility = Visibility.Collapsed;
        await AfterThemesChangedAsync($"Thème « {cat.Name} » supprimé");
    }

    private async void ResetThemes_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Revenir aux thèmes et mots-clés par défaut ?\n\nTes thèmes personnalisés seront supprimés. Tes autres choix sont conservés.",
                "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Settings.ResetThemes();
        await AfterThemesChangedAsync("Thèmes par défaut rétablis");
    }

    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "CentrePC.config.json", Filter = "Configuration Centre PC (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return;
        try { Settings.Export(dlg.FileName); ShowToast("Configuration exportée"); }
        catch (Exception ex) { ShowToast("Export impossible : " + ex.Message); }
    }

    private async void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Configuration Centre PC (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show("Remplacer tes thèmes, choix et préférences par ceux de ce fichier ?", "Centre PC",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Settings.Import(dlg.FileName);
            _modeInit = true;
            (DesktopManager.FoldersMode ? ModeFoldersRadio : ModeAppRadio).IsChecked = true;
            _modeInit = false;
            TidyFilesCheck.IsChecked = Settings.Current.TidyFiles;
            StyleFoldersCheck.IsChecked = Settings.Current.StyleFolders;
            await AfterThemesChangedAsync("Configuration importée");
        }
        catch (Exception ex) { ShowToast("Import impossible : " + ex.Message); }
    }

    /// <summary>Après un changement de thèmes : tout est retrié, et les raccourcis rangés suivent en mode « dossiers ».</summary>
    private async Task AfterThemesChangedAsync(string message)
    {
        foreach (var x in _apps.Concat(_desk).Where(x => x.IsShortcut))
            x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
        if (DesktopManager.FoldersMode)
        {
            DesktopManager.Refile(_desk.Where(x => x.Location == ItemLocation.Stored).ToList());
            await AfterTidyAsync();
        }
        else RefreshAll();
        RefreshSettingsPage();
        ShowToast(message);
    }

    // ---------- Connexion et mode dégradé
    private bool _online = true;
    private bool _installRunning;

    private void ApplyOnlineState()
    {
        OnlineDot.Fill = _online ? Brushes.MediumSeaGreen : Brushes.Goldenrod;
        FooterText.Text = $"Centre PC {AppUpdater.Current}" + (_online ? "" : " · hors connexion");
        OfflineBanner.Visibility = _online ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.IsEnabled = _online;
        if (!_installRunning) InstallButton.IsEnabled = _online;
    }

    // ---------- Mise à jour de Centre PC
    private ReleaseInfo _latest;

    private async Task CheckAppUpdateAsync(bool fromUser)
    {
        AppUpdateStatus.Text = "Recherche d'une nouvelle version…";
        _online = await Network.IsOnlineAsync(force: fromUser);
        ApplyOnlineState();
        if (!_online)
        {
            AppUpdateStatus.Text = "Hors connexion : la vérification se fera quand internet reviendra.";
            if (fromUser) ShowToast("Pas de connexion internet.");
            return;
        }
        var r = await AppUpdater.GetLatestAsync();
        if (AppUpdater.IsNewer(r))
        {
            _latest = r;
            AppUpdateStatus.Text = $"La version {r.Version} est disponible.";
            AppUpdateButtonText.Text = $"Installer la version {r.Version}";
            AppUpdateBadge.Visibility = Visibility.Visible;
            AppUpdateBadgeText.Text = $"Version {r.Version} disponible";
            // Proposée au démarrage, sauf si elle a déjà été refusée (elle reste installable dans Réglages)
            if (fromUser || Settings.Current.SkippedAppVersion != r.Version.ToString()) ShowAppUpdateOverlay();
        }
        else
        {
            AppUpdateStatus.Text = $"Centre PC est à jour (vérifié à {DateTime.Now:HH:mm}).";
            AppUpdateButtonText.Text = "Rechercher une mise à jour";
            AppUpdateBadge.Visibility = Visibility.Collapsed;
            if (fromUser) ShowToast("Centre PC est à jour");
        }
    }

    private void ShowAppUpdateOverlay()
    {
        if (_latest == null) return;
        AppUpdateSubtitle.Text = $"Version {_latest.Version} · tu as la version {AppUpdater.Current}";
        var notes = (_latest.Notes ?? "").Trim();
        AppUpdateNotes.Text = notes.Length > 600 ? notes[..600] + "…" : notes;
        AppUpdateNotes.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateProgress.Visibility = Visibility.Collapsed;
        AppUpdateNowButton.IsEnabled = AppUpdateLaterButton.IsEnabled = true;
        AppUpdateOverlay.Visibility = Visibility.Visible;
    }

    private async void AppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (AppUpdater.IsNewer(_latest)) ShowAppUpdateOverlay();
        else await CheckAppUpdateAsync(fromUser: true);
    }

    private void AppUpdateLater_Click(object sender, RoutedEventArgs e)
    {
        if (_latest != null) { Settings.Current.SkippedAppVersion = _latest.Version.ToString(); Settings.Save(); }
        AppUpdateOverlay.Visibility = Visibility.Collapsed;
        ShowToast("D'accord. Tu pourras mettre à jour depuis Réglages.");
    }

    private async void AppUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_latest == null) return;
        AppUpdateNowButton.IsEnabled = AppUpdateLaterButton.IsEnabled = false;
        AppUpdateProgress.Visibility = Visibility.Visible;
        AppUpdateProgress.Value = 0;
        AppUpdateSubtitle.Text = "Téléchargement en cours…";
        try
        {
            await AppUpdater.InstallAsync(_latest, new Progress<double>(v => AppUpdateProgress.Value = v));
            App.ReleaseInstanceLock();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppUpdateSubtitle.Text = "La mise à jour n'a pas pu se faire : " + ex.Message;
            AppUpdateNowButton.IsEnabled = AppUpdateLaterButton.IsEnabled = true;
            AppUpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoAppUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsInit) return;
        Settings.Current.AutoCheckAppUpdate = AutoAppUpdateCheck.IsChecked == true;
        Settings.Save();
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => Updates.Open(AppUpdater.RepoUrl);
    private void Footer_Click(object sender, MouseButtonEventArgs e) => NavSettings.IsChecked = true;

    private void OpenDataDir_Click(object sender, RoutedEventArgs e) => Updates.Open(Settings.DataDir);

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = LogBox.Visibility != Visibility.Visible;
        LogBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogToggle.Content = show ? "Masquer les détails" : "Afficher les détails";
    }

    // =====================================================================================
    //  Tuiles (communes aux pages)
    // =====================================================================================
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppItem a) Launch(a);
    }

    private void Launch(AppItem a)
    {
        try
        {
            if (a.FilePath != null)
                Process.Start(new ProcessStartInfo(a.FilePath) { UseShellExecute = true });
            else if (a.Aumid != null)
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + a.Aumid) { UseShellExecute = true });
            ShowToast($"Lancement de {a.Name}…");
        }
        catch (Exception ex) { ShowToast($"Impossible de lancer {a.Name} : {ex.Message}"); }
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppItem a) return;
        e.Handled = true;
        var menu = new ContextMenu();

        menu.Items.Add(MakeItem("Ouvrir", "", () => Launch(a)));

        bool fav = Settings.Current.Favorites.Contains(a.Name, StringComparer.OrdinalIgnoreCase);
        menu.Items.Add(MakeItem(fav ? "Retirer des favoris" : "Ajouter aux favoris", fav ? "" : "", () =>
        {
            if (fav) Settings.Current.Favorites.RemoveAll(n => n.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
            else Settings.Current.Favorites.Add(a.Name);
            Settings.Save(); RefreshFavorites();
        }));

        if (a.IsShortcut)
            menu.Items.Add(MakeItem("Masquer cette appli", "\uED1A", () =>
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
            menu.Items.Add(MakeItem(deskItem.Keep ? "Ne plus garder sur le bureau" : "Toujours garder sur le bureau", "", () =>
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

        // Changer de thème
        if (a.IsShortcut)
        {
            menu.Items.Add(new Separator());
            var themeMenu = MakeItem("Changer de thème", "", null);
            foreach (var cat in Settings.AllCategoryNames)
            {
                var c = cat;
                var it = MakeItem(c, a.Theme == c ? "" : null, () => ChangeTheme(a, c));
                themeMenu.Items.Add(it);
            }
            themeMenu.Items.Add(new Separator());
            themeMenu.Items.Add(MakeItem("Automatique", "", () => ChangeTheme(a, null)));
            menu.Items.Add(themeMenu);
        }

        if (a.FilePath != null)
            menu.Items.Add(MakeItem("Ouvrir l'emplacement", "", () =>
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{a.FilePath}\"") { UseShellExecute = true })));

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private static MenuItem MakeItem(string header, string glyph, Action action)
    {
        var mi = new MenuItem { Header = header };
        if (glyph != null)
            mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 14 };
        if (action != null) mi.Click += (_, _) => action();
        return mi;
    }

    private void ChangeTheme(AppItem a, string category)
    {
        Settings.SetOverride(a.Name, category);
        foreach (var x in _apps.Concat(_desk).Where(x => x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase) && x.IsShortcut))
            x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
        RefreshAll();
        ShowToast(category == null ? $"{a.Name} : thème automatique" : $"{a.Name} → {category}");
        // En mode « dossiers », le raccourci rangé suit son nouveau thème
        if (DesktopManager.FoldersMode)
        {
            var moved = _desk.Where(x => x.Location == ItemLocation.Stored && x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (moved.Count > 0) { DesktopManager.Refile(moved); _ = AfterTidyAsync(); }
        }
    }

    // =====================================================================================
    //  Page Bureau
    // =====================================================================================
    private async void Tidy_Click(object sender, RoutedEventArgs e)
    {
        var items = _desk.Where(d => d.OnDesktop && !d.Keep && (d.IsShortcut || Settings.Current.TidyFiles)).ToList();
        var stored = _desk.Where(d => d.Location == ItemLocation.Stored).ToList();
        int n = await Task.Run(() => DesktopManager.Store(items) + DesktopManager.Refile(stored));
        await AfterTidyAsync();
        ShowToast(n == 0 ? "Le bureau est déjà rangé." : $"{n} élément(s) rangé(s)");
    }

    /// <summary>Après un rangement : supprime les dossiers de thème vides, stylise les nouveaux, recharge.</summary>
    private async Task AfterTidyAsync()
    {
        DesktopManager.RemoveEmptyThemeFolders();
        if (DesktopManager.FoldersMode && IconStyler.IsStyled && Settings.Current.StyleFolders)
        {
            var newFolders = DesktopManager.ThemeFolders()
                .Where(d => !IconStyler.Backups.Any(b => b.Path.Equals(d, StringComparison.OrdinalIgnoreCase)))
                .Select(d => new StyleTarget { Path = d, Name = System.IO.Path.GetFileName(d), IsFolder = true }).ToList();
            if (newFolders.Count > 0) await IconStyler.ApplyAsync(newFolders);
        }
        IconLoader.ClearCache();
        await ReloadAsync();
        UpdateStyleStatus();
    }

    private bool _modeInit;
    private async void TidyMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_modeInit) return;
        var mode = (string)((FrameworkElement)sender).Tag;
        if (mode == Settings.Current.TidyMode) return;
        Settings.Current.TidyMode = mode;
        Settings.Save();
        var stored = _desk.Where(d => d.Location == ItemLocation.Stored).ToList();
        if (stored.Count > 0 && MessageBox.Show(
                mode == DesktopManager.ModeFolders
                    ? $"Placer les {stored.Count} élément(s) déjà rangés dans des dossiers de thème sur le bureau ?"
                    : $"Ramener les {stored.Count} élément(s) des dossiers de thème dans Centre PC et retirer ces dossiers du bureau ?",
                "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            int n = await Task.Run(() => DesktopManager.Refile(stored));
            await AfterTidyAsync();
            ShowToast($"{n} élément(s) déplacé(s)");
        }
        else RefreshAll();
    }

    private async Task StoreAsync(IEnumerable<AppItem> items)
    {
        var list = items.ToList();
        int n = await Task.Run(() => DesktopManager.Store(list));
        await AfterTidyAsync();
        ShowToast(n == 0 ? "Aucun élément n'a pu être rangé." : $"{n} élément(s) rangé(s) dans Centre PC");
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var items = _desk.Where(d => d.Location == ItemLocation.Stored).ToList();
        if (items.Count == 0) { ShowToast("Aucun élément rangé à remettre."); return; }
        await RestoreAsync(items);
    }

    private async Task RestoreAsync(IEnumerable<AppItem> items)
    {
        var list = items.ToList();
        int n = await Task.Run(() => DesktopManager.Restore(list));
        await AfterTidyAsync();
        ShowToast($"{n} élément(s) remis sur le bureau");
    }

    private async void Legacy_Click(object sender, RoutedEventArgs e)
    {
        int n = DesktopManager.RemoveEmptyThemeFolders();
        await ReloadAsync();
        ShowToast($"{n} dossier(s) vide(s) supprimé(s)");
    }

    private void OpenStore_Click(object sender, RoutedEventArgs e) => Updates.Open(Settings.StoreDir);

    private void TidyFiles_Changed(object sender, RoutedEventArgs e)
    {
        Settings.Current.TidyFiles = TidyFilesCheck.IsChecked == true;
        Settings.Save();
    }

    // ---------- Apparence des icônes ----------
    private List<StyleTarget> StyleTargets()
    {
        var list = _desk.Where(d => d.OnDesktop && d.IsShortcut && d.FilePath != null &&
                                    (d.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || d.FilePath.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
            .Where(d => !IconStyler.IsOwnShortcut(d.FilePath))
            .Select(d => new StyleTarget { Path = d.FilePath, Name = d.Name, Theme = d.Theme })
            .OrderBy(t => Settings.Order(t.Theme)).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (Settings.Current.StyleFolders)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(AppScanner.UserDesktop))
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.Hidden) != 0) continue;
                    list.Add(new StyleTarget { Path = dir, Name = di.Name, IsFolder = true });
                }
            }
            catch { }
        }
        return list;
    }

    private void UpdateStyleStatus()
    {
        int n = IconStyler.Backups.Count;
        StyleStatus.Text = n > 0 ? $"Style appliqué à {n} icône(s). Les icônes d'origine sont sauvegardées." : "Icônes d'origine de Windows.";
        RevertStyleButton.IsEnabled = n > 0;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var targets = StyleTargets();
        if (targets.Count == 0) { ShowToast("Aucune icône sur le bureau à styliser."); return; }
        PreviewButton.IsEnabled = false;
        try
        {
            PreviewList.ItemsSource = await IconStyler.PreviewAsync(targets);
            PreviewPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { ShowToast("Aperçu impossible : " + ex.Message); }
        finally { PreviewButton.IsEnabled = true; }
    }

    private async void ApplyStyle_Click(object sender, RoutedEventArgs e)
    {
        var targets = StyleTargets();
        if (targets.Count == 0) { ShowToast("Aucune icône sur le bureau à styliser."); return; }
        ApplyStyleButton.IsEnabled = false;
        try
        {
            // Rend son icône d'origine au raccourci de Centre PC s'il avait été stylisé
            await IconStyler.RevertAsync(b => IconStyler.IsOwnShortcut(b.Path));
            int n = await IconStyler.ApplyAsync(targets);
            IconLoader.ClearCache();
            await ReloadAsync();
            UpdateStyleStatus();
            ShowToast($"Nouveau style appliqué à {n} icône(s)");
        }
        catch (Exception ex) { ShowToast("Erreur : " + ex.Message); }
        finally { ApplyStyleButton.IsEnabled = true; }
    }

    private async void RevertStyle_Click(object sender, RoutedEventArgs e)
    {
        RevertStyleButton.IsEnabled = false;
        try
        {
            int n = await IconStyler.RevertAsync();
            IconLoader.ClearCache();
            await ReloadAsync();
            PreviewPanel.Visibility = Visibility.Collapsed;
            ShowToast($"{n} icône(s) remises à l'origine");
        }
        catch (Exception ex) { ShowToast("Erreur : " + ex.Message); }
        finally { UpdateStyleStatus(); }
    }

    private void StyleFolders_Changed(object sender, RoutedEventArgs e)
    {
        Settings.Current.StyleFolders = StyleFoldersCheck.IsChecked == true;
        Settings.Save();
    }

    private async void Arrows_Click(object sender, RoutedEventArgs e)
    {
        bool hide = ArrowsCheck.IsChecked == true;
        var msg = (hide ? "Masquer les flèches de raccourci ?" : "Rétablir les flèches de raccourci ?") +
                  "\n\nWindows va te demander une autorisation, puis l'Explorateur redémarrera (la barre des tâches disparaît quelques secondes).";
        if (MessageBox.Show(msg, "Centre PC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            ArrowsCheck.IsChecked = !hide;
            return;
        }
        ArrowsCheck.IsEnabled = false;
        bool ok = await Task.Run(() => IconStyler.SetArrows(hide));
        ArrowsCheck.IsEnabled = true;
        ArrowsCheck.IsChecked = IconStyler.ArrowsHidden();
        ShowToast(ok ? (hide ? "Flèches masquées" : "Flèches rétablies") : "Modification annulée");
    }

    // =====================================================================================
    //  Page Mises à jour
    // =====================================================================================
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _online = await Network.IsOnlineAsync();
        ApplyOnlineState();
        if (!_online) { if (sender != this) ShowToast("Pas de connexion internet : recherche impossible pour le moment."); return; }
        ScanButton.IsEnabled = false;
        ScanButtonText.Text = "Recherche en cours…";
        SumApps.Text = SumWu.Text = "…";
        SumAppsSub.Text = SumWuSub.Text = "recherche…";
        WingetEmpty.Text = "Recherche des applis à mettre à jour…";
        WuEmpty.Text = "Interrogation de Windows Update (peut prendre une minute)…";
        WingetEmpty.Visibility = WuEmpty.Visibility = Visibility.Visible;
        _winget.Clear(); _wu.Clear();

        // Le matériel d'abord : il indique quel pilote graphique est géré par le logiciel du fabricant
        try { _hw = await Updates.ScanHardwareAsync(); ShowHardware(); } catch { }
        var tWinget = Updates.ScanWingetAsync();
        var tWu = Updates.ScanWindowsUpdateAsync(_hw);

        try
        {
            foreach (var u in await tWinget) _winget.Add(u);
            _wingetScanned = true;
            SumApps.Text = _winget.Count.ToString();
            SumAppsSub.Text = _winget.Count == 0 ? "tout est à jour" : "mise(s) à jour disponible(s)";
            WingetEmpty.Text = "Toutes tes applis sont à jour.";
        }
        catch (Exception ex) { SumApps.Text = "!"; SumAppsSub.Text = "winget indisponible"; WingetEmpty.Text = ex.Message; }
        WingetEmpty.Visibility = _winget.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllAppsCheck.IsChecked = true;

        try
        {
            foreach (var u in (await tWu).OrderBy(u => u.Ignored).ThenBy(u => u.IsDriver)) _wu.Add(u);
            int real = _wu.Count(u => !u.Ignored);
            SumWu.Text = real.ToString();
            SumWuSub.Text = real == 0 ? "Windows est à jour" : $"dont {_wu.Count(u => u.IsDriver && !u.Ignored)} pilote(s)";
            WuEmpty.Text = "Windows est à jour.";
        }
        catch (Exception ex) { SumWu.Text = "!"; SumWuSub.Text = "erreur"; WuEmpty.Text = "Windows Update : " + ex.Message; }
        WuEmpty.Visibility = _wu.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Settings.Current.LastUpdateCheck = DateTime.Now;
        Settings.Save();
        UpdateLastCheck();
        UpdateBadge();
        ScanButton.IsEnabled = true;
        ScanButtonText.Text = "Rechercher à nouveau";
    }

    private void UpdateBadge()
    {
        int n = _winget.Count + _wu.Count(u => !u.Ignored);
        UpdatesBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesBadgeText.Text = n.ToString();
    }

    private void UpdateLastCheck()
    {
        var d = Settings.Current.LastUpdateCheck;
        LastCheckText.Text = d == null ? "Clique sur « Rechercher » pour vérifier ton PC." : $"Dernière vérification : {d:dddd d MMMM à HH:mm}";
    }

    private void AllApps_Changed(object sender, RoutedEventArgs e)
    {
        foreach (var u in _winget) u.Selected = AllAppsCheck.IsChecked == true;
    }

    private void ShowHardware()
    {
        if (_hw == null) return;
        GpuInfo.Children.Clear(); BoardInfo.Children.Clear(); PeriphInfo.Children.Clear();

        // ---- Carte graphique
        int? gpuAge = _hw.GpuDriverDate is DateTime gd ? (int)(DateTime.Now - gd).TotalDays : null;
        AddInfo(GpuInfo, _hw.GpuName ?? "Non détectée", true);
        if (_hw.GpuDriver != null)
            AddInfo(GpuInfo, $"Pilote {_hw.GpuDriver}" + (_hw.GpuDriverDate is DateTime d1 ? $" du {d1:dd/MM/yyyy}" : ""));
        if (_hw.GpuToolName != null)
            AddInfo(GpuInfo, _hw.GpuToolVersion != null ? $"{_hw.GpuToolName} {_hw.GpuToolVersion}" : $"{_hw.GpuToolName} : non installé");
        foreach (var g in _hw.OtherGpus) AddInfo(GpuInfo, "Aussi : " + g);
        GpuToolButton.Content = _hw.GpuToolName == null ? "Pilotes graphiques"
            : _hw.GpuToolPath != null ? $"Ouvrir {_hw.GpuToolName}" : $"Télécharger {_hw.GpuToolName}";
        GpuToolButton.Visibility = _hw.GpuToolName == null && _hw.GpuName == null ? Visibility.Collapsed : Visibility.Visible;
        SumGpu.Text = gpuAge == null ? "?" : gpuAge <= 90 ? "À jour" : "À vérifier";
        SumGpuSub.Text = gpuAge == null ? (_hw.GpuName ?? "") : $"{_hw.GpuVendor} · pilote de {gpuAge} jours";

        // ---- PC / carte mère / BIOS / chipset
        int? biosAge = _hw.BiosDate is DateTime bd ? (int)(DateTime.Now - bd).TotalDays : null;
        BoardTitle.Text = _hw.IsLaptop ? "PC PORTABLE ET BIOS" : "CARTE MÈRE ET BIOS";
        bool branded = _hw.IsLaptop || (_hw.SystemMaker != null && !Regex.IsMatch(_hw.SystemMaker, "System manufacturer|To be filled|Default string|O.E.M", RegexOptions.IgnoreCase)
                                        && !string.Equals(_hw.SystemMaker, _hw.BoardMaker, StringComparison.OrdinalIgnoreCase));
        AddInfo(BoardInfo, (branded ? $"{_hw.SystemMaker} {_hw.SystemModel}" : $"{_hw.BoardMaker} {_hw.BoardModel}").Trim(), true);
        if (branded && !string.IsNullOrEmpty(_hw.BoardModel)) AddInfo(BoardInfo, $"Carte mère {_hw.BoardMaker} {_hw.BoardModel}");
        AddInfo(BoardInfo, $"BIOS {_hw.BiosVersion}" + (_hw.BiosDate is DateTime d2 ? $" du {d2:dd/MM/yyyy}" : ""));
        if (_hw.CpuName != null) AddInfo(BoardInfo, _hw.CpuName);
        if (_hw.ChipsetName != null)
            AddInfo(BoardInfo, _hw.ChipsetVersion != null ? $"{_hw.ChipsetName} {_hw.ChipsetVersion}" : $"{_hw.ChipsetName} : non détecté");
        BiosButton.Content = _hw.SupportName != null ? $"Support {_hw.SupportName} (BIOS, pilotes)" : "Chercher une mise à jour du BIOS";
        SumBios.Text = biosAge == null ? "?" : biosAge <= 365 ? "Récent" : "À vérifier";
        SumBiosSub.Text = biosAge == null ? "" : $"{_hw.BiosVersion} · {biosAge / 30} mois";

        // ---- Périphériques et outils du constructeur
        foreach (var p in _hw.MakerTools) AddInfo(PeriphInfo, "• " + p + " (outil constructeur)");
        foreach (var p in _hw.PeripheralApps) AddInfo(PeriphInfo, "• " + p);
        if (_hw.PeripheralApps.Count == 0 && _hw.MakerTools.Count == 0)
            AddInfo(PeriphInfo, "Aucun logiciel de périphérique détecté : les pilotes passent par Windows Update.");
    }

    private static void AddInfo(Panel panel, string text, bool strong = false)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            FontSize = strong ? 14.5 : 13, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.FindResource(strong ? "Text" : "Text2")
        });
    }

    private void OpenGpuTool_Click(object sender, RoutedEventArgs e)
    {
        if (_hw?.GpuToolPath != null) Updates.Open(_hw.GpuToolPath);
        else if (_hw?.GpuToolUrl != null) Updates.Open(_hw.GpuToolUrl);
        else Updates.Open("ms-settings:windowsupdate");
    }

    private void Bios_Click(object sender, RoutedEventArgs e)
    {
        if (_hw?.SupportUrl != null) { Updates.Open(_hw.SupportUrl); return; }
        var model = _hw?.IsLaptop == true ? $"{_hw.SystemMaker} {_hw.SystemModel}" : $"{_hw?.BoardMaker} {_hw?.BoardModel}";
        Updates.Open("https://www.google.com/search?q=" + Uri.EscapeDataString($"{model} BIOS support".Trim()));
    }

    // ---------- Installation ----------
    private DispatcherTimer _tail;
    private long _logPos;
    private Process _installProc;
    private string _logPath;
    private readonly StringBuilder _partial = new();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _online = await Network.IsOnlineAsync();
        ApplyOnlineState();
        if (!_online) { ShowToast("Pas de connexion internet : installation impossible pour le moment."); return; }
        var opt = new Updates.InstallOptions
        {
            GpuFilter = _hw?.GpuDriverFilter ?? "",
            RestorePoint = OptRestore.IsChecked == true,
            Apps = OptApps.IsChecked == true,
            WindowsUpdate = OptWu.IsChecked == true,
            Store = OptStore.IsChecked == true,
            Defender = OptDefender.IsChecked == true,
        };
        if (opt.Apps)
        {
            if (_wingetScanned) opt.AppIds = _winget.Where(u => u.Selected).Select(u => u.Id).ToList();
            else opt.AllApps = true;   // pas encore de recherche : on met tout à jour
        }

        _steps.Clear();
        if (opt.RestorePoint) _steps.Add(new StepItem { Key = "restore", Title = "Point de restauration" });
        if (opt.Apps) _steps.Add(new StepItem { Key = "apps", Title = "Applications" });
        if (opt.WindowsUpdate) _steps.Add(new StepItem { Key = "wu", Title = "Windows Update" });
        if (opt.Store) _steps.Add(new StepItem { Key = "store", Title = "Microsoft Store" });
        if (opt.Defender) _steps.Add(new StepItem { Key = "defender", Title = "Antivirus Defender" });
        if (_steps.Count == 0) { ShowToast("Coche au moins une option."); return; }

        _logPath = Updates.StartInstall(opt, out _installProc);
        if (_logPath == null) { ShowToast("Installation annulée (autorisation administrateur refusée)."); return; }

        LogBox.Clear(); _logPos = 0; _partial.Clear();
        InstallPanel.Visibility = Visibility.Visible;
        RebootBanner.Visibility = Visibility.Collapsed;
        InstallProgress.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = false;
        _installRunning = true;
        _tail = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _tail.Tick += (_, _) => TailLog();
        _tail.Start();
    }

    private void TailLog()
    {
        try
        {
            if (File.Exists(_logPath))
            {
                using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > _logPos)
                {
                    fs.Seek(_logPos, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - _logPos];
                    int read = fs.Read(buf, 0, buf.Length);
                    int lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
                    if (lastNl >= 0)
                    {
                        _logPos += lastNl + 1;
                        var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1).Replace("﻿", "");
                        foreach (var line in text.Split('\n')) HandleLogLine(line.TrimEnd('\r'));
                    }
                }
            }
            bool exited = false;
            try { exited = _installProc != null && _installProc.HasExited; } catch { }
            if (exited && _tail != null && _tail.IsEnabled)
            {
                // Le script s'est arrêté sans écrire de fin : on lit une dernière fois puis on clôt
                _installProc = null;
                Dispatcher.InvokeAsync(() => { if (_tail?.IsEnabled == true) FinishInstall(false, interrupted: true); }, DispatcherPriority.Background);
            }
        }
        catch { }
    }

    private void HandleLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.StartsWith("##ETAPE|"))
        {
            var p = line.Split('|');
            var step = _steps.FirstOrDefault(s => s.Key == p.ElementAtOrDefault(1));
            if (step != null)
            {
                step.Status = p.ElementAtOrDefault(2) ?? "";
                step.Detail = p.ElementAtOrDefault(3) ?? "";
            }
            return;
        }
        if (line.StartsWith("##FIN|"))
        {
            FinishInstall(line.EndsWith("1"));
            return;
        }
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private async void FinishInstall(bool reboot, bool interrupted = false)
    {
        _tail?.Stop();
        InstallProgress.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = true;
        _installRunning = false;
        foreach (var s in _steps.Where(s => s.Status is "En attente" or "En cours"))
            s.Status = interrupted ? "ERREUR" : "IGNORÉ";
        RebootBanner.Visibility = reboot ? Visibility.Visible : Visibility.Collapsed;
        ShowToast(interrupted ? "L'installation s'est arrêtée prématurément (voir le journal)." : "Mises à jour terminées");
        // Rafraîchit la liste des applis à jour
        if (_wingetScanned)
        {
            try
            {
                var fresh = await Updates.ScanWingetAsync();
                _winget.Clear(); foreach (var u in fresh) _winget.Add(u);
                SumApps.Text = _winget.Count.ToString();
                SumAppsSub.Text = _winget.Count == 0 ? "tout est à jour" : "mise(s) à jour disponible(s)";
                WingetEmpty.Text = "Toutes tes applis sont à jour.";
                WingetEmpty.Visibility = _winget.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateBadge();
            }
            catch { }
        }
    }

    private void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Redémarrer le PC maintenant ? Pense à enregistrer ton travail.", "Centre PC",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
    }

    // =====================================================================================
    //  Fenêtre, navigation, notifications
    // =====================================================================================
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageApps == null) return;
        var tag = (string)((FrameworkElement)sender).Tag;
        FrameworkElement page = tag switch { "desktop" => PageDesktop, "updates" => PageUpdates, "settings" => PageSettings, _ => PageApps };
        if (tag == "settings") RefreshSettingsPage();
        foreach (var p in new FrameworkElement[] { PageApps, PageDesktop, PageUpdates, PageSettings })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        if (tag == "apps") SearchBox.Focus();
    }

    private DispatcherTimer _toastTimer;
    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        };
        _toastTimer.Start();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxState()
    {
        bool max = WindowState == WindowState.Maximized;
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
    }
}
