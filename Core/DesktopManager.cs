using System.Diagnostics;
using System.Text.Json;

namespace CentrePC.Core;

public record MoveOp(string From, string To);

/// <summary>
/// Range les éléments du bureau, selon le mode choisi :
/// - « app »     : dans le dossier de rangement de Centre PC (bureau vide)
/// - « folders » : dans des dossiers de thème sur le bureau (Jeux, Internet et communication…)
/// et les remet à leur place d'origine.
/// </summary>
public static class DesktopManager
{
    public const string ModeApp = "app";
    public const string ModeFolders = "folders";
    public static bool FoldersMode => Settings.Current.TidyMode == ModeFolders;

    /// <summary>Noms des dossiers de thème possibles sur le bureau.</summary>
    public static HashSet<string> ThemeFolderNames =>
        Settings.AllCategoryNames.Append(Settings.FilesCategory)
            .Concat(DefaultThemes.Renamed.Keys)   // anciens noms : leur contenu est reclassé puis le dossier retiré
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dossiers de thème présents sur le bureau.</summary>
    public static List<string> ThemeFolders()
    {
        var names = ThemeFolderNames;
        try { return Directory.GetDirectories(AppScanner.UserDesktop).Where(d => names.Contains(Path.GetFileName(d))).ToList(); }
        catch { return new(); }
    }

