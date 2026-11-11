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

/// <summary>Page Bureau : rangement des raccourcis, remise sur le bureau, apparence des icônes.</summary>
public partial class MainWindow
{
    // =====================================================================================
    //  Page Bureau
    // =====================================================================================
    // ---------- Historique
    private int _historyMax = 8;

    private void RefreshHistory()
    {
        var items = History.Recent(_historyMax);
        HistoryList.ItemsSource = items;
        HistoryBox.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryMoreButton.Visibility = items.Count >= _historyMax && _historyMax < 200 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistoryMore_Click(object sender, RoutedEventArgs e)
    {
        _historyMax = 200;
        RefreshHistory();
    }

    private async void HistoryUndo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryEntry h) return;
        var err = await Task.Run(() => History.Undo(h));
        if (err != null) { ShowToast($"{h.Name} : {err}"); RefreshHistory(); return; }
        ShowToast($"{h.Name} : rétabli à son emplacement précédent.");
        await AfterTidyAsync();
        RefreshDocuments();
    }

    private async void Tidy_Click(object sender, RoutedEventArgs e)
    {
        // Icônes déjà rangées remises sur le bureau par un installateur : pas de second exemplaire « (2) »
        var dup = await Task.Run(() => ShortcutGuard.CleanDuplicates(includePublic: true));
        if (dup.Total > 0) { await ReloadAsync(); }
        // Raccourcis → rangement de Centre PC. Fichiers et dossiers → Documents, Images… (assistant ci-dessous)
        var items = _desk.Where(d => d.OnDesktop && !d.Keep && d.IsShortcut).ToList();
        var stored = _desk.Where(d => d.Location == ItemLocation.Stored).ToList();
        int n = dup.Total + await Task.Run(() => DesktopManager.Store(items) + DesktopManager.Refile(stored));
        await AfterTidyAsync();
        var files = Settings.Current.TidyFiles ? DocumentLibrary.Pending() : new List<DocItem>();
        if (files.Count > 0)
        {
            ShowToast((n > 0 ? $"{n} icône(s) rangée(s). " : "") + $"Rangez à présent vos {files.Count} fichier(s) dans vos dossiers.");
            OpenDocSorter(files);
        }
        else ShowToast(n == 0 ? "Le bureau est déjà rangé." : $"{n} élément(s) rangé(s)");
    }

    /// <summary>Après un rangement : supprime les dossiers de catégorie vides, stylise les nouveaux, recharge.</summary>
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
                    ? $"Placer les {stored.Count} élément(s) déjà rangés dans des dossiers de catégorie sur le bureau ?"
                    : $"Ramener les {stored.Count} élément(s) des dossiers de catégorie dans Centre PC et retirer ces dossiers du bureau ?",
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
        var all = items.ToList();
        // Les fichiers ne vont plus dans le rangement caché : ils se rangent dans Documents, Images…
        var files = all.Where(i => !i.IsShortcut && i.FilePath != null)
            .Select(i => new DocItem { Path = i.FilePath, IsFolder = Directory.Exists(i.FilePath), Origin = i.Location == ItemLocation.Stored ? "ancien rangement de Centre PC (dossier caché)" : "sur le bureau" })
            .ToList();
        if (files.Count > 0) { OpenDocSorter(files); if (files.Count == all.Count) return; }
        var list = all.Where(i => i.IsShortcut).ToList();
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
        UpdateDeskOptions();
    }

    // ---------- Organisation du bureau (options repliées sous la carte principale)
    private void DeskOptionsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = DeskOptionsPanel.Visibility != Visibility.Visible;
        DeskOptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DeskOptionsToggle.Content = show ? "Réduire" : "Personnaliser";
    }

    /// <summary>
    /// Résumé d'une ligne et options adaptées au mode de rangement : l'apparence des icônes n'a d'intérêt que
    /// lorsque des icônes restent sur le bureau (dossiers de catégorie), ou pour revenir en arrière si elle a déjà été appliquée.
    /// </summary>
    private void UpdateDeskOptions()
    {
        if (DeskOptionsSummary == null) return;
        bool folders = DesktopManager.FoldersMode, styled = IconStyler.IsStyled;
        var parts = new List<string> { folders ? "Rangement dans des dossiers de catégorie" : "Rangement dans Centre PC" };
        if (IconStyler.ArrowsHidden()) parts.Add("flèches masquées");
        if (styled) parts.Add("icônes stylisées");
        DeskOptionsSummary.Text = string.Join(" · ", parts);

        StyleSection.Visibility = folders || styled ? Visibility.Visible : Visibility.Collapsed;
        StyleIntro.Text = folders
            ? "Chaque icône du bureau est placée sur une tuile aux couleurs de sa catégorie. Les icônes d'origine sont sauvegardées et peuvent être restaurées à tout moment."
            : "Avec le rangement dans Centre PC, le bureau reste vide : l'apparence ne concerne que les éléments conservés sur le bureau.";
        OpenStoreButton.Visibility = folders ? Visibility.Collapsed : Visibility.Visible;
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
                  "\n\nWindows demandera une autorisation, puis l'Explorateur redémarrera (la barre des tâches disparaît quelques secondes).";
        if (MessageBox.Show(msg, "Centre PC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            ArrowsCheck.IsChecked = !hide;
            return;
        }
        ArrowsCheck.IsEnabled = false;
        bool ok = await Task.Run(() => IconStyler.SetArrows(hide));
        ArrowsCheck.IsEnabled = true;
        ArrowsCheck.IsChecked = IconStyler.ArrowsHidden();
        UpdateDeskOptions();
        ShowToast(ok ? (hide ? "Flèches masquées" : "Flèches rétablies") : "Modification annulée");
    }
}
