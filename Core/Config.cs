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
    /// <summary>Thème choisi à la main : nom de l'appli → catégorie.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new();
    /// <summary>Applis épinglées en favoris (page Applications).</summary>
    public List<string> Favorites { get; set; } = new();
    /// <summary>Noms de fichiers qui restent toujours sur le bureau.</summary>
    public List<string> KeepOnDesktop { get; set; } = new() { "Centre PC", "Corbeille", "Recycle Bin" };
    /// <summary>Fichier rangé (nom dans le dossier de rangement) → chemin d'origine.</summary>
    public Dictionary<string, string> Stored { get; set; } = new();
    public bool TidyFiles { get; set; } = true;
    /// <summary>« app » : rangement dans Centre PC (bureau vide) ; « folders » : dossiers de thème sur le bureau.</summary>
    public string TidyMode { get; set; } = "app";
    /// <summary>Applis masquées par l'utilisateur (n'apparaissent plus dans Applications).</summary>
    public List<string> HiddenApps { get; set; } = new();
    /// <summary>Applis volontairement laissées dans « Autres » (l'assistant de tri ne les repropose pas).</summary>
    public List<string> SortSkipped { get; set; } = new();
    /// <summary>Identifier automatiquement en ligne les applis inconnues.</summary>
    public bool AutoOnline { get; set; } = true;
    public bool OnboardingDone { get; set; }
    /// <summary>Vérifier au démarrage si une nouvelle version de Centre PC est publiée.</summary>
    public bool AutoCheckAppUpdate { get; set; } = true;
    /// <summary>Version proposée et refusée : elle n'est plus proposée au démarrage (reste installable dans Réglages).</summary>
    public string SkippedAppVersion { get; set; }
    public List<string> WingetExclusions { get; set; } = new();
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>État d'origine des icônes personnalisées (pour revenir en arrière).</summary>
    public List<IconBackup> IconBackups { get; set; } = new();
    public string ArrowPreviousValue { get; set; }
    public bool StyleFolders { get; set; } = true;
    /// <summary>Version des mots-clés par défaut fusionnés dans cette configuration.</summary>
    public int DefaultsVersion { get; set; }
}

