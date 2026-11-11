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

/// <summary>Page Réglages : options, thèmes et mots-clés, export / import, connexion, mise à jour de Centre PC.</summary>
public partial class MainWindow
{
    // ---------- Page Réglages
    private void RefreshSettingsPage()
    {
        _settingsInit = true;
        AutoOnlineCheck.IsChecked = Settings.Current.AutoOnline;
        _settingsInit = false;
        int unsorted = SortCandidates(includeSkipped: true).Count;
        OnlineSummary.Text = unsorted == 0
            ? "Toutes les applications ont une catégorie. « Vérifier le tri automatique » permet de corriger les classements erronés."
            : $"{unsorted} application(s) encore dans « Autres ».";
        // Boutons utiles seulement s'il reste des applications non classées
        SortUnsortedButton.Visibility = unsorted > 0 ? Visibility.Visible : Visibility.Collapsed;
        SortUnsortedText.Text = unsorted == 1 ? "Trier l'application non classée" : $"Trier les {unsorted} applications non classées";
        RetryOnlineButton.Visibility = unsorted > 0 ? Visibility.Visible : Visibility.Collapsed;

        var rows = new List<ChoiceRow>();
        foreach (var kv in Settings.Current.Overrides.OrderBy(k => k.Key, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new ChoiceRow { Name = kv.Key, Kind = "theme", Label = kv.Value, Brush = Settings.BrushOf(kv.Value) });
        foreach (var n in Settings.Current.HiddenApps.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new ChoiceRow { Name = n, Kind = "hidden", Label = "Masquée", Brush = Brushes.Gray });
        foreach (var n in Settings.Current.SortSkipped.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new ChoiceRow { Name = n, Kind = "skipped", Label = "Laissée dans Autres", Brush = Settings.BrushOf(Settings.Current.DefaultCategory) });
        ChoicesList.ItemsSource = rows;
        ChoicesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChoicesSummary.Text = rows.Count == 0 ? "Aucun choix à réinitialiser." : $"{rows.Count} choix enregistré(s). « Annuler » rétablit le classement automatique.";
        ResetChoicesButton.IsEnabled = rows.Count > 0;
        ThemesList.ItemsSource = Settings.Current.Categories.Select(c => new ThemeRow
        {
            Name = c.Name, Glyph = Settings.GlyphOf(c.Name), Brush = Settings.BrushOf(c.Name),
            Detail = $"{c.Keywords?.Count ?? 0} mots-clés · {_apps.Count(a => a.Theme == c.Name)} applications"
        }).ToList();
        AppVersionText.Text = $"Version {AppUpdater.Current}" + (Native.IsPackaged ? " · Microsoft Store" : "");
        if (Native.IsPackaged)
        {
            AppUpdateStatus.Text = "Les mises à jour sont installées automatiquement par le Microsoft Store.";
            AppUpdateButtonText.Text = "Ouvrir le Microsoft Store";
            AutoAppUpdateCheck.Visibility = Visibility.Collapsed;
        }
        _settingsInit = true;
        AutoAppUpdateCheck.IsChecked = Settings.Current.AutoCheckAppUpdate;
        _settingsInit = false;
        if (string.IsNullOrEmpty(AppUpdateStatus.Text))
            AppUpdateStatus.Text = _online ? "Cliquez sur « Rechercher une mise à jour » pour vérifier." : "Hors connexion : la vérification aura lieu au retour de la connexion.";
        RefreshExclusions();
        _settingsInit = true;
        DailyCheckBox.IsChecked = Settings.Current.DailyCheck;
        _settingsInit = false;
        bool canSchedule = !Native.IsPackaged && SelfInstall.IsInstalledCopy;
        DailyCheckBox.IsEnabled = TestNotifButton.IsEnabled = canSchedule;
        DailyCheckBox.ToolTip = canSchedule ? null : "Disponible dans la version installée de Centre PC";
        InitQuickAccessSettings();
        AboutText.Text = $"© 2026 Kevin Hardy · Licence PolyForm Noncommercial 1.0.0 · réglages enregistrés dans {Settings.DataDir}";
    }

    private bool _settingsInit;

    private async void DailyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsInit) return;
        Settings.Current.DailyCheck = DailyCheckBox.IsChecked == true;
        Settings.Save();
        await Task.Run(() => BackgroundCheck.Apply(Settings.Current.DailyCheck));
        ShowToast(Settings.Current.DailyCheck ? "Vérification quotidienne activée." : "Vérification quotidienne désactivée.");
    }

    private async void TestNotif_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            BackgroundCheck.RegisterNotifications();
            BackgroundCheck.Notify("Centre PC", "Les notifications sont opérationnelles. Un clic ici ouvre la page Mises à jour.");
        });
    }
    private async void AutoOnline_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsInit) return;
        Settings.Current.AutoOnline = AutoOnlineCheck.IsChecked == true;
        Settings.Save();
        if (Settings.Current.AutoOnline) await RunOnlineAsync(silent: true);
    }

    private async void RetryOnline_Click(object sender, RoutedEventArgs e)
    {
        // Seules les applications encore dans « Autres » et les recherches sans résultat sont relancées
        var unsorted = _apps.Where(a => a.Theme == Settings.Current.DefaultCategory).Select(a => a.Name).ToList();
        if (unsorted.Count == 0) { ShowToast("Toutes les applications ont une catégorie : aucune recherche en ligne nécessaire."); return; }
        OnlineClassifier.Forget(unsorted);
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
        if (MessageBox.Show("Réinitialiser tous les choix de classement ?\n\nLes catégories définies manuellement, les applications masquées et celles laissées dans « Autres » reviendront au classement automatique.",
                "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Settings.ResetUserChoices();
        foreach (var x in _apps.Concat(_desk).Where(x => x.IsShortcut))
            x.Theme = Settings.Classify(x.Name, x.Target, x.Context, x.LooksLikeGame);
        RefreshAll();
        RefreshSettingsPage();
        ShowToast("Choix réinitialisés : classement automatique rétabli");
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
        ThemeEditorTitle.Text = c == null ? "Nouvelle catégorie" : "Modifier la catégorie";
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
        if (name.Length == 0) { ShowToast("Indiquez un nom de catégorie."); return; }
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
        await AfterThemesChangedAsync($"Catégorie « {name} » enregistrée");
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
        if (MessageBox.Show($"Supprimer la catégorie « {cat.Name} » ?\n\nSes applications seront reclassées dans les autres catégories.", "Centre PC",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Settings.RemoveCategory(cat);
        ThemeEditor.Visibility = Visibility.Collapsed;
        await AfterThemesChangedAsync($"Catégorie « {cat.Name} » supprimée");
    }

    private async void ResetThemes_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Revenir aux catégories et mots-clés par défaut ?\n\nLes catégories personnalisées seront supprimées. Les autres choix sont conservés.",
                "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Settings.ResetThemes();
        await AfterThemesChangedAsync("Catégories par défaut rétablies");
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
        if (MessageBox.Show("Remplacer les catégories, choix et préférences actuels par ceux de ce fichier ?", "Centre PC",
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
        ReclassifyAll();
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
        if (Native.IsPackaged)
        {
            // Version Store : c'est le Microsoft Store qui installe les nouvelles versions
            Updates.Open("ms-windows-store://downloadsandupdates");
            return;
        }
        AppUpdateStatus.Text = "Recherche d'une nouvelle version…";
        _online = await Network.IsOnlineAsync(force: fromUser);
        ApplyOnlineState();
        if (!_online)
        {
            AppUpdateStatus.Text = "Hors connexion : la vérification aura lieu au retour de la connexion.";
            if (fromUser) ShowToast("Pas de connexion Internet.");
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
        AppUpdateSubtitle.Text = $"Version {_latest.Version} · version installée : {AppUpdater.Current}";
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
        ShowToast("Mise à jour reportée. Elle reste disponible dans Réglages.");
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
            _quitting = true;   // fermeture réelle (pas de retour dans la zone de notification)
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppUpdateSubtitle.Text = "La mise à jour a échoué : " + ex.Message;
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
}
