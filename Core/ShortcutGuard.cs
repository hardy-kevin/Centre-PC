using System.Text.Json;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>État des raccourcis du bureau photographié juste avant les mises à jour.</summary>
public class ShortcutSnapshot
{
    public List<string> DesktopFiles { get; set; } = new();
    /// <summary>Nombre d'éléments rangés : indique que l'utilisateur range son bureau.</summary>
    public int StoredCount { get; set; }
    public string LogPath { get; set; }
    public DateTime Taken { get; set; } = DateTime.Now;
}

public class ReconcileResult
{
    /// <summary>Ancien raccourci cassé remplacé par celui de la nouvelle version (même place, mêmes réglages).</summary>
    public int Replaced;
    /// <summary>Raccourci recréé par l'installateur alors qu'il était déjà rangé : retiré du bureau.</summary>
    public int Removed;
    /// <summary>Nouveau raccourci ajouté sur le bureau par un installateur : rangé.</summary>
    public int Stored;
    /// <summary>Raccourci rangé cassé réparé grâce au menu Démarrer.</summary>
    public int Repaired;
    /// <summary>Raccourcis à re-styliser (ils remplacent un raccourci qui avait une icône personnalisée).</summary>
    public List<StyleTarget> ToStyle = new();
    public int Total => Replaced + Removed + Stored + Repaired;
}

/// <summary>
/// Après les mises à jour, remet le bureau et les raccourcis rangés dans l'état d'avant :
/// - les installateurs recréent souvent leur icône sur le bureau → si elle était rangée, le doublon est retiré ;
/// - les applis qui changent de dossier à chaque version (JetBrains, PostgreSQL…) cassent l'ancien raccourci rangé
///   → il est remplacé par le nouveau, à la même place, avec le même thème, favori et style ;
/// - une icône apparue sur un bureau rangé est rangée à son tour.
/// Rien n'est supprimé définitivement : les raccourcis retirés vont dans « Raccourcis remplacés » (gardés 30 jours).
/// </summary>
public static class ShortcutGuard
{
    private static readonly string[] Ext = { ".lnk", ".url" };
    public static string ReplacedDir => Path.Combine(Settings.DataDir, "Raccourcis remplacés");
    private static string SnapshotFile => Path.Combine(Settings.DataDir, "bureau-avant-maj.json");

    // ------------------------------------------------------------------ photo avant mise à jour
    public static ShortcutSnapshot Take() => new()
    {
        DesktopFiles = DesktopShortcuts().ToList(),
        StoredCount = StoredShortcuts().Count()
    };

    public static void Save(ShortcutSnapshot snap)
    {
        try { File.WriteAllText(SnapshotFile, JsonSerializer.Serialize(snap)); } catch { }
    }