/// <summary>Chargement / sauvegarde de la configuration et classement des applis par thème.</summary>
public static class Settings
{
    /// <summary>
    /// Dossier de Centre PC. Version Store : Windows isole AppData pour les applis du Store (invisible pour l'Explorateur
    /// et supprimé à la désinstallation) ; on utilise donc Documents\Centre PC pour que les raccourcis rangés et les icônes
    /// restent visibles par Windows et ne soient jamais perdus.
    /// </summary>
    public static readonly string DataDir = Native.IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Centre PC")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentrePC");
    public static readonly string StoreDir = Path.Combine(DataDir, "Bureau rangé");
    private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");

    public static Config Current { get; private set; }
    public const string FilesCategory = "Fichiers";
    public const string FavoritesCategory = "Favoris";

    private static List<(Category cat, Regex rx)> _rules = new();
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
        catch { /* fichier abîmé : on repart des valeurs par défaut */ }
        cfg ??= new Config();
        if (cfg.Categories == null || cfg.Categories.Count == 0) { cfg.Categories = DefaultCategories(); cfg.DefaultsVersion = DefaultsVersion; }
        if (cfg.DefaultsVersion < DefaultsVersion) MergeDefaults(cfg);
        cfg.Overrides = new Dictionary<string, string>(cfg.Overrides ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Stored = new Dictionary<string, string>(cfg.Stored ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.Favorites ??= new(); cfg.KeepOnDesktop ??= new(); cfg.WingetExclusions ??= new(); cfg.IconBackups ??= new(); cfg.HiddenApps ??= new(); cfg.SortSkipped ??= new();
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
            AutoOnline = c.AutoOnline
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
        if (p.TidyMode != null) c.TidyMode = p.TidyMode;
        if (p.TidyFiles != null) c.TidyFiles = p.TidyFiles.Value;
        if (p.StyleFolders != null) c.StyleFolders = p.StyleFolders.Value;
        if (p.AutoOnline != null) c.AutoOnline = p.AutoOnline.Value;
        if (!c.KeepOnDesktop.Contains("Centre PC", StringComparer.OrdinalIgnoreCase)) c.KeepOnDesktop.Add("Centre PC");
        Rebuild();
        Save();
    }

    public static void Save()
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, JsonOpts)); } catch { }
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
            // Choix manuels vers un thème qui n'existe plus : retirés (l'appli est triée à nouveau automatiquement)
            foreach (var k in cfg.Overrides.Where(kv => !cfg.Categories.Any(c => c.Name == kv.Value)).Select(kv => kv.Key).ToList())
                cfg.Overrides.Remove(k);
            // Les résultats en ligne utilisaient les anciens thèmes : nouvelle recherche
            try { File.Delete(Path.Combine(DataDir, "classement-en-ligne.json")); } catch { }
        }
        else
        {
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
        foreach (var k in Current.Overrides.Where(kv => !Current.Categories.Any(c => c.Name == kv.Value)).Select(kv => kv.Key).ToList())
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
        _rules = new();
        foreach (var c in Current.Categories)
        {
            // Les mots-clés les plus longs d'abord : « Dolby Access » passe avant « Access »
            var parts = c.Keywords.Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(k => k.Length)
                .Select(k =>
                {
                    var e = Regex.Escape(k);
                    if (char.IsLetterOrDigit(k[0])) e = @"(?<![\p{L}\d])" + e;
                    if (char.IsLetterOrDigit(k[^1])) e += @"(?:s|x)?(?![\p{L}\d])";   // pluriel toléré
                    return e;
                });
            var pattern = string.Join("|", parts);
            if (pattern.Length == 0) continue;
            _rules.Add((c, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
        }
    }

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
    private static string BestMatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string best = null; int bestLen = 0;
        foreach (var (cat, rx) in _rules)
            foreach (Match m in rx.Matches(text))
                if (m.Length > bestLen) { bestLen = m.Length; best = cat.Name; }
        return best;
    }

    // Outils intégrés à Windows : dossier System32, applis « Microsoft.Windows… », dossiers « Outils d'administration »…
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
        var n = Normalize(name);
        if (Current.Overrides.TryGetValue(n, out o)) return o;
        var byName = BestMatch(n);
        if (byName != null) return byName;
        if (looksLikeGame && Find("Jeux") != null) return "Jeux";
        var hay = Normalize($"{target} {context}");
        var byContext = BestMatch(hay);
        if (byContext != null) return byContext;
        var online = OnlineClassifier.Lookup(name);
        if (online != null) return online;
        if (WindowsTool.IsMatch(target ?? "") || WindowsTool.IsMatch(context ?? ""))
        {
            var tools = Current.Categories.FirstOrDefault(c => c.Name == "Outils Windows") ?? Current.Categories.FirstOrDefault(c => c.Name == "Utilitaires");
            if (tools != null) return tools.Name;
        }
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

    /// <summary>Efface tous les choix de tri faits à la main (thèmes choisis, applis masquées ou laissées dans Autres).</summary>
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
        return category == FilesCategory ? 1000 : 999;
    }

    public static Category Find(string name) =>
        Current.Categories.FirstOrDefault(c => c.Name == name);

    public static string GlyphOf(string category) => category switch
    {
        FavoritesCategory => "",
        FilesCategory => "",
        _ => Find(category)?.Glyph ?? ""
    };

    public static Brush BrushOf(string category)
    {
        category ??= "";
        if (_brushes.TryGetValue(category, out var b)) return b;
        string hex = category switch
        {
            FavoritesCategory => "#F5B83D",
            FilesCategory => "#8B93A7",
            _ => Find(category)?.Color ?? "#6B7280"
        };
        try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { b = new SolidColorBrush(Colors.SlateGray); }
        b.Freeze();
        _brushes[category] = b;
        return b;
    }

    private static List<Category> DefaultCategories() => DefaultThemes.Build();
}
