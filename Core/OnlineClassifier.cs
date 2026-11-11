using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Résultat mémorisé d'une recherche en ligne (une seule recherche par appli).</summary>
public class OnlineResult
{
    public string Category { get; set; }   // null = rien trouvé
    public string Source { get; set; }     // winget, wikidata
    public string Evidence { get; set; }   // ce qui a permis de décider (étiquettes, description)
    public DateTime Date { get; set; }
}

/// <summary>
/// Affine le tri des applis restées dans « Autres » grâce à Internet, sans clé ni compte, du plus fiable au moins fiable :
/// 1) winget (outil d'installation de Windows) : étiquettes et description du logiciel installé
/// 2) Flathub (catalogue d'applis multiplateformes) : catégories normalisées (« InstantMessaging », « IDE », « Game »…)
/// 3) Steam : jeux, et logiciels classés par genre (« Utilities », « Video Production »…)
/// 4) Wikidata (base de Wikipédia) : nature de l'objet (« jeu vidéo », « navigateur web »…), genre et description
/// Seul le nom de l'appli est envoyé. Le nom trouvé doit être identique pour éviter les homonymes.
/// Les résultats sont gardés en cache sur le PC.
/// </summary>
public static class OnlineClassifier
{
    private static readonly string CachePath = Path.Combine(Settings.DataDir, "classement-en-ligne.json");
    private static Dictionary<string, OnlineResult> _cache;
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("CentrePC/1.0 (outil de rangement d'applications Windows)");
        return h;
    }

    // Vocabulaire des descriptions (anglais + français) → thème
    private static readonly (string category, string[] words)[] Lexicon =
    {
        ("Jeux", new[] { "video game", "jeu vidéo", "jeux vidéo", "jeu de rôle", "game", "games", "rpg", "mmorpg", "mmo", "fps",
            "first-person shooter", "shooter", "roguelike", "roguelite", "battle royale", "survival game", "racing game", "jeu de course",
            "strategy game", "jeu de stratégie", "puzzle game", "platformer", "simulation game", "racing simulator", "sim racing",
            "multiplayer", "multijoueur", "indie game", "early access", "free-to-play", "dedicated server" }),
        ("Lanceurs de jeux", new[] { "game launcher", "lanceur de jeux", "launcher", "game store", "magasin de jeux", "game library",
            "digital distribution", "distribution numérique", "emulator", "émulateur", "cloud gaming" }),
        ("Internet", new[] { "web browser", "navigateur web", "navigateur", "browser", "torrent", "bittorrent", "ftp", "download manager",
            "gestionnaire de téléchargement", "rss", "news reader" }),
        ("Communication", new[] { "instant messaging", "messagerie instantanée", "messagerie", "messaging", "chat", "email client",
            "client de messagerie", "e-mail", "email", "voip", "video call", "visioconférence", "videoconferencing", "video conferencing",
            "social network", "réseau social", "collaboration", "team chat" }),
        ("IA et assistants", new[] { "artificial intelligence", "intelligence artificielle", "ai assistant", "assistant ia", "chatbot",
            "large language model", "llm", "generative ai", "ia générative", "image generation", "machine learning" }),
        ("Musique et audio", new[] { "music", "musique", "audio", "music player", "lecteur de musique", "digital audio workstation",
            "station audio numérique", "podcast", "sound", "son", "equalizer", "égaliseur", "dj", "music notation", "partition" }),
        ("Vidéo et streaming", new[] { "media player", "lecteur multimédia", "video player", "lecteur vidéo", "video editor", "éditeur vidéo",
            "montage vidéo", "video", "vidéo", "streaming", "live streaming", "screen recorder", "enregistrement d'écran", "codec",
            "webcam", "subtitles", "sous-titres", "transcoder" }),
        ("Photo et graphisme", new[] { "photo", "image editor", "éditeur d'images", "retouche photo", "graphics editor", "raster graphics",
            "vector graphics", "graphisme", "image viewer", "visionneuse", "screenshot", "capture d'écran", "3d modeling", "modélisation 3d",
            "3d computer graphics", "drawing", "dessin", "painting", "illustration", "design", "ui design", "desktop publishing", "pao" }),
        ("Bureautique", new[] { "office suite", "suite bureautique", "bureautique", "word processor", "traitement de texte", "spreadsheet",
            "tableur", "presentation", "présentation", "pdf", "note-taking", "prise de notes", "calendar", "calendrier", "productivity",
            "productivité", "e-book", "livre électronique", "ebook reader", "to-do", "task management", "gestion de tâches", "project management",
            "gestion de projet", "dictionary", "dictionnaire", "translation", "traduction", "reference management", "text editor" }),
        ("Gestion et finance", new[] { "accounting", "comptabilité", "finance", "financial", "invoicing", "facturation", "bookkeeping",
            "crm", "customer relationship management", "erp", "enterprise resource planning", "payroll", "paie", "banking", "trading",
            "personal finance", "budget", "cryptocurrency wallet", "electronic signature" }),
        ("Ingénierie et CAO", new[] { "computer-aided design", "conception assistée par ordinateur", "cad", "cao", "engineering", "ingénierie",
            "finite element", "éléments finis", "electronic design automation", "pcb", "circuit", "electronics", "électronique", "3d printing",
            "impression 3d", "slicer", "architecture software", "bim", "gis", "système d'information géographique", "plc" }),
        ("Science et données", new[] { "statistics", "statistique", "statistical", "data analysis", "analyse de données", "data science",
            "data visualization", "visualisation de données", "business intelligence", "scientific", "scientifique", "mathematics",
            "mathématiques", "numerical computing", "calcul numérique", "computer algebra", "typesetting", "latex", "notebook", "astronomy",
            "chemistry", "chimie", "bioinformatics" }),
        ("Éducation", new[] { "education", "éducation", "educational", "éducatif", "learning", "apprentissage", "language learning",
            "flashcard", "school", "école", "e-learning", "typing tutor", "quiz", "learning management system" }),
        ("Développement", new[] { "integrated development environment", "environnement de développement", "ide", "code editor",
            "éditeur de code", "source code editor", "programming", "programmation", "programming language", "langage de programmation",
            "developer", "développeur", "software development", "développement logiciel", "sdk", "compiler", "compilateur", "database",
            "base de données", "sql", "dbms", "sgbd", "version control", "gestion de versions", "git", "debugger", "débogueur",
            "api client", "game engine", "moteur de jeu", "web server", "serveur web", "runtime", "containerization" }),
        ("Administration système et réseau", new[] { "remote desktop", "bureau à distance", "remote access", "accès à distance", "ssh",
            "telnet", "terminal emulator", "émulateur de terminal", "command-line", "ligne de commande", "shell", "network", "réseau",
            "network monitoring", "packet analyzer", "port scanner", "virtualization", "virtualisation", "virtual machine", "machine virtuelle",
            "hypervisor", "hyperviseur", "system administration", "administration système", "sysadmin", "server", "serveur",
            "infrastructure", "devops", "configuration management", "monitoring", "supervision", "active directory" }),
        ("Sécurité", new[] { "antivirus", "anti-malware", "malware", "security", "sécurité", "password manager", "gestionnaire de mots de passe",
            "vpn", "virtual private network", "encryption", "chiffrement", "firewall", "pare-feu", "two-factor", "authenticator",
            "parental control", "contrôle parental", "privacy", "confidentialité" }),
        ("Cloud et sauvegarde", new[] { "cloud storage", "stockage en ligne", "stockage cloud", "file hosting", "hébergement de fichiers",
            "file synchronization", "synchronisation de fichiers", "backup", "sauvegarde", "disk imaging", "image disque", "nas", "sync" }),
        ("Matériel et pilotes", new[] { "device driver", "pilote", "driver", "firmware", "micrologiciel", "hardware", "matériel", "rgb",
            "lighting", "éclairage", "peripheral", "périphérique", "mouse", "souris", "keyboard", "clavier", "headset", "casque", "gamepad",
            "manette", "racing wheel", "volant", "printer", "imprimante", "scanner", "hardware monitoring", "sensor", "capteur", "overclocking",
            "fan control", "ventilateur", "motherboard", "carte mère", "graphics card", "carte graphique", "chipset", "bios", "virtual reality",
            "réalité virtuelle", "vr headset" }),
        ("Utilitaires", new[] { "utility", "utilitaire", "system utility", "utilitaire système", "cleaner", "nettoyage", "file archiver",
            "archiveur", "compression", "file manager", "gestionnaire de fichiers", "uninstaller", "désinstallateur", "disk", "disque",
            "partition", "optimization", "optimisation", "clipboard", "presse-papiers", "package manager", "gestionnaire de paquets",
            "launcher application", "file search", "recherche de fichiers", "customization", "personnalisation", "wallpaper", "fond d'écran",
            "bootable usb", "data recovery", "récupération de données", "duplicate finder", "automation", "automatisation", "macro" }),
        // Catégories du catalogue (créées automatiquement si une application en relève)
        ("Outils pour les jeux", new[] { "mod manager", "gestionnaire de mods", "modding", "addon manager", "game overlay", "game companion",
            "compagnon de jeu", "game helper", "aide de jeu", "game guide", "guide de jeu", "quest helper", "aide aux quêtes", "game tracker",
            "game statistics", "statistiques de jeu", "game trainer", "game booster", "fps counter", "game clips", "game modification" }),
        ("Santé et sport", new[] { "fitness", "workout", "entraînement sportif", "running", "course à pied", "cycling", "cyclisme",
            "health tracking", "suivi de santé", "nutrition", "meditation", "méditation", "sleep tracking", "heart rate", "fréquence cardiaque" }),
        ("Voyages et transports", new[] { "travel", "voyage", "tourism", "tourisme", "gps navigation", "navigation gps", "flight tracking",
            "public transport", "transports en commun", "hotel booking", "route planner" }),
        ("Lecture et bandes dessinées", new[] { "comic book reader", "lecteur de bandes dessinées", "comics", "bande dessinée", "manga reader",
            "audiobook", "livre audio", "epub", "e-reader", "liseuse" }),
        ("Maison connectée", new[] { "home automation", "domotique", "smart home", "maison connectée", "smart lighting", "éclairage connecté",
            "iot", "internet of things", "internet des objets", "thermostat", "zigbee", "z-wave" }),
    };

    private static readonly List<(string category, Regex rx)> LexiconRx = Lexicon
        .Select(l => (l.category, new Regex(@"(?<![\p{L}\d])(" + string.Join("|", l.words.OrderByDescending(w => w.Length).Select(Regex.Escape)) + @")(?![\p{L}\d])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        .ToList();

    // ------------------------------------------------------------------ cache
    public static void Load()
    {
        try
        {
            _cache = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, OnlineResult>>(File.ReadAllText(CachePath))
                : new();
        }
        catch { _cache = new(); }
        _cache = new Dictionary<string, OnlineResult>(_cache ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private static void Save()
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    /// <summary>Thème trouvé en ligne pour cette appli (ou null).</summary>
    public static string Lookup(string name)
    {
        if (_cache == null) Load();
        return _cache.TryGetValue(Key(name), out var r) && r.Category != null && Settings.IsAvailable(r.Category) ? r.Category : null;
    }

    public static bool AlreadySearched(string name)
    {
        if (_cache == null) Load();
        return _cache.ContainsKey(Key(name));
    }

    public static void Forget()
    {
        _cache = new(StringComparer.OrdinalIgnoreCase);
        Save();
    }

    /// <summary>Oublie les recherches sans résultat et celles des applis données (pour les relancer), garde les autres.</summary>
    public static void Forget(IEnumerable<string> names)
    {
        if (_cache == null) Load();
        foreach (var k in _cache.Where(kv => kv.Value.Category == null).Select(kv => kv.Key).ToList()) _cache.Remove(k);
        foreach (var n in names) _cache.Remove(Key(n));
        Save();
    }

    /// <summary>Source du thème trouvé en ligne (« Steam », « Flathub »…), ou null.</summary>
    /// <summary>Oublie les résultats venant d'une source (ex. « Wikidata ») : ils seront recherchés à nouveau.</summary>
    public static void ForgetSource(string sourcePrefix)
    {
        if (_cache == null) Load();
        foreach (var k in _cache.Where(kv => kv.Value.Source?.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) == true)
                                .Select(kv => kv.Key).ToList())
            _cache.Remove(k);
        Save();
    }

    public static string SourceOf(string name)
    {
        if (_cache == null) Load();
        if (!_cache.TryGetValue(Key(name), out var r) || r.Category == null) return null;
        var src = (r.Source ?? "").Split(' ')[0];
        return string.IsNullOrEmpty(r.Evidence) ? src : $"{src} : {r.Evidence}";
    }

    private static string Key(string name) => Settings.Normalize(name).ToLowerInvariant();
    private static string Simple(string s) => Regex.Replace(Settings.Normalize(s).ToLowerInvariant(), @"[^\p{L}\d]", "");

    // ------------------------------------------------------------------ recherche
    /// <summary>Cherche en ligne le type des applis données. Renvoie le nombre d'applis classées.</summary>
    public static async Task<int> ClassifyAsync(IList<AppItem> apps, IProgress<(int done, int total)> progress, CancellationToken ct = default)
    {
        if (_cache == null) Load();
        var todo = apps.Where(a => !AlreadySearched(a.Name)).GroupBy(a => Key(a.Name)).Select(g => g.First()).ToList();
        int done = 0, found = 0;
        progress?.Report((0, todo.Count));

        // Liste des logiciels installés connus de winget (nom → identifiant)
        List<(string name, string id)> wingetList = new();
        try
        {
            wingetList = await Updates.ListWingetAsync()
                         ?? ParseList(await Updates.RunCaptureAsync("winget", "list --accept-source-agreements --disable-interactivity"));
        }
        catch { }

        using var gate = new SemaphoreSlim(3);
        var tasks = todo.Select(async app =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var name = AppKinds.StripShortcutSuffix(app.Name);
                var pkg = AppKinds.AndroidPackage(app.Target);
                OnlineResult r;
                if (pkg != null)
                    // Application Android lancée par un émulateur : sa fiche Google Play, et rien d'autre (le nom seul est ambigu)
                    r = await Try(() => FromGooglePlay(pkg));
                else
                {
                    // Ce que le programme contient (sites cités, site officiel) : lu sur le PC, sans connexion
                    var embedded = await Task.Run(() => EmbeddedHints(app));
                    var homepage = app.Homepage ?? embedded.homepage;
                    r = await Try(() => FromWinget(name, wingetList))
                        ?? await Try(() => FromWebsite(homepage))
                        ?? embedded.result
                        ?? await Try(() => FromFlathub(name))
                        ?? await Try(() => FromSteam(name, app))
                        ?? await Try(() => FromGitHub(name, app, homepage))
                        ?? await Try(() => FromWikidata(name));
                }
                lock (_cache) _cache[Key(app.Name)] = r ?? new OnlineResult { Date = DateTime.Now };
                if (r?.Category != null) Interlocked.Increment(ref found);
            }
            catch { }
            finally
            {
                gate.Release();
                progress?.Report((Interlocked.Increment(ref done), todo.Count));
            }
        });
        await Task.WhenAll(tasks);
        Save();
        return found;
    }

    private static async Task<OnlineResult> FromWinget(string name, List<(string name, string id)> installed)
    {
        var s = Simple(name);
        if (s.Length < 3) return null;
        var match = installed.FirstOrDefault(i => Simple(i.name) == s);
        if (match.id == null)
            match = installed.FirstOrDefault(i => { var x = Simple(i.name); return x.Length >= 4 && (x.StartsWith(s) || s.StartsWith(x)); });
        // Identifiants locaux (ARP\…, MSIX\…) ou tronqués : pas de fiche en ligne
        if (match.id == null || match.id.Contains('\\') || match.id.EndsWith("…")) return null;

        var text = await Updates.RunCaptureAsync("winget", $"show --id \"{match.id}\" --exact --accept-source-agreements --disable-interactivity");
        var cat = Decide(text, out var evidence);
        return cat == null ? null : new OnlineResult { Category = cat, Source = "winget " + match.id, Evidence = evidence, Date = DateTime.Now };
    }

    /// <summary>Une source en panne ou injoignable ne bloque pas les suivantes.</summary>
    private static async Task<OnlineResult> Try(Func<Task<OnlineResult>> source)
    {
        try { return await source(); }
        catch (Exception ex) { Log.Error("Recherche en ligne", ex); return null; }
    }

    /// <summary>Nom sans « (x64) », « 64-bit »… pour la recherche.</summary>
    private static string Clean(string name) =>
        Regex.Replace(Settings.Normalize(name), @"\s*\(.*?\)\s*|\s+(x64|x86|64-bit|32-bit)$", " ", RegexOptions.IgnoreCase).Trim();

    private static string Valid(string theme) => Settings.IsAvailable(theme) ? theme : null;

    // ------------------------------------------------------------------ Flathub
    // Catégories freedesktop : les sous-catégories (précises) passent avant les catégories principales
    private static readonly (string cat, string theme)[] FlathubSub =
    {
        ("InstantMessaging", "Communication"), ("Chat", "Communication"), ("Email", "Communication"), ("VideoConference", "Communication"),
        ("IRCClient", "Communication"), ("Telephony", "Communication"), ("ContactManagement", "Communication"),
        ("WebBrowser", "Internet"), ("FileTransfer", "Internet"), ("P2P", "Internet"), ("News", "Internet"), ("Feed", "Internet"),
        ("RemoteAccess", "Administration système et réseau"), ("TerminalEmulator", "Administration système et réseau"),
        ("Monitor", "Administration système et réseau"), ("Emulator", "Lanceurs de jeux"),
        ("Finance", "Gestion et finance"), ("WordProcessor", "Bureautique"), ("Spreadsheet", "Bureautique"),
        ("Presentation", "Bureautique"), ("ProjectManagement", "Bureautique"), ("Calendar", "Bureautique"), ("Dictionary", "Bureautique"),
        ("Engineering", "Ingénierie et CAO"), ("Electronics", "Ingénierie et CAO"), ("Robotics", "Ingénierie et CAO"),
        ("Math", "Science et données"), ("Astronomy", "Science et données"), ("DataVisualization", "Science et données"),
        ("Chemistry", "Science et données"), ("Biology", "Science et données"), ("Physics", "Science et données"),
        ("IDE", "Développement"), ("Database", "Développement"), ("Debugger", "Développement"), ("RevisionControl", "Développement"),
        ("WebDevelopment", "Développement"), ("GUIDesigner", "Développement"), ("Translation", "Développement"),
        ("Security", "Sécurité"), ("Archiving", "Utilitaires"), ("Compression", "Utilitaires"), ("FileManager", "Utilitaires"),
        ("HardwareSettings", "Matériel et pilotes"), ("Printing", "Matériel et pilotes"),
        ("Music", "Musique et audio"), ("Audio", "Musique et audio"), ("Midi", "Musique et audio"), ("Mixer", "Musique et audio"),
        ("Sequencer", "Musique et audio"), ("Video", "Vidéo et streaming"), ("TV", "Vidéo et streaming"),
        ("Photography", "Photo et graphisme"), ("3DGraphics", "Photo et graphisme"), ("RasterGraphics", "Photo et graphisme"),
        ("VectorGraphics", "Photo et graphisme"), ("Scanning", "Photo et graphisme"),
    };
    private static readonly (string cat, string theme)[] FlathubMain =
    {
        ("Game", "Jeux"), ("Development", "Développement"), ("Education", "Éducation"), ("Graphics", "Photo et graphisme"),
        ("Office", "Bureautique"), ("Science", "Science et données"), ("Network", "Internet"), ("AudioVideo", "Vidéo et streaming"),
        ("System", "Utilitaires"), ("Utility", "Utilitaires"), ("Settings", "Utilitaires"),
    };

    /// <summary>Catégories freedesktop (Flathub) → thème ; les sous-catégories précises passent avant les principales.</summary>
    internal static string ThemeFromFlathub(IEnumerable<string> categories)
    {
        var cats = categories.ToList();
        return FlathubSub.Concat(FlathubMain)
            .Where(m => cats.Contains(m.cat, StringComparer.OrdinalIgnoreCase))
            .Select(m => Valid(m.theme)).FirstOrDefault(t => t != null);
    }

    /// <summary>Fiche Steam → thème : genre « logiciel » s'il y en a un, sinon jeu (jeu, DLC, démo, mod).</summary>
    internal static string ThemeFromSteam(string type, IEnumerable<string> genreIds)
    {
        var soft = genreIds.Select(g => SteamSoftware.TryGetValue(g, out var t) ? Valid(t) : null).FirstOrDefault(t => t != null);
        return soft ?? (type is "game" or "dlc" or "demo" or "mod" ? Valid("Jeux") : null);
    }

    private static async Task<OnlineResult> FromFlathub(string name)
    {
        var clean = Clean(name);
        if (clean.Length < 3) return null;
        using var body = new StringContent(JsonSerializer.Serialize(new { query = clean }), System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://flathub.org/api/v2/search", body);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return null;
        foreach (var h in hits.EnumerateArray().Take(10))
        {
            var label = h.TryGetProperty("name", out var n) ? n.GetString() : "";
            if (Simple(label) != Simple(clean)) continue;
            var cats = new List<string>();
            foreach (var field in new[] { "sub_categories", "main_categories", "categories" })
                if (h.TryGetProperty(field, out var v))
                {
                    if (v.ValueKind == JsonValueKind.String) cats.Add(v.GetString());
                    else if (v.ValueKind == JsonValueKind.Array) cats.AddRange(v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
                }
            var theme = ThemeFromFlathub(cats);
            if (theme == null)   // catégories inconnues : on tente le résumé
                theme = Decide(h.TryGetProperty("summary", out var sm) ? sm.GetString() : "", out _);
            if (theme != null)
                return new OnlineResult { Category = theme, Source = "Flathub " + (h.TryGetProperty("app_id", out var id) ? id.GetString() : ""),
                                          Evidence = string.Join(", ", cats), Date = DateTime.Now };
        }
        return null;
    }

    // ------------------------------------------------------------------ Steam
    // Genres « logiciels » de Steam (identifiants fixes, indépendants de la langue)
    private static readonly Dictionary<string, string> SteamSoftware = new()
    {
        ["50"] = "Gestion et finance", ["51"] = "Photo et graphisme", ["52"] = "Musique et audio", ["53"] = "Photo et graphisme",
        ["54"] = "Éducation", ["55"] = "Photo et graphisme", ["56"] = "Éducation", ["57"] = "Utilitaires",
        ["58"] = "Vidéo et streaming", ["59"] = "Développement", ["60"] = "Développement",
    };

    private static async Task<OnlineResult> FromSteam(string name, AppItem app = null)
    {
        var clean = Clean(name);
        if (clean.Length < 3) return null;
        using var search = JsonDocument.Parse(await Http.GetStringAsync(
            $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(clean)}&l=english&cc=FR"));
        if (!search.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var hit = items.EnumerateArray().FirstOrDefault(i => i.TryGetProperty("name", out var n) && Simple(n.GetString()) == Simple(clean));
        if (hit.ValueKind == JsonValueKind.Undefined || !hit.TryGetProperty("id", out var idEl)) return null;
        var id = idEl.ToString();

        using var details = JsonDocument.Parse(await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&l=english"));
        if (!details.RootElement.TryGetProperty(id, out var entry) || !entry.TryGetProperty("data", out var data)) return null;
        var genres = data.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => (id: x.TryGetProperty("id", out var gi) ? gi.ToString() : "", label: x.TryGetProperty("description", out var gd) ? gd.GetString() : "")).ToList()
            : new();
        var type = data.TryGetProperty("type", out var ty) ? ty.GetString() : "";
        // Homonymes : un programme qui ne vient pas de Steam doit avoir le même éditeur que la fiche Steam
        if (app != null && !IsSteamInstalled(app))
        {
            var makers = new List<string>();
            foreach (var prop in new[] { "developers", "publishers" })
                if (data.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    makers.AddRange(arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
            var pub = app.Publisher == null ? null : Affinity.PublisherKey(app.Publisher);
            if (string.IsNullOrEmpty(pub) || !makers.Any(m => { var k = Affinity.PublisherKey(m); return k.Length > 1 && (k.Contains(pub) || pub.Contains(k)); }))
                return null;
        }
        var theme = ThemeFromSteam(type, genres.Select(x => x.id));
        return theme == null ? null : new OnlineResult
        {
            Category = theme, Source = $"Steam {id}", Evidence = string.Join(", ", genres.Select(x => x.label)), Date = DateTime.Now
        };
    }

    private static bool IsSteamInstalled(AppItem app)
    {
        var t = (app.Target ?? "") + " " + (app.InstallRoot ?? "");
        return t.Contains("steam", StringComparison.OrdinalIgnoreCase) || app.LooksLikeGame;
    }

    // ------------------------------------------------------------------ Google Play (applications Android)
    private static readonly (string prefix, string theme)[] PlayCategories =
    {
        ("GAME", "Jeux"), ("COMMUNICATION", "Communication"), ("SOCIAL", "Communication"), ("DATING", "Communication"),
        ("MUSIC_AND_AUDIO", "Musique et audio"), ("VIDEO_PLAYERS", "Vidéo et streaming"), ("ENTERTAINMENT", "Vidéo et streaming"),
        ("PHOTOGRAPHY", "Photo et graphisme"), ("ART_AND_DESIGN", "Photo et graphisme"), ("PRODUCTIVITY", "Bureautique"),
        ("BUSINESS", "Bureautique"), ("FINANCE", "Gestion et finance"), ("EDUCATION", "Éducation"), ("TOOLS", "Utilitaires"),
        ("PERSONALIZATION", "Utilitaires"), ("HEALTH_AND_FITNESS", "Santé et sport"), ("SPORTS", "Santé et sport"),
        ("MEDICAL", "Santé et sport"), ("TRAVEL_AND_LOCAL", "Voyages et transports"), ("MAPS_AND_NAVIGATION", "Voyages et transports"),
        ("AUTO_AND_VEHICLES", "Voyages et transports"), ("BOOKS_AND_REFERENCE", "Lecture et bandes dessinées"),
        ("COMICS", "Lecture et bandes dessinées"), ("HOUSE_AND_HOME", "Maison connectée"), ("NEWS_AND_MAGAZINES", "Internet"),
        ("SHOPPING", "Internet"), ("WEATHER", "Bureautique"),
    };

    /// <summary>Catégorie Google Play (« GAME_RACING », « COMMUNICATION »…) → thème.</summary>
    internal static string ThemeFromPlayCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return null;
        return PlayCategories.Where(c => category.StartsWith(c.prefix, StringComparison.OrdinalIgnoreCase)).Select(c => Valid(c.theme)).FirstOrDefault(t => t != null);
    }

    /// <summary>
    /// Fiche Google Play d'une application Android : son vrai nom (« PlayStation App » pour com.scee.psxandroid),
    /// sa catégorie et sa description. Le vrai nom est d'abord comparé aux mots-clés (plus précis que la catégorie).
    /// </summary>
    private static async Task<OnlineResult> FromGooglePlay(string package)
    {
        using var resp = await Http.GetAsync($"https://play.google.com/store/apps/details?id={Uri.EscapeDataString(package)}&hl=fr&gl=FR");
        if (!resp.IsSuccessStatusCode) return null;
        var html = await resp.Content.ReadAsStringAsync();
        if (html.Length > 3_000_000) html = html.Substring(0, 3_000_000);
        // Données structurées de la page (schema.org SoftwareApplication) : nom et catégorie exacts
        var ld = string.Join("\n", System.Text.RegularExpressions.Regex.Matches(html, @"<script[^>]*application/ld\+json[^>]*>(.*?)</script>",
                     System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                     .Select(m => m.Groups[1].Value));
        string Json(string prop) =>
            System.Text.RegularExpressions.Regex.Match(ld, "\"" + prop + "\"\\s*:\\s*\"([^\"]{1,400})\"").Groups[1].Value;
        var title = System.Net.WebUtility.HtmlDecode(Json("name"));
        var category = Json("applicationCategory");
        var summary = PageSummary(html);
        if (title.Length == 0)
            title = System.Text.RegularExpressions.Regex.Replace(summary.Split('\n').FirstOrDefault() ?? "", @"\s+[–-]\s+.*(Google Play).*$", "").Trim();
        var theme = Settings.ThemeFromName(title);
        string evidence = theme != null ? $"« {title} »" : null;
        if (theme == null && (theme = ThemeFromPlayCategory(category)) != null) evidence = $"« {title} », catégorie {category}";
        if (theme == null && (theme = Decide(summary, out var ev)) != null) evidence = $"« {title} » : {ev}";
        return theme == null ? null : new OnlineResult { Category = theme, Source = "Google Play " + package, Evidence = evidence, Date = DateTime.Now };
    }

    // ------------------------------------------------------------------ indices contenus dans le programme
    private static readonly System.Text.RegularExpressions.Regex HostRx = new(
        // Les chaînes d'un programme se suivent souvent sans séparateur (« …noobs.comhttps://… ») : liste fermée d'extensions
        @"https?://(?:www\.)?((?:[a-z0-9-]+\.)+(?:com|net|org|fr|io|app|gg|tv|de|eu|me|co|dev|info|be|ch|ca|us|jp|uk|xyz|gl|ly|to|es|it|pl|nl|br|ru))(?=[^a-z0-9.-]|https?:|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Sites trop généraux pour renseigner sur une application.</summary>
    private static readonly string[] CommonHosts =
    {
        "microsoft.com", "windows.com", "live.com", "google.com", "googleapis.com", "gstatic.com", "github.com", "githubusercontent.com",
        "sentry.io", "sentry-cdn.com", "docs.rs", "crates.io", "npmjs.com", "nodejs.org", "python.org", "w3.org", "whatwg.org", "mozilla.org",
        "apple.com", "youtube.com", "youtu.be", "twitch.tv", "x.com", "twitter.com", "facebook.com", "discord.com", "discord.gg", "reddit.com",
        "digicert.com", "verisign.com", "symantec.com", "globalsign.com", "sectigo.com", "usertrust.com", "comodoca.com", "letsencrypt.org",
        "schemas.microsoft.com", "openssl.org", "codeberg.org", "electronjs.org", "chromium.org", "tauri.app", "example.com", "localhost.com"
    };

    /// <summary>
    /// Sites cités par le programme lui-même (lu sur le PC, 64 Mo au plus) :
    /// - un site qui porte le nom de l'application devient son site officiel ;
    /// - un programme qui n'est pas un jeu mais cite plusieurs sites d'un même jeu (dofusdb.fr, dofuspourlesnoobs.com…)
    ///   est un outil pour ce jeu.
    /// </summary>
    internal static (string homepage, OnlineResult result) EmbeddedHints(AppItem app)
    {
        try
        {
            if (app.LooksLikeGame || AppKinds.IsHosted(app.Target)) return default;
            var exe = app.FilePath != null && app.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Shortcuts.GetTargetPath(app.FilePath)
                    : AppScanner.ResolveAppsFolderPath(app.Aumid);
            exe = AppScanner.NormalizePath(exe);
            if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return default;
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win) && exe.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return default;
            var info = new FileInfo(exe);
            if (info.Length > 64L * 1024 * 1024) return default;
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(exe));
            var hosts = HostRx.Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant())
                .Where(h => !CommonHosts.Any(c => h == c || h.EndsWith("." + c))).Distinct().Take(400).ToList();
            return AnalyzeHosts(app.Name, hosts);
        }
        catch (Exception ex) { Log.Error("Lecture des indices de " + app.Name, ex); return default; }
    }

    /// <summary>Site officiel et éventuel jeu cité, d'après la liste des sites cités par le programme.</summary>
    internal static (string homepage, OnlineResult result) AnalyzeHosts(string appName, IList<string> hosts)
    {
        var simple = Simple(AppKinds.StripShortcutSuffix(appName));
        string homepage = null;
        if (simple.Length >= 4)
        {
            var own = hosts.Where(h => Simple(h.Split('.')[0]).StartsWith(simple) || Simple(h.Replace("-", "")).StartsWith(simple))
                           .OrderBy(h => h.Length).FirstOrDefault();
            if (own != null) homepage = "https://" + own;
        }
        // Jeux cités dans les adresses (« dofusdb.fr » cite Dofus) : au moins deux sites différents
        var games = Settings.GameTitlesIn(hosts);
        var top = games.GroupBy(g => g.game, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        OnlineResult result = null;
        if (top != null && top.Count() >= 2 && Settings.IsAvailable(Settings.GameToolsCategory))
            result = new OnlineResult
            {
                Category = Settings.GameToolsCategory, Source = "contenu du programme",
                Evidence = $"cite {top.Count()} sites sur {top.Key} ({string.Join(", ", top.Take(3).Select(g => g.host))})", Date = DateTime.Now
            };
        return (homepage, result);
    }

    // ------------------------------------------------------------------ site de l'éditeur
    /// <summary>
    /// Page d'accueil indiquée par l'application elle-même (programme d'installation, package.json) :
    /// titre et description de la page. C'est la source la plus directe pour les logiciels peu connus.
    /// </summary>
    private static async Task<OnlineResult> FromWebsite(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http")) return null;
        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType?.Contains("html") != true) return null;
        using var stream = await resp.Content.ReadAsStreamAsync();
        var buf = new char[200_000];
        using var reader = new StreamReader(stream);
        int n = await reader.ReadBlockAsync(buf, 0, buf.Length);
        var text = PageSummary(new string(buf, 0, n));
        var cat = Decide(text, out var evidence);
        return cat == null ? null : new OnlineResult { Category = cat, Source = "site " + uri.Host, Evidence = evidence, Date = DateTime.Now };
    }

    /// <summary>Titre, description et mots-clés d'une page web (balises title et meta).</summary>
    internal static string PageSummary(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var parts = new List<string>();
        var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (title.Success) parts.Add(title.Groups[1].Value);
        foreach (Match m in Regex.Matches(html, @"<meta\s[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var key = Regex.Match(tag, @"(?:name|property)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            if (key is not ("description" or "keywords" or "og:description" or "og:title" or "twitter:description")) continue;
            var content = Regex.Match(tag, @"content\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase).Groups[1].Value;
            if (content.Length > 0) parts.Add(content);
        }
        return System.Net.WebUtility.HtmlDecode(string.Join("\n", parts.Distinct()));
    }

    // ------------------------------------------------------------------ GitHub
    /// <summary>Logiciels libres et outils de passionnés : description et sujets du dépôt GitHub du même nom.</summary>
    private static async Task<OnlineResult> FromGitHub(string name, AppItem app = null, string homepage = null)
    {
        var clean = Clean(name);
        if (Simple(clean).Length < 4) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(clean)}+in:name&per_page=8");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;   // limite de requêtes atteinte : ce sera pour la prochaine fois
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("items", out var items)) return null;
        // Dépôts au nom de l'application (« ganymede », « ganymede-app », « ganymede-desktop »…)
        string Core(string repoName) => System.Text.RegularExpressions.Regex.Replace(Simple(repoName), "(app|desktop|client|launcher|windows|gui)$", "");
        var candidates = items.EnumerateArray().Take(8)
            .Where(it => it.TryGetProperty("name", out var nm) && (Simple(nm.GetString()) == Simple(clean) || Core(nm.GetString()) == Simple(clean)))
            .ToList();
        // Homonymes : plusieurs dépôts possibles → seul celui que confirme le site ou l'éditeur de l'application est retenu
        if (candidates.Count > 1)
        {
            var host = Uri.TryCreate(homepage ?? "", UriKind.Absolute, out var hu) ? hu.Host.Replace("www.", "") : null;
            var pub = app?.Publisher == null ? null : Simple(app.Publisher);
            candidates = candidates.Where(it =>
            {
                var hp = it.TryGetProperty("homepage", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : "";
                var full = it.TryGetProperty("full_name", out var f) ? f.GetString() : "";
                return (host != null && hp.Contains(host, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(pub) && pub.Length >= 3 && Simple(full.Split('/')[0]).Contains(pub));
            }).ToList();
        }
        foreach (var it in candidates)
        {
            var repo = it.TryGetProperty("name", out var nm) ? nm.GetString() : "";
            var parts = new List<string>();
            if (it.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) parts.Add(d.GetString());
            if (it.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array)
                parts.AddRange(t.EnumerateArray().Select(x => x.GetString()?.Replace('-', ' ')));
            var text = string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
            var cat = Decide(text, out var evidence);
            if (cat != null)
                return new OnlineResult { Category = cat, Source = "GitHub " + (it.TryGetProperty("full_name", out var fn) ? fn.GetString() : repo),
                                          Evidence = evidence, Date = DateTime.Now };
        }
        return null;
    }

    // ------------------------------------------------------------------ Wikidata
    private static async Task<OnlineResult> FromWikidata(string name)
    {
        var clean = Clean(name);
        if (clean.Length < 3) return null;
        foreach (var lang in new[] { "fr", "en" })
        {
            var url = $"https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&type=item&limit=7&language={lang}&uselang={lang}&search={Uri.EscapeDataString(clean)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("search", out var results)) continue;
            // Le libellé doit correspondre au nom de l'appli pour éviter les homonymes
            var ids = results.EnumerateArray()
                .Where(r => r.TryGetProperty("label", out var l) && Simple(l.GetString()) == Simple(clean))
                .Select(r => (id: r.GetProperty("id").GetString(), desc: r.TryGetProperty("description", out var d) ? d.GetString() : ""))
                .ToList();
            foreach (var (qid, desc) in ids)
            {
                // Nature (P31), genre (P136) et usage (P366) : plus fiables que la seule description
                var facts = await WikidataFacts(qid);
                var text = string.Join("\n", new[] { desc }.Concat(facts));
                // Homonymes : « Ganymède » est aussi une lune de Jupiter. Seul un logiciel ou un jeu est retenu.
                if (!LooksLikeSoftware(text)) continue;
                var cat = Decide(text, out var evidence);
                if (cat != null)
                    return new OnlineResult { Category = cat, Source = "Wikidata " + qid, Evidence = evidence, Date = DateTime.Now };
            }
        }
        return null;
    }

    private static async Task<List<string>> WikidataFacts(string qid)
    {
        var res = new List<string>();
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(
            $"https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=claims&ids={qid}"));
        if (!doc.RootElement.TryGetProperty("entities", out var ents) || !ents.TryGetProperty(qid, out var ent)
            || !ent.TryGetProperty("claims", out var claims)) return res;
        var values = new List<string>();
        foreach (var prop in new[] { "P31", "P136", "P366" })
            if (claims.TryGetProperty(prop, out var list))
                foreach (var c in list.EnumerateArray())
                    if (c.TryGetProperty("mainsnak", out var ms) && ms.TryGetProperty("datavalue", out var dv)
                        && dv.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var vid))
                        values.Add(vid.GetString());
        values = values.Distinct().Take(20).ToList();
        if (values.Count == 0) return res;
        using var labels = JsonDocument.Parse(await Http.GetStringAsync(
            $"https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=labels&languages=en|fr&ids={string.Join("|", values)}"));
        if (!labels.RootElement.TryGetProperty("entities", out var le)) return res;
        foreach (var e in le.EnumerateObject())
            if (e.Value.TryGetProperty("labels", out var ls))
                foreach (var l in ls.EnumerateObject())
                    if (l.Value.TryGetProperty("value", out var lv)) res.Add(lv.GetString());
        return res;
    }

    private static readonly Regex SoftwareWords = new(
        @"\b(software|logiciel\w*|application\w*|app|apps|program\w*|video ?game|jeu vidéo|jeux vidéo|computer game|mobile game|game engine|"
        + @"web browser|navigateur|client|launcher|lanceur|utility|utilitaire|tool|outil|framework|ide|editor|éditeur de|emulator|émulateur|"
        + @"freeware|shareware|open[- ]source|free software|logiciel libre|operating system|système d'exploitation|driver|pilote|plugin|extension|"
        + @"web service|service en ligne|online service|streaming service|messaging|messagerie|video game console|developer|développeur)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>La fiche décrit-elle un logiciel ou un jeu (et pas un lieu, une personne, un astre…) ?</summary>
    internal static bool LooksLikeSoftware(string text) => !string.IsNullOrWhiteSpace(text) && SoftwareWords.IsMatch(text);

    /// <summary>Compte les termes reconnus par thème ; le thème avec le plus de termes distincts l'emporte.</summary>
    internal static string Decide(string text, out string evidence)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        // On ignore les liens (sites, licences…) qui faussent le comptage
        text = string.Join("\n", text.Split('\n').Where(l => !l.Contains("http", StringComparison.OrdinalIgnoreCase)));
        var scores = new List<(string cat, int score, string terms)>();
        foreach (var (cat, rx) in LexiconRx)
        {
            if (!Settings.IsAvailable(cat)) continue;
            var terms = rx.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
            // Les expressions de plusieurs mots comptent double (plus précises)
            int score = terms.Sum(t => t.Contains(' ') ? 2 : 1);
            if (score > 0) scores.Add((cat, score, string.Join(", ", terms)));
        }
        // Un jeu cité (« Dofus », « video game ») et des mots d'outil (« guide », « aide aux quêtes ») : outil pour ce jeu
        // (un titre de jeu cité seul compte si rien d'autre ne domine : « écrit en Rust » ne fait pas d'un outil de développement un outil de jeu)
        bool game = scores.Any(x => x.cat == "Jeux")
                    || (Settings.MentionsGame(text) && scores.All(x => x.score < 2));
        var tool = Settings.GameToolWords.Match(text);
        if (game && tool.Success && Settings.IsAvailable(Settings.GameToolsCategory) && !scores.Any(x => x.cat == "Lanceurs de jeux" && x.score >= 2))
        {
            evidence = "jeu + « " + tool.Value.ToLowerInvariant() + " »";
            return Settings.GameToolsCategory;
        }
        if (scores.Count == 0) return null;
        var ordered = scores.OrderByDescending(s => s.score).ToList();
        if (ordered.Count > 1 && ordered[0].score == ordered[1].score) return null;   // trop ambigu
        evidence = ordered[0].terms;
        return ordered[0].cat;
    }

    /// <summary>Lit le tableau de « winget list » : nom et identifiant.</summary>
    private static List<(string name, string id)> ParseList(string output)
    {
        var res = new List<(string, string)>();
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l.Substring(l.LastIndexOf('\r') + 1) : l).ToList();
        int dash = lines.FindIndex(l => Regex.IsMatch(l, @"^-{10,}\s*$"));
        if (dash < 1) return res;
        var header = lines[dash - 1];
        var cols = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) cols.Add(i);
        if (cols.Count < 3) return res;
        cols[0] = 0;
        for (int i = dash + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length < cols[2]) continue;
            var name = line.Substring(0, cols[1]).Trim();
            var id = line.Substring(cols[1], Math.Min(cols[2], line.Length) - cols[1]).Trim();
            if (name.Length > 0 && id.Length > 0 && !id.Contains(' ')) res.Add((name, id));
        }
        return res;
    }
}
