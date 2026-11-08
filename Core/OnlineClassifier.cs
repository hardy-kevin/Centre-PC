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
/// Affine le tri des applis restées dans « Autres » grâce à Internet, sans clé ni compte :
/// 1) winget (outil d'installation de Windows) : étiquettes et description du logiciel
/// 2) Wikidata (base de Wikipédia) : courte description (« jeu vidéo », « navigateur web »…)
/// Seul le nom de l'appli est envoyé. Les résultats sont gardés en cache sur le PC.
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
            "digital distribution", "distribution numérique", "mod manager", "gestionnaire de mods", "emulator", "émulateur", "cloud gaming" }),
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
            "bootable usb", "data recovery", "récupération de données", "duplicate finder" })
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
        return _cache.TryGetValue(Key(name), out var r) && r.Category != null && Settings.Find(r.Category) != null ? r.Category : null;
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
        try { wingetList = ParseList(await Updates.RunCaptureAsync("winget", "list --accept-source-agreements --disable-interactivity")); }
        catch { }

        using var gate = new SemaphoreSlim(3);
        var tasks = todo.Select(async app =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await FromWinget(app.Name, wingetList) ?? await FromWikidata(app.Name);
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

    private static async Task<OnlineResult> FromWikidata(string name)
    {
        var clean = Regex.Replace(Settings.Normalize(name), @"\s*\(.*?\)\s*|\s+(x64|x86|64-bit|32-bit)$", " ", RegexOptions.IgnoreCase).Trim();
        if (clean.Length < 3) return null;
        foreach (var lang in new[] { "fr", "en" })
        {
            var url = $"https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&type=item&limit=5&language={lang}&uselang={lang}&search={Uri.EscapeDataString(clean)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("search", out var results)) continue;
            foreach (var r in results.EnumerateArray())
            {
                var label = r.TryGetProperty("label", out var l) ? l.GetString() : "";
                var desc = r.TryGetProperty("description", out var d) ? d.GetString() : "";
                // Le libellé doit correspondre au nom de l'appli pour éviter les homonymes
                if (string.IsNullOrEmpty(desc) || Simple(label) != Simple(clean)) continue;
                var cat = Decide(desc, out var evidence);
                if (cat != null)
                    return new OnlineResult { Category = cat, Source = "wikidata " + (r.TryGetProperty("id", out var id) ? id.GetString() : ""), Evidence = desc, Date = DateTime.Now };
            }
        }
        return null;
    }

    /// <summary>Compte les termes reconnus par thème ; le thème avec le plus de termes distincts l'emporte.</summary>
    private static string Decide(string text, out string evidence)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        // On ignore les liens (sites, licences…) qui faussent le comptage
        text = string.Join("\n", text.Split('\n').Where(l => !l.Contains("http", StringComparison.OrdinalIgnoreCase)));
        var scores = new List<(string cat, int score, string terms)>();
        foreach (var (cat, rx) in LexiconRx)
        {
            if (Settings.Find(cat) == null) continue;
            var terms = rx.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
            // Les expressions de plusieurs mots comptent double (plus précises)
            int score = terms.Sum(t => t.Contains(' ') ? 2 : 1);
            if (score > 0) scores.Add((cat, score, string.Join(", ", terms)));
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
