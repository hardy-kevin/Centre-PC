namespace CentrePC.Core;

/// <summary>
/// Catégorie déduite des applications voisines, pour celles qu'aucun indice direct ne permet de classer :
/// 1) même suite (« Cyberpunk 2077 - Bonus Content » rejoint « Cyberpunk 2077 »)
/// 2) même dossier d'installation (« C:\Program Files\BlueStacks_nxt »)
/// 3) même sous-dossier du menu Démarrer
/// 4) même éditeur (sauf les grands éditeurs aux produits très variés : Microsoft, Google…)
/// Les voisines doivent s'accorder (au moins deux tiers d'entre elles). Le choix de l'utilisateur
/// (thème défini, « Laisser dans Autres ») passe toujours avant.
/// </summary>
public static class Affinity
{
    private static readonly object Gate = new();
    private static Dictionary<string, (string theme, string reason)> _map = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BroadPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft Corporation", "Microsoft Corp.", "Google", "Google LLC", "Google Inc.", "Apple", "Apple Inc.",
        "Amazon", "Amazon.com", "Valve", "Valve Corporation", "Electronic Arts", "Ubisoft", "Epic Games", "Epic Games, Inc.",
        "NVIDIA Corporation", "Advanced Micro Devices, Inc.", "Intel Corporation", "Intel(R) Corporation", "Sony Corporation"
    };

    public static string Lookup(string name)
    {
        lock (Gate) return name != null && _map.TryGetValue(name, out var v) ? v.theme : null;
    }

    public static string Reason(string name)
    {
        lock (Gate) return name != null && _map.TryGetValue(name, out var v) ? v.reason : null;
    }

    internal static void Clear() { lock (Gate) _map = new(StringComparer.OrdinalIgnoreCase); }

    /// <summary>Recalcule les déductions et met à jour le thème des applications (à appeler après un inventaire ou un changement de tri).</summary>
    public static void Compute(IList<AppItem> apps)
    {
        Clear();
        var shortcuts = apps.Where(a => a.IsShortcut).ToList();
        string Def() => Settings.Current.DefaultCategory;
        // Thème direct (sans déduction)
        foreach (var a in shortcuts) a.Theme = Settings.Classify(a.Name, a.Target, a.Context, a.LooksLikeGame);

        bool Open(AppItem a) => a.Theme == Def() && !Settings.IsSkipped(a.Name) && !Settings.Current.Overrides.ContainsKey(a.Name);
        var map = new Dictionary<string, (string theme, string reason)>(StringComparer.OrdinalIgnoreCase);

        void Infer(IEnumerable<IGrouping<string, AppItem>> groups, Func<AppItem, string> reason)
        {
            foreach (var g in groups)
            {
                var members = g.ToList();
                var known = members.Where(m => m.Theme != Def() && !map.ContainsKey(m.Name)).ToList();
                if (known.Count == 0) continue;
                var top = known.GroupBy(m => m.Theme).OrderByDescending(x => x.Count()).First();
                if (top.Count() * 3 < known.Count * 2) continue;          // voisines en désaccord : pas de déduction
                var example = top.OrderBy(m => m.IsCompanion ? 1 : 0).ThenBy(m => m.Name.Length).First();
                foreach (var m in members.Where(m => Open(m) && !map.ContainsKey(m.Name)))
                {
                    map[m.Name] = (top.Key, reason(example));
                    m.Theme = top.Key;
                }
            }
        }

        // 1) Suites (Git, LibreOffice, « Jeu » + « Jeu - Bonus Content »…)
        var suites = AppSuites.Group(shortcuts).Where(s => s.IsSuite).ToList();
        Infer(suites.SelectMany(s => s.Members.Select(m => (s.Name, m))).GroupBy(x => x.Name, x => x.m),
              ex => $"fait partie de la même suite que « {ex.Name} »");
        // 2) Même dossier d'installation
        Infer(shortcuts.Where(a => a.InstallRoot != null).GroupBy(a => a.InstallRoot, StringComparer.OrdinalIgnoreCase),
              ex => $"installée dans le même dossier que « {ex.Name} »");
        // 3) Même sous-dossier du menu Démarrer (hors dossiers fourre-tout)
        Infer(shortcuts.Where(a => a.StartFolder != null && !AppSuites.IsGenericFolder(a.StartFolder))
                       .GroupBy(a => a.StartFolder, StringComparer.OrdinalIgnoreCase),
              ex => $"même dossier du menu Démarrer que « {ex.Name} »");
        // 4) Même éditeur
        Infer(shortcuts.Where(a => a.Publisher != null && !BroadPublishers.Contains(a.Publisher))
                       .GroupBy(a => PublisherKey(a.Publisher), StringComparer.OrdinalIgnoreCase),
              ex => $"même éditeur que « {ex.Name} » ({ex.Publisher})");

        lock (Gate) _map = map;
    }

    /// <summary>« Adobe Inc. », « Adobe Systems Incorporated » → « adobe ».</summary>
    internal static string PublisherKey(string publisher)
    {
        var p = System.Text.RegularExpressions.Regex.Replace(publisher.ToLowerInvariant(),
            @"[,.]|\b(inc|incorporated|corp|corporation|ltd|limited|llc|gmbh|sas|sa|sarl|s\.a\.|co|company|systems|software|technologies|interactive|entertainment|studios?|games)\b", " ");
        return System.Text.RegularExpressions.Regex.Replace(p, @"\s+", " ").Trim();
    }
}
