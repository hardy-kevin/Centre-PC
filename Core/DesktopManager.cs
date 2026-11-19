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
        // Raccourcis seulement : les documents se rangent dans Documents, Images… (DocumentLibrary), jamais dans un dossier caché.
        // Bureau de l'utilisateur seulement : ceux du bureau Public (communs à tous les comptes) demandent les droits administrateur.
        foreach (var it in items.Where(i => i.Location == ItemLocation.Desktop && i.FilePath != null && i.IsShortcut))
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
            // Rangé depuis le bureau Public par une version précédente : remis sur le bureau de l'utilisateur
            // (même affichage, et aucun droit administrateur nécessaire)
            if (!Directory.Exists(dir) || IsThemeFolder(dir) || IsPublicDesktop(dir))
            { dir = AppScanner.UserDesktop; original = Path.Combine(dir, Path.GetFileName(it.FilePath)); }
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

    // ------------------------------------------------------------------ bureau Public (communs à tous les comptes)
    /// <summary>
    /// Ramène les raccourcis du bureau Public sur le bureau de l'utilisateur, où Centre PC peut ensuite les ranger sans droits.
    /// Le déplacement est fait par un script PowerShell lancé en administrateur (fenêtre d'autorisation de Windows) :
    /// Centre PC ne se relance jamais lui-même en administrateur. Le script est vérifié par empreinte, ne déplace que des
    /// raccourcis (.lnk, .url) posés directement sur le bureau Public, et seulement vers un dossier du profil d'un utilisateur.
    /// Renvoie le nombre de raccourcis déplacés, ou -1 si l'autorisation a été refusée.
    /// </summary>
    public static int MovePublicShortcutsToUser()
    {
        var pub = AppScanner.PublicDesktop;
        if (string.IsNullOrEmpty(pub) || !Directory.Exists(pub) || !IsPublicDesktop(pub)) return 0;
        int Count() { try { return Directory.EnumerateFiles(pub).Count(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)); } catch { return 0; } }
        int before = Count();
        if (before == 0) return 0;
        try
        {
            var script = Path.Combine(Settings.DataDir, "bureau-public.ps1");
            using var p = Updates.StartVerifiedElevated(script, PublicMoveScript, ("Source", pub), ("Dest", AppScanner.UserDesktop));
            if (p == null) return -1;
            if (!p.WaitForExit(60_000)) return 0;
            try { if (p.ExitCode != 0) Log.Info($"Bureau Public : le script s'est arrêté (code {p.ExitCode})"); } catch { }
        }
        catch (System.ComponentModel.Win32Exception) { return -1; }   // autorisation refusée
        catch (Exception ex) { Log.Error("Bureau Public", ex); return 0; }
        return Math.Max(0, before - Count());
    }

    private const string PublicMoveScript = @"
param([string]$Source, [string]$Dest)
$ErrorActionPreference = 'Stop'
# Départ : le bureau Public, et lui seul
$pub = [Environment]::GetFolderPath('CommonDesktopDirectory')
if (-not $pub -or $Source.TrimEnd('\') -ne $pub.TrimEnd('\')) { exit 2 }
# Arrivée : un dossier existant (pas un lien) dans le profil d'un utilisateur, hors bureau Public
$users = Split-Path $env:PUBLIC -Parent
$full = [IO.Path]::GetFullPath($Dest)
if (-not $full.StartsWith($users.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $full.StartsWith($env:PUBLIC.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { exit 3 }
$d = Get-Item -LiteralPath $full -Force
if (-not $d.PSIsContainer -or ($d.Attributes -band [IO.FileAttributes]::ReparsePoint)) { exit 3 }
foreach ($f in @(Get-ChildItem -LiteralPath $pub -File -Force | Where-Object {
        $_.Extension -in '.lnk', '.url' -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })) {
    $to = Join-Path $full $f.Name
    $i = 2
    while (Test-Path -LiteralPath $to) { $to = Join-Path $full (""{0} ({1}){2}"" -f $f.BaseName, $i, $f.Extension); $i++ }
    try { Move-Item -LiteralPath $f.FullName -Destination $to } catch { }
}
exit 0
";

    /// <summary>Le dossier est-il le bureau Public (raccourcis communs à tous les comptes du PC) ?</summary>
    public static bool IsPublicDesktop(string dir) =>
        !string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(AppScanner.PublicDesktop)
        && !string.Equals(AppScanner.PublicDesktop.TrimEnd('\\'), AppScanner.UserDesktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && string.Equals(dir.TrimEnd('\\'), AppScanner.PublicDesktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

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
    /// <summary>
    /// Déplace les fichiers, sans jamais demander les droits administrateur : un déplacement refusé par Windows
    /// (raccourci commun à tous les comptes, fichier ouvert…) est simplement laissé en place.
    /// </summary>
    internal static List<MoveOp> Execute(List<MoveOp> ops)
    {
        var done = new List<MoveOp>();
        foreach (var op in ops)
        {
            try { File.Move(op.From, op.To); done.Add(op); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log.Info($"Déplacement impossible : {op.From} ({ex.Message})"); }
        }
        return done;
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
