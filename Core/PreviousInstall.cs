using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>
/// Déplacement automatique des données des versions précédentes vers le dossier de ce PC (<see cref="DataLocation"/>).
///
/// Jusqu'à la 1.2.7, la version GitHub (.exe) rangeait ses données dans %LOCALAPPDATA%\CentrePC et la version
/// Microsoft Store directement dans Documents\Centre PC. Au démarrage, ce qui s'y trouve encore est déplacé dans
/// Documents\Centre PC\&lt;nom du PC&gt; :
/// - raccourcis rangés (« Remettre sur le bureau » les remet toujours à leur emplacement d'origine), raccourcis mis de
///   côté, icônes de Centre PC (les raccourcis et dossiers stylisés sont redirigés vers leur nouvel emplacement),
///   copies de sauvegarde des réglages d'autres applications ;
/// - réglages : au premier lancement, la configuration la plus récente est reprise telle quelle ; une autre est
///   fusionnée (ce qui est déjà choisi ici l'emporte) ;
/// - historique (les déplacements restent annulables), journaux, fichiers de travail ;
/// - installateurs téléchargés : dans le cache (%LOCALAPPDATA%\CentrePC\Cache).
/// Rien n'est supprimé, à part le cache d'icônes de l'ancienne version Store (il se reconstitue tout seul) : les anciens
/// fichiers de réglages déjà repris sont gardés dans « Ancienne installation ».
///
/// Une version précédente encore installée (1.2.7 ou avant) continue d'utiliser son ancien dossier : on n'y touche pas,
/// l'utilisateur est prévenu une fois, et le déplacement se fait au démarrage qui suit sa mise à jour ou sa désinstallation.
/// </summary>
public static class PreviousInstall
{
    public class Result
    {
        public int Shortcuts { get; set; }
        public int Documents { get; set; }
        public int DocFolders { get; set; }
        public bool Choices { get; set; }
        /// <summary>Autre version encore installée (rien n'a été repris de son dossier) : son nom lisible.</summary>
        public string BlockedBy { get; set; }
        /// <summary>Nombre de raccourcis rangés que garde l'autre version encore installée.</summary>
        public int BlockedShortcuts { get; set; }
        public bool Imported => Shortcuts + Documents + DocFolders > 0 || Choices;
    }

    /// <summary>Résultat du dernier démarrage (message affiché par la fenêtre principale), ou null.</summary>
    public static Result Last { get; private set; }

    /// <summary>Tests : remplace la détection d'une ancienne version encore installée (dossier → nom de la version, ou null).</summary>
    internal static Func<string, string> InstalledOverride { get; set; }

    /// <summary>Première version qui range ses données dans Documents\Centre PC\&lt;nom du PC&gt;.</summary>
    private static readonly Version FirstSharedVersion = new(1, 2, 8);

    /// <summary>Ancien dossier de la version .exe.</summary>
    internal static string ExeDataDir => Path.Combine(Paths.Get(Environment.SpecialFolder.LocalApplicationData), "CentrePC");
    /// <summary>Ancien dossier de la version Store (racine de Documents\Centre PC, aujourd'hui dossier commun aux PC).</summary>
    internal static string StoreDataDir => DataLocation.Root;

    public static List<string> LegacyDirs() => new() { ExeDataDir, StoreDataDir };

    private static string Label(string dir) =>
        Same(dir, ExeDataDir) ? "Version GitHub" : "Version Microsoft Store";

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// À appeler au démarrage normal (pas pour la vérification quotidienne ni la désinstallation) :
    /// déplacement des données des versions précédentes, puis dossiers de documents créés par Centre PC remis dans l'arborescence.
    /// </summary>
    public static Result RunAtStartup()
    {
        Result res = null;
        try { res = Run(); }
        catch (Exception ex) { Log.Error("Reprise d'une installation précédente", ex); }
        try
        {
            var adopted = DocPlanner.AdoptOwnFolders().Count;
            if (adopted > 0) { res ??= new Result(); res.DocFolders += adopted; }
        }
        catch (Exception ex) { Log.Error("Dossiers de Centre PC", ex); }
        Last = res;
        return res;
    }