    /// <summary>Le dossier est-il un dossier de thème du bureau ?</summary>
    public static bool IsThemeFolder(string dir) =>
        dir != null && string.Equals(Path.GetDirectoryName(dir.TrimEnd('\\')), AppScanner.UserDesktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && ThemeFolderNames.Contains(Path.GetFileName(dir.TrimEnd('\\')));

    private static string DestinationDir(AppItem it)
    {
        if (!FoldersMode) return Settings.StoreDir;
        var dir = Path.Combine(AppScanner.UserDesktop, it.Theme ?? Settings.Current.DefaultCategory);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Range des éléments du bureau. Renvoie le nombre d'éléments déplacés.</summary>
    public static int Store(IEnumerable<AppItem> items)
    {
        var ops = new List<MoveOp>();
        // Raccourcis seulement : les documents se rangent dans Documents, Images… (DocumentLibrary), jamais dans un dossier caché
        foreach (var it in items.Where(i => i.OnDesktop && i.FilePath != null && i.IsShortcut))
            ops.Add(new MoveOp(it.FilePath, UniquePath(DestinationDir(it), Path.GetFileName(it.FilePath))));
        var done = Execute(ops);
        foreach (var op in done)
        {
            Settings.Current.Stored[op.To] = op.From;    // emplacement rangé → emplacement d'origine
            IconStyler.OnMoved(op.From, op.To);
            History.Record("rangement", op.From, op.To);
        }
        Settings.Save();
        return done.Count;
    }

    /// <summary>Déplace des éléments déjà rangés vers le bon endroit (changement de mode ou de thème).</summary>
    public static int Refile(IEnumerable<AppItem> items)
    {
        var ops = new List<MoveOp>();
        foreach (var it in items.Where(i => i.Location == ItemLocation.Stored && i.FilePath != null))
        {
            var dir = DestinationDir(it);
            if (string.Equals(Path.GetDirectoryName(it.FilePath), dir, StringComparison.OrdinalIgnoreCase)) continue;
            ops.Add(new MoveOp(it.FilePath, UniquePath(dir, Path.GetFileName(it.FilePath))));
        }
        var done = Execute(ops);
        foreach (var op in done)
        {
            var original = OriginalOf(op.From);
            Settings.Current.Stored.Remove(op.From);
            Settings.Current.Stored.Remove(Path.GetFileName(op.From));
            Settings.Current.Stored[op.To] = original;
            IconStyler.OnMoved(op.From, op.To);
        }
        Settings.Save();
        return done.Count;
    }

    /// <summary>Remet des éléments rangés à leur place d'origine.</summary>
    public static int Restore(IEnumerable<AppItem> items)
    {
        var ops = new List<MoveOp>();
        foreach (var it in items.Where(i => i.Location == ItemLocation.Stored && i.FilePath != null))
        {
            var original = OriginalOf(it.FilePath);
            var dir = Path.GetDirectoryName(original);
            if (!Directory.Exists(dir) || IsThemeFolder(dir)) { dir = AppScanner.UserDesktop; original = Path.Combine(dir, Path.GetFileName(it.FilePath)); }
            var dest = File.Exists(original) ? UniquePath(dir, Path.GetFileName(original)) : original;
            ops.Add(new MoveOp(it.FilePath, dest));
        }
        var done = Execute(ops);
        foreach (var op in done)
        {
            Settings.Current.Stored.Remove(op.From);
            Settings.Current.Stored.Remove(Path.GetFileName(op.From));   // ancien format (nom de fichier seul)
            IconStyler.OnMoved(op.From, op.To);
            History.Record("remise", op.From, op.To);
        }
        Settings.Save();
        return done.Count;
    }

    internal static string OriginalOf(string storedPath)
    {
        if (Settings.Current.Stored.TryGetValue(storedPath, out var o) && !string.IsNullOrEmpty(o)) return o;
        if (Settings.Current.Stored.TryGetValue(Path.GetFileName(storedPath), out o) && !string.IsNullOrEmpty(o)) return o;
        return Path.Combine(AppScanner.UserDesktop, Path.GetFileName(storedPath));
    }

    /// <summary>Un dossier de thème est « vide » s'il ne contient que son fichier d'icône (desktop.ini).</summary>
    public static bool IsEffectivelyEmpty(string dir)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(dir)
                .Any(e => !Path.GetFileName(e).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Supprime les dossiers de thème vides du bureau (et l'icône personnalisée qui allait avec).</summary>
    public static int RemoveEmptyThemeFolders()
    {
        int n = 0;
        foreach (var dir in ThemeFolders().Where(IsEffectivelyEmpty))
        {
            try
            {
                IconStyler.Backups.RemoveAll(b => b.Path.Equals(dir, StringComparison.OrdinalIgnoreCase));
                var ini = Path.Combine(dir, "desktop.ini");
                if (File.Exists(ini)) { File.SetAttributes(ini, FileAttributes.Normal); File.Delete(ini); }
                var di = new DirectoryInfo(dir);
                di.Attributes &= ~FileAttributes.ReadOnly;
                di.Delete();
                n++;
            }
            catch { }
        }
        IconStyler.SaveBackups();
        return n;
    }

    // ------------------------------------------------------------------ déplacements
    internal static List<MoveOp> Execute(List<MoveOp> ops)
    {
        var done = new List<MoveOp>();
        var needAdmin = new List<MoveOp>();
        foreach (var op in ops)
        {
            try { File.Move(op.From, op.To); done.Add(op); }
            catch (UnauthorizedAccessException) { needAdmin.Add(op); }
            catch (IOException) { needAdmin.Add(op); }
        }
        // Les raccourcis « pour tous les utilisateurs » (bureau Public) demandent les droits admin
        if (needAdmin.Count > 0)
        {
            var json = Path.Combine(Settings.DataDir, "deplacements.json");
            File.WriteAllText(json, JsonSerializer.Serialize(needAdmin));
            Elevated.Run($"--move \"{json}\"");
            done.AddRange(needAdmin.Where(op => !File.Exists(op.From) && File.Exists(op.To)));
        }
        return done;
    }

    /// <summary>Appelé par l'instance élevée (« CentrePC.exe --move fichier.json »).</summary>
    public static void RunElevatedMoves(string jsonPath)
    {
        try
        {
            var ops = JsonSerializer.Deserialize<List<MoveOp>>(File.ReadAllText(jsonPath));
            foreach (var op in ops)
            {
                // Fichier d'instructions modifiable sans droits : départ et arrivée doivent être des emplacements autorisés
                if (!Elevated.IsAllowedLocation(op.From) || !Elevated.IsAllowedLocation(op.To) || File.Exists(op.To))
                {
                    Log.Info($"Déplacement administrateur refusé : {op.From} → {op.To}");
                    continue;
                }
                try { File.Move(op.From, op.To); } catch (Exception ex) { Log.Error("Déplacement " + op.From, ex); }
            }
        }
        catch (Exception ex) { Log.Error("Déplacements administrateur", ex); }
    }

    public static string UniquePath(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
            dest = Path.Combine(dir, $"{stem} ({i}){ext}");
        return dest;
    }
}