    /// <summary>
    /// Photo laissée par une mise à jour que Centre PC n'a pas vue se terminer (appli fermée pendant l'installation).
    /// Renvoyée seulement si l'installation est bien finie.
    /// </summary>
    public static ShortcutSnapshot Pending()
    {
        try
        {
            if (!File.Exists(SnapshotFile)) return null;
            var snap = JsonSerializer.Deserialize<ShortcutSnapshot>(File.ReadAllText(SnapshotFile));
            if (snap == null) { Clear(); return null; }
            bool finished = snap.LogPath == null || !File.Exists(snap.LogPath)
                || DateTime.Now - snap.Taken > TimeSpan.FromHours(3)
                || ReadShared(snap.LogPath).Contains("##FIN|");
            return finished ? snap : null;
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { File.Delete(SnapshotFile); } catch { }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    // ------------------------------------------------------------------ remise en état
    /// <summary>
    /// Doublons déjà présents (icônes remises sur le bureau par d'anciennes mises à jour alors qu'elles étaient rangées) :
    /// retirés du bureau, ou remplacent la version rangée si elle est plus ancienne. Bureau de l'utilisateur seulement
    /// par défaut (aucune demande d'autorisation).
    /// </summary>
    public static ReconcileResult CleanDuplicates(bool includePublic = false) =>
        Reconcile(new ShortcutSnapshot { DesktopFiles = DesktopShortcuts().ToList(), StoredCount = 0 }, includePublic, keepSnapshot: true);

    public static ReconcileResult Reconcile(ShortcutSnapshot snap) => Reconcile(snap, includePublic: true, keepSnapshot: false);

    private static ReconcileResult Reconcile(ShortcutSnapshot snap, bool includePublic, bool keepSnapshot)
    {
        var res = new ReconcileResult();
        if (snap == null) return res;
        if (!keepSnapshot) Clear();
        PurgeReplaced();

        var before = new HashSet<string>(snap.DesktopFiles ?? new(), StringComparer.OrdinalIgnoreCase);
        var desktopNow = DesktopShortcuts()
            .Where(f => includePublic || Path.GetDirectoryName(f).Equals(AppScanner.UserDesktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .ToList();
        // Toutes les icônes du bureau sont comparées aux raccourcis rangés (doublons) ; les nouvelles seulement sont rangées
        var candidates = desktopNow.Select(Info).Where(n => !n.Broken && !IsOwn(n) && !IsKept(n.Name)).ToList();
        var freshSet = new HashSet<string>(desktopNow.Where(f => !before.Contains(f)), StringComparer.OrdinalIgnoreCase);
        var oldDesk = desktopNow.Where(before.Contains).Select(Info).ToList();
        var stored = StoredShortcuts().Select(Info).ToList();

        var moves = new List<Plan>();
        var discards = new List<string>();
        var toStore = new List<string>();

        foreach (var n in candidates)
        {
            bool isFresh = freshSet.Contains(n.Path);
            // 1) L'appli est déjà rangée : même nom, même cible, ou ancien raccourci cassé de la même appli
            var s = stored.FirstOrDefault(x => Eq(x.Name, n.Name))
                    ?? stored.FirstOrDefault(x => !x.Broken && SameTarget(x, n))
                    ?? stored.FirstOrDefault(x => x.Broken && Similar(x, n));
            if (s != null)
            {
                stored.Remove(s);
                // Identique : le doublon quitte le bureau. Sinon (nom de version plus récent, nouvelle cible) il remplace l'ancien.
                if (!s.Broken && Eq(s.Name, n.Name) && Eq(s.Target, n.Target)) { discards.Add(n.Path); res.Removed++; }
                else { moves.Add(PlanReplace(s, n, res)); }
                continue;
            }
            if (!isFresh) continue;   // icône que l'utilisateur a laissée sur le bureau : on n'y touche pas
            // 2) L'ancienne version était sur le bureau et ne marche plus : le nouveau raccourci prend sa place
            var d = oldDesk.FirstOrDefault(x => x.Broken && Similar(x, n));
            if (d != null)
            {
                oldDesk.Remove(d);
                discards.Add(d.Path);
                IconStyler.Backups.RemoveAll(b => b.Path.Equals(d.Path, StringComparison.OrdinalIgnoreCase));
                TransferChoices(d.Name, n.Name);
                res.Replaced++;
                continue;
            }
            // 3) Icône nouvelle sur un bureau que l'utilisateur range : elle est rangée aussi
            if (snap.StoredCount > 0) toStore.Add(n.Path);
        }

        // Raccourcis rangés encore cassés (l'installateur n'a pas créé d'icône sur le bureau) : réparés depuis le menu Démarrer
        var broken = stored.Where(x => x.Broken).ToList();
        if (broken.Count > 0)
        {
            var start = StartMenuShortcuts().Select(Info).Where(x => !x.Broken).ToList();
            foreach (var s in broken)
            {
                var m = start.FirstOrDefault(x => Similar(s, x));
                if (m == null) continue;
                try
                {
                    var dest = DestinationFor(s, m);
                    if (dest.Equals(s.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        SetAside(s.Path, copy: true);
                        File.Copy(m.Path, dest, overwrite: true);
                    }
                    else { File.Copy(m.Path, dest); discards.Add(s.Path); }
                    Register(s, dest, m, res);
                    res.Repaired++;
                }
                catch { }
            }
        }

        // Exécution : une seule demande administrateur au plus (raccourcis du bureau Public)
        if (moves.Count > 0)
        {
            foreach (var m in moves) m.Prepare?.Invoke();
            var done = DesktopManager.Execute(moves.Select(m => m.Op).ToList());
            foreach (var m in moves)
            {
                if (done.Any(x => x.From.Equals(m.Op.From, StringComparison.OrdinalIgnoreCase))) m.Done();
                else m.Undo?.Invoke();
            }
        }
        if (discards.Count > 0)
        {
            Directory.CreateDirectory(ReplacedDir);
            var ops = new List<MoveOp>();
            foreach (var f in discards.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var to = DesktopManager.UniquePath(ReplacedDir, Path.GetFileName(f));
                if (ops.Any(o => o.To.Equals(to, StringComparison.OrdinalIgnoreCase)))
                    to = Path.Combine(ReplacedDir, $"{Path.GetFileNameWithoutExtension(f)} ({Guid.NewGuid():N}){Path.GetExtension(f)}");
                ops.Add(new MoveOp(f, to));
            }
            foreach (var op in DesktopManager.Execute(ops))
            {
                try { File.SetLastWriteTime(op.To, DateTime.Now); } catch { }   // conservé 30 jours à partir d'aujourd'hui
                History.Record("remplacement", op.From, op.To);
            }
        }
        if (toStore.Count > 0)
        {
            var set = new HashSet<string>(toStore, StringComparer.OrdinalIgnoreCase);
            res.Stored += DesktopManager.Store(AppScanner.ScanDesktop().Where(i => i.FilePath != null && set.Contains(i.FilePath)));
        }
        Settings.Save();
        IconStyler.SaveBackups();
        return res;
    }

    private class Plan
    {
        public MoveOp Op;
        public Action Prepare, Done, Undo;
    }

    /// <summary>
    /// Raccourcis rangés d'applis désinstallées (la cible n'existe plus et aucune nouvelle version n'est trouvée) :
    /// mis de côté dans « Raccourcis remplacés » (30 jours) pour ne plus apparaître dans Centre PC.
    /// Renvoie les noms des applis concernées.
    /// </summary>
    public static List<string> RemoveOrphans()
    {
        var names = new List<string>();
        try
        {
            var broken = StoredShortcuts().Select(Info).Where(x => x.Broken).ToList();
            if (broken.Count == 0) return names;
            // Une nouvelle version existe (menu Démarrer ou bureau) : c'est Reconcile qui la remplace, pas une désinstallation
            var others = StartMenuShortcuts().Concat(DesktopShortcuts()).Select(Info).Where(x => !x.Broken).ToList();
            var cfg = Settings.Current;
            foreach (var o in broken.Where(b => !others.Any(x => Similar(b, x))))
            {
                if (SetAside(o.Path) == null) continue;
                cfg.Stored.Remove(o.Path);
                cfg.Stored.Remove(Path.GetFileName(o.Path));
                IconStyler.Backups.RemoveAll(b => b.Path.Equals(o.Path, StringComparison.OrdinalIgnoreCase));
                names.Add(o.Name);
            }
            if (names.Count > 0) { Settings.Save(); IconStyler.SaveBackups(); }
        }
        catch { }
        return names;
    }

    /// <summary>Raccourci (.lnk) vers un programme local qui n'existe plus (appli désinstallée).</summary>
    public static bool IsBrokenShortcut(string path) =>
        path != null && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        && IsBroken(Shortcuts.GetTargetPath(path));

    /// <summary>Le nouveau raccourci prend la place de l'ancien (dans le dossier de rangement ou le dossier de thème).</summary>
    private static Plan PlanReplace(Lnk old, Lnk neu, ReconcileResult res)
    {
        var dest = DestinationFor(old, neu);
        bool samePath = dest.Equals(old.Path, StringComparison.OrdinalIgnoreCase);
        string aside = null;
        return new Plan
        {
            Op = new MoveOp(neu.Path, dest),
            // Même nom de fichier : l'ancien libère la place juste avant (et la reprend si le déplacement échoue)
            Prepare = samePath ? () => aside = SetAside(old.Path) : null,
            Undo = () => { try { if (aside != null && !File.Exists(old.Path)) File.Move(aside, old.Path); } catch { } },
            Done = () =>
            {
                // Sinon, l'ancien est mis de côté seulement une fois le nouveau en place
                if (!samePath) SetAside(old.Path);
                Register(old, dest, neu, res);
                res.Replaced++;
            }
        };
    }

    /// <summary>Met un raccourci de côté dans « Raccourcis remplacés ». Renvoie son nouveau chemin.</summary>
    private static string SetAside(string path, bool copy = false)
    {
        try
        {
            if (!File.Exists(path)) return null;
            Directory.CreateDirectory(ReplacedDir);
            var dest = DesktopManager.UniquePath(ReplacedDir, Path.GetFileName(path));
            if (copy) File.Copy(path, dest); else { File.Move(path, dest); History.Record("remplacement", path, dest); }
            File.SetLastWriteTime(dest, DateTime.Now);   // délai de conservation compté à partir d'aujourd'hui
            return dest;
        }
        catch { return null; }
    }

    private static string DestinationFor(Lnk old, Lnk neu)
    {
        var dir = Path.GetDirectoryName(old.Path);
        var dest = Path.Combine(dir, Path.GetFileName(neu.Path));
        return dest.Equals(old.Path, StringComparison.OrdinalIgnoreCase) || !File.Exists(dest)
            ? dest : DesktopManager.UniquePath(dir, Path.GetFileName(neu.Path));
    }

    /// <summary>Reporte sur le nouveau raccourci tout ce qui concernait l'ancien.</summary>
    private static void Register(Lnk old, string dest, Lnk neu, ReconcileResult res)
    {
        var cfg = Settings.Current;
        // Emplacement d'origine : celui de l'ancien raccourci (pour « remettre sur le bureau »)
        var original = DesktopManager.OriginalOf(old.Path);
        cfg.Stored.Remove(old.Path);
        cfg.Stored.Remove(Path.GetFileName(old.Path));
        cfg.Stored[dest] = Path.Combine(Path.GetDirectoryName(original) ?? AppScanner.UserDesktop, Path.GetFileName(dest));

        TransferChoices(old.Name, Path.GetFileNameWithoutExtension(dest));

        // Icône personnalisée : la sauvegarde de l'ancien n'a plus d'objet, le nouveau est stylisé à son tour
        if (IconStyler.Backups.RemoveAll(b => b.Path.Equals(old.Path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            var name = Path.GetFileNameWithoutExtension(dest);
            var (ctx, game) = AppScanner.Analyze(dest);
            res.ToStyle.Add(new StyleTarget { Path = dest, Name = name, Theme = Settings.Classify(name, neu.Target, ctx, game) });
        }
    }

    /// <summary>Thème choisi, favori, appli masquée… suivent l'appli quand le nom de son raccourci change.</summary>
    private static void TransferChoices(string oldName, string newName)
    {
        if (Eq(oldName, newName)) return;
        var cfg = Settings.Current;
        if (cfg.Overrides.TryGetValue(oldName, out var theme) && !cfg.Overrides.ContainsKey(newName)) cfg.Overrides[newName] = theme;
        foreach (var list in new[] { cfg.Favorites, cfg.KeepOnDesktop, cfg.HiddenApps, cfg.SortSkipped })
        {
            int i = list.FindIndex(x => Eq(x, oldName));
            if (i >= 0 && !list.Any(x => Eq(x, newName))) list[i] = newName;
        }
    }

    private static void PurgeReplaced()
    {
        try
        {
            if (!Directory.Exists(ReplacedDir)) return;
            foreach (var f in new DirectoryInfo(ReplacedDir).GetFiles().Where(f => f.LastWriteTime < DateTime.Now.AddDays(-30)))
                try { f.Delete(); } catch { }
        }
        catch { }
    }

    // ------------------------------------------------------------------ inventaire
    private record Lnk(string Path, string Name, string Exe, string Target, bool Broken);

    private static Lnk Info(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (System.IO.Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = Shortcuts.GetTarget(path);
            return new Lnk(path, name, url, url, false);
        }
        var exe = Shortcuts.GetTargetPath(path) ?? "";
        return new Lnk(path, name, exe, Shortcuts.GetTarget(path)?.Trim() ?? "", IsBroken(exe));
    }

    /// <summary>La cible est un fichier local (disque présent) qui n'existe plus.</summary>
    private static bool IsBroken(string exe)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exe) || exe.StartsWith(@"\\") || !System.IO.Path.IsPathFullyQualified(exe)) return false;
            var root = System.IO.Path.GetPathRoot(exe);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;   // disque externe débranché
            return !File.Exists(exe) && !Directory.Exists(exe);
        }
        catch { return false; }
    }

    private static readonly Regex VersionTokens = new(
        @"\b(v?\d+([.\-_]\d+)*[a-z]?|x64|x86|amd64|arm64|64[- ]?bits?|32[- ]?bits?)\b|[()\[\]]", RegexOptions.IgnoreCase);
    private static readonly Regex Spaces = new(@"[\s\-_.]+");

    /// <summary>Nom ou dossier sans numéros de version ni architecture (« IntelliJ IDEA 2025.2 » → « intellij idea »).</summary>
    internal static string Norm(string s) =>
        Spaces.Replace(VersionTokens.Replace(s ?? "", " "), " ").Trim().ToLowerInvariant();

    /// <summary>Deux raccourcis de la même appli, dans deux versions différentes.</summary>
    private static bool Similar(Lnk a, Lnk b)
    {
        var na = Norm(a.Name);
        if (na.Length >= 3 && na == Norm(b.Name)) return true;
        if (string.IsNullOrEmpty(a.Exe) || string.IsNullOrEmpty(b.Exe) || a.Exe.Contains("://") || b.Exe.Contains("://")) return false;
        return Eq(Path.GetFileName(a.Exe), Path.GetFileName(b.Exe))
            && Norm(Path.GetDirectoryName(a.Exe)) == Norm(Path.GetDirectoryName(b.Exe));
    }

    /// <summary>Même programme lancé avec les mêmes arguments (pas une adresse web : plusieurs jeux partagent un lanceur).</summary>
    private static bool SameTarget(Lnk a, Lnk b) =>
        !string.IsNullOrWhiteSpace(a.Target) && !a.Target.Contains("://") && Eq(a.Target, b.Target);

    private static bool Eq(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool IsKept(string name) => Settings.Current.KeepOnDesktop.Any(k => Eq(k, name));
    private static bool IsOwn(Lnk l) => Eq(l.Name, "Centre PC") || (l.Target ?? "").Contains("CentrePC.exe", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ShortcutsIn(string dir, bool recurse = false)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true })
                .Where(f => Ext.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> DesktopShortcuts()
    {
        var dirs = new[] { AppScanner.UserDesktop, AppScanner.PublicDesktop }.Distinct(StringComparer.OrdinalIgnoreCase);
        return dirs.SelectMany(d => ShortcutsIn(d));
    }

    private static IEnumerable<string> StoredShortcuts() =>
        new[] { Settings.StoreDir }.Concat(DesktopManager.ThemeFolders()).SelectMany(d => ShortcutsIn(d));

    private static IEnumerable<string> StartMenuShortcuts() =>
        new[] { Paths.Get(Environment.SpecialFolder.CommonPrograms), Paths.Get(Environment.SpecialFolder.Programs) }
            .SelectMany(d => ShortcutsIn(d, recurse: true));
}
