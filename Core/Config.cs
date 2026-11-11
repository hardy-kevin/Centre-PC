using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace CentrePC.Core;

public class Category
{
    public string Name { get; set; }
    public string Glyph { get; set; }
    public string Color { get; set; }
    public List<string> Keywords { get; set; } = new();
}

public class Config
{
    public List<Category> Categories { get; set; }
    public string DefaultCategory { get; set; } = "Autres";
    /// <summary>Thème choisi à la main : nom de l'application → catégorie.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new();
    /// <summary>Applis épinglées en favoris (page Applications).</summary>
    public List<string> Favorites { get; set; } = new();
    /// <summary>Noms de fichiers qui restent toujours sur le bureau.</summary>
    public List<string> KeepOnDesktop { get; set; } = new() { "Centre PC", "Corbeille", "Recycle Bin" };
    /// <summary>Fichier rangé (nom dans le dossier de rangement) → chemin d'origine.</summary>
    public Dictionary<string, string> Stored { get; set; } = new();
    public bool TidyFiles { get; set; } = true;
    /// <summary>Vérifier les mises à jour chaque jour, même quand Centre PC est fermé (notification Windows).</summary>
    public bool DailyCheck { get; set; } = true;
    /// <summary>Arborescence de rangement des documents (chemins de dossiers dans Documents, Images, Musique, Vidéos).</summary>
    public List<string> DocFolders { get; set; } = new();
    /// <summary>Derniers documents rangés (accès rapide).</summary>
    public List<string> DocRecent { get; set; } = new();
    /// <summary>L'utilisateur a refusé l'arborescence de départ proposée.</summary>
    public bool DocStarterDismissed { get; set; }
    /// <summary>Éléments dont l'utilisateur a refusé le rangement proposé : plus jamais proposés.</summary>
    public List<string> DocRejected { get; set; } = new();
    /// <summary>Rangements effectués par le plan, en attente de validation (remise à sa place possible).</summary>
    public List<DocReviewEntry> DocReview { get; set; } = new();
    /// <summary>Plan de rangement remis à plus tard jusqu'à cette date.</summary>
    public DateTime? DocPlanSnoozedUntil { get; set; }
    /// <summary>« app » : rangement dans Centre PC (bureau vide) ; « folders » : dossiers de thème sur le bureau.</summary>
    public string TidyMode { get; set; } = "app";
    /// <summary>Applis masquées par l'utilisateur (n'apparaissent plus dans Applications).</summary>
    public List<string> HiddenApps { get; set; } = new();
    /// <summary>Applis volontairement laissées dans « Autres » (l'assistant de tri ne les repropose pas).</summary>
    public List<string> SortSkipped { get; set; } = new();
    /// <summary>Applications obsolètes que l'utilisateur garde : plus signalées par le bandeau.</summary>
    public List<string> ObsoleteKept { get; set; } = new();
    /// <summary>Catégories proposées par Centre PC que l'utilisateur a supprimées : elles ne sont plus recréées automatiquement.</summary>
    public List<string> DeclinedThemes { get; set; } = new();
    /// <summary>Dossiers d'applications créés par l'utilisateur (glisser-déposer) : nom → applications.</summary>
    public Dictionary<string, List<string>> AppFolders { get; set; } = new();
    /// <summary>Applications sorties à la main d'un regroupement automatique.</summary>
    public List<string> SuiteExcluded { get; set; } = new();
    /// <summary>Identifier automatiquement en ligne les applications inconnues.</summary>
    public bool AutoOnline { get; set; } = true;
    public bool OnboardingDone { get; set; }
    /// <summary>Accès rapide : icône près de l'horloge et raccourci clavier ouvrant le lanceur (proposé une fois, jamais imposé).</summary>
    public bool QuickAccess { get; set; }
    public bool QuickAccessAsked { get; set; }
    /// <summary>Raccourci du lanceur (« modificateurs:touche », « none ») ; voir <see cref="HotkeyCombo"/>.</summary>
    public string QuickAccessHotkey { get; set; } = "3:32";
    /// <summary>Réglages d'autres applications modifiés pour libérer un raccourci, remis à l'identique ensuite.</summary>
    public List<HotkeyBackup> HotkeyBackups { get; set; } = new();
    /// <summary>Propriétaires identifiés par l'utilisateur (« Identifier… » : il appuie, on regarde quelle fenêtre réagit).</summary>
    public Dictionary<string, string> HotkeyIdentified { get; set; } = new();
    /// <summary>Réglages de Windows corrigés depuis Périphériques : valeur d'origine, remise par « Annuler ».</summary>
    public Dictionary<string, string> DeviceFixBackups { get; set; } = new();
    /// <summary>Tests du processeur et de la carte graphique (30 derniers), pour comparer d'une fois sur l'autre.</summary>
    public List<BenchResult> BenchHistory { get; set; } = new();
    /// <summary>Démarrer à l'ouverture de session, sans fenêtre (seulement l'icône près de l'horloge).</summary>
    public bool StartWithWindows { get; set; }
    public bool TrayHintShown { get; set; }
    /// <summary>Applications lancées récemment (les plus récentes d'abord).</summary>
    public List<string> RecentApps { get; set; } = new();
    /// <summary>Vérifier au démarrage si une nouvelle version de Centre PC est publiée.</summary>
    public bool AutoCheckAppUpdate { get; set; } = true;
    /// <summary>Version proposée et refusée : elle n'est plus proposée au démarrage (reste installable dans Réglages).</summary>
    public string SkippedAppVersion { get; set; }
    public List<string> WingetExclusions { get; set; } = new();
    /// <summary>
    /// Mises à jour installées avec succès mais toujours proposées par winget (version mal lue) :
    /// identifiant → « version actuelle→version proposée » au moment de l'installation.
    /// </summary>
    public Dictionary<string, string> PhantomUpdates { get; set; } = new();
    /// <summary>Pilotes à ne plus proposer (identifiant matériel du périphérique).</summary>
    public List<string> DriverExclusions { get; set; } = new();
    /// <summary>Nom affiché des pilotes exclus (identifiant matériel → nom du périphérique), pour la page Réglages.</summary>
    public Dictionary<string, string> DriverExclusionNames { get; set; } = new();
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>État d'origine des icônes personnalisées (pour revenir en arrière).</summary>
    public List<IconBackup> IconBackups { get; set; } = new();
    public string ArrowPreviousValue { get; set; }
    /// <summary>Dernière tentative de réparation de l'icône qui masque les flèches (carrés noirs).</summary>
    public DateTime? ArrowRepairAsked { get; set; }
    public bool StyleFolders { get; set; } = true;
    /// <summary>Version des mots-clés par défaut fusionnés dans cette configuration.</summary>
    public int DefaultsVersion { get; set; }
}

