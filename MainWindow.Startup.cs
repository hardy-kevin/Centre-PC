using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// « Démarrage de Windows » : ce qui se lance tout seul à l'ouverture de session, avec un bouton pour désactiver ou
/// réactiver chaque programme (même mécanisme que le Gestionnaire des tâches : rien n'est supprimé).
/// </summary>
public partial class MainWindow
{
    private async void BuildStartupTool()
    {
        DeviceTestHost.Children.Add(TestText("Ces programmes se lancent tout seuls à l'allumage du PC. En désactiver rend le démarrage plus rapide ; "
            + "rien n'est désinstallé, et chacun se réactive d'un clic."));
        var summary = TestText("Recherche…", 13.5, subtle: false);
        DeviceTestHost.Children.Add(summary);
        var list = new StackPanel();
        DeviceTestHost.Children.Add(list);

        List<StartupEntry> entries;
        try { entries = await Task.Run(StartupPrograms.List); }
        catch (Exception ex) { Log.Error("Démarrage", ex); summary.Text = "La liste n'a pas pu être lue."; return; }
        if (_testKind != "startup") return;

        void Summary()
        {
            int on = entries.Count(e => e.Enabled), off = entries.Count - on;
            summary.Text = entries.Count == 0 ? "Aucun programme ne se lance au démarrage."
                : (on == 1 ? "1 programme se lance au démarrage" : $"{on} programmes se lancent au démarrage") + (off > 0 ? $" · {off} désactivé(s)" : "") + ".";
        }
        Summary();

        foreach (var entry in entries)
        {
            var e = entry;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = e.Command };
            var detail = new TextBlock { Style = (Style)FindResource("Subtle"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            texts.Children.Add(name);
            texts.Children.Add(detail);
            grid.Children.Add(texts);

            Button action = null;
            void Paint()
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(e.Publisher)) parts.Add(e.Publisher);
                if (e.Missing) parts.Add("programme introuvable sur le disque (reste d'une désinstallation)");
                if (e.Machine) parts.Add("pour tous les utilisateurs du PC");
                parts.Add(e.Enabled ? "se lance au démarrage" : "désactivé");
                detail.Text = string.Join(" · ", parts);
                texts.Opacity = e.Enabled ? 1 : 0.6;
                if (action != null && !e.Machine)
                    ((TextBlock)((StackPanel)action.Content).Children[1]).Text = e.Enabled ? "Désactiver" : "Réactiver";
            }
            if (e.Machine)
                action = SmallButton("\uE713", "Paramètres Windows", (_, _) => Updates.Open("ms-settings:startupapps"),
                                     tip: "Entrée commune à tous les utilisateurs : elle se règle dans les paramètres de Windows");
            else
                action = SmallButton("\uE7E8", "Désactiver", (_, _) =>
                {
                    var err = StartupPrograms.SetEnabled(e, !e.Enabled);
                    if (err != null) { ShowToast(err); return; }
                    Paint();
                    Summary();
                    ShowToast(e.Enabled ? $"« {e.Name} » se lancera de nouveau au démarrage." : $"« {e.Name} » ne se lancera plus au démarrage.", () =>
                    {
                        if (StartupPrograms.SetEnabled(e, !e.Enabled) is string undoErr) { ShowToast(undoErr); return; }
                        Paint();
                        Summary();
                    });
                });
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
            Paint();
            list.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid });
        }
        if (entries.Count > 0)
            DeviceTestHost.Children.Add(TestText("Programmes lancés à l'ouverture de session.\nMême liste que l'onglet « Démarrage » du Gestionnaire des tâches. Les services et les tâches planifiées n'y figurent pas.", 12));
    }
}
