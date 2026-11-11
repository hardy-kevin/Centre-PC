using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Grand dossier de Windows (Documents, Images, Musique, Vidéos).</summary>
public class DocRoot
{
    public string Key { get; set; }
    public string Name { get; set; }
    public string Glyph { get; set; }
    public string Color { get; set; }
    public string Path { get; set; }
}

/// <summary>Dossier de l'arborescence de l'utilisateur.</summary>
public class DocFolder
{
    public string Path { get; set; }
    public DocRoot Root { get; set; }
    /// <summary>Chemin lisible depuis la racine : « Jeux › Dofus ». Vide pour la racine elle-même.</summary>
    public string Relative { get; set; }
    public string Name => string.IsNullOrEmpty(Relative) ? Root.Name : System.IO.Path.GetFileName(Path);
    public string Label => string.IsNullOrEmpty(Relative) ? Root.Name : $"{Root.Name} › {Relative}";
    public int Depth => string.IsNullOrEmpty(Relative) ? 0 : Relative.Split('›').Length;
    public int FileCount { get; set; }
}

/// <summary>Fichier ou dossier à ranger (sur le bureau, ou laissé dans l'ancien rangement caché de Centre PC).</summary>
public class DocItem
{
    public string Path { get; set; }
    public bool IsFolder { get; set; }
    /// <summary>« sur le bureau » ou « ancien rangement de Centre PC ».</summary>
    public string Origin { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Rangement des documents dans les dossiers habituels de Windows (Documents, Images, Musique, Vidéos)
/// et dans les sous-dossiers que l'utilisateur crée. Centre PC ne garde aucun fichier chez lui :
/// si l'application est désinstallée, tout reste à sa place, dans une arborescence normale.
/// </summary>
public static class DocumentLibrary
{
    public static List<DocRoot> Roots => new()
    {
        new() { Key = "documents", Name = "Documents", Glyph = "", Color = "#3B82F6", Path = Paths.Get(Environment.SpecialFolder.MyDocuments) },
        new() { Key = "images", Name = "Images", Glyph = "", Color = "#EC4899", Path = Paths.Get(Environment.SpecialFolder.MyPictures) },
        new() { Key = "musique", Name = "Musique", Glyph = "", Color = "#10B981", Path = Paths.Get(Environment.SpecialFolder.MyMusic) },
        new() { Key = "videos", Name = "Vidéos", Glyph = "", Color = "#F59E0B", Path = Paths.Get(Environment.SpecialFolder.MyVideos) },
        // Téléchargements : reçoit les programmes d'installation, archives et images disque du plan de rangement
        new() { Key = "telechargements", Name = "Téléchargements", Glyph = "\uE896", Color = "#8B93A7", Path = Paths.Downloads },
    };

    /// <summary>Arborescence proposée au premier lancement (l'utilisateur la modifie ensuite à sa guise).</summary>
    public static readonly (string root, string path)[] Starter =
    {
        ("documents", "Administratif"), ("documents", "Banque et factures"), ("documents", "Travail"),
        ("documents", "Jeux"), ("documents", "Personnel"), ("images", "Photos"), ("images", "Captures d'écran"),
    };

    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".svg", ".ico", ".raw", ".cr2", ".nef", ".psd" };
    private static readonly string[] AudioExt = { ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".aac", ".wma", ".opus", ".mid", ".midi" };
    private static readonly string[] VideoExt = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv" };

    /// <summary>Grand dossier conseillé d'après le type de fichier.</summary>
    public static string SuggestRoot(string path, bool isFolder)
    {
        if (isFolder) return "documents";
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ImageExt.Contains(ext) ? "images" : AudioExt.Contains(ext) ? "musique" : VideoExt.Contains(ext) ? "videos" : "documents";
    }

    public static string KindOf(string path, bool isFolder)
    {
        if (isFolder) return "Dossier";
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ImageExt.Contains(ext)) return "Image";
        if (AudioExt.Contains(ext)) return "Son";
        if (VideoExt.Contains(ext)) return "Vidéo";
        return ext switch
        {
            ".pdf" => "PDF",
            ".doc" or ".docx" or ".odt" or ".rtf" => "Texte",
            ".xls" or ".xlsx" or ".ods" or ".csv" => "Tableur",
            ".ppt" or ".pptx" or ".odp" => "Présentation",
            ".txt" or ".md" => "Note",
            ".zip" or ".rar" or ".7z" => "Archive",
            ".exe" or ".msi" => "Programme d'installation",
            _ => ext.Length > 1 ? ext.TrimStart('.').ToUpperInvariant() : "Fichier"
        };
    }