/// <summary>Chargement / sauvegarde de la configuration et classement des applications par thème.</summary>
public static class Settings
{
    /// <summary>
    /// Dossier de Centre PC. Version Store : Windows isole AppData pour les applications du Store (invisible pour l'Explorateur
    /// et supprimé à la désinstallation) ; on utilise donc Documents\Centre PC pour que les raccourcis rangés et les icônes
    /// restent visibles par Windows et ne soient jamais perdus.
    /// </summary>
    public static readonly string DataDir = Native.IsPackaged
        ? Path.Combine(Paths.Get(Environment.SpecialFolder.MyDocuments), "Centre PC")
        : Path.Combine(Paths.Get(Environment.SpecialFolder.LocalApplicationData), "CentrePC");
    public static readonly string StoreDir = Path.Combine(DataDir, "Bureau rangé");
    private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");

    public static Config Current { get; private set; }
    public const string FilesCategory = "Fichiers";
    public const string FavoritesCategory = "Favoris";

    private static List<(Category cat, Regex rx)> _rules = new();
    private static Dictionary<string, string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private static List<(Category cat, Regex rx)> _weak = new();
    private static readonly Dictionary<string, Brush> _brushes = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Load()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(StoreDir);
        Config cfg = null;
        bool firstRun = !File.Exists(ConfigPath);
        try
        {
            if (File.Exists(ConfigPath))
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOpts);
        }
        catch (Exception ex) { Log.Error("Lecture de config.json", ex); }
        // Fichier abîmé (coupure de courant pendant l'écriture…) : on reprend la copie de secours
        if (cfg == null && File.Exists(BackupPath))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(BackupPath), JsonOpts);
                if (cfg != null) { firstRun = false; Log.Info("config.json abîmé : copie de secours utilisée"); }
            }
            catch (Exception ex) { Log.Error("Lecture de config.bak", ex); }
        }
        cfg ??= new Config();
        if (cfg.Categories == null || cfg.Categories.Count == 0) { cfg.Categories = DefaultCategories(); cfg.DefaultsVersion = DefaultsVersion; }
        if (cfg.DefaultsVersion < DefaultsVersion) MergeDefaults(cfg);
        cfg.Overrides = new Dictionary<string, string>(cfg.Overrides ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Stored = new Dictionary<string, string>(cfg.Stored ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Favorites ??= new(); cfg.KeepOnDesktop ??= new(); cfg.WingetExclusions ??= new(); cfg.PhantomUpdates ??= new(); cfg.DriverExclusions ??= new(); cfg.DocFolders ??= new(); cfg.DocRecent ??= new(); cfg.DriverExclusionNames ??= new(); cfg.IconBackups ??= new(); cfg.HiddenApps ??= new(); cfg.SortSkipped ??= new(); cfg.ObsoleteKept ??= new(); cfg.DeclinedThemes ??= new(); cfg.SuiteExcluded ??= new(); cfg.DocRejected ??= new(); cfg.DocReview ??= new(); cfg.RecentApps ??= new(); cfg.HotkeyBackups ??= new(); cfg.HotkeyIdentified ??= new(); cfg.DeviceFixBackups ??= new(); cfg.BenchHistory ??= new();
        cfg.AppFolders = new Dictionary<string, List<string>>(cfg.AppFolders ?? new(), StringComparer.CurrentCultureIgnoreCase);
        if (!cfg.KeepOnDesktop.Contains("Centre PC", StringComparer.OrdinalIgnoreCase)) cfg.KeepOnDesktop.Add("Centre PC");
        Current = cfg;
        BuildRules();
        // Déploiement : une configuration « CentrePC.config.json » posée à côté de l'exe est importée au premier lancement
        if (firstRun)
        {
            try
            {
                var beside = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "CentrePC.config.json");
                if (File.Exists(beside)) Import(beside);
            }
            catch { }
        }
        Save();
    }

    // ------------------------------------------------------------------ Export / import de la configuration
    public class PortableConfig
    {
        public string Format { get; set; } = "CentrePC";
        public int FormatVersion { get; set; } = 1;
        public DateTime Exported { get; set; } = DateTime.Now;
        public List<Category> Categories { get; set; }
        public string DefaultCategory { get; set; }
        public Dictionary<string, string> Overrides { get; set; }
        public List<string> HiddenApps { get; set; }
        public List<string> SortSkipped { get; set; }
        public List<string> Favorites { get; set; }
        public List<string> KeepOnDesktop { get; set; }
        public List<string> WingetExclusions { get; set; }
        public string TidyMode { get; set; }
        public bool? TidyFiles { get; set; }
        public bool? StyleFolders { get; set; }
        public bool? AutoOnline { get; set; }
        /// <summary>Arborescence des documents, portable d'un PC à l'autre : « documents/Jeux/Dofus ».</summary>
        public List<string> DocTree { get; set; }
    }

    /// <summary>Exporte thèmes, choix et préférences (sans ce qui est propre à ce PC : rangement, sauvegardes d'icônes).</summary>
    public static void Export(string path)
    {
        var c = Current;
        var p = new PortableConfig
        {
            Categories = c.Categories, DefaultCategory = c.DefaultCategory, Overrides = c.Overrides.ToDictionary(k => k.Key, k => k.Value),
            HiddenApps = c.HiddenApps, SortSkipped = c.SortSkipped, Favorites = c.Favorites, KeepOnDesktop = c.KeepOnDesktop,
            WingetExclusions = c.WingetExclusions, TidyMode = c.TidyMode, TidyFiles = c.TidyFiles, StyleFolders = c.StyleFolders,
            AutoOnline = c.AutoOnline,
            DocTree = DocumentLibrary.Folders().Where(f => !string.IsNullOrEmpty(f.Relative) && DocumentLibrary.IsUnder(f.Path, f.Root.Path))
                .Select(f => f.Root.Key + "/" + System.IO.Path.GetRelativePath(f.Root.Path, f.Path).Replace('\\', '/')).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(p, JsonOpts));
    }

    /// <summary>Importe une configuration exportée (remplace thèmes, choix et préférences).</summary>
    public static void Import(string path)
    {
        var p = JsonSerializer.Deserialize<PortableConfig>(File.ReadAllText(path), JsonOpts);
        if (p == null || p.Format != "CentrePC") throw new InvalidDataException("Ce fichier n'est pas une configuration Centre PC.");
        var c = Current;
        if (p.Categories?.Count > 0) { c.Categories = p.Categories; c.DefaultsVersion = DefaultsVersion; }
        if (!string.IsNullOrWhiteSpace(p.DefaultCategory)) c.DefaultCategory = p.DefaultCategory;
        if (p.Overrides != null) c.Overrides = new Dictionary<string, string>(p.Overrides, StringComparer.OrdinalIgnoreCase);
        if (p.HiddenApps != null) c.HiddenApps = p.HiddenApps;
        if (p.SortSkipped != null) c.SortSkipped = p.SortSkipped;
        if (p.Favorites != null) c.Favorites = p.Favorites;
        if (p.KeepOnDesktop != null) c.KeepOnDesktop = p.KeepOnDesktop;
        if (p.WingetExclusions != null) c.WingetExclusions = p.WingetExclusions;
        // Arborescence des documents : les dossiers sont créés (rien n'est supprimé)
        foreach (var t in p.DocTree ?? new())
        {
            var parts = t.Split('/', 2);
            var root = DocumentLibrary.Roots.FirstOrDefault(r => r.Key == parts[0]);
            if (root == null || parts.Length < 2 || string.IsNullOrEmpty(root.Path)) continue;
            var rel = parts[1].Replace('/', '\\');
            if (rel.Split('\\').Any(x => DocumentLibrary.CheckName(x) != null || x == "..")) continue;
            try { DocumentLibrary.CreateFolder(root.Path, rel); } catch { }
        }
        if (p.TidyMode != null) c.TidyMode = p.TidyMode;
        if (p.TidyFiles != null) c.TidyFiles = p.TidyFiles.Value;
        if (p.StyleFolders != null) c.StyleFolders = p.StyleFolders.Value;
        if (p.AutoOnline != null) c.AutoOnline = p.AutoOnline.Value;
        if (!c.KeepOnDesktop.Contains("Centre PC", StringComparer.OrdinalIgnoreCase)) c.KeepOnDesktop.Add("Centre PC");
        Rebuild();
        Save();
    }

    private static readonly string BackupPath = Path.Combine(DataDir, "config.bak");
    private static readonly object SaveGate = new();
    private static System.Threading.Timer _saveTimer;
    private static bool _dirty;

    /// <summary>Instance administrateur lancée pour une action précise : elle ne réécrit jamais la configuration.</summary>
    public static bool ReadOnly { get; set; }

    /// <summary>
    /// Enregistre la configuration. Les demandes rapprochées (assistant de tri, rangement…) sont regroupées :
    /// l'écriture a lieu 0,4 s après la dernière demande, et au plus tard à la fermeture (<see cref="Flush"/>).
    /// </summary>
    public static void Save()
    {
        if (ReadOnly || Current == null) return;
        lock (SaveGate)
        {
            _dirty = true;
            _saveTimer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Écrit tout de suite ce qui est en attente. Écriture sûre : fichier temporaire puis remplacement,
    /// l'ancienne version devenant config.bak (une coupure ne peut pas laisser un fichier à moitié écrit).
    /// </summary>
    public static void Flush()
    {
        if (ReadOnly) return;
        lock (SaveGate)
        {
            if (!_dirty || Current == null) return;
            try
            {
                var json = JsonSerializer.Serialize(Current, JsonOpts);
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, BackupPath, ignoreMetadataErrors: true);
                else File.Move(tmp, ConfigPath);
                _dirty = false;
            }
            catch (Exception ex) { Log.Error("Enregistrement de config.json", ex); }
        }
    }

    /// <summary>Augmenter ce numéro quand les mots-clés par défaut changent : ils seront ajoutés à la config existante.</summary>
    private const int DefaultsVersion = DefaultThemes.Version;

    /// <summary>Ajoute les nouveaux mots-clés par défaut sans perdre ceux que l'utilisateur a ajoutés.</summary>
    private static readonly string[] OldDefaultNames =
        { "Jeux", "Lanceurs de jeux", "Internet et communication", "Multimédia", "Bureautique", "Développement", "Matériel et pilotes", "Outils système" };

    private static void MergeDefaults(Config cfg)
    {
        var defs = DefaultThemes.Build();
        if (cfg.DefaultsVersion < 3)
        {
            // Nouvelle liste de thèmes : on garde les thèmes créés par l'utilisateur
            var custom = cfg.Categories.Where(c => !OldDefaultNames.Contains(c.Name) && !defs.Any(d => d.Name == c.Name)).ToList();
            cfg.Categories = defs.Concat(custom).ToList();
            // Choix manuels vers un thème qui n'existe plus : retirés (l'application est triée à nouveau automatiquement)
            foreach (var k in cfg.Overrides.Where(kv => !cfg.Categories.Any(c => c.Name == kv.Value)).Select(kv => kv.Key).ToList())
                cfg.Overrides.Remove(k);
            // Les résultats en ligne utilisaient les anciens thèmes : nouvelle recherche
            try { File.Delete(Path.Combine(DataDir, "classement-en-ligne.json")); } catch { }
        }
        else
        {
            // Mots-clés retirés ou déplacés par les versions intermédiaires
            foreach (var (ver, byTheme) in DefaultThemes.Removed.Where(r => r.Key > cfg.DefaultsVersion && r.Key <= DefaultsVersion))
                foreach (var (theme, words) in byTheme)
                {
                    var cur = cfg.Categories.FirstOrDefault(c => c.Name.Equals(theme, StringComparison.OrdinalIgnoreCase));
                    cur?.Keywords?.RemoveAll(k => words.Contains(k, StringComparer.OrdinalIgnoreCase));
                }
            // Version 4 : les fiches Wikidata qui ne décrivaient pas un logiciel (homonymes) sont recherchées à nouveau
            if (cfg.DefaultsVersion < 4) { try { OnlineClassifier.ForgetSource("Wikidata"); } catch { } }
            // Version 6 : homonymes écartés (Steam, GitHub) et nouvelles sources : toutes les recherches en ligne sont refaites
            if (cfg.DefaultsVersion < 6) { try { OnlineClassifier.Forget(); } catch { } }
            foreach (var def in defs)
            {
                var cur = cfg.Categories.FirstOrDefault(c => c.Name == def.Name);
                if (cur == null) { cfg.Categories.Add(def); continue; }
                cur.Keywords = (cur.Keywords ?? new()).Union(def.Keywords, StringComparer.OrdinalIgnoreCase).ToList();
                cur.Glyph ??= def.Glyph; cur.Color ??= def.Color;
            }
        }
        cfg.DefaultsVersion = DefaultsVersion;
    }

    /// <summary>Remet les thèmes et mots-clés par défaut (les choix manuels vers des thèmes disparus sont retirés).</summary>
    public static void ResetThemes()
    {
        Current.Categories = DefaultThemes.Build();
        Current.DeclinedThemes.Clear();
        foreach (var k in Current.Overrides.Where(kv => !Current.Categories.Any(c => c.Name == kv.Value) && DefaultThemes.CatalogEntry(kv.Value).cat == null)
                                           .Select(kv => kv.Key).ToList())
            Current.Overrides.Remove(k);
        Current.DefaultsVersion = DefaultsVersion;
        Rebuild();
        Save();
    }

    /// <summary>À appeler après une modification des thèmes.</summary>
    public static void Rebuild()
    {
        _brushes.Clear();
        BuildRules();
    }

    private static void BuildRules()
    {
        var rules = new List<(Category cat, Regex rx)>();
        var weak = new List<(Category cat, Regex rx)>();
        var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Catégories existantes, puis catégories du catalogue (créées automatiquement à la première application qui en relève)
        var all = Current.Categories.Concat(DefaultThemes.Catalog.Select(c => c.cat)
            .Where(c => Find(c.Name) == null && !IsDeclined(c.Name))).ToList();
        foreach (var c in all)
        {
            // « =Run » : nom exact uniquement (« Run » seul attraperait « Temple Run »)
            foreach (var k in c.Keywords.Where(k => k != null && k.Length > 1 && k[0] == '='))
                exact.TryAdd(Normalize(k.Substring(1)), c.Name);
            // Les mots-clés les plus longs d'abord : « Dolby Access » passe avant « Access »
            var words = c.Keywords.Where(k => !string.IsNullOrWhiteSpace(k) && k[0] != '=').Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var strong = Pattern(words.Where(k => !DefaultThemes.WeakKeywords.Contains(k)));
            if (strong != null) rules.Add((c, strong));
            // Mots-clés trop généraux : utilisés en dernier recours seulement
            var loose = Pattern(words.Where(k => DefaultThemes.WeakKeywords.Contains(k)));
            if (loose != null) weak.Add((c, loose));
        }
        // Remplacement d'un bloc : un tri en cours sur un autre fil n'est jamais perturbé
        _exact = exact;
        _weak = weak;
        _rules = rules;
    }

    private static Regex Pattern(IEnumerable<string> keywords)
    {
        // Les mots-clés les plus longs d'abord : « Dolby Access » passe avant « Access »
        var parts = keywords.OrderByDescending(k => k.Length).Select(k =>
        {
            var e = Regex.Escape(k);
            if (char.IsLetterOrDigit(k[0])) e = @"(?<![\p{L}\d])" + e;
            if (char.IsLetterOrDigit(k[^1])) e += @"(?:s|x)?(?![\p{L}\d])";   // pluriel toléré
            return e;
        }).ToList();
        return parts.Count == 0 ? null : new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ------------------------------------------------------------------ catégories créées à la demande
    public const string GameToolsCategory = "Outils pour les jeux";

    private static bool IsDeclined(string name) => Current?.DeclinedThemes?.Contains(name, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>La catégorie existe, ou elle est au catalogue et peut être créée automatiquement.</summary>
    public static bool IsAvailable(string name) =>
        !string.IsNullOrEmpty(name) && (Find(name) != null || name == Current.DefaultCategory
                                        || (DefaultThemes.CatalogEntry(name).cat != null && !IsDeclined(name)));

    /// <summary>
    /// Crée les catégories du catalogue dont des applications relèvent (à appeler sur le fil de l'interface).
    /// Renvoie les noms des catégories créées.
    /// </summary>
    public static List<string> ActivateCategories(IEnumerable<string> used)
    {
        var created = new List<string>();
        foreach (var name in used.Where(n => n != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Find(name) != null || IsDeclined(name)) continue;
            var (cat, after) = DefaultThemes.CatalogEntry(name);
            if (cat == null) continue;
            var copy = new Category { Name = cat.Name, Glyph = cat.Glyph, Color = cat.Color, Keywords = cat.Keywords.ToList() };
            var i = Current.Categories.FindIndex(c => c.Name.Equals(after, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Current.Categories.Insert(i + 1, copy); else Current.Categories.Add(copy);
            created.Add(cat.Name);
        }
        if (created.Count > 0) { Rebuild(); Save(); }
        return created;
    }

    private static readonly string[] Palette =
        { "#7C5CFF", "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#EC4899", "#14B8A6", "#8B5CF6", "#0EA5E9", "#84CC16", "#F97316", "#6366F1" };

    /// <summary>Vérifie le nom d'une nouvelle catégorie. Renvoie un message d'erreur, ou null.</summary>
    public static string CheckCategoryName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length < 2) return "Saisissez un nom d'au moins 2 caractères.";
        if (name.Length > 40) return "Nom trop long (40 caractères au plus).";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Ce nom contient un caractère non autorisé.";
        if (Find(name) != null || name.Equals(Current.DefaultCategory, StringComparison.OrdinalIgnoreCase)
            || name.Equals(FavoritesCategory, StringComparison.OrdinalIgnoreCase) || name.Equals(FilesCategory, StringComparison.OrdinalIgnoreCase))
            return "Une catégorie porte déjà ce nom.";
        return null;
    }

    /// <summary>Catégorie créée par l'utilisateur (depuis l'assistant de tri ou le menu d'une tuile).</summary>
    public static Category CreateCategory(string name)
    {
        name = name.Trim();
        var existing = Find(name);
        if (existing != null) return existing;
        Current.DeclinedThemes.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        var (fromCatalog, _) = DefaultThemes.CatalogEntry(name);
        var used = Current.Categories.Select(c => c.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cat = fromCatalog != null
            ? new Category { Name = fromCatalog.Name, Glyph = fromCatalog.Glyph, Color = fromCatalog.Color, Keywords = fromCatalog.Keywords.ToList() }
            : new Category { Name = name, Glyph = "\uE8B7", Color = Palette.FirstOrDefault(c => !used.Contains(c)) ?? Palette[Current.Categories.Count % Palette.Length] };
        Current.Categories.Add(cat);
        Rebuild();
        Save();
        return cat;
    }

    /// <summary>Catégorie supprimée par l'utilisateur : si elle vient du catalogue, elle n'est plus recréée automatiquement.</summary>
    public static void RemoveCategory(Category cat)
    {
        Current.Categories.Remove(cat);
        if (DefaultThemes.CatalogEntry(cat.Name).cat != null && !IsDeclined(cat.Name)) Current.DeclinedThemes.Add(cat.Name);
        foreach (var k in Current.Overrides.Where(kv => kv.Value == cat.Name).Select(kv => kv.Key).ToList())
            Current.Overrides.Remove(k);
        Rebuild();
        Save();
    }

    // ------------------------------------------------------------------ compagnons de jeu
    /// <summary>
    /// Mots d'un outil (guide, compagnon, suivi, mods…). Associés à un jeu, ils désignent un outil pour ce jeu
    /// (« Dofus Guide », « WoW Addon Manager », « aide aux quêtes de Dofus ») et non le jeu lui-même.
    /// </summary>
    internal static readonly Regex GameToolWords = new(
        @"(?<![\p{L}\d])(guides?|helper|companion|compagnon|overlay|tracker|mod manager|mods? loader|modding|addons?|add-ons?|"
        + @"addon manager|gestionnaire d.(?:add-?ons|extensions|mods)|trainer|calculat(?:or|eur)|planner|planificateur|wiki|"
        + @"quêtes?|quests? helper|quest tracker|aide (?:aux|au|à la|de|pour)|outils? (?:pour|d'aide)|tools? for|utility for|"
        + @"utilitaire (?:pour|de jeu)|map editor|éditeur de cartes|stats tracker|statistiques de jeu)(?![\p{L}\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Le texte mentionne-t-il un jeu connu (mot-clé du thème Jeux) ?</summary>
    internal static bool MentionsGame(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var rules = _rules;
        return rules.Any(r => r.cat.Name == "Jeux" && r.rx.IsMatch(text));
    }

    /// <summary>
    /// Titres de jeux (mots-clés du thème Jeux) cités dans des adresses de sites : « dofusdb.fr » → Dofus.
    /// Seuls les titres d'au moins 5 lettres comptent (les sigles courts sont trop ambigus).
    /// </summary>
    internal static List<(string game, string host)> GameTitlesIn(IEnumerable<string> hosts)
    {
        var res = new List<(string, string)>();
        var titles = (Find("Jeux")?.Keywords ?? new())
            .Where(k => k != null && k.Length > 0 && k[0] != '=' && k.All(ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '\''))
            .Select(k => (title: k, compact: new string(k.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()))
            .Where(t => t.compact.Length >= 5).ToList();
        foreach (var h in hosts)
        {
            var label = h.Contains('.') ? h.Substring(0, h.LastIndexOf('.')) : h;
            var compact = new string(label.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var hit = titles.Where(t => compact.Contains(t.compact)).OrderByDescending(t => t.compact.Length).FirstOrDefault();
            if (hit.title != null) res.Add((hit.title, h));
        }
        return res;
    }

    /// <summary>Thème déduit d'un nom de produit seul (nom canonique renvoyé par une source en ligne), ou null.</summary>
    internal static string ThemeFromName(string productName)
    {
        var n = Normalize(productName);
        if (n.Length == 0) return null;
        if (_exact.TryGetValue(n, out var exact)) return exact;
        var t = BestMatch(n);
        return t == null ? null : GameToolOr(t, n);
    }

    /// <summary>Un jeu + des mots d'outil : c'est un outil pour ce jeu.</summary>
    private static string GameToolOr(string theme, string text) =>
        theme == "Jeux" && IsAvailable(GameToolsCategory) && GameToolWords.IsMatch(text ?? "") ? GameToolsCategory : theme;

    /// <summary>Espaces insécables, caractères invisibles, « ® » / « ™ » : remplacés pour comparer proprement.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '\u200B' or '\u200E' or '\u200F' or '\uFEFF' or (>= '\u202A' and <= '\u202E')) continue;   // caractères invisibles
            if (ch is '\u00AE' or '\u2122' or '\u00A9') continue;                                                     // ® ™ ©
            if (ch is '\u2019' or '\u2018' or '\u02BC' or '\u00B4' or '`') { sb.Append('\''); continue; }           // apostrophes typographiques
            if (ch is '\u2013' or '\u2014') { sb.Append('-'); continue; }                                               // tirets longs
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        }
        return Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
    }

    /// <summary>Thème dont un mot-clé correspond le plus précisément (le plus long) au texte.</summary>
    private static string BestMatch(string text) => BestMatch(text, out _);

    private static string BestMatch(string text, out string keyword) => BestMatch(text, _rules, out keyword);

    private static string BestMatch(string text, List<(Category cat, Regex rx)> rules, out string keyword)
    {
        keyword = null;
        if (string.IsNullOrEmpty(text)) return null;
        string best = null; int bestLen = 0;
        foreach (var (cat, rx) in rules)
            foreach (Match m in rx.Matches(text))
                if (m.Length > bestLen) { bestLen = m.Length; best = cat.Name; keyword = m.Value; }
        return best;
    }

    /// <summary>Pourquoi une application est dans ce thème (affiché dans l'assistant de tri).</summary>
    public static string Explain(string name, string target, string context = null, bool looksLikeGame = false)
    {
        if (Current.Overrides.ContainsKey(name) || Current.Overrides.ContainsKey(Normalize(name))) return "défini manuellement";
        var n = Normalize(AppKinds.StripShortcutSuffix(name));
        if (AppKinds.IsPersonalScript(target) && IsAvailable(AppKinds.ScriptsCategory))
            return $"script personnel ({Path.GetFileName(AppKinds.ScriptPath(target))})";
        var pkg = AppKinds.AndroidPackage(target);
        target = AppKinds.ClassifyTarget(target);
        if (_exact.ContainsKey(n)) return "nom exact connu";
        var byName = BestMatch(n, out var k);
        if (byName == null) { var split = AppKinds.SplitWords(n); if (split != n) byName = BestMatch(split, out k); }
        if (byName != null)
            return GameToolOr(byName, n) != byName ? $"outil pour un jeu (« {k} » et « {GameToolWords.Match(n).Value} » dans le nom)" : $"mot-clé « {k} » dans le nom";
        if (looksLikeGame && Find("Jeux") != null) return "jeu détecté (fichiers d'un moteur de jeu)";
        var hay = Normalize($"{AppKinds.MatchableTarget(target)} {context}");
        var byContext = BestMatch(hay, out k);
        if (byContext != null)
            return GameToolOr(byContext, hay) != byContext ? $"outil pour un jeu (« {k} » dans sa description)" : $"mot-clé « {k} » (dossier, éditeur, description ou cible)";
        var src = OnlineClassifier.SourceOf(name);
        if (src != null) return $"trouvé en ligne ({src})";
        if (pkg != null) return $"application Android ({pkg}) : identifiée en ligne dès que possible";
        if (OnlineClassifier.Decide(context, out var ev) != null) return $"description de l'application ({ev})";
        if (WindowsTool.IsMatch(target ?? "") || WindowsTool.IsMatch(context ?? "")) return "outil intégré à Windows";
        var aff = Affinity.Reason(name);
        if (aff != null) return aff;
        if (BestMatch(n, _weak, out k) != null || BestMatch(hay, _weak, out k) != null) return $"mot-clé général « {k} » (faute de mieux)";
        return "aucun indice trouvé";
    }

    // Outils intégrés à Windows : dossier System32, applications « Microsoft.Windows… », dossiers « Outils d'administration »…
    private static readonly Regex WindowsTool = new(
        @"\\Windows\\(System32|SysWOW64)\\|%windir%|%systemroot%|\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7\}|\{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27\}|^Microsoft\.Windows|^MicrosoftWindows\.|^windows\.|Administrative Tools|Outils d.administration|Windows Tools|Outils Windows|Accessibility|Accessibilité|System Tools|Outils système",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// 1) choix manuel  2) mots-clés dans le nom  3) détection de jeu (moteur, bibliothèque de jeux)
    /// 4) mots-clés dans la cible, le dossier du menu Démarrer et l'éditeur  5) outil Windows  6) Autres
    /// </summary>
    public static string Classify(string name, string target, string context = null, bool looksLikeGame = false)
    {
        if (Current.Overrides.TryGetValue(name, out var o)) return o;
        if (Current.Overrides.TryGetValue(Normalize(name), out o)) return o;
        // Le suffixe « - Raccourci » ajouté par Windows ne dit rien de l'application
        var n = Normalize(AppKinds.StripShortcutSuffix(name));
        if (Current.Overrides.TryGetValue(n, out o)) return o;
        // Script personnel (.bat, .ps1, .py…) : catégorie « Scripts », quel que soit le dossier où il se trouve
        if (AppKinds.IsPersonalScript(target) && IsAvailable(AppKinds.ScriptsCategory)) return AppKinds.ScriptsCategory;
        // Application hébergée (Android dans un émulateur, application web) : l'hôte ne compte pas
        target = AppKinds.ClassifyTarget(target);
        if (_exact.TryGetValue(n, out var exact)) return exact;
        // Nom collé (« SenaDeviceManager ») : mots séparés pour la recherche
        var byName = BestMatch(n);
        if (byName == null) { var split = AppKinds.SplitWords(n); if (split != n) byName = BestMatch(split); }
        if (byName != null) return GameToolOr(byName, n);
        if (looksLikeGame && Find("Jeux") != null) return "Jeux";
        var hay = Normalize($"{AppKinds.MatchableTarget(target)} {context}");
        var byContext = BestMatch(hay);
        if (byContext != null) return GameToolOr(byContext, hay);
        var online = OnlineClassifier.Lookup(name);
        if (online != null) return online;
        // Description de l'application elle-même (exécutable, programme d'installation, paquet du Store, package.json)
        var local = OnlineClassifier.Decide(context, out _);
        if (local != null) return local;
        if (WindowsTool.IsMatch(target ?? "") || WindowsTool.IsMatch(context ?? ""))
        {
            var tools = Current.Categories.FirstOrDefault(c => c.Name == "Outils Windows") ?? Current.Categories.FirstOrDefault(c => c.Name == "Utilitaires");
            if (tools != null) return tools.Name;
        }
        // Applications voisines : même suite, même dossier d'installation, même éditeur (voir Affinity)
        var near = Affinity.Lookup(name);
        if (near != null && IsAvailable(near)) return near;
        // En dernier recours : mots-clés généraux (« Launcher », « Tools »…)
        var loose = BestMatch(n, _weak, out _) ?? BestMatch(hay, _weak, out _);
        if (loose != null) return loose;
        return Current.DefaultCategory;
    }

    public static void SetOverride(string name, string category)
    {
        if (category == null) Current.Overrides.Remove(name);
        else Current.Overrides[name] = category;
        Save();
    }

    public static bool IsHidden(string name) => Current.HiddenApps.Contains(name, StringComparer.OrdinalIgnoreCase);
    public static bool IsSkipped(string name) => Current.SortSkipped.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static void SetHidden(string name, bool hidden)
    {
        Current.HiddenApps.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hidden) Current.HiddenApps.Add(name);
        Save();
    }

    public static void SetSkipped(string name, bool skipped)
    {
        Current.SortSkipped.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skipped) Current.SortSkipped.Add(name);
        Save();
    }

    /// <summary>Efface tous les choix de tri faits à la main (thèmes choisis, applications masquées ou laissées dans Autres).</summary>
    public static void ResetUserChoices()
    {
        Current.Overrides.Clear();
        Current.HiddenApps.Clear();
        Current.SortSkipped.Clear();
        Save();
    }

    public static IEnumerable<string> AllCategoryNames =>
        Current.Categories.Select(c => c.Name).Append(Current.DefaultCategory);

    public static int Order(string category)
    {
        if (category == FavoritesCategory) return -1;
        var i = Current.Categories.FindIndex(c => c.Name == category);
        if (i >= 0) return i;
        var ci = Array.FindIndex(DefaultThemes.Catalog, c => c.cat.Name == category);
        if (ci >= 0) return Current.Categories.Count + ci;
        return category == FilesCategory ? 1000 : 999;
    }

    public static Category Find(string name) =>
        Current.Categories.FirstOrDefault(c => c.Name == name);

    public static string GlyphOf(string category) => category switch
    {
        FavoritesCategory => "",
        FilesCategory => "",
        _ => Find(category)?.Glyph ?? DefaultThemes.CatalogEntry(category).cat?.Glyph ?? ""
    };

    public static Brush BrushOf(string category)
    {
        category ??= "";
        if (_brushes.TryGetValue(category, out var b)) return b;
        string hex = category switch
        {
            FavoritesCategory => "#F5B83D",
            FilesCategory => "#8B93A7",
            _ => Find(category)?.Color ?? DefaultThemes.CatalogEntry(category).cat?.Color ?? "#6B7280"
        };
        try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { b = new SolidColorBrush(Colors.SlateGray); }
        b.Freeze();
        _brushes[category] = b;
        return b;
    }

    private static List<Category> DefaultCategories() => DefaultThemes.Build();
}
