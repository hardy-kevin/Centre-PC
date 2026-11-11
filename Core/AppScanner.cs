using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>
/// Inventaire des applications : menu Démarrer, Microsoft Store, bureau et bureau rangé.
/// Rien n'est caché : les éléments annexes (désinstalleur, aide, contenu bonus, bande-son…) sont marqués
/// « compagnons » et rangés avec l'application à laquelle ils appartiennent (voir AppSuites).
/// </summary>
public static class AppScanner
{
    /// <summary>Éléments annexes d'une application : rangés avec elle plutôt que présentés comme une application à part.</summary>
    internal static readonly Regex Companion = new(
        @"^(install(?:er)?|setup)\b|\b(uninstall\w*|unins\d+|d[ée]sinstall\w*|readme|lisez-?moi|release notes|notes de version|installation notes|documentation|manuel|"
        + @"user guide|guide de l.utilisateur|help|aide|site web|website|license|licence|changelog|faqs?|error report\w*|bug report\w*|"
        + @"crash report\w*|crash handler|crash ?reporter|updater|outil de mise à jour|support center|app recovery|repair tool|safe mode|"
        + @"mode sans échec|troubleshooter|bonus content|contenu bonus|digital extras|soundtrack|bande[- ]originale|bande[- ]son|art ?book|"
        + @"wallpapers? pack|redeem\w*|benchmark tool|settings tool|configuration tool|config tool|reload configuration|htdocs folder|support center)\b",
        RegexOptions.IgnoreCase);

    internal static bool IsCompanionName(string name, string target = null) =>
        Companion.IsMatch(name ?? "") || (!string.IsNullOrEmpty(target) && Companion.IsMatch(SafeFileName(target)));

    private static string SafeFileName(string target)
    {
        try { return Path.GetFileNameWithoutExtension(target.Trim().Trim('"').Split('"')[0]); } catch { return ""; }
    }

    private static readonly string[] ShortcutExt = { ".lnk", ".url", ".appref-ms", ".exe" };

