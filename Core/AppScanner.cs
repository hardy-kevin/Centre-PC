using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Inventaire des applications : menu Démarrer, Microsoft Store, bureau et bureau rangé.</summary>
public static class AppScanner
{
    // Désinstalleurs, documentations et petits utilitaires techniques qui encombrent la liste
    private static readonly Regex Junk = new(
        @"\b(uninstall\w*|unins\d+|d[ée]sinstall\w*|readme|lisez-?moi|release notes|notes de version|installation notes|documentation|manuel|help|aide|site web|website|license|licence|changelog|faqs?|error report\w*|bug report\w*|crash report\w*|crash handler|updater|outil de mise à jour|support center|app recovery|repair tool|safe mode|mode sans échec|troubleshooter|bluestacks services|crash ?reporter)\b",
        RegexOptions.IgnoreCase);

    private static readonly string[] ShortcutExt = { ".lnk", ".url", ".appref-ms", ".exe" };

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>Tout ce qui est sur le bureau ou rangé par Centre PC (raccourcis ET fichiers).</summary>
    public static List<AppItem> ScanDesktop()
    {
        var list = new List<AppItem>();
        void Add(string dir, ItemLocation loc)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in SafeFiles(dir))
            {
                var fi = new FileInfo(f);
                if (fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if ((fi.Attributes & FileAttributes.Hidden) != 0) continue;
                var isShortcut = ShortcutExt.Contains(fi.Extension.ToLowerInvariant());
                var name = isShortcut ? Path.GetFileNameWithoutExtension(f) : fi.Name;
                var target = isShortcut ? Shortcuts.GetTarget(f) : f;
                var (ctx, game) = isShortcut ? Analyze(f) : ("", false);
                list.Add(new AppItem
                {
                    Name = name, FilePath = f, Target = target, IsShortcut = isShortcut, Location = loc,
                    Context = ctx, LooksLikeGame = game,
                    Theme = isShortcut ? Settings.Classify(name, target, ctx, game) : Settings.FilesCategory,
                    Keep = Settings.Current.KeepOnDesktop.Contains(name, StringComparer.OrdinalIgnoreCase)
                });
            }
        }
        Add(UserDesktop, ItemLocation.Desktop);
        if (!string.Equals(PublicDesktop, UserDesktop, StringComparison.OrdinalIgnoreCase)) Add(PublicDesktop, ItemLocation.PublicDesktop);
        Add(Settings.StoreDir, ItemLocation.Stored);
        // Raccourcis rangés dans les dossiers de thème du bureau (mode « dossiers »)
        foreach (var dir in DesktopManager.ThemeFolders()) Add(dir, ItemLocation.Stored);
        return list;
    }

    /// <summary>Toutes les applications lançables (sans doublons, sans désinstalleurs).</summary>
    public static async Task<List<AppItem>> ScanAllAsync(List<AppItem> desktopItems)
    {
        var apps = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        // 1) Bureau + bureau rangé (raccourcis uniquement)
        foreach (var d in desktopItems.Where(i => i.IsShortcut))
            if (!Junk.IsMatch(d.Name)) apps.TryAdd(d.Name, d);

        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in apps.Values) { var k = (d.Target ?? "").Trim(); if (k.Length > 3) seenTargets.Add(k); }

        // 2) Menu Démarrer (fichiers .lnk)
        var startDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };
        await Task.Run(() =>
        {
            foreach (var dir in startDirs.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
                catch { continue; }
                foreach (var f in files)
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url" && ext != ".appref-ms") continue;
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (Junk.IsMatch(name) || apps.ContainsKey(name)) continue;
                    var target = Shortcuts.GetTarget(f);
                    if (Junk.IsMatch(target ?? "")) continue;
                    // Deux raccourcis vers le même programme (ex. « Riot Client » / « Client Riot ») : un seul est gardé
                    var key = (target ?? "").Trim();
                    if (key.Length > 3 && !key.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !seenTargets.Add(key)) continue;
                    var (ctx, game) = Analyze(f, dir);
                    apps[name] = new AppItem
                    {
                        Name = name, FilePath = f, Target = target, Location = ItemLocation.StartMenu,
                        Context = ctx, LooksLikeGame = game,
                        Theme = Settings.Classify(name, target, ctx, game)
                    };
                }
            }
        });

        // 3) Applications du Store et autres entrées du dossier « Applications » de Windows
        try
        {
            var shellApps = await Native.RunSta(() =>
            {
                var result = new List<(string name, string id)>();
                var t = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(t);
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                dynamic items = folder.Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    try { dynamic it = items.Item(i); result.Add(((string)it.Name, (string)it.Path)); } catch { }
                }
                return result;
            });
            foreach (var (name, id) in shellApps)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (Junk.IsMatch(name) || apps.ContainsKey(name)) continue;
                // Les entrées qui sont des URL ou des fichiers déjà couverts sont ignorées
                if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                apps[name] = new AppItem
                {
                    Name = name, Aumid = id, Target = id, Location = ItemLocation.Store,
                    Theme = Settings.Classify(name, id, id)
                };
            }
        }
        catch { /* pas bloquant */ }

        // Centre PC lui-même et ses anciens scripts (Mise à jour PC, Ranger le bureau…) n'apparaissent pas dans la liste
        return apps.Values.Where(a => !IsOwnApp(a)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsOwnApp(AppItem a)
    {
        if (a.Name.Equals("Centre PC", StringComparison.OrdinalIgnoreCase)) return true;
        var t = a.Target ?? "";
        return t.Contains("CentrePC.exe", StringComparison.OrdinalIgnoreCase)
            || t.Contains(@"\RangeBureau\", StringComparison.OrdinalIgnoreCase)
            || t.Contains(@"\MiseAJourPC\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Indices supplémentaires pour le tri : dossier du menu Démarrer (« PostgreSQL 16 », « Outils d'administration »…),
    /// éditeur et description de l'exécutable, et détection d'un jeu d'après les fichiers de son moteur.
    /// </summary>
    public static (string context, bool game) Analyze(string file, string startRoot = null)
    {
        var ctx = new List<string>();
        bool game = false;
        try
        {
            if (startRoot != null)
            {
                var rel = Path.GetDirectoryName(Path.GetRelativePath(startRoot, file));
                if (!string.IsNullOrEmpty(rel) && rel != ".") ctx.Add(rel);
            }
            string exe = null;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".lnk") exe = Shortcuts.GetTargetPath(file);
            else if (ext == ".exe") exe = file;
            if (!string.IsNullOrEmpty(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                foreach (var s in new[] { vi.CompanyName, vi.ProductName, vi.FileDescription })
                    if (!string.IsNullOrWhiteSpace(s)) ctx.Add(s);
                game = LooksLikeGame(exe);
            }
        }
        catch { }
        return (string.Join(" | ", ctx), game);
    }

    private static readonly Regex GameFolders = new(
        @"\\(steamapps\\common|SteamLibrary|XboxGames|Epic Games|GOG Games|GOG Galaxy\\Games|Riot Games|Ubisoft Game Launcher\\games|EA Games|Rockstar Games|Games)\\",
        RegexOptions.IgnoreCase);
    private static readonly string[] GameEngineFiles =
    {
        "UnityPlayer.dll", "GameAssembly.dll", "steam_api64.dll", "steam_api.dll", "EOSSDK-Win64-Shipping.dll",
        "Galaxy64.dll", "bink2w64.dll", "fmod.dll", "fmodstudio.dll", "PhysX3_x64.dll", "PhysX_64.dll",
        "nvngx_dlss.dll", "amd_fidelityfx_dx12.dll", "libxess.dll", "discord_game_sdk.dll"
    };

    /// <summary>Un exécutable est un jeu s'il est dans une bibliothèque de jeux ou s'il embarque un moteur de jeu.</summary>
    public static bool LooksLikeGame(string exe)
    {
        try
        {
            // Les outils de Windows ne sont jamais des jeux (System32 contient des fichiers de pilotes graphiques)
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win) && exe.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (GameFolders.IsMatch(exe)) return true;
            var name = Path.GetFileNameWithoutExtension(exe);
            if (name.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase))
                return true;
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return false;
            // Les logiciels « classiques » (Program Files sans moteur de jeu) ne sont pas concernés
            if (GameEngineFiles.Any(f => File.Exists(Path.Combine(dir, f)))) return true;
            return Directory.Exists(Path.Combine(dir, name + "_Data"));
        }
        catch { return false; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); }
    }
}

