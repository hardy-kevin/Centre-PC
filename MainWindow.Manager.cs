using System.Windows;
using System.Windows.Controls;
using CentrePC.Core;

namespace CentrePC;

/// <summary>Gestionnaire d'applications : applications obsolètes et désinstallation.</summary>
public partial class MainWindow
{
    private List<ManagedAppRow> _managerRows = new();
    private List<ArpEntry> _installed;
    private bool _managerObsolete;

    /// <summary>Applications obsolètes présentes (suites comprises), hors celles que l'utilisateur a choisi de garder.</summary>
    private List<AppItem> ObsoleteToReport() => _apps
        .Where(a => a.IsObsolete && !Settings.Current.ObsoleteKept.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
        .ToList();

    private void UpdateObsoleteBanner()
    {
        var list = ObsoleteToReport();
        bool searching = SearchBox.Text.Trim().Length > 0;
        if (list.Count == 0 || searching || _chip != null) { ObsoleteBanner.Visibility = Visibility.Collapsed; return; }
        ObsoleteBanner.Visibility = Visibility.Visible;
        ObsoleteBannerTitle.Text = list.Count == 1 ? $"« {list[0].Name} » est obsolète" : $"{list.Count} applications obsolètes";
        ObsoleteBannerText.Text = list.Count == 1
            ? list[0].Obsolete.Reason + " Vous pouvez la désinstaller."
            : "Elles ne sont plus mises à jour par leur éditeur : " + string.Join(", ", list.Take(4).Select(a => a.Name)) + (list.Count > 4 ? "…" : "") + ".";
    }

    private void ObsoleteLater_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in ObsoleteToReport()) Settings.Current.ObsoleteKept.Add(a.Name);
        Settings.Save();
        UpdateObsoleteBanner();
        ShowToast("Ces applications ne seront plus signalées. Elles restent visibles dans « Gérer ».");
    }

    // « Gérer les applications » : toutes les applications ; le bandeau « Voir » : seulement les applications obsolètes
    private void OpenManager_Click(object sender, RoutedEventArgs e) => OpenManager(null, obsolete: false);
    private void OpenObsolete_Click(object sender, RoutedEventArgs e) => OpenManager(null, obsolete: true);

    private async void OpenManager(string search, bool obsolete)
    {
        // Une ligne par application ou par suite (une suite se désinstalle en une fois)
        _managerRows = AppSuites.Group(_apps)
            .OrderBy(a => a.IsObsolete ? 0 : 1).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ManagedAppRow { App = a, Items = a.IsSuite ? a.Members : new List<AppItem> { a } })
            .ToList();
        int nObs = _managerRows.Count(r => r.App.IsObsolete);
        ManagerObsoleteText.Text = $"Obsolètes  {nObs}";
        ManagerAllText.Text = $"Toutes  {_managerRows.Count}";
        _managerObsolete = obsolete && nObs > 0;
        (_managerObsolete ? ManagerObsoleteChip : ManagerAllChip).IsChecked = true;
        ManagerSearch.Text = search ?? "";
        ManagerFooter.Text = "";
        FilterManager();
        ManagerOverlay.Visibility = Visibility.Visible;
        if (search != null) ManagerSearch.Focus();

        // Programmes installés (lecture du Registre, en arrière-plan)
        try
        {
            _installed = await Task.Run(() => AppUninstaller.ReadInstalled());
            var rows = _managerRows;
            var plans = await Task.Run(() => rows.Select(r =>
            {
                // Suite : le premier membre qui a un programme de désinstallation
                UninstallPlan first = null;
                foreach (var m in r.Items)
                {
                    var p = AppUninstaller.Plan(m, _installed);
                    first ??= p;
                    if (p.CanUninstall) return p;
                }
                return first ?? new UninstallPlan { Reason = "Programme de désinstallation introuvable" };
            }).ToList());
            for (int i = 0; i < rows.Count; i++) rows[i].Plan = plans[i];
            int ok = plans.Count(p => p.CanUninstall);
            ManagerFooter.Text = $"{ok} application(s) peuvent être désinstallées ici. Les outils intégrés à Windows ne peuvent pas l'être.";
        }
        catch (Exception ex)
        {
            Log.Error("Gestionnaire d'applications", ex);
            ManagerFooter.Text = "Liste des programmes installés indisponible : utilisez « Applications installées de Windows ».";
        }
    }

    private void FilterManager()
    {
        if (ManagerList == null) return;
        ManagerTitle.Text = _managerObsolete ? "Applications obsolètes" : "Gérer les applications";
        var q = ManagerSearch.Text.Trim();
        var rows = _managerRows.Where(r => !_managerObsolete || r.App.IsObsolete);
        if (q.Length > 0)
            rows = rows.Where(r => q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .All(w => r.Items.Any(m => Cmp.IndexOf(m.Name, w, CmpOpts) >= 0) || Cmp.IndexOf(r.Name, w, CmpOpts) >= 0));
        var list = rows.ToList();
        ManagerList.ItemsSource = list;
        ManagerEmpty.Text = _managerObsolete ? "Aucune application obsolète. Votre PC est à jour de ce côté." : "Aucune application ne correspond à votre recherche.";
        ManagerEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ManagerFilter_Changed(object sender, RoutedEventArgs e)
    {
        _managerObsolete = sender == ManagerObsoleteChip;
        FilterManager();
    }

    private void ManagerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // Une recherche porte sur toutes les applications
        if (ManagerSearch.Text.Trim().Length > 0 && _managerObsolete) ManagerAllChip.IsChecked = true;
        FilterManager();
    }

    private void CloseManager_Click(object sender, RoutedEventArgs e) => ManagerOverlay.Visibility = Visibility.Collapsed;

    private void ManagerWindowsSettings_Click(object sender, RoutedEventArgs e) => AppUninstaller.OpenWindowsSettings();

    private async void ManagerUninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManagedAppRow row || !row.CanAct) return;
        var plan = row.Plan;
        var what = row.App.IsSuite ? $"la suite « {row.Name} » ({row.Items.Count} applications)" : $"« {row.Name} »";
        var how = plan.Kind switch
        {
            UninstallKind.Store => "L'application sera retirée de votre compte Windows.",
            UninstallKind.WindowsFeature => "C'est une fonctionnalité facultative de Windows : Windows la retire lui-même (autorisation administrateur demandée). "
                                           + "Vous pourrez la réinstaller depuis Paramètres › Système › Fonctionnalités facultatives. Un redémarrage peut être nécessaire.",
            UninstallKind.Steam => "Steam va vous demander de confirmer.",
            _ => "Le programme de désinstallation de l'éditeur va s'ouvrir : suivez ses étapes."
        };
        var name = plan.Kind == UninstallKind.Program && plan.Entry.DisplayName != row.Name ? $"\n\nProgramme concerné : {plan.Entry.DisplayName}" : "";
        if (MessageBox.Show($"Désinstaller {what} ?{name}\n\n{how}", "Centre PC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        row.Busy = true;
        row.Status = plan.Kind switch
        {
            UninstallKind.Store => "Désinstallation…",
            UninstallKind.WindowsFeature => "Windows retire la fonctionnalité (cela peut prendre une minute)…",
            _ => "Suivez les étapes du programme de désinstallation. Centre PC attend la fin."
        };
        var error = await AppUninstaller.RunAsync(plan);
        row.Busy = false;
        if (error != null)
        {
            row.Status = error;
            return;
        }
        row.Done = true;
        row.Status = "Désinstallée.";
        ShowToast($"{row.Name} a été désinstallée.");
        foreach (var m in row.Items)
        {
            Settings.Current.Favorites.RemoveAll(n => n.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            Settings.Current.ObsoleteKept.RemoveAll(n => n.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
        }
        Settings.Save();
        // Raccourcis rangés devenus inutiles : mis de côté, puis liste à jour
        AppUninstaller.ForgetInstalled();
        await Task.Run(() => ShortcutGuard.RemoveOrphans());
        await ReloadAsync();
    }
}
