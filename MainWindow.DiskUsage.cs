using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// « Espace disque » : ce qui prend de la place sur un lecteur, dossier par dossier (on descend d'un clic), et les plus gros
/// fichiers. Lecture seule : Centre PC ne supprime rien, il ouvre l'Explorateur au bon endroit.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource _usageCts;
    private string _usageRoot;

    private void BuildDiskUsageTool()
    {
        DeviceTestHost.Children.Add(TestText("Voyez ce qui occupe vos disques. Centre PC ne supprime rien : il vous montre l'endroit, "
            + "et vous faites le ménage dans l'Explorateur (la Corbeille permet de revenir en arrière)."));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<(string, object)> { ("Mes fichiers", profile) };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            roots.Add(($"{d.Name.TrimEnd('\\')}  ·  {DiskUsage.Size(d.TotalSize - d.TotalFreeSpace)} utilisés sur {DiskUsage.Size(d.TotalSize)}", d.Name));
        _usageRoot ??= profile;
        var status = TestText("", 13);
        var host = new StackPanel();
        DeviceTestHost.Children.Add(Choices(roots, _usageRoot, v => { _usageRoot = (string)v; _ = RunDiskUsageAsync(status, host); }));
        DeviceTestHost.Children.Add(status);
        DeviceTestHost.Children.Add(host);
        _testCleanup = () => { _usageCts?.Cancel(); _usageCts = null; };
        _ = RunDiskUsageAsync(status, host);
    }

    private async Task RunDiskUsageAsync(TextBlock status, StackPanel host)
    {
        _usageCts?.Cancel();
        var cts = _usageCts = new CancellationTokenSource();
        var root = _usageRoot;
        host.Children.Clear();
        status.Text = "Analyse en cours…";
        var progress = new Progress<string>(p => { if (_usageCts == cts) status.Text = "Analyse en cours… " + Shorten(p, 90); });
        UsageResult result;
        try { result = await Task.Run(() => DiskUsage.Scan(root, progress, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Error("Espace disque", ex); if (_usageCts == cts) status.Text = "L'analyse n'a pas pu se faire : " + ex.Message; return; }
        if (_usageCts != cts || _testKind != "space") return;
        status.Text = $"{DiskUsage.Size(result.Root.Size)} dans {result.Root.Files:N0} fichiers et {result.Folders:N0} dossiers."
            + (result.Root.Partial ? " Certains dossiers du système ne sont pas lisibles sans droits administrateur : ils ne sont pas comptés." : "");
        ShowUsageNode(host, result, result.Root);
    }

    private static string Shorten(string path, int max) => path.Length <= max ? path : path[..(max / 2 - 2)] + "…" + path[^(max / 2 - 2)..];

    private void ShowUsageNode(StackPanel host, UsageResult result, UsageNode node)
    {
        host.Children.Clear();

        // Fil d'Ariane : chaque niveau est cliquable pour remonter
        var crumbs = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        var chain = new List<UsageNode>();
        for (var n = node; n != null; n = n.Parent) chain.Insert(0, n);
        foreach (var n in chain)
        {
            var target = n;
            if (crumbs.Children.Count > 0) crumbs.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            if (n == node) crumbs.Children.Add(new TextBlock { Text = $"{n.Name}  ·  {DiskUsage.Size(n.Size)}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            else
            {
                var b = SmallButton(null, n.Name, (_, _) => ShowUsageNode(host, result, target));
                b.Margin = new Thickness(0);
                b.Padding = new Thickness(8, 3, 8, 3);
                crumbs.Children.Add(b);
            }
        }
        var open = SmallButton("", "Ouvrir dans l'Explorateur", (_, _) => DocumentLibrary.ShowInExplorer(node.Path));
        open.Margin = new Thickness(14, 0, 0, 0);
        crumbs.Children.Add(open);
        host.Children.Add(crumbs);

        // Sous-dossiers, du plus gros au plus petit ; les fichiers placés directement dans le dossier forment une ligne à part
        var rows = node.Children.Select(c => (name: c.Name, size: c.Size, child: c)).ToList();
        if (node.OwnSize > 0) rows.Add(("Fichiers de ce dossier", node.OwnSize, null));
        long max = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.size));
        int shown = 0;
        long rest = 0; int restCount = 0;
        foreach (var (name, size, child) in rows.OrderByDescending(r => r.size))
        {
            // Liste courte : au-delà de 14 lignes, ou sous 0,5 % du dossier, le reste est regroupé
            if (shown >= 14 || (shown >= 5 && size < node.Size / 200)) { rest += size; restCount++; continue; }
            shown++;
            host.Children.Add(UsageRow(name, size, node.Size, max, child?.Partial == true,
                child == null ? null : () => { if (child.Children.Count > 0 || child.OwnSize > 0) ShowUsageNode(host, result, child); else DocumentLibrary.ShowInExplorer(child.Path); },
                child == null ? null : child.Path));
        }
        if (restCount > 0) host.Children.Add(TestText($"… et {restCount} autre(s) élément(s) plus petits : {DiskUsage.Size(rest)}.", 12));
        if (rows.Count == 0) host.Children.Add(TestText("Ce dossier est vide.", 12.5));

        // Plus gros fichiers (de tout ce qui a été analysé, limités au dossier affiché)
        var prefix = node.Path.EndsWith('\\') ? node.Path : node.Path + "\\";
        var big = result.BiggestFiles.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        if (big.Count > 0)
        {
            host.Children.Add(Section("Plus gros fichiers"));
            long bmax = Math.Max(1, big[0].Size);
            foreach (var (path, size) in big)
            {
                var p = path;
                var row = UsageRow(System.IO.Path.GetFileName(p), size, node.Size, bmax, false, () => DocumentLibrary.ShowInExplorer(p), p);
                host.Children.Add(row);
            }
            host.Children.Add(TestText("Un clic affiche le fichier dans l'Explorateur. Ne supprimez que ce que vous reconnaissez : les fichiers de Windows et des programmes sont nécessaires.", 12));
        }
    }

    /// <summary>Ligne « nom — barre — taille — part » ; cliquable quand une action est fournie.</summary>
    private FrameworkElement UsageRow(string name, long size, long total, long max, bool partial, Action click, string tip)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.Children.Add(new TextBlock { Text = name + (partial ? "  (partiel)" : ""), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                                          FontWeight = click != null ? FontWeights.SemiBold : FontWeights.Normal });
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("Surface2"), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var barGrid = new Grid();
        double part = Math.Clamp(size / (double)max, 0.004, 1);
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(part, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - part, GridUnitType.Star) });
        barGrid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("Accent") });
        track.Child = barGrid;
        Grid.SetColumn(track, 1); grid.Children.Add(track);
        var sizeText = new TextBlock { Text = DiskUsage.Size(size), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sizeText, 2); grid.Children.Add(sizeText);
        var share = new TextBlock { Text = total > 0 ? $"{size * 100.0 / total:0} %" : "", Style = (Style)FindResource("Subtle"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(share, 3); grid.Children.Add(share);

        if (click == null) return new Border { Padding = new Thickness(10, 7, 10, 7), Child = grid, ToolTip = tip };
        var b = new Button { Content = grid, Template = RowTemplate, Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = tip };
        b.Click += (_, _) => click();
        var holder = new Border { Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8), Child = b, Background = Brushes.Transparent };
        holder.MouseEnter += (_, _) => holder.Background = (Brush)FindResource("Hover");
        holder.MouseLeave += (_, _) => holder.Background = Brushes.Transparent;
        return holder;
    }
}
