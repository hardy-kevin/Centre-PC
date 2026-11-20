using System.Text.Json;

namespace CentrePC.Core;

/// <summary>Un déplacement fait par Centre PC (rangement, remise sur le bureau, document rangé, raccourci remplacé).</summary>
public class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; } = DateTime.Now;
    /// <summary>rangement, remise, document, remplacement</summary>
    public string Kind { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public bool IsFolder { get; set; }
    public bool Undone { get; set; }

    public string Name => Path.GetFileNameWithoutExtension(From ?? To ?? "");
    public string Label => Kind switch
    {
        "rangement" => "Rangé dans Centre PC",
        "remise" => "Remis sur le bureau",
        "document" => "Rangé dans " + Path.GetFileName(Path.GetDirectoryName(To) ?? ""),
        "remplacement" => "Mis de côté (remplacé ou en double)",
        _ => Kind
    };
    public string When => Date.Date == DateTime.Today ? $"aujourd'hui à {Date:HH:mm}"
        : Date.Date == DateTime.Today.AddDays(-1) ? $"hier à {Date:HH:mm}" : Date.ToString("dd/MM à HH:mm");
    public bool CanUndo => !Undone && (File.Exists(To) || Directory.Exists(To)) && !File.Exists(From) && !Directory.Exists(From);
}

/// <summary>
/// Historique des 30 derniers jours : chaque déplacement peut être annulé plus tard, pas seulement tout de suite
/// (page Bureau > Historique). Rien n'est jamais supprimé : annuler remet simplement l'élément à son ancienne place.
/// </summary>
public static class History
{
    private static readonly object Gate = new();
    private static List<HistoryEntry> _items;
    private static string FilePath => Path.Combine(Settings.DataDir, "historique.json");

    private static List<HistoryEntry> Items
    {
        get
        {
            if (_items != null) return _items;
            try { _items = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) : null; }
            catch (Exception ex) { Log.Error("Lecture de l'historique", ex); }
            _items ??= new();
            return _items;
        }
    }

    /// <summary>Relit le fichier au prochain accès (tests).</summary>
    internal static void ResetCache() { lock (Gate) _items = null; }

    public static void Record(string kind, string from, string to, bool isFolder = false)
    {
        if (Settings.ReadOnly || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
        lock (Gate)
        {
            Items.Insert(0, new HistoryEntry { Kind = kind, From = from, To = to, IsFolder = isFolder });
            Items.RemoveAll(e => e.Date < DateTime.Now.AddDays(-30));
            if (Items.Count > 500) Items.RemoveRange(500, Items.Count - 500);
            Save();
        }
    }

    /// <summary>
    /// Reprend l'historique d'une installation précédente (voir <see cref="PreviousInstall"/>), pour que ses déplacements
    /// restent annulables. <paramref name="moved"/> : ancien emplacement → nouvel emplacement des éléments repris.
    /// </summary>
    public static int Import(string historyFile, IReadOnlyDictionary<string, string> moved)
    {
        if (Settings.ReadOnly || !File.Exists(historyFile)) return 0;
        List<HistoryEntry> old;
        try { old = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(historyFile)); }
        catch (Exception ex) { Log.Error("Lecture de l'historique précédent", ex); return 0; }
        if (old == null) return 0;
        int n = 0;
        lock (Gate)
        {
            foreach (var e in old.Where(e => e != null && e.Date >= DateTime.Now.AddDays(-30)))
            {
                if (Items.Any(x => x.Id == e.Id)) continue;
                if (e.To != null && moved.TryGetValue(e.To, out var to)) e.To = to;
                if (e.From != null && moved.TryGetValue(e.From, out var from)) e.From = from;
                Items.Add(e);
                n++;
            }
            if (n == 0) return 0;
            _items = Items.OrderByDescending(e => e.Date).Take(500).ToList();
            Save();
        }
        return n;
    }

    public static List<HistoryEntry> Recent(int max = 40)
    {
        lock (Gate) return Items.Where(e => e.Date >= DateTime.Now.AddDays(-30)).Take(max).ToList();
    }

    /// <summary>Remet l'élément à son ancienne place. Renvoie un message d'erreur, ou null si c'est fait.</summary>
    public static string Undo(HistoryEntry e)
    {
        if (e.Undone) return "Déjà annulé.";
        if (!File.Exists(e.To) && !Directory.Exists(e.To)) return "L'élément n'est plus à l'emplacement où Centre PC l'avait placé.";
        // Venu du bureau Public (versions précédentes) : remis sur le bureau de l'utilisateur, sans droits administrateur
        var from = DesktopManager.IsPublicDesktop(Path.GetDirectoryName(e.From))
            ? Path.Combine(AppScanner.UserDesktop, Path.GetFileName(e.From)) : e.From;
        if (File.Exists(from) || Directory.Exists(from)) return "Un autre élément porte déjà ce nom à l'emplacement d'origine.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(from));
            if (e.IsFolder || Directory.Exists(e.To)) Directory.Move(e.To, from); else File.Move(e.To, from);
        }
        catch (UnauthorizedAccessException) { return "Accès refusé par Windows."; }
        catch (Exception ex) { Log.Error("Annulation " + e.To, ex); return ex.Message; }

        // Ce que Centre PC retient des emplacements suit le mouvement
        var cfg = Settings.Current;
        switch (e.Kind)
        {
            case "rangement":                           // rangé → de retour sur le bureau
                cfg.Stored.Remove(e.To);
                cfg.Stored.Remove(Path.GetFileName(e.To));
                IconStyler.OnMoved(e.To, from);
                break;
            case "remise":                              // remis sur le bureau → de nouveau rangé
                cfg.Stored[e.From] = e.To;
                IconStyler.OnMoved(e.To, e.From);
                break;
            case "remplacement":
                if (DocumentLibrary.IsUnder(from, Settings.StoreDir))
                    cfg.Stored[from] = Path.Combine(AppScanner.UserDesktop, Path.GetFileName(e.From));
                break;
            case "document":
                cfg.DocRecent.RemoveAll(p => p.Equals(e.To, StringComparison.OrdinalIgnoreCase));
                break;
        }
        Settings.Save();
        lock (Gate) { e.Undone = true; Save(); }
        return null;
    }

    private static void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Enregistrement de l'historique", ex); }
    }
}
