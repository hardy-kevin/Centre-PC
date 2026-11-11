using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>
/// Regroupe sous une seule tuile les applications qui vont ensemble (dans cet ordre) :
/// - les dossiers créés par l'utilisateur par glisser-déposer (toujours prioritaires) ;
/// - un dossier du menu Démarrer propre à un éditeur ou un produit (« PostgreSQL 16 », « Git », « JetBrains ») ;
/// - les raccourcis vers un même dossier d'installation (« LGHUB » et « Logitech G HUB ») ;
/// - les petites applications d'une suite : « Git Bash », « Git CMD », « Git GUI » → « Git » ;
/// - les applications d'une même marque, où qu'elles soient installées : « BlueStacks 5 », « BlueStacks X » → « BlueStacks » ;
/// - les éléments annexes avec leur application : « Cyberpunk 2077 - Bonus Content », « Désinstaller Git », « Aide de LibreOffice ».
/// Garde-fous : jamais pour un dossier fourre-tout (Accessoires, Steam…), un éditeur aux produits variés (Microsoft, Adobe…)
/// ou un mot courant (« World of Warcraft » et « World of Tanks » restent séparés).
/// </summary>
public static class AppSuites
{
    /// <summary>Éditeurs dont les produits n'ont rien en commun : leur nom seul ne forme jamais une suite.</summary>
    private static readonly HashSet<string> GenericPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Adobe", "Google", "Apple", "Amazon", "Steam", "Xbox", "NVIDIA", "AMD", "Intel", "Epic", "EA", "Ubisoft",
        "Sony", "Autodesk", "Corel", "Mozilla", "Oracle", "IBM", "HP", "Dell", "Lenovo", "Samsung", "Meta", "Outil", "Outils", "Tools", "Tool",
        "Jeux", "Games", "Game", "The", "Le", "La", "Les", "L", "My", "Mon", "Ma", "Mes", "New", "Nouveau"
    };

    /// <summary>Mots courants : un début de nom fait de ce seul mot ne suffit pas à rapprocher deux applications installées à part.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "of", "de", "du", "des", "et", "and", "for", "pour", "app", "apps", "application", "pro", "free", "open", "easy", "smart",
        "super", "ultra", "mega", "max", "mini", "little", "big", "best", "new", "old", "my", "your", "one", "first", "last", "final", "real",
        "true", "total", "power", "quick", "simple", "auto", "magic", "digital", "global", "live", "home", "net", "web", "online", "cloud",
        "data", "file", "files", "remote", "visual", "media", "video", "photo", "image", "music", "sound", "audio", "office", "studio", "control",
        "center", "centre", "hub", "launcher", "manager", "player", "editor", "reader", "viewer", "assistant", "service", "services",
        "system", "systems", "security", "network", "driver", "update", "setup", "install", "test", "demo", "star", "world", "call", "age",
        "city", "cities", "road", "space", "sea", "sky", "sun", "moon", "fire", "ice", "dark", "light", "black", "white", "red", "blue",
        "green", "night", "day", "time", "life", "mind", "dream", "death", "dead", "war", "wars", "battle", "army", "legend", "legends",
        "hero", "heroes", "king", "kings", "lord", "lords", "empire", "dragon", "dragons", "monster", "zombie", "grand", "great", "lost",
        "jeu", "lecteur", "gestionnaire", "éditeur", "centre", "outil", "service"
    };

    private static readonly Regex GenericFolderRx = new(
        @"^(accessories|accessoires|windows accessories|accessoires windows|system tools|outils système|administrative tools|outils d.administration|"
        + @"windows tools|outils windows|accessibility|accessibilité|ease of access|options d.ergonomie|windows ease of access|maintenance|startup|démarrage|"
        + @"windows system|système windows|steam|epic games|gog\.com|games|jeux|programs|programmes|utilities|utilitaires|xboxgames|xbox games|"
        + @"applications chrome|chrome apps|applications edge|edge apps|applications web|web apps|range bureau)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsGenericFolder(string folder) =>
        string.IsNullOrEmpty(folder) || GenericFolderRx.IsMatch(Path.GetFileName(folder.TrimEnd('\\')));

    private static string[] Words(string name) =>
        Regex.Replace(name ?? "", @"[\(\)\[\]:–—]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Plus long début de nom commun à toutes les applications (en mots entiers).</summary>
    internal static string CommonPrefix(IEnumerable<string> names)
    {
        var lists = names.Select(Words).ToList();
        if (lists.Count == 0) return "";
        int n = lists.Min(l => l.Length);
        var res = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var w = lists[0][i];
            if (lists.All(l => l[i].Equals(w, StringComparison.CurrentCultureIgnoreCase))) res.Add(w); else break;
        }
        // « Cyberpunk 2077 - » → « Cyberpunk 2077 »
        while (res.Count > 0 && res[^1].All(c => !char.IsLetterOrDigit(c))) res.RemoveAt(res.Count - 1);
        return string.Join(" ", res);
    }

    private static bool StartsWithWords(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase) && (name.Length == prefix.Length || !char.IsLetterOrDigit(name[prefix.Length]));

    /// <summary>« Git » est une marque ; « World of » ou « Total » ne le sont pas.</summary>
    private static bool IsBrand(string prefix)
    {
        var w = Words(prefix);
        if (w.Length == 0 || GenericPrefixes.Contains(prefix)) return false;
        // Le premier mot seul doit être distinctif (sinon « Star Wars » et « Star Citizen » seraient rapprochés)
        if (CommonWords.Contains(w[0]) || GenericPrefixes.Contains(w[0])) return false;
        return w[0].Length >= 3 || w.Length >= 2;
    }

    /// <summary>Le nom contient-il celui de l'application en mots entiers (« Uninstall Git », « Aide de LibreOffice ») ?</summary>
    private static bool Mentions(string text, string name)
    {
        if (name.Length < 2 || text.Length < name.Length) return false;
        for (int i = text.IndexOf(name, StringComparison.CurrentCultureIgnoreCase); i >= 0;
             i = i + 1 < text.Length ? text.IndexOf(name, i + 1, StringComparison.CurrentCultureIgnoreCase) : -1)
        {
            int end = i + name.Length;
            if ((i == 0 || !char.IsLetterOrDigit(text[i - 1])) && (end >= text.Length || !char.IsLetterOrDigit(text[end]))) return true;
        }
        return false;
    }

    private class Suite
    {
        public string Name;
        public string Folder;
        public bool User;
        public List<AppItem> Members = new();
    }

    private static string Label(AppItem a) => AppKinds.StripShortcutSuffix(a.Name);

    private static readonly Regex FolderNoise = new(
        @"(\s+(v?\d+([.\-]\d+)*|\(?x(64|86)\)?|\(?(64|32)[- ]?bits?\)?|inc\.?|ltd\.?|llc|gmbh|corporation|corp\.?|technologies|software|sas|sarl))+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>« PostgreSQL 16 » → « PostgreSQL », « Discord Inc » → « Discord », « OpenOffice 4.1.14 » → « OpenOffice ».</summary>
    internal static string CleanFolderName(string folder)
    {
        var n = FolderNoise.Replace(folder.Trim(), "").Trim();
        return n.Length >= 2 ? n : folder.Trim();
    }

    private static string TopFolder(string startFolder) =>
        string.IsNullOrEmpty(startFolder) ? null : startFolder.Split('\\')[0];

    private static readonly Regex PlaceholderPublisher = new(@"^(company ?name|your company( name)?|default company( name)?|n/?a|unknown|inconnu|none|test|publisher)$",
        RegexOptions.IgnoreCase);

    /// <summary>Éditeur comparable, ou null (vide ou valeur par défaut laissée par le développeur : « COMPANY NAME »).</summary>
    private static string PublisherKey(AppItem a) =>
        string.IsNullOrWhiteSpace(a.Publisher) || PlaceholderPublisher.IsMatch(a.Publisher.Trim()) ? null : Affinity.PublisherKey(a.Publisher);

    private static string Simple(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\d]", "");

    /// <summary>Applications « principales » d'un groupe : ni annexe, ni script personnel.</summary>
    private static bool IsMainCandidate(AppItem a) => !a.IsCompanion && !AppKinds.IsPersonalScript(a.Target);

    /// <summary>
    /// Nom définitif d'un groupe, d'après tous ses membres : le dossier de l'éditeur si les applications le citent
    /// (« 7-Zip », « XAMPP »), l'application unique (« Telegram »), le début de nom commun (« BlueStacks »),
    /// sinon le dossier (« JetBrains ») ou le nom le plus complet.
    /// </summary>
    private static string FinalName(List<AppItem> members, string folder, string current)
    {
        var folderName = folder == null ? null : CleanFolderName(folder);
        if (folderName != null && members.Count(m => Mentions(Label(m), folderName)) * 2 >= members.Count) return folderName;
        var apps = members.Where(IsMainCandidate).ToList();
        if (apps.Count == 0) apps = members.Where(m => !m.IsCompanion).ToList();
        if (apps.Count == 1) return Label(apps[0]);
        var prefix = CommonPrefix(apps.Select(Label));
        if (prefix.Length >= 2 && !GenericPrefixes.Contains(prefix)) return prefix;
        return folderName ?? apps.Select(Label).OrderByDescending(n => n.Length).FirstOrDefault() ?? current;
    }

    /// <summary>« BlueStack » et « BlueStacks », « The Document Foundation » et « Document Foundation » : même éditeur.</summary>
    internal static bool SameMaker(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        static string C(string v) => Regex.Replace(v.ToLowerInvariant(), @"\bthe\b|[^\p{L}\d]", "");
        var p = C(a); var q = C(b);
        return p == q || (Math.Min(p.Length, q.Length) >= 4 && (p.StartsWith(q) || q.StartsWith(p)));
    }

    /// <summary>
    /// Éditeur et produit d'après l'identifiant Windows d'une application (« TheDocumentFoundation.LibreOffice.Writer »
    /// → « TheDocumentFoundation.LibreOffice »). Null pour les paquets du Store et les chemins.
    /// </summary>
    internal static string AumidVendor(AppItem a)
    {
        var id = a.Aumid;
        if (string.IsNullOrEmpty(id) || id.Contains('!') || id.StartsWith("{") || id.Contains('\\')) return null;
        var parts = id.Split('.');
        return parts.Length >= 3 && parts[0].Length >= 2 && parts[1].Length >= 2 ? parts[0] + "." + parts[1] : null;
    }

    /// <summary>Nom d'une suite : application unique, début de nom commun, sinon nom le plus complet ou du dossier.</summary>
    private static string NameFor(List<AppItem> members, string folder)
    {
        var apps = members.Where(m => !m.IsCompanion).ToList();
        if (apps.Count == 1) return Label(apps[0]);
        var prefix = CommonPrefix(apps.Select(Label));
        if (prefix.Length >= 2 && !GenericPrefixes.Contains(prefix)) return prefix;
        if (folder != null) return CleanFolderName(folder);
        return apps.Select(Label).OrderByDescending(n => n.Length).FirstOrDefault() ?? Label(members[0]);
    }

    /// <summary>
    /// Remplace les membres de chaque suite par une tuile « suite ». Les applications seules sont rendues telles quelles.
    /// Jamais regroupés automatiquement : applications obsolètes (elles doivent rester visibles), composants de Windows,
    /// applications sorties d'un groupe par l'utilisateur.
    /// </summary>
    public static List<AppItem> Group(IEnumerable<AppItem> source)
    {
        var apps = source.ToList();
        var cfg = Settings.Current;
        var excluded = new HashSet<string>(cfg?.SuiteExcluded ?? new(), StringComparer.CurrentCultureIgnoreCase);
        var suites = new List<Suite>();
        var taken = new HashSet<AppItem>();
        var builtIn = new Dictionary<AppItem, bool>();
        // Composant de Windows hors d'un dossier d'éditeur (« Node.js command prompt » lance cmd.exe mais appartient à Node.js)
        bool BuiltIn(AppItem a)
        {
            if (!builtIn.TryGetValue(a, out var b))
                builtIn[a] = b = IsGenericFolder(TopFolder(a.StartFolder)) && AppKinds.IsWindowsBuiltIn(a);
            return b;
        }
        bool Free(AppItem a) => !taken.Contains(a) && !excluded.Contains(a.Name) && !a.IsObsolete && !BuiltIn(a);
        Suite New(string name, bool user = false) { var s = new Suite { Name = name, User = user }; suites.Add(s); return s; }
        void Take(Suite s, AppItem a) { if (taken.Add(a)) s.Members.Add(a); }
        Suite SuiteOf(AppItem a) => suites.FirstOrDefault(s => s.Members.Contains(a));

        // 0) Dossiers créés par l'utilisateur (glisser-déposer)
        if (cfg?.AppFolders != null)
            foreach (var (folder, names) in cfg.AppFolders)
            {
                var members = apps.Where(a => !taken.Contains(a) && names.Contains(a.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
                if (members.Count == 0) continue;
                var s = New(folder, user: true);
                foreach (var m in members) Take(s, m);
            }

        // 1) Dossier du menu Démarrer propre à un produit ou un éditeur (sous-dossiers compris : « PostgreSQL 16\Documentation »)
        foreach (var folder in apps.Where(a => Free(a) && a.StartFolder != null).GroupBy(a => TopFolder(a.StartFolder), StringComparer.OrdinalIgnoreCase))
        {
            if (IsGenericFolder(folder.Key)) continue;
            var members = folder.ToList();
            // Plusieurs jeux différents dans le dossier d'un éditeur (« Ubisoft ») : chaque jeu garde sa tuile
            var games = members.Where(m => !m.IsCompanion && m.Theme == "Jeux").ToList();
            if (games.Count >= 2 && !IsBrand(CommonPrefix(games.Select(Label)))) members = members.Except(games).ToList();
            if (members.Count < 2 || members.All(m => m.IsCompanion)) continue;
            var s = New(NameFor(members, folder.Key));
            s.Folder = folder.Key;
            foreach (var m in members) Take(s, m);
        }

        // 2) Raccourcis vers un même dossier d'installation (hors applications hébergées par un émulateur ou un navigateur)
        foreach (var root in apps.Where(a => a.InstallRoot != null && !AppKinds.IsHosted(a.Target) && !excluded.Contains(a.Name) && !a.IsObsolete && !BuiltIn(a))
                                 .GroupBy(a => a.InstallRoot, StringComparer.OrdinalIgnoreCase))
        {
            var free = root.Where(Free).ToList();
            if (free.Count == 0) continue;
            var owner = root.Where(a => taken.Contains(a)).Select(SuiteOf).FirstOrDefault(x => x != null && !x.User);
            if (owner != null) { foreach (var a in free) Take(owner, a); continue; }
            if (free.Count < 2 || free.All(m => m.IsCompanion)) continue;
            var s = New(NameFor(free, null));
            foreach (var a in free) Take(s, a);
        }

        // 2 bis) Même identifiant d'éditeur Windows (« TheDocumentFoundation.LibreOffice.* »)
        foreach (var vendor in apps.Where(a => AumidVendor(a) != null && !excluded.Contains(a.Name) && !a.IsObsolete && !BuiltIn(a))
                                   .GroupBy(AumidVendor, StringComparer.OrdinalIgnoreCase))
        {
            var free = vendor.Where(Free).ToList();
            if (free.Count == 0) continue;
            var owner = vendor.Where(a => taken.Contains(a)).Select(SuiteOf).FirstOrDefault(x => x != null && !x.User);
            if (owner != null) { foreach (var a in free) Take(owner, a); continue; }
            if (free.Count < 2 || free.All(m => m.IsCompanion)) continue;
            var s = New(NameFor(free, null));
            foreach (var a in free) Take(s, a);
        }

        // 3) Même marque, installée à plusieurs endroits : le nom ne suffit pas, l'éditeur doit le confirmer
        //    (« Enregistreur d'actions » et « Enregistreur vocal » ne sont pas une suite)
        bool Corroborated(IEnumerable<AppItem> items, string prefix, bool joining)
        {
            var list = items.ToList();
            // Le dossier d'installation de chacune porte la marque (« BlueStacks_nxt », « bluestacks-services ») : même produit,
            // même si l'éditeur déclaré diffère (rachat, filiale)
            var brand = Simple(prefix);
            if (brand.Length >= 4 && list.All(a => a.InstallRoot != null && Simple(Path.GetFileName(a.InstallRoot.TrimEnd('\\'))).Contains(brand)))
                return true;
            var keys = list.Select(PublisherKey).Where(k => !string.IsNullOrEmpty(k)).ToList();
            if (keys.Any(k => !SameMaker(k, keys[0]))) return false;                                 // éditeurs différents
            if (keys.Count >= 2) return true;                                                        // même éditeur
            var vendors = list.Select(AumidVendor).Where(v => v != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (vendors.Count == 1 && list.Count(a => AumidVendor(a) != null) >= 2) return true;     // même identifiant d'éditeur Windows
            if (vendors.Count > 1) return false;
            // Rejoindre une suite déjà établie (dossier, installation) : possible si rien ne le contredit
            return joining || Words(prefix).Length >= 2;                                            // « Need for Speed » : nom assez précis
        }
        foreach (var s in suites.Where(x => !x.User).ToList())
        {
            if (!IsBrand(s.Name)) continue;
            foreach (var a in apps.Where(a => Free(a) && !a.IsCompanion && !AppKinds.IsHosted(a.Target) && StartsWithWords(Label(a), s.Name)).ToList())
                if (Corroborated(s.Members.Where(m => !m.IsCompanion).Append(a), s.Name, joining: true)) Take(s, a);
        }
        foreach (var bucket in apps.Where(a => Free(a) && !a.IsCompanion && !AppKinds.IsHosted(a.Target))
                                   .GroupBy(a => Words(Label(a)).FirstOrDefault() ?? "", StringComparer.CurrentCultureIgnoreCase).ToList())
        {
            var items = bucket.ToList();
            if (items.Count < 2) continue;
            var prefix = CommonPrefix(items.Select(Label));
            if (prefix.Length < 2 || !IsBrand(prefix) || !Corroborated(items, prefix, joining: false)) continue;
            var s = New(prefix);
            foreach (var a in items) Take(s, a);
        }

        // 4) Éléments annexes : avec l'application dont ils portent le nom (« pgAdmin documentation » → « pgAdmin 4 »),
        //    sinon avec celle du même dossier du menu Démarrer ou d'installation
        // (une annexe de Windows, comme « Power Automate Troubleshooter », rejoint l'application dont elle porte le nom)
        bool FreeLoose(AppItem a) => !taken.Contains(a) && !excluded.Contains(a.Name) && !a.IsObsolete;
        foreach (var c in apps.Where(a => a.IsCompanion && FreeLoose(a)).ToList())
        {
            var label = Label(c);
            var core = AppScanner.Companion.Replace(label, " ");
            core = Regex.Replace(core, @"[\(\)\-–:]+|\b(for|pour|de|du|des|la|le|les|the|of)\b", " ", RegexOptions.IgnoreCase);
            core = Regex.Replace(core, @"\s+", " ").Trim();
            var bySuite = suites.Where(s => !s.User && (Mentions(label, s.Name) || (core.Length >= 2 && StartsWithWords(s.Name, core))))
                                .OrderByDescending(s => s.Name.Length).FirstOrDefault();
            if (bySuite != null) { Take(bySuite, c); continue; }
            var cFolder = c.StartFolder != null && !IsGenericFolder(TopFolder(c.StartFolder)) ? CleanFolderName(TopFolder(c.StartFolder)) : null;
            bool builtInCompanion = BuiltIn(c);
            var parent = apps.Where(a => a != c && !a.IsCompanion && (Mentions(label, Label(a)) || (core.Length >= 2 && StartsWithWords(Label(a), core))))
                             .OrderByDescending(a => Label(a).Length).FirstOrDefault()
                      ?? (builtInCompanion ? null : apps.Where(a => a != c && !a.IsCompanion && (
                                 // Même dossier du menu Démarrer
                                 (cFolder != null && string.Equals(TopFolder(a.StartFolder), TopFolder(c.StartFolder), StringComparison.OrdinalIgnoreCase))
                                 // Dossier du menu Démarrer au nom de l'application (« LogMeIn Hamachi\Uninstall » → LogMeIn Hamachi)
                              || (cFolder != null && cFolder.Length >= 3 && (StartsWithWords(Label(a), cFolder) || StartsWithWords(cFolder, Label(a))))
                                 // Même dossier d'installation, ou dossier d'installation cité (« Uninstall Oculus » → appli installée dans « Oculus »)
                              || (c.InstallRoot != null && string.Equals(a.InstallRoot, c.InstallRoot, StringComparison.OrdinalIgnoreCase))
                              || (a.InstallRoot != null && Path.GetFileName(a.InstallRoot.TrimEnd('\\')).Length >= 4 && Mentions(label, Path.GetFileName(a.InstallRoot.TrimEnd('\\'))))))
                             .OrderBy(a => Label(a).Length).FirstOrDefault());
            if (parent == null || parent.IsObsolete) continue;   // annexe isolée : présentée seule, classée comme les autres
            var owner = SuiteOf(parent);
            if (owner == null)
            {
                if (!FreeLoose(parent)) continue;
                owner = New(Label(parent));
                Take(owner, parent);
            }
            Take(owner, c);
        }

        // Nom définitif d'après l'ensemble des membres (le groupe a pu s'agrandir depuis sa création)
        foreach (var x in suites.Where(x => !x.User && x.Members.Count > 0)) x.Name = FinalName(x.Members, x.Folder, x.Name);

        // Deux groupes du même nom (« BlueStacks » trouvé par deux dossiers d'installation) : un seul
        foreach (var dup in suites.Where(x => !x.User).GroupBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1).ToList())
        {
            var first = dup.First();
            foreach (var other in dup.Skip(1)) { first.Members.AddRange(other.Members); suites.Remove(other); }
        }

        var result = apps.Where(a => !taken.Contains(a)).ToList();
        foreach (var s in suites)
        {
            if (s.Members.Count < 2 && !s.User) { result.AddRange(s.Members); continue; }
            if (s.Members.Count == 0) continue;
            var main = s.Members.FirstOrDefault(m => IsMainCandidate(m) && Label(m).Equals(s.Name, StringComparison.CurrentCultureIgnoreCase))
                       ?? s.Members.Where(IsMainCandidate).OrderBy(m => Label(m).Length).FirstOrDefault()
                       ?? s.Members.Where(m => !m.IsCompanion).OrderBy(m => Label(m).Length).FirstOrDefault()
                       ?? s.Members[0];
            var ordered = s.Members.OrderBy(m => m == main ? 0 : m.IsCompanion || !IsMainCandidate(m) ? 2 : 1)
                                   .ThenBy(m => Label(m), StringComparer.CurrentCultureIgnoreCase).ToList();
            // Thème : celui de l'application principale ; si elle n'est pas classée, celui d'une autre application de la suite
            var def = cfg?.DefaultCategory;
            var theme = main.Theme != def ? main.Theme
                      : ordered.Where(m => m.Theme != def).GroupBy(m => m.Theme).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                        ?? main.Theme;
            var suite = new AppItem
            {
                Name = s.Name, Theme = theme, Members = ordered, Icon = main.Icon, Target = main.Target,
                IsShortcut = true, Location = main.Location, IsUserGroup = s.User
            };
            // L'icône de l'application principale arrive après coup (chargement en arrière-plan)
            if (main.Icon == null)
                main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppItem.Icon)) suite.Icon = main.Icon; };
            result.Add(suite);
        }
        return result;
    }
}
