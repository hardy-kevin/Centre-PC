using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Dossiers d'applications créés par l'utilisateur : glisser une tuile sur une autre les regroupe
/// (comme sur un téléphone). Les regroupements automatiques peuvent aussi être défaits.
/// </summary>
public partial class MainWindow
{
    private Point _dragStart;
    private AppItem _dragItem;
    private DateTime _lastDrag;

    /// <summary>Une tuile de la page Applications (hors recherche) peut être regroupée.</summary>
    private bool CanGroup(AppItem a) =>
        a != null && a.IsShortcut && PageApps.Visibility == Visibility.Visible && SearchBox.Text.Trim().Length == 0
        && (a.IsSuite || _apps.Contains(a));

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = (sender as FrameworkElement)?.Tag as AppItem;
        _dragStart = e.GetPosition(this);
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2
            && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var item = _dragItem;
        _dragItem = null;
        if (!CanGroup(item)) return;
        _lastDrag = DateTime.Now;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AppItem), item), DragDropEffects.Move); }
        catch (Exception ex) { Log.Error("Glisser-déposer", ex); }
        _lastDrag = DateTime.Now;
    }

    private static AppItem Dragged(DragEventArgs e) => e.Data.GetDataPresent(typeof(AppItem)) ? e.Data.GetData(typeof(AppItem)) as AppItem : null;

    private bool CanDrop(object sender, DragEventArgs e, out AppItem source, out AppItem target)
    {
        source = Dragged(e);
        target = (sender as FrameworkElement)?.Tag as AppItem;
        return source != null && target != null && !ReferenceEquals(source, target) && CanGroup(target)
               && !(source.IsSuite && target.IsSuite && source.Name == target.Name);
    }

    private void Tile_DragEnter(object sender, DragEventArgs e)
    {
        if (CanDrop(sender, e, out _, out _) && sender is Button b)
            b.Background = new SolidColorBrush(Color.FromArgb(0x44, 0x7C, 0x5C, 0xFF));
    }

    private void Tile_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanDrop(sender, e, out _, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tile_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button b) b.ClearValue(Control.BackgroundProperty);
    }

    private async void Tile_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button b) b.ClearValue(Control.BackgroundProperty);
        if (!CanDrop(sender, e, out var source, out var target)) return;
        e.Handled = true;
        await GroupAsync(source, target);
    }

    /// <summary>Range « source » avec « target » : dans le dossier de target, ou dans un nouveau dossier à nommer.</summary>
    private async Task GroupAsync(AppItem source, AppItem target)
    {
        var folders = Settings.Current.AppFolders;
        var srcNames = MembersOf(source).Select(m => m.Name).ToList();
        string folder;
        List<string> names;
        if (target.IsUserGroup)
        {
            folder = target.Name;
            names = folders.TryGetValue(folder, out var existing) ? existing : new List<string>();
        }
        else
        {
            var labels = MembersOf(target).Concat(MembersOf(source)).Where(m => !m.IsCompanion).Select(m => m.Label).ToList();
            var prefix = AppSuites.CommonPrefix(labels);
            var suggestion = target.IsSuite ? target.Name : prefix.Length >= 2 ? prefix : target.Label;
            folder = await PromptAsync("Nouveau dossier d'applications",
                $"« {source.Label} » et « {target.Label} » seront réunies sous une seule tuile. Un clic sur la tuile affiche ses applications.",
                suggestion, n => CheckFolderName(n, null));
            if (string.IsNullOrWhiteSpace(folder)) return;
            folder = folder.Trim();
            names = MembersOf(target).Select(m => m.Name).ToList();
        }
        // Une application n'est que dans un dossier à la fois
        foreach (var (k, list) in folders.ToList())
            if (!k.Equals(folder, StringComparison.CurrentCultureIgnoreCase))
            {
                list.RemoveAll(n => srcNames.Contains(n, StringComparer.CurrentCultureIgnoreCase));
                if (list.Count == 0) folders.Remove(k);
            }
        names = names.Concat(srcNames).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        folders[folder] = names;
        Settings.Current.SuiteExcluded.RemoveAll(n => names.Contains(n, StringComparer.CurrentCultureIgnoreCase));
        Settings.Save();
        RefreshAll();
        ShowToast($"« {source.Label} » rangée dans « {folder} »");
    }

    private static string CheckFolderName(string name, string current)
    {
        name = (name ?? "").Trim();
        if (name.Length < 2) return "Saisissez un nom d'au moins 2 caractères.";
        if (name.Length > 40) return "Nom trop long (40 caractères au plus).";
        if (!name.Equals(current, StringComparison.CurrentCultureIgnoreCase) && Settings.Current.AppFolders.ContainsKey(name))
            return "Un dossier porte déjà ce nom.";
        return null;
    }

    /// <summary>Actions propres aux regroupements, ajoutées au menu d'une suite.</summary>
    private void AddGroupActions(ContextMenu menu, AppItem suite)
    {
        var folders = Settings.Current.AppFolders;
        var remove = MakeItem(suite.IsUserGroup ? "Retirer une application du dossier" : "Retirer une application du groupe", "", null);
        foreach (var m in suite.Members)
        {
            var app = m;
            remove.Items.Add(MakeItem(app.Label, null, () =>
            {
                if (suite.IsUserGroup && folders.TryGetValue(suite.Name, out var list))
                {
                    list.RemoveAll(n => n.Equals(app.Name, StringComparison.CurrentCultureIgnoreCase));
                    if (list.Count == 0) folders.Remove(suite.Name);
                }
                // Sortie d'un regroupement automatique : ne sera plus regroupée d'elle-même
                if (!Settings.Current.SuiteExcluded.Contains(app.Name, StringComparer.CurrentCultureIgnoreCase))
                    Settings.Current.SuiteExcluded.Add(app.Name);
                Settings.Save();
                RefreshAll();
                ShowToast($"« {app.Label} » a sa propre tuile");
            }));
        }
        menu.Items.Add(remove);
        if (suite.IsUserGroup)
        {
            menu.Items.Add(MakeItem("Renommer le dossier…", "", async () =>
            {
                var name = await PromptAsync("Renommer le dossier", "Nouveau nom du dossier d'applications.", suite.Name, n => CheckFolderName(n, suite.Name));
                if (string.IsNullOrWhiteSpace(name) || !folders.TryGetValue(suite.Name, out var list)) return;
                folders.Remove(suite.Name);
                folders[name.Trim()] = list;
                Settings.Save();
                RefreshAll();
            }));
            menu.Items.Add(MakeItem("Supprimer le dossier (les applications restent)", "", () =>
            {
                folders.Remove(suite.Name);
                Settings.Save();
                RefreshAll();
                ShowToast($"Dossier « {suite.Name} » supprimé : ses applications ont retrouvé leur place.");
            }));
        }
        else
            menu.Items.Add(MakeItem("Dissocier ce groupe", "", () =>
            {
                foreach (var m in suite.Members.Where(m => !Settings.Current.SuiteExcluded.Contains(m.Name, StringComparer.CurrentCultureIgnoreCase)))
                    Settings.Current.SuiteExcluded.Add(m.Name);
                Settings.Save();
                RefreshAll();
                ShowToast($"Les applications de « {suite.Name} » ont chacune leur tuile.");
            }));
    }

    // =====================================================================================
    //  Diagnostic du classement (Réglages)
    // =====================================================================================
    private void ExportDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Centre PC - diagnostic du classement {DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Tableau CSV (*.csv)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var tiles = AppSuites.Group(_apps);
            string Group(AppItem a) => tiles.FirstOrDefault(t => t.IsSuite && t.Members.Contains(a))?.Name ?? "";
            static string C(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Nom;Catégorie;Raison;Regroupée dans;Annexe;Obsolète;Emplacement;Dossier du menu Démarrer;Cible;Éditeur;Dossier d'installation;Site;Indices");
            foreach (var a in _apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                sb.AppendLine(string.Join(";", C(a.Name), C(a.Theme), C(Settings.Explain(a.Name, a.Target, a.Context, a.LooksLikeGame)), C(Group(a)),
                    C(a.IsCompanion ? "oui" : ""), C(a.IsObsolete ? "oui" : ""), C(a.Location.ToString()), C(a.StartFolder), C(a.Target),
                    C(a.Publisher), C(a.InstallRoot), C(a.Homepage), C(a.Context)));
            File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
            ShowToast("Diagnostic enregistré : " + System.IO.Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) { ShowToast("Enregistrement impossible : " + ex.Message); }
    }
}