    public static string UserDesktop => Paths.Get(Environment.SpecialFolder.DesktopDirectory);
    public static string PublicDesktop => Paths.Get(Environment.SpecialFolder.CommonDesktopDirectory);

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
                var item = new AppItem
                {
                    Name = name, FilePath = f, Target = target, IsShortcut = isShortcut, Location = loc,
                    IsCompanion = isShortcut && IsCompanionName(name, target),
                    Keep = Settings.Current.KeepOnDesktop.Contains(name, StringComparer.OrdinalIgnoreCase)
                };
                if (isShortcut) Describe(item, f);
                item.Theme = isShortcut ? Settings.Classify(name, target, item.Context, item.LooksLikeGame) : Settings.FilesCategory;
                list.Add(item);
            }
        }
        Add(UserDesktop, ItemLocation.Desktop);
        if (!string.Equals(PublicDesktop, UserDesktop, StringComparison.OrdinalIgnoreCase)) Add(PublicDesktop, ItemLocation.PublicDesktop);
        Add(Settings.StoreDir, ItemLocation.Stored);
        // Raccourcis rangés dans les dossiers de thème du bureau (mode « dossiers »)
        foreach (var dir in DesktopManager.ThemeFolders()) Add(dir, ItemLocation.Stored);
        return list;
    }

    /// <summary>Toutes les applications lançables (sans doublons). Les éléments annexes sont gardés et marqués.</summary>
    public static async Task<List<AppItem>> ScanAllAsync(List<AppItem> desktopItems)
    {
        var apps = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        // 1) Bureau + bureau rangé (raccourcis uniquement)
        // (les raccourcis d'applis désinstallées ne sont pas des applis lançables)
        // Deux raccourcis du bureau vers le même programme (« Riot Client » / « Client Riot ») : une seule application
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in desktopItems.Where(i => i.IsShortcut))
        {
            if (ShortcutGuard.IsBrokenShortcut(d.FilePath)) continue;
            var k = (d.Target ?? "").Trim();
            if (k.Length > 3 && !k.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !seenTargets.Add(k)) continue;
            apps.TryAdd(d.Name, d);
        }

        // 2) Menu Démarrer (fichiers .lnk)
        var startDirs = new[]
        {
            Paths.Get(Environment.SpecialFolder.CommonPrograms),
            Paths.Get(Environment.SpecialFolder.Programs)
        };
        await Task.Run(() =>
        {
            foreach (var dir in startDirs.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                // AttributesToSkip = 0 : un dossier personnalisé (desktop.ini) porte souvent l'attribut Système ; il doit être lu
                try { files = Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }); }
                catch { continue; }
                foreach (var f in files)
                {
                    // Un raccourci illisible ne doit pas empêcher l'inventaire des autres
                    try
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".lnk" && ext != ".url" && ext != ".appref-ms") continue;
                        // Nom affiché par Windows dans la langue de l'utilisateur (« File Explorer.lnk » → « Explorateur de fichiers »)
                        var name = DisplayName(f);
                        if (apps.ContainsKey(name)) continue;
                        var target = Shortcuts.GetTarget(f);
                        if (ShortcutGuard.IsBrokenShortcut(f)) continue;
                        // Deux raccourcis vers le même programme (ex. « Riot Client » / « Client Riot ») : un seul est gardé
                        var key = (target ?? "").Trim();
                        if (key.Length > 3 && !key.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !seenTargets.Add(key)) continue;
                        var rel = Path.GetDirectoryName(Path.GetRelativePath(dir, f));
                        var item = new AppItem
                        {
                            Name = name, FilePath = f, Target = target, Location = ItemLocation.StartMenu,
                            StartFolder = string.IsNullOrEmpty(rel) || rel == "." ? null : rel,
                            IsCompanion = IsCompanionName(name, target)
                        };
                        Describe(item, f, dir);
                        item.Theme = Settings.Classify(name, target, item.Context, item.LooksLikeGame);
                        apps[name] = item;
                    }
                    catch (Exception ex) { Log.Error("Menu Démarrer : " + f, ex); }
                }
            }
        });

        // 3) Applications du Store et autres entrées du dossier « Applications » de Windows
        try
        {
            var shellApps = await Native.RunSta(() =>
            {
                var result = new List<(string name, string id, string link)>();
                var t = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(t);
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                dynamic items = folder.Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic it = items.Item(i);
                        // Programme visé par une entrée déclarée par son identifiant Windows (« TheDocumentFoundation.LibreOffice.Writer »)
                        string link = null;
                        try { link = it.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                        result.Add(((string)it.Name, (string)it.Path, link));
                    }
                    catch { }
                }
                return result;
            });
            // Programmes du menu Démarrer par chemin : la même application listée sous un autre nom (autre langue) n'est pas ajoutée deux fois
            var byPath = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in apps.Values.Where(a => a.FilePath != null && a.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !AppKinds.IsHosted(a.Target)))
            {
                var p = NormalizePath(Shortcuts.GetTargetPath(a.FilePath));
                if (p != null) byPath.TryAdd(p, a);
            }
            await Task.Run(() =>
            {
                foreach (var (name, id, link) in shellApps)
                {
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    if (apps.ContainsKey(name)) continue;
                    // Les entrées qui sont des URL ou des fichiers déjà couverts sont ignorées
                    if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = NormalizePath(ResolveAppsFolderPath(id)) ?? NormalizePath(link);
                    if (path != null && byPath.TryGetValue(path, out var same))
                    {
                        // Windows affiche ce nom dans le menu Démarrer : c'est celui de la langue de l'utilisateur
                        // (un raccourci du bureau garde le nom que l'utilisateur lui a donné)
                        if (same.Location == ItemLocation.StartMenu && !same.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) && !apps.ContainsKey(name))
                        {
                            apps.Remove(same.Name);
                            same.Name = name;
                            same.Theme = Settings.Classify(name, same.Target, same.Context, same.LooksLikeGame);
                            apps[name] = same;
                        }
                        continue;
                    }
                    var item = new AppItem
                    {
                        Name = name, Aumid = id, Target = path != null && link != null ? link : id, Location = ItemLocation.Store,
                        IsCompanion = IsCompanionName(name)
                    };
                    if (path != null && File.Exists(path)) Describe(item, path);
                    else DescribeStoreApp(item);
                    item.Context = string.IsNullOrEmpty(item.Context) ? id : id + " | " + item.Context;
                    item.Theme = Settings.Classify(name, id, item.Context, item.LooksLikeGame);
                    apps[name] = item;
                }
            });
        }
        catch { /* pas bloquant */ }

        // Centre PC lui-même et ses anciens scripts (Mise à jour PC, Ranger le bureau…) n'apparaissent pas dans la liste
        var list = apps.Values.Where(a => !IsOwnApp(a)).ToList();
        // Même outil présent en français et en anglais (« Narrateur » / « Narrator ») : celui de la langue de Windows est gardé
        list = MergeLanguageDuplicates(list, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        // Applications encore sans catégorie : déduite des applications voisines (même suite, dossier, éditeur)
        Affinity.Compute(list);
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ noms localisés et doublons de langue
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> LocalizedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Nom affiché d'un raccourci : Windows traduit certains raccourcis via le fichier caché desktop.ini du dossier
    /// (section [LocalizedFileNames]) ; sans traduction, c'est le nom du fichier.
    /// </summary>
    public static string DisplayName(string file)
    {
        var plain = Path.GetFileNameWithoutExtension(file);
        try
        {
            var dir = Path.GetDirectoryName(file);
            var map = LocalizedCache.GetOrAdd(dir, d => ReadLocalizedNames(Path.Combine(d, "desktop.ini")));
            if (map.TryGetValue(Path.GetFileName(file), out var loc) && !string.IsNullOrWhiteSpace(loc)) return loc.Trim();
        }
        catch { }
        return plain;
    }

    /// <summary>Lit la section [LocalizedFileNames] d'un desktop.ini (valeurs « @dll,-id » traduites par Windows).</summary>
    internal static Dictionary<string, string> ReadLocalizedNames(string ini)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(ini)) return map;
        bool inSection = false;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith("[")) { inSection = line.Equals("[LocalizedFileNames]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection || line.Length == 0 || line[0] == ';') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();
            if (val.StartsWith("@")) val = LoadIndirect(val);
            if (!string.IsNullOrWhiteSpace(val)) map[key] = val;
        }
        return map;
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, int size, IntPtr reserved);

    private static string LoadIndirect(string value)
    {
        try
        {
            var sb = new System.Text.StringBuilder(512);
            return SHLoadIndirectString(Environment.ExpandEnvironmentVariables(value), sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
                                                   uint flags, IntPtr token, out IntPtr path);

    /// <summary>« {1AC14E77-…}\narrator.exe » (dossier connu + chemin) → C:\Windows\System32\narrator.exe. Sinon null.</summary>
    internal static string ResolveAppsFolderPath(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (id.Length > 3 && id[1] == ':' && id[2] == '\\') return id;
        if (id.Length < 40 || id[0] != '{' || id[37] != '}' || !Guid.TryParse(id.Substring(0, 38), out var g)) return null;
        try
        {
            if (SHGetKnownFolderPath(g, 0, IntPtr.Zero, out var p) != 0) return null;
            var root = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
            return Path.Combine(root, id.Substring(38).TrimStart('\\'));
        }
        catch { return null; }
    }

    internal static string NormalizePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            if (p.Length < 4 || p[1] != ':') return null;
            var full = Path.GetFullPath(p);
            // Les programmes 32 bits de Windows (SysWOW64) sont le même outil que ceux de System32
            return full.Replace(@"\SysWOW64\", @"\System32\", StringComparison.OrdinalIgnoreCase)
                       .Replace(@"\Sysnative\", @"\System32\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    /// <summary>Noms français et anglais des outils de Windows (quand Windows propose les deux).</summary>
    internal static readonly (string fr, string en)[] LanguagePairs =
    {
        ("Explorateur de fichiers", "File Explorer"), ("Narrateur", "Narrator"), ("Exécuter", "Run"), ("Loupe", "Magnifier"),
        ("Panneau de configuration", "Control Panel"), ("Gestionnaire des tâches", "Task Manager"), ("Clavier visuel", "On-Screen Keyboard"),
        ("Invite de commandes", "Command Prompt"), ("Bloc-notes", "Notepad"), ("Paramètres", "Settings"), ("Outils Windows", "Windows Tools"),
        ("Calculatrice", "Calculator"), ("Table des caractères", "Character Map"), ("Observateur d'événements", "Event Viewer"),
        ("Éditeur du Registre", "Registry Editor"), ("Moniteur de ressources", "Resource Monitor"), ("Informations système", "System Information"),
        ("Configuration du système", "System Configuration"), ("Nettoyage de disque", "Disk Cleanup"), ("Assistance rapide", "Quick Assist"),
        ("Connexion Bureau à distance", "Remote Desktop Connection"), ("Outil Capture d'écran", "Snipping Tool"), ("Enregistreur d'actions", "Steps Recorder"),
        ("Planificateur de tâches", "Task Scheduler"), ("Gestion de l'ordinateur", "Computer Management"), ("Services de composants", "Component Services"),
        ("Sécurité Windows", "Windows Security"), ("Pense-bête", "Sticky Notes"), ("Cartes", "Maps"), ("Météo", "Weather"), ("Horloge", "Clock"),
        ("Calendrier", "Calendar"), ("Courrier", "Mail"), ("Enregistreur audio", "Sound Recorder"), ("Lecteur multimédia", "Media Player"),
        ("Analyseur de performances", "Performance Monitor"), ("Optimiser les lecteurs", "Defragment and Optimize Drives"),
        ("Diagnostic de la mémoire Windows", "Windows Memory Diagnostic"), ("Sauvegarde Windows", "Windows Backup"),
        ("Lecteur de récupération", "Recovery Drive"), ("Sous-titres en direct", "Live Captions"), ("Accès vocal", "Voice Access"),
        ("Hub de commentaires", "Feedback Hub"), ("Obtenir de l'aide", "Get Help"), ("Conseils", "Tips"), ("Prise en main", "Get Started"),
        ("Télécopie et numérisation Windows", "Windows Fax and Scan"), ("Reconnaissance vocale Windows", "Windows Speech Recognition"),
    };

    /// <summary>
    /// Retire le doublon de langue d'un outil de Windows : le nom dans la langue de l'utilisateur est gardé.
    /// Un élément du bureau (choisi par l'utilisateur) n'est jamais retiré.
    /// </summary>
    internal static List<AppItem> MergeLanguageDuplicates(List<AppItem> apps, string uiLanguage)
    {
        bool french = string.Equals(uiLanguage, "fr", StringComparison.OrdinalIgnoreCase);
        var byName = apps.GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCultureIgnoreCase);
        var drop = new HashSet<AppItem>();
        foreach (var (fr, en) in LanguagePairs)
        {
            if (!byName.TryGetValue(fr, out var f) || !byName.TryGetValue(en, out var e)) continue;
            var (keep, other) = french ? (f, e) : (e, f);
            bool UserItem(AppItem x) => x.Location is ItemLocation.Desktop or ItemLocation.PublicDesktop or ItemLocation.Stored;
            if (!UserItem(other)) drop.Add(other);
            else if (!UserItem(keep)) drop.Add(keep);
        }
        return drop.Count == 0 ? apps : apps.Where(a => !drop.Contains(a)).ToList();
    }

    public static bool IsOwnApp(AppItem a)
    {
        if (a.Name.Equals("Centre PC", StringComparison.OrdinalIgnoreCase)) return true;
        var t = a.Target ?? "";
        return t.Contains("CentrePC.exe", StringComparison.OrdinalIgnoreCase)
            || t.Contains(@"\RangeBureau\", StringComparison.OrdinalIgnoreCase)
            || t.Contains(@"\MiseAJourPC\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ce que l'application dit d'elle-même : texte d'indices, jeu ou non, éditeur, dossier d'installation, site.</summary>
    public class AppMeta
    {
        public string Context { get; set; } = "";
        public bool Game { get; set; }
        public string Publisher { get; set; }
        public string InstallRoot { get; set; }
        public string Homepage { get; set; }
    }

    /// <summary>
    /// Indices pour le tri, lus sur le PC (aucune connexion) :
    /// dossier du menu Démarrer ; éditeur, nom du produit, description et commentaires de l'exécutable ;
    /// fiche du programme dans « Applications installées » (éditeur, commentaires, site) ;
    /// description et mots-clés des applications Electron (package.json) ; détection d'un jeu (moteur, bibliothèque).
    /// </summary>
    public static AppMeta Inspect(string file, string startRoot = null, string name = null)
    {
        var meta = new AppMeta();
        var ctx = new List<string>();
        try
        {
            if (startRoot != null)
            {
                var rel = Path.GetDirectoryName(Path.GetRelativePath(startRoot, file));
                if (!string.IsNullOrEmpty(rel) && rel != ".") ctx.Add(rel);
            }
            string exe = null;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            // Application hébergée (Android dans un émulateur, application web) : rien n'est repris de l'hôte
            if (ext == ".lnk")
            {
                var full = Shortcuts.GetTarget(file);
                var pkg = AppKinds.AndroidPackage(full);
                if (pkg != null || AppKinds.IsWebApp(full))
                {
                    ctx.Add(pkg != null ? "Application Android " + pkg : "Application web");
                    meta.Context = string.Join(" | ", ctx);
                    return meta;
                }
            }
            if (ext == ".lnk") exe = Shortcuts.GetTargetPath(file);
            else if (ext == ".exe") exe = file;
            exe = NormalizePath(exe);
            // Jeu Steam (« steam://rungameid/1091500 ») : sa fiche d'installation précise (éditeur, dossier)
            if (ext == ".url")
            {
                var url = Shortcuts.GetTarget(file);
                var steam = Regex.Match(url ?? "", @"steam://rungameid/(\d+)", RegexOptions.IgnoreCase);
                if (steam.Success)
                {
                    var e = AppUninstaller.Installed().FirstOrDefault(x => x.KeyName.Equals("Steam App " + steam.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                    if (e != null)
                    {
                        foreach (var s in new[] { e.DisplayName, e.Publisher }) if (!string.IsNullOrWhiteSpace(s)) ctx.Add(s.Trim());
                        meta.Publisher = Clean(e.Publisher);
                        meta.InstallRoot = InstallRootOf(e.InstallLocation);
                        meta.Game = true;
                    }
                    meta.Context = string.Join(" | ", ctx);
                    return meta;
                }
            }
            // Programme hôte de Windows (cmd, PowerShell, msiexec, rundll32…) : c'est le fichier passé en argument qui compte
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (exe != null && !string.IsNullOrEmpty(win) && exe.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && ext == ".lnk")
            {
                var args = Shortcuts.GetTarget(file).Substring(Math.Min(Shortcuts.GetTarget(file).Length, Shortcuts.GetTargetPath(file).Length));
                var inner = Regex.Matches(args, @"""([A-Za-z]:\\[^""]+)""|([A-Za-z]:\\[^\s""]+)").Select(m => NormalizePath(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value))
                                 .FirstOrDefault(p => p != null && !p.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && (File.Exists(p) || Directory.Exists(p)));
                if (inner != null)
                {
                    meta.InstallRoot = InstallRootOf(inner);
                    var arpHost = AppUninstaller.Match(AppUninstaller.Installed(), inner, name ?? Path.GetFileNameWithoutExtension(file), byNameToo: false);
                    if (arpHost != null)
                    {
                        foreach (var s in new[] { arpHost.DisplayName, arpHost.Publisher, arpHost.Comments })
                            if (!string.IsNullOrWhiteSpace(s) && !ctx.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase)) ctx.Add(s.Trim());
                        meta.Publisher = Clean(arpHost.Publisher);
                        meta.Homepage = arpHost.UrlInfoAbout ?? arpHost.HelpLink;
                    }
                    meta.Context = string.Join(" | ", ctx);
                    return meta;
                }
            }
            if (exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                foreach (var s in new[] { vi.CompanyName, vi.ProductName, vi.FileDescription, vi.Comments })
                    if (!string.IsNullOrWhiteSpace(s) && !ctx.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase)) ctx.Add(s.Trim());
                meta.Publisher = Clean(vi.CompanyName);
                meta.Game = LooksLikeGame(exe);
                meta.InstallRoot = InstallRootOf(exe);
                var pkg = ReadPackageJson(Path.GetDirectoryName(exe));
                if (pkg.text != null) ctx.Add(pkg.text);
                meta.Homepage ??= pkg.homepage;
                meta.Publisher ??= Clean(pkg.author);
            }
            // Fiche du programme installé (désinstallation) : éditeur, commentaires, site de l'éditeur
            var arp = AppUninstaller.Match(AppUninstaller.Installed(), exe, name ?? Path.GetFileNameWithoutExtension(file), byNameToo: exe == null);
            if (arp != null)
            {
                foreach (var s in new[] { arp.DisplayName, arp.Publisher, arp.Comments })
                    if (!string.IsNullOrWhiteSpace(s) && !ctx.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase)) ctx.Add(s.Trim());
                meta.Publisher ??= Clean(arp.Publisher);
                meta.Homepage ??= arp.UrlInfoAbout ?? arp.HelpLink;
                meta.InstallRoot ??= InstallRootOf(arp.InstallLocation);
            }
        }
        catch { }
        meta.Context = string.Join(" | ", ctx);
        return meta;
    }

    private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Remplit les indices d'une application à partir de son raccourci ou de son exécutable.</summary>
    internal static void Describe(AppItem item, string file, string startRoot = null)
    {
        var m = Inspect(file, startRoot, item.Name);
        item.Context = m.Context;
        item.LooksLikeGame = m.Game;
        item.Publisher = m.Publisher;
        item.InstallRoot = m.InstallRoot;
        item.Homepage = m.Homepage;
    }

    /// <summary>Compatibilité : indices et détection de jeu seulement.</summary>
    public static (string context, bool game) Analyze(string file, string startRoot = null)
    {
        var m = Inspect(file, startRoot);
        return (m.Context, m.Game);
    }

    /// <summary>Dossiers qui contiennent plusieurs programmes : le dossier d'un programme est le niveau juste en dessous.</summary>
    private static readonly Lazy<List<string>> Containers = new(() =>
    {
        var list = new List<string>();
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                  Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.CommonProgramFiles,
                                  Environment.SpecialFolder.CommonProgramFilesX86 })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) list.Add(p.TrimEnd('\\'));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) list.Add(Path.Combine(local, "Programs"));
        // Les plus longs d'abord : « AppData\Local\Programs » avant « AppData\Local »
        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length).ToList();
    });

    private static readonly Regex LibraryFolder = new(
        @"^(.*?\\(?:steamapps\\common|SteamLibrary\\steamapps\\common|XboxGames|Epic Games|GOG Games|GOG Galaxy\\Games|Riot Games|Ubisoft Game Launcher\\games|EA Games|Games|Jeux))\\[^\\]+",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Dossier d'installation d'un programme : « C:\Program Files\Git\bin\git.exe » → « C:\Program Files\Git ».
    /// Null si le chemin n'est pas dans un dossier de programmes connu (pour ne jamais rapprocher des programmes sans lien).
    /// </summary>
    internal static string InstallRootOf(string path)
    {
        var p = NormalizePath(path);
        if (p == null) return null;
        p = p.TrimEnd('\\');
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(win) && p.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var lib = LibraryFolder.Match(p);
        if (lib.Success) return lib.Value;
        foreach (var c in Containers.Value)
        {
            if (!p.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = p.Substring(c.Length + 1);
            var first = rest.Split('\\')[0];
            // « Common Files\Adobe », paquets du Store (WindowsApps\…) et autres dossiers partagés : un niveau de plus
            if (rest.Contains('\\') && (first.Equals("Common Files", StringComparison.OrdinalIgnoreCase) || first.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                                        || first.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = rest.Split('\\');
                // Alias d'exécution du Store (…\Microsoft\WindowsApps\x.exe) : aucun lien entre eux
                if (parts.Length > 1 && parts[1].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return null;
                return parts.Length > 2 ? Path.Combine(c, parts[0], parts[1]) : null;
            }
            return rest.Contains('\\') || Path.GetExtension(rest) == "" ? Path.Combine(c, first) : null;
        }
        return null;
    }

    /// <summary>
    /// Applications Electron (Discord, outils de passionnés…) : le package.json embarqué contient une description,
    /// des mots-clés, l'auteur et souvent le site. Lu dans resources\app\package.json ou dans l'archive resources\app.asar.
    /// </summary>
    internal static (string text, string homepage, string author) ReadPackageJson(string exeDir)
    {
        try
        {
            if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return default;
            var dirs = new List<string> { exeDir };
            // Squirrel (Update.exe à la racine) : la version installée est dans app-x.y.z
            dirs.AddRange(Directory.GetDirectories(exeDir, "app-*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).Take(1));
            foreach (var d in dirs)
            {
                string json = null;
                var plain = Path.Combine(d, "resources", "app", "package.json");
                var asar = Path.Combine(d, "resources", "app.asar");
                if (File.Exists(plain)) json = File.ReadAllText(plain);
                else if (File.Exists(asar)) json = PackageJsonFromAsar(asar);
                if (json == null) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var r = doc.RootElement;
                string S(string prop) => r.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                var parts = new List<string>();
                foreach (var prop in new[] { "productName", "description" }) { var v = S(prop); if (!string.IsNullOrWhiteSpace(v)) parts.Add(v); }
                if (r.TryGetProperty("keywords", out var kw) && kw.ValueKind == System.Text.Json.JsonValueKind.Array)
                    parts.Add(string.Join(", ", kw.EnumerateArray().Where(k => k.ValueKind == System.Text.Json.JsonValueKind.String).Select(k => k.GetString())));
                string author = S("author");
                if (author == null && r.TryGetProperty("author", out var au) && au.ValueKind == System.Text.Json.JsonValueKind.Object
                    && au.TryGetProperty("name", out var an) && an.ValueKind == System.Text.Json.JsonValueKind.String) author = an.GetString();
                var home = S("homepage");
                return (parts.Count == 0 ? null : string.Join(" | ", parts), home, author);
            }
        }
        catch { }
        return default;
    }

    /// <summary>Extrait package.json de l'archive Electron (format asar : en-tête JSON puis fichiers bout à bout, non compressés).</summary>
    internal static string PackageJsonFromAsar(string asar)
    {
        using var fs = File.OpenRead(asar);
        using var br = new BinaryReader(fs);
        if (fs.Length < 16) return null;
        br.ReadUInt32();                         // taille du champ suivant (4)
        uint headerSize = br.ReadUInt32();       // taille de l'en-tête (pickle)
        br.ReadUInt32();                         // taille utile du pickle
        int jsonLen = br.ReadInt32();            // longueur de la chaîne JSON
        if (jsonLen <= 0 || jsonLen > 20_000_000 || jsonLen > fs.Length) return null;
        var header = System.Text.Encoding.UTF8.GetString(br.ReadBytes(jsonLen));
        using var doc = System.Text.Json.JsonDocument.Parse(header);
        if (!doc.RootElement.TryGetProperty("files", out var files) || !files.TryGetProperty("package.json", out var pj)) return null;
        if (!pj.TryGetProperty("size", out var sz) || !pj.TryGetProperty("offset", out var off)) return null;
        long size = sz.GetInt64();
        long offset = off.ValueKind == System.Text.Json.JsonValueKind.String ? long.Parse(off.GetString()) : off.GetInt64();
        long start = 8 + headerSize + offset;
        if (size <= 0 || size > 1_000_000 || start + size > fs.Length) return null;
        fs.Position = start;
        return System.Text.Encoding.UTF8.GetString(br.ReadBytes((int)size));
    }

    // ------------------------------------------------------------------ applications du Microsoft Store
    private const string PackageRepository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Éditeur et description d'une application du Store, lus dans son manifeste (AppxManifest.xml).</summary>
    internal static void DescribeStoreApp(AppItem item)
    {
        try
        {
            var aumid = item.Aumid ?? "";
            int bang = aumid.IndexOf('!');
            if (bang <= 0) return;
            var family = aumid.Substring(0, bang);
            int us = family.LastIndexOf('_');
            if (us <= 0) return;
            string pkgName = family.Substring(0, us), pubId = family.Substring(us + 1);
            using var repo = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PackageRepository);
            var full = repo?.GetSubKeyNames().Where(k => k.StartsWith(pkgName + "_", StringComparison.OrdinalIgnoreCase)
                                                         && k.EndsWith("_" + pubId, StringComparison.OrdinalIgnoreCase))
                                             .OrderByDescending(k => k).FirstOrDefault();
            if (full == null) return;
            using var k2 = repo.OpenSubKey(full);
            var root = k2?.GetValue("PackageRootFolder") as string;
            var manifest = root == null ? null : Path.Combine(root, "AppxManifest.xml");
            if (manifest == null || !File.Exists(manifest)) return;
            var x = System.Xml.Linq.XDocument.Load(manifest);
            string Res(string v)
            {
                if (string.IsNullOrWhiteSpace(v) || !v.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return v;
                var key = v.Substring("ms-resource:".Length);
                var src = key.StartsWith("//") ? $"@{{{full}?{v}}}"
                        : $"@{{{full}?ms-resource://{pkgName}/{(key.Contains('/') ? key.TrimStart('/') : "resources/" + key)}}}";
                var sb = new System.Text.StringBuilder(1024);
                return SHLoadIndirectString(src, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
            }
            var props = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var publisher = Res(props?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value);
            var desc = Res(props?.Elements().FirstOrDefault(e => e.Name.LocalName == "Description")?.Value);
            var visual = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")?.Attribute("Description")?.Value;
            var parts = new[] { publisher, desc, Res(visual) }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            item.Publisher = Clean(publisher);
            item.Context = string.Join(" | ", parts);
        }
        catch { }
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

/// <summary>
/// Lecture des raccourcis. Chaque .lnk n'est lu qu'une fois tant qu'il n'a pas changé (cache selon la date de
/// modification) : au démarrage, un même raccourci était sinon ouvert 3 à 4 fois (cible, analyse, vérification…).
/// </summary>
public static class Shortcuts
{
    [ThreadStatic] private static object _wsh;
    private record Info(DateTime Stamp, string TargetPath, string Arguments, string IconLocation);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Info> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static dynamic Wsh()
    {
        _wsh ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        return _wsh;
    }

    private static Info Read(string lnk)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(lnk); } catch { return null; }
        if (Cache.TryGetValue(lnk, out var c) && c.Stamp == stamp) return c;
        try
        {
            dynamic l = Wsh().CreateShortcut(lnk);
            var info = new Info(stamp, (string)l.TargetPath ?? "", (string)l.Arguments ?? "", (string)l.IconLocation ?? "");
            Cache[lnk] = info;
            return info;
        }
        catch { return null; }
    }

    public static string GetIconLocation(string lnk)
    {
        var loc = Read(lnk)?.IconLocation ?? "";
        return loc == ",0" ? "" : loc;
    }

    public static string GetTargetPath(string lnk) => Read(lnk)?.TargetPath ?? "";

    /// <summary>Change l'icône d'un raccourci (lève une exception si l'accès est refusé).</summary>
    public static void SetIconLocation(string lnk, string location)
    {
        Cache.TryRemove(lnk, out _);
        dynamic l = Wsh().CreateShortcut(lnk);
        l.IconLocation = string.IsNullOrEmpty(location) ? ",0" : location;
        l.Save();
        Cache.TryRemove(lnk, out _);
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
                var i = Read(path);
                return i == null ? "" : $"{i.TargetPath} {i.Arguments}";
            }
            return path;
        }
        catch { return ""; }
    }
}
