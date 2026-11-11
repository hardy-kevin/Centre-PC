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
    private readonly ObservableCollection<DriverUpdate> _drivers = new();
    private readonly ObservableCollection<AppStep> _driverSteps = new();
    private readonly ObservableCollection<StepItem> _steps = new();
    private readonly ObservableCollection<AppStep> _appSteps = new();
    private readonly ObservableCollection<ReportAction> _report = new();
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
        DriverList.ItemsSource = _drivers;
        DriversProgressList.ItemsSource = _driverSteps;
        StepsList.ItemsSource = _steps;
        AppsProgressList.ItemsSource = _appSteps;
        ReportActionsList.ItemsSource = _report;
        TidyFilesCheck.IsChecked = Settings.Current.TidyFiles;
        _modeInit = true;
        (DesktopManager.FoldersMode ? ModeFoldersRadio : ModeAppRadio).IsChecked = true;
        _modeInit = false;
        StyleFoldersCheck.IsChecked = Settings.Current.StyleFolders;
        ArrowsCheck.IsChecked = IconStyler.ArrowsHidden();
        UpdateStyleStatus();
        UpdateLastCheck();
        // Fermeture de session : on quitte vraiment (pas de retour dans la zone de notification)
        Application.Current.SessionEnding += (_, _) => _quitting = true;
        InitModal();
        BuildTestTiles();
        InitDevicesLive();
        InitScreenshotMenu();
        // Test du clavier : les relâchements de touches aussi
        PreviewKeyUp += (_, e) => { if (DeviceTestOverlay.Visibility == Visibility.Visible) DeviceTestKey(e, false); };
        // Démarré en arrière-plan : la proposition d'accès rapide attend que la fenêtre soit affichée
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && _started) Dispatcher.InvokeAsync(MaybeOfferQuickAccess); };
        ApplyQuickAccess();
    }

    private bool _started;

    private async Task StartupAsync()
    {
        if (_started) return;   // déjà lancé en arrière-plan (démarrage avec Windows), la fenêtre s'affiche seulement maintenant
        _started = true;
        SearchBox.Focus();
        if (!Settings.Current.OnboardingDone) WelcomeOverlay.Visibility = Visibility.Visible;
        ApplyOnlineState();
        if (App.OpenUpdatesPage) ShowUpdatesPage();
        // Vérification quotidienne application fermée : tâche planifiée créée / retirée selon le réglage (copie installée seulement)
        if (SelfInstall.IsInstalledCopy) _ = Task.Run(() => BackgroundCheck.Apply(Settings.Current.DailyCheck));
        // La liste s'affiche d'abord ; l'entretien du bureau se fait ensuite, sans faire attendre
        var sw = Stopwatch.StartNew();
        await ReloadAsync();
        MaybeOfferQuickAccess();
        _ = ScanDevicesLaterAsync();   // pastille « Périphériques », sans faire attendre
        Log.Info($"Démarrage : liste affichée en {sw.ElapsedMilliseconds} ms ({_apps.Count} applications)");
        bool changed = false;
        try
        {
            // Mise à jour terminée pendant que Centre PC était fermé : remise en état du bureau
            var pending = ShortcutGuard.Pending();
            if (pending != null) changed |= await AfterUpdateShortcutsAsync(pending, reload: false) != null;
            // Icônes laissées en double sur le bureau par d'anciennes mises à jour (bureau de l'utilisateur, sans autorisation)
            else changed |= (await Task.Run(() => ShortcutGuard.CleanDuplicates())).Total > 0;
            // Applis désinstallées depuis la dernière fois : leur raccourci rangé disparaît de Centre PC
            var orphans = await Task.Run(() => ShortcutGuard.RemoveOrphans());
            changed |= orphans.Count > 0;
            if (orphans.Count > 0)
                ShowToast(orphans.Count == 1 ? $"{orphans[0]} a été désinstallé : retiré de Centre PC."
                                             : $"{orphans.Count} applications désinstallées retirées de Centre PC.");
        }
        catch (Exception ex) { Log.Error("Entretien du bureau au démarrage", ex); }
        if (changed) await ReloadAsync();
        // Carrés noirs sur les icônes du bureau (icône vide des flèches enregistrée au mauvais format avant la 1.2.2)
        if (IconStyler.NeedsArrowRepair())
        {
            ShowToast("Réparation des icônes du bureau : veuillez accepter la demande d'autorisation de Windows.");
            if (await Task.Run(() => IconStyler.RepairArrowOverlay())) ShowToast("Icônes du bureau réparées.");
        }
        if (await Task.Run(() => IconStyler.RepairIconFormats())) ShowToast("Affichage des icônes du bureau réparé.");
        RefreshSettingsPage();
        try { _hw = await Updates.ScanHardwareAsync(); ShowHardware(); } catch { }

        // Tout ce qui suit a besoin d'internet : sans connexion, l'application reste pleinement utilisable (mode dégradé)
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
        if (Settings.Current.AutoCheckAppUpdate && !Native.IsPackaged) await CheckAppUpdateAsync(fromUser: false);
        // Tri en ligne des applications inconnues, sans action de l'utilisateur
        if (Settings.Current.AutoOnline) await RunOnlineAsync(silent: true);
        // Vérification des mises à jour en arrière-plan (au plus une fois toutes les 12 h) : une pastille prévient
        var last = Settings.Current.LastUpdateCheck;
        if (last == null || DateTime.Now - last > TimeSpan.FromHours(12) || App.OpenUpdatesPage) Scan_Click(this, null);
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
            ObsoleteApps.Mark(all);
            // Éléments du bureau qui ne figurent pas dans la liste (doublons…) : même classement que les applications
            foreach (var d in desk.Where(d => d.IsShortcut && !all.Contains(d)))
                d.Theme = Settings.Classify(d.Name, d.Target, d.Context, d.LooksLikeGame);

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
        // Catégories du catalogue dont des applications relèvent désormais : créées automatiquement
        var created = Settings.ActivateCategories(_apps.Select(a => a.Theme).Concat(_desk.Select(d => d.Theme)));
        if (created.Count > 0)
        {
            RefreshSettingsPage();
            ShowToast(created.Count == 1 ? $"Nouvelle catégorie « {created[0]} » créée pour vos applications"
                                         : $"Nouvelles catégories créées : {string.Join(", ", created)}");
        }
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

        int tidyable = _desk.Count(d => d.OnDesktop && !d.Keep && (d.IsShortcut || Settings.Current.TidyFiles));   // les fichiers passent par « Ranger mes fichiers »
        DeskHint.Text = tidyable > 0
            ? $"{tidyable} icône(s) peuvent être rangées : cliquez sur « Ranger le bureau ». Rien n'est supprimé : chaque icône peut retrouver sa place à tout moment."
            : "Le bureau est rangé. Les icônes restent accessibles ici, classées par catégorie.";
        StoredLabel.Text = DesktopManager.FoldersMode ? "rangées dans les dossiers de catégorie" : "rangées dans Centre PC";
        RefreshHistory();
        UpdateDeskOptions();
        UpdateDocsBadge();
        LegacyBanner.Visibility = Visibility.Collapsed;
    }

    private void RefreshApps()
    {
        var visible = _apps.Where(a => !Settings.IsHidden(a.Name));
        // Les suites (Git, LibreOffice…) sont regroupées ; une recherche montre chaque application
        bool searching = SearchBox.Text.Trim().Length > 0;
        _appGroups = ThemeGroup.Build((searching ? visible : AppSuites.Group(visible)).Where(AppFilter));
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
        // Bandeau de guidage : recherche en ligne en cours, ou applications à trier à la main
        var toSort = SortCandidates(includeSkipped: false);
        if (_onlineRunning)
        {
            SortBanner.Visibility = Visibility.Visible;
            SortBannerTitle.Text = "Identification des applications inconnues…";
            SortBannerText.Text = "Centre PC recherche en ligne la catégorie de chaque application. Aucune action requise.";
            SortBannerButton.Visibility = Visibility.Collapsed;
        }
        else if (toSort.Count > 0 && !searching && _chip == null)
        {
            SortBanner.Visibility = Visibility.Visible;
            SortBannerTitle.Text = toSort.Count == 1 ? $"« {toSort[0].Name} » n'a pas pu être classée automatiquement"
                                                     : $"{toSort.Count} applications n'ont pas pu être classées automatiquement";
            SortBannerText.Text = "Rangez-les en un clic dans une catégorie existante, ou créez la catégorie qui leur convient.";
            SortBannerButton.Visibility = Visibility.Visible;
        }
        else SortBanner.Visibility = Visibility.Collapsed;
        OnlineButton.Visibility = Visibility.Collapsed;
        UpdateObsoleteBanner();
        AppsSubtitle.Text = _apps.Count == 0
            ? "Chargement des applications…"
            : $"{_apps.Count(a => !Settings.IsHidden(a.Name))} applications · saisissez un nom pour lancer";
        // Astuce affichée tant que l'utilisateur n'a créé aucun groupe
        AppsTip.Visibility = _apps.Count > 0 && Settings.Current.AppFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Présentation
    private void Welcome_Click(object sender, RoutedEventArgs e)
    {
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        Settings.Current.OnboardingDone = true;
        Settings.Save();
        SearchBox.Focus();
        MaybeOfferQuickAccess();
    }

    private void ShowWelcome_Click(object sender, RoutedEventArgs e) => WelcomeOverlay.Visibility = Visibility.Visible;

    // =====================================================================================
    //  Fenêtre, navigation, notifications
    // =====================================================================================
    /// <summary>Ouvre la page Mises à jour (clic sur la notification de la vérification quotidienne).</summary>
    public void ShowUpdatesPage()
    {
        NavUpdates.IsChecked = true;
        if (!_installRunning && (Settings.Current.LastUpdateCheck == null || DateTime.Now - Settings.Current.LastUpdateCheck > TimeSpan.FromHours(1)))
            Scan_Click(this, null);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageApps == null) return;
        var tag = (string)((FrameworkElement)sender).Tag;
        FrameworkElement page = tag switch { "desktop" => PageDesktop, "documents" => PageDocuments, "updates" => PageUpdates, "devices" => PageDevices, "settings" => PageSettings, _ => PageApps };
        // Périphériques : réanalyse si la dernière date de plus de 2 minutes (un câble a pu être branché entre-temps)
        if (tag == "devices" && !_devicesScanning && DateTime.Now - _devicesScannedAt > TimeSpan.FromMinutes(2)) _ = ScanDevicesAsync();
        if (tag == "devices") Dispatcher.InvokeAsync(() => { UpdateLiveInterval(); _ = CheckFingerprintsAsync(); }, System.Windows.Threading.DispatcherPriority.Background);
        else Dispatcher.InvokeAsync(UpdateLiveInterval, System.Windows.Threading.DispatcherPriority.Background);
        if (tag == "documents") RefreshDocuments();
        if (tag == "settings") RefreshSettingsPage();
        foreach (var p in new FrameworkElement[] { PageApps, PageDesktop, PageDocuments, PageUpdates, PageDevices, PageSettings })
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