/// <summary>Lecture de la cible d'un raccourci.</summary>
public static class Shortcuts
{
    [ThreadStatic] private static object _wsh;

    private static dynamic Wsh()
    {
        _wsh ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        return _wsh;
    }

    public static string GetIconLocation(string lnk)
    {
        try { dynamic l = Wsh().CreateShortcut(lnk); string loc = l.IconLocation; return loc == ",0" ? "" : loc; }
        catch { return ""; }
    }

    public static string GetTargetPath(string lnk)
    {
        try { dynamic l = Wsh().CreateShortcut(lnk); return (string)l.TargetPath; }
        catch { return ""; }
    }

    /// <summary>Change l'icône d'un raccourci (lève une exception si l'accès est refusé).</summary>
    public static void SetIconLocation(string lnk, string location)
    {
        dynamic l = Wsh().CreateShortcut(lnk);
        l.IconLocation = string.IsNullOrEmpty(location) ? ",0" : location;
        l.Save();
    }

    public static string GetTarget(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".url")
            {
                foreach (var line in File.ReadLines(path))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line.Substring(4);
                return "";
            }
            if (ext == ".lnk")
            {
                _wsh ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic wsh = _wsh;
                dynamic l = wsh.CreateShortcut(path);
                return $"{l.TargetPath} {l.Arguments}";
            }
            return path;
        }
        catch { return ""; }
    }
}