    /// <summary>Déplace les données des anciens dossiers. Renvoie ce qui a été fait, ou null s'il n'y avait rien.</summary>
    public static Result Run()
    {
        if (Settings.ReadOnly || Settings.Current == null) return null;
        Result res = null;
        var dirs = LegacyDirs().Where(HasLegacyData).OrderByDescending(Stamp).ToList();   // la plus récente d'abord
        bool takeWhole = Settings.FirstRun;
        foreach (var dir in dirs)
        {
            res ??= new Result();
            var other = UsedByOldVersion(dir);
            if (other != null)
            {
                res.BlockedBy = other;
                res.BlockedShortcuts += StoredFiles(Path.Combine(dir, "Bureau rangé")).Count;
                Log.Info($"{other} de Centre PC encore installée : {dir} n'est pas déplacé");
                continue;
            }
            try
            {
                Migrate(dir, takeWhole, res);
                takeWhole = false;
            }
            catch (Exception ex) { Log.Error("Déplacement de " + dir, ex); }
        }
        if (res != null) { Settings.Save(); IconStyler.SaveBackups(); }
        return res;
    }

    /// <summary>Le dossier contient-il encore des données d'une version précédente ?</summary>
    internal static bool HasLegacyData(string dir)
    {
        if (!Directory.Exists(dir) || Same(dir, Settings.DataDir)) return false;
        return File.Exists(Path.Combine(dir, "config.json")) || File.Exists(Path.Combine(dir, "config.bak"))
            || StoredFiles(Path.Combine(dir, "Bureau rangé")).Count > 0;
    }

    private static DateTime Stamp(string dir)
    {
        var t = DateTime.MinValue;
        foreach (var n in new[] { "config.json", "config.bak" })
            try { var f = Path.Combine(dir, n); if (File.Exists(f) && File.GetLastWriteTime(f) > t) t = File.GetLastWriteTime(f); } catch { }
        return t;
    }