    // ------------------------------------------------------------------ arborescence
    /// <summary>Racines puis dossiers de l'utilisateur (ceux qui existent encore), dans l'ordre de l'arborescence.</summary>
    public static List<DocFolder> Folders(bool withCounts = false)
    {
        var roots = Roots;
        var res = new List<DocFolder>();
        foreach (var r in roots.Where(r => !string.IsNullOrEmpty(r.Path)))
        {
            res.Add(new DocFolder { Path = r.Path, Root = r, Relative = "", FileCount = withCounts ? CountFiles(r.Path) : 0 });
            foreach (var p in Settings.Current.DocFolders
                         .Where(p => IsUnder(p, r.Path) && Directory.Exists(p))
                         .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
            {
                var rel = System.IO.Path.GetRelativePath(r.Path, p).Replace("\\", " › ");
                res.Add(new DocFolder { Path = p, Root = r, Relative = rel, FileCount = withCounts ? CountFiles(p) : 0 });
            }
        }
        // Dossiers ajoutés ailleurs (autre disque…) : groupe « Autres emplacements »
        var other = new DocRoot { Key = "ailleurs", Name = "Autres emplacements", Glyph = "\uE8B7", Color = "#6B7280", Path = "" };
        foreach (var p in Settings.Current.DocFolders.Where(p => Directory.Exists(p) && !roots.Any(r => IsUnder(p, r.Path))))
            res.Add(new DocFolder { Path = p, Root = other, Relative = System.IO.Path.GetFileName(p.TrimEnd('\\')), FileCount = withCounts ? CountFiles(p) : 0 });
        return res;
    }

    private static int CountFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Count(f => !IsHiddenOrSystem(f)); } catch { return 0; }
    }

    public static bool IsUnder(string path, string root) =>
        !string.IsNullOrEmpty(root) && path.TrimEnd('\\').StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex BadName = new(@"[\\/:*?""<>|]");

    /// <summary>Nom de dossier valide pour Windows ? Renvoie un message d'erreur, ou null si c'est bon.</summary>
    public static string CheckName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Indiquez un nom de dossier.";
        if (BadName.IsMatch(name)) return "Un nom de dossier ne peut pas contenir \\ / : * ? \" < > |";
        if (name.EndsWith('.')) return "Un nom de dossier ne peut pas finir par un point.";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase)) return "Ce nom est réservé par Windows.";
        return null;
    }

    /// <summary>Crée un dossier (ou reprend celui qui existe déjà) et l'ajoute à l'arborescence.</summary>
    public static string CreateFolder(string parent, string name)
    {
        var path = System.IO.Path.Combine(parent, name.Trim());
        Directory.CreateDirectory(path);
        AddFolder(path);
        return path;
    }

    /// <summary>Ajoute un dossier existant (et ses parents jusqu'à la racine) à l'arborescence affichée.</summary>
    public static void AddFolder(string path)
    {
        var list = Settings.Current.DocFolders;
        var root = Roots.FirstOrDefault(r => IsUnder(path, r.Path));
        var p = path.TrimEnd('\\');
        while (p != null && (root == null || IsUnder(p, root.Path)))
        {
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
            if (root == null) break;
            p = System.IO.Path.GetDirectoryName(p);
        }
        Settings.Save();
    }

    public static void CreateStarter()
    {
        foreach (var (rootKey, rel) in Starter)
        {
            var root = Roots.First(r => r.Key == rootKey);
            if (!string.IsNullOrEmpty(root.Path)) CreateFolder(root.Path, rel);
        }
    }

    /// <summary>Renomme un dossier (sur le disque) et met à jour l'arborescence.</summary>
    public static string RenameFolder(string path, string newName)
    {
        var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), newName.Trim());
        Directory.Move(path, dest);
        var list = Settings.Current.DocFolders;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(path, StringComparison.OrdinalIgnoreCase)) list[i] = dest;
            else if (IsUnder(list[i], path)) list[i] = dest + list[i].Substring(path.TrimEnd('\\').Length);
        }
        Settings.Save();
        return dest;
    }

    /// <summary>
    /// Retire un dossier de l'arborescence de Centre PC. Il n'est supprimé du disque que s'il est vide :
    /// aucun document n'est jamais effacé.
    /// </summary>
    public static bool RemoveFolder(string path)
    {
        Settings.Current.DocFolders.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) || IsUnder(p, path));
        Settings.Save();
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) { Directory.Delete(path); return true; }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ à ranger
    /// <summary>Fichiers et dossiers du bureau (hors raccourcis, dossiers de thème, éléments à garder) et de l'ancien rangement.</summary>
    public static List<DocItem> Pending()
    {
        var res = new List<DocItem>();
        var keep = Settings.Current.KeepOnDesktop;
        var desk = AppScanner.UserDesktop;
        try
        {
            foreach (var f in Directory.GetFiles(desk))
            {
                if (IsHiddenOrSystem(f) || IsShortcut(f)) continue;
                if (keep.Any(k => k.Equals(System.IO.Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))) continue;
                res.Add(new DocItem { Path = f, Origin = "sur le bureau" });
            }
            foreach (var d in Directory.GetDirectories(desk))
            {
                if (IsHiddenOrSystem(d) || DesktopManager.IsThemeFolder(d)) continue;
                if (keep.Any(k => k.Equals(System.IO.Path.GetFileName(d), StringComparison.OrdinalIgnoreCase))) continue;
                res.Add(new DocItem { Path = d, IsFolder = true, Origin = "sur le bureau" });
            }
        }
        catch { }
        // Anciennes versions : documents rangés dans le dossier caché de Centre PC
        try
        {
            if (Directory.Exists(Settings.StoreDir))
                foreach (var f in Directory.GetFiles(Settings.StoreDir).Where(f => !IsHiddenOrSystem(f) && !IsShortcut(f)))
                    res.Add(new DocItem { Path = f, Origin = "ancien rangement de Centre PC (dossier caché)" });
        }
        catch { }
        return res;
    }

    private static bool IsShortcut(string f) =>
        System.IO.Path.GetExtension(f).ToLowerInvariant() is ".lnk" or ".url" or ".appref-ms";

    private static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            return (a & (FileAttributes.Hidden | FileAttributes.System)) != 0
                   || System.IO.Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>Range un fichier ou un dossier. Renvoie son nouveau chemin (un nom libre est choisi en cas de doublon).</summary>
    public static string Move(DocItem item, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var dest = DesktopManager.UniquePath(destDir, item.Name);
        if (item.IsFolder) MoveDirectory(item.Path, dest);
        else File.Move(item.Path, dest);
        ForgetStored(item.Path);
        Remember(dest);
        History.Record("document", item.Path, dest, item.IsFolder);
        return dest;
    }

    /// <summary>Annule un rangement : remet l'élément à sa place d'origine.</summary>
    public static void Undo(string movedTo, string originalPath, bool isFolder)
    {
        var dir = System.IO.Path.GetDirectoryName(originalPath);
        Directory.CreateDirectory(dir);
        var back = File.Exists(originalPath) || Directory.Exists(originalPath)
            ? DesktopManager.UniquePath(dir, System.IO.Path.GetFileName(originalPath)) : originalPath;
        if (isFolder) MoveDirectory(movedTo, back); else File.Move(movedTo, back);
        Settings.Current.DocRecent.RemoveAll(p => p.Equals(movedTo, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
    }

    /// <summary>Dossier : déplacement direct sur le même disque, sinon copie complète puis suppression de l'original.</summary>
    private static void MoveDirectory(string from, string to)
    {
        if (string.Equals(System.IO.Path.GetPathRoot(from), System.IO.Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }
        CopyDirectory(from, to);
        Directory.Delete(from, recursive: true);   // seulement une fois la copie complète réussie
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
    }

    /// <summary>Envoie un élément à la Corbeille de Windows (récupérable).</summary>
    public static void Recycle(DocItem item)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3,                              // FO_DELETE
            pFrom = item.Path + "\0\0",          // liste terminée par deux caractères nuls
            fFlags = 0x0040 | 0x0010 | 0x0400 | 0x0004   // ALLOWUNDO (Corbeille) | NOCONFIRMATION | NOERRORUI | SILENT
        };
        int r = SHFileOperation(ref op);
        if (r != 0 || op.fAnyOperationsAborted || File.Exists(item.Path) || Directory.Exists(item.Path))
            throw new IOException("impossible de l'envoyer à la Corbeille");
        ForgetStored(item.Path);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Un document qui quitte l'ancien rangement n'a plus d'« emplacement d'origine » à retenir.</summary>
    private static void ForgetStored(string path)
    {
        var cfg = Settings.Current;
        if (cfg.Stored.Remove(path) | cfg.Stored.Remove(System.IO.Path.GetFileName(path))) Settings.Save();
    }

    private static void Remember(string path)
    {
        var list = Settings.Current.DocRecent;
        list.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 20) list.RemoveRange(20, list.Count - 20);
        Settings.Save();
    }

    /// <summary>Derniers documents rangés qui existent encore.</summary>
    public static List<string> Recent() =>
        Settings.Current.DocRecent.Where(p => File.Exists(p) || Directory.Exists(p)).Take(12).ToList();

    /// <summary>Ouvre l'Explorateur sur un dossier, ou sur le dossier d'un fichier avec le fichier sélectionné.</summary>
    public static void ShowInExplorer(string path)
    {
        if (Directory.Exists(path)) Updates.Open("explorer.exe", $"\"{path}\"");
        else if (File.Exists(path)) Updates.Open("explorer.exe", $"/select,\"{path}\"");
    }
}