    // ------------------------------------------------------------------ ancienne version encore installée ?
    /// <summary>Nom lisible de la version 1.2.7 (ou avant) qui utilise encore ce dossier, sinon null.</summary>
    private static string UsedByOldVersion(string dir)
    {
        if (InstalledOverride != null) return InstalledOverride(dir);
        if (Paths.IsTest) return null;
        try
        {
            if (Same(dir, ExeDataDir))
            {
                if (!Native.IsPackaged && SelfInstall.IsInstalledCopy) return null;   // c'est nous, mis à jour
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\CentrePC");
                if (k == null || !File.Exists(SelfInstall.InstalledExe)) return null;
                var v = FileVersionInfo.GetVersionInfo(SelfInstall.InstalledExe);
                var installed = new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
                return installed < FirstSharedVersion ? "La version téléchargée sur GitHub" : null;
            }
            if (Same(dir, StoreDataDir))
            {
                if (Native.IsPackaged) return null;   // c'est nous, mis à jour
                // Version Store installée (son dossier de paquet existe jusqu'à sa désinstallation). Sa version n'est pas
                // lisible d'ici : si elle est à jour, elle a déjà déplacé ses données elle-même à son démarrage.
                var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                return Directory.Exists(packages) && Directory.EnumerateDirectories(packages, "*.CentrePC_*").Any()
                    ? "La version Microsoft Store" : null;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ déplacement
    private static void Migrate(string dir, bool takeWhole, Result res)
    {
        var cfg = Settings.Current;
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var archive = Path.Combine(Settings.DataDir, "Ancienne installation", Label(dir));

        // 1) Réglages de l'ancienne version (lus avant de déplacer quoi que ce soit)
        Config old = null;
        foreach (var n in new[] { "config.json", "config.bak" })
        {
            var f = Path.Combine(dir, n);
            if (old != null || !File.Exists(f)) continue;
            try { old = JsonSerializer.Deserialize<Config>(File.ReadAllText(f), JsonOpts); }
            catch (Exception ex) { Log.Error("Lecture de " + f, ex); }
        }
        var oldStored = new Dictionary<string, string>(old?.Stored ?? new(), StringComparer.OrdinalIgnoreCase);

        // 2) Raccourcis rangés, avec leur emplacement d'origine
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in StoredFiles(Path.Combine(dir, "Bureau rangé")))
        {
            var name = Path.GetFileName(f);
            var original = oldStored.TryGetValue(f, out var o) && !string.IsNullOrEmpty(o) ? o
                         : oldStored.TryGetValue(name, out o) && !string.IsNullOrEmpty(o) ? o
                         : Path.Combine(AppScanner.UserDesktop, name);
            var existing = Path.Combine(Settings.StoreDir, name);
            string to;
            if (File.Exists(existing) && SameContent(existing, f))
            {
                // Même raccourci déjà rangé ici : un seul exemplaire suffit, l'autre est gardé dans « Ancienne installation »
                to = existing;
                MoveFile(f, Unique(Path.Combine(archive, "Bureau rangé"), name));
            }
            else
            {
                to = Unique(Settings.StoreDir, name);
                if (!MoveFile(f, to)) continue;
                if (IsShortcut(to)) res.Shortcuts++; else res.Documents++;   // documents d'anciennes versions : proposés dans la page Documents
            }
            moved[f] = to;
            if (IsShortcut(to)) stored[to] = original;
        }

        // 3) Autres dossiers de travail
        MoveTree(Path.Combine(dir, "Raccourcis remplacés"), ShortcutGuard.ReplacedDir, moved);
        MoveTree(Path.Combine(dir, "Sauvegardes raccourcis"), Path.Combine(Settings.DataDir, "Sauvegardes raccourcis"), moved);
        MoveTree(Path.Combine(dir, "Icones"), IconStyler.IconsDir, moved);
        MoveTree(Path.Combine(dir, "Journaux"), Path.Combine(Settings.DataDir, "Journaux"), moved);
        MoveTree(Path.Combine(dir, "Installateurs"), Path.Combine(Settings.CacheDir, "Installateurs"), moved);
        // Cache de l'ancienne version Store (Documents\Centre PC\Cache) : se reconstitue, inutile de le synchroniser
        if (Same(dir, StoreDataDir)) TryDeleteTree(Path.Combine(dir, "Cache"));

        // 4) Réglages : repris tels quels au premier lancement, fusionnés sinon
        if (old != null)
        {
            Remap(old, moved, dir);
            if (takeWhole)
            {
                old.OtherInstallNoticed = cfg.OtherInstallNoticed;
                Settings.Adopt(old);
                cfg = Settings.Current;
                res.Choices = true;
                res.DocFolders += cfg.DocFolders.Count(Directory.Exists);
            }
            else
            {
                MergeChoices(cfg, old, res);
                MergeDocuments(cfg, old, res);
                MergeBackups(cfg, old);
                foreach (var kv in old.Stored.Where(kv => File.Exists(kv.Key) && !cfg.Stored.ContainsKey(kv.Key)))
                    cfg.Stored[kv.Key] = kv.Value;
            }
            Settings.Rebuild();
        }
        foreach (var kv in stored) cfg.Stored[kv.Key] = kv.Value;
        // Raccourcis et dossiers stylisés : leurs icônes sont maintenant dans le dossier de ce PC
        try { IconStyler.RetargetIcons(Path.Combine(dir, "Icones")); } catch (Exception ex) { Log.Error("Icônes déplacées", ex); }

        // 5) Historique (déplacements toujours annulables)
        try { History.Import(Path.Combine(dir, "historique.json"), moved); } catch (Exception ex) { Log.Error("Historique précédent", ex); }

        // 6) Le reste : fichiers de travail repris s'ils manquent ici, sinon gardés dans « Ancienne installation »
        foreach (var f in SafeFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (name.Equals("Lisez-moi.txt", StringComparison.OrdinalIgnoreCase)) continue;
            bool imported = name is "config.json" or "config.bak" or "historique.json" or "icones-sauvegarde.json";
            var here = Path.Combine(Settings.DataDir, name);
            MoveFile(f, !imported && !File.Exists(here) ? here : Unique(archive, name));
        }
        foreach (var d in SafeDirs(dir))
        {
            var name = Path.GetFileName(d);
            if (name.Equals("Cache", StringComparison.OrdinalIgnoreCase) || name.Equals("Bilans", StringComparison.OrdinalIgnoreCase)) continue;
            if (DataLocation.IsPcFolder(d) || Same(d, Settings.DataDir)) continue;   // dossiers des PC (nouvel emplacement)
            if (IsEmptyTree(d)) { TryDeleteTree(d); continue; }
            MoveTree(d, Path.Combine(archive, name), moved);
        }
        // Ancien dossier de la version .exe : vide (le cache, s'il y est, reste à sa place)
        if (Same(dir, ExeDataDir) && IsEmptyTree(dir) && !Directory.Exists(Settings.CacheDir)) TryDeleteTree(dir);
        Log.Info($"Données de la {Label(dir).ToLowerInvariant()} déplacées dans {Settings.DataDir} : {res.Shortcuts} raccourci(s), {res.Documents} document(s)");
    }

    /// <summary>Chemins des éléments déplacés, dans les réglages de l'ancienne version.</summary>
    private static void Remap(Config old, Dictionary<string, string> moved, string dir)
    {
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in old.Stored ?? new())
        {
            if (moved.TryGetValue(kv.Key, out var to)) stored[to] = kv.Value;
            else if (Path.IsPathRooted(kv.Key) && !DocumentLibrary.IsUnder(kv.Key, dir)) stored[kv.Key] = kv.Value;   // mode « dossiers » : resté sur le bureau
        }
        old.Stored = stored;
        foreach (var b in old.IconBackups ?? new())
            if (b?.Path != null && moved.TryGetValue(b.Path, out var to)) b.Path = to;
    }

    private static List<string> StoredFiles(string storeDir) =>
        SafeFiles(storeDir).Where(f =>
        {
            if (Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return false;
            try { return (File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.System)) == 0; } catch { return false; }
        }).ToList();

    private static string[] SafeFiles(string dir) { try { return Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>(); } catch { return Array.Empty<string>(); } }
    private static string[] SafeDirs(string dir) { try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); } catch { return Array.Empty<string>(); } }

    private static bool IsShortcut(string f) => Path.GetExtension(f).ToLowerInvariant() is ".lnk" or ".url" or ".appref-ms";

    private static string Unique(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        return DesktopManager.UniquePath(dir, name);
    }

    /// <summary>Déplace le contenu d'un dossier dans un autre (sous-dossiers compris) ; un nom déjà pris reçoit un numéro.</summary>
    private static void MoveTree(string from, string to, Dictionary<string, string> moved)
    {
        if (!Directory.Exists(from)) return;
        foreach (var f in SafeFiles(from))
        {
            var dest = Unique(to, Path.GetFileName(f));
            if (MoveFile(f, dest)) moved[f] = dest;
        }
        foreach (var d in SafeDirs(from)) MoveTree(d, Path.Combine(to, Path.GetFileName(d)), moved);
        if (IsEmptyTree(from)) TryDeleteTree(from);
    }

    /// <summary>Déplacement ; si Windows refuse (dossier AppData vu depuis la version Store…), copie, l'original restant en place.</summary>
    private static bool MoveFile(string from, string to)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Copy(from, to); }
            catch (Exception ex2) { Log.Info($"Déplacement impossible : {from} ({ex2.Message})"); return false; }
            try { File.Delete(from); } catch { Log.Info($"Copié (l'original reste en place) : {from}"); }
            return true;
        }
    }

    private static bool IsEmptyTree(string dir)
    {
        try { return Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// <summary>Supprime un dossier (appelé seulement sur un dossier vide ou sur un cache qui se reconstitue).</summary>
    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(dir, true);
        }
        catch { }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            return new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ messages
    /// <summary>Ancienne version encore installée : message montré une seule fois (null sinon).</summary>
    public static string TakeBlockedNotice()
    {
        var r = Last;
        if (r?.BlockedBy == null) return null;
        var key = r.BlockedBy;
        if (Settings.Current.OtherInstallNoticed.Contains(key, StringComparer.OrdinalIgnoreCase)) return null;
        Settings.Current.OtherInstallNoticed.Add(key);
        Settings.Save();
        var kept = r.BlockedShortcuts > 0 ? $" et garde {Plural(r.BlockedShortcuts, "icône rangée", "icônes rangées")}" : "";
        return $"{r.BlockedBy} de Centre PC (ancienne version) est encore installée{kept}. "
             + "Mettez-la à jour ou désinstallez-la : ses réglages et ses icônes seront repris ici au démarrage suivant.";
    }

    /// <summary>Message après un déplacement, ou null.</summary>
    public static string ImportedNotice()
    {
        var r = Last;
        if (r == null || !r.Imported) return null;
        var parts = new List<string>();
        if (r.Shortcuts > 0) parts.Add(Plural(r.Shortcuts, "icône rangée", "icônes rangées"));
        if (r.Documents > 0) parts.Add(Plural(r.Documents, "document", "documents"));
        if (r.DocFolders > 0) parts.Add(Plural(r.DocFolders, "dossier de documents", "dossiers de documents"));
        if (r.Choices) parts.Add("vos réglages");
        if (parts.Count == 0) return null;
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " et " + parts[^1];
        if (r.Shortcuts == 0 && r.Documents == 0 && !r.Choices)
            return $"Centre PC a retrouvé {list} de son plan de rangement : ils figurent de nouveau dans la page Documents.";
        return $"Données de l'ancienne version de Centre PC reprises : {list}. Tout se remet en place comme avant (page Bureau, « Remettre sur le bureau »).";
    }

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    // ------------------------------------------------------------------ fusion (seconde ancienne installation)
    private static void Union(List<string> into, IEnumerable<string> from)
    {
        foreach (var s in from ?? Enumerable.Empty<string>())
            if (!string.IsNullOrEmpty(s) && !into.Contains(s, StringComparer.OrdinalIgnoreCase)) into.Add(s);
    }

    private static void AddMissing<T>(IDictionary<string, T> into, IDictionary<string, T> from)
    {
        foreach (var kv in from ?? new Dictionary<string, T>())
            if (!into.ContainsKey(kv.Key)) into[kv.Key] = kv.Value;
    }

    private static void UnionJson<T>(List<T> into, IEnumerable<T> from)
    {
        var seen = into.Select(x => JsonSerializer.Serialize(x)).ToHashSet();
        foreach (var x in from ?? Enumerable.Empty<T>())
            if (x != null && seen.Add(JsonSerializer.Serialize(x))) into.Add(x);
    }

    /// <summary>Choix de l'utilisateur : ce qui est déjà choisi ici l'emporte, le reste est ajouté.</summary>
    private static void MergeChoices(Config cfg, Config old, Result res)
    {
        int before = Signature(cfg);
        foreach (var c in old.Categories ?? new())
        {
            if (string.IsNullOrWhiteSpace(c?.Name)) continue;
            var cur = cfg.Categories.FirstOrDefault(x => x.Name != null && x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
            if (cur == null)
            {
                if (cfg.DeclinedThemes.Contains(c.Name, StringComparer.OrdinalIgnoreCase)) continue;   // supprimée depuis
                cfg.Categories.Add(c);
            }
            else cur.Keywords = (cur.Keywords ?? new()).Union(c.Keywords ?? new(), StringComparer.OrdinalIgnoreCase).ToList();
        }
        AddMissing(cfg.Overrides, old.Overrides);
        Union(cfg.Favorites, old.Favorites);
        Union(cfg.HiddenApps, old.HiddenApps);
        Union(cfg.SortSkipped, old.SortSkipped);
        Union(cfg.ObsoleteKept, old.ObsoleteKept);
        Union(cfg.DeclinedThemes, old.DeclinedThemes);
        Union(cfg.SuiteExcluded, old.SuiteExcluded);
        Union(cfg.KeepOnDesktop, old.KeepOnDesktop);
        Union(cfg.WingetExclusions, old.WingetExclusions);
        Union(cfg.DriverExclusions, old.DriverExclusions);
        AddMissing(cfg.DriverExclusionNames, old.DriverExclusionNames);
        AddMissing(cfg.PhantomUpdates, old.PhantomUpdates);
        foreach (var kv in old.AppFolders ?? new())
        {
            if (!cfg.AppFolders.TryGetValue(kv.Key, out var apps)) cfg.AppFolders[kv.Key] = kv.Value ?? new();
            else Union(apps, kv.Value);
        }
        res.Choices |= Signature(cfg) != before;
    }

    private static int Signature(Config c) =>
        HashCode.Combine(c.Categories.Count + c.Categories.Sum(x => x.Keywords?.Count ?? 0), c.Overrides.Count, c.Favorites.Count + c.HiddenApps.Count + c.SortSkipped.Count,
                         c.AppFolders.Count + c.AppFolders.Values.Sum(v => v?.Count ?? 0), c.DeclinedThemes.Count + c.SuiteExcluded.Count + c.ObsoleteKept.Count);

    /// <summary>Documents : arborescence, rangements à vérifier, refus (jamais reproposés).</summary>
    private static void MergeDocuments(Config cfg, Config old, Result res)
    {
        foreach (var p in (old.DocFolders ?? new()).Where(Directory.Exists))
        {
            if (cfg.DocFolders.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            if (cfg.DocFoldersRemoved.Contains(p.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)) continue;
            cfg.DocFolders.Add(p);
            res.DocFolders++;
        }
        Union(cfg.DocFoldersRemoved, old.DocFoldersRemoved);
        Union(cfg.DocRejected, old.DocRejected);
        Union(cfg.DocRecent, (old.DocRecent ?? new()).Where(File.Exists));
        foreach (var e in (old.DocReview ?? new()).Where(e => e?.To != null && (File.Exists(e.To) || Directory.Exists(e.To))))
            if (!cfg.DocReview.Any(x => x.To != null && x.To.Equals(e.To, StringComparison.OrdinalIgnoreCase))) cfg.DocReview.Add(e);
        if (old.DocStarterDismissed) cfg.DocStarterDismissed = true;
    }

    /// <summary>Sauvegardes qui permettent de revenir en arrière : sans elles, « Annuler » ne saurait plus quoi remettre.</summary>
    private static void MergeBackups(Config cfg, Config old)
    {
        foreach (var b in old.IconBackups ?? new())
            if (b?.Path != null && !cfg.IconBackups.Any(x => x.Path != null && x.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase)))
                cfg.IconBackups.Add(b);
        UnionJson(cfg.HotkeyBackups, old.HotkeyBackups);
        AddMissing(cfg.DeviceFixBackups, old.DeviceFixBackups);
    }
}
