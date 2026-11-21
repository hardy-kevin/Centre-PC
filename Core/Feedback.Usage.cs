using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>
/// Retours sur ce que l'utilisateur modifie (pour améliorer les propositions de tous) : catégories et groupes
/// d'applications, mots-clés, renommages et noms proposés, dossiers créés ou ajoutés, raccourci clavier choisi,
/// applications présentes.
///
/// Règle : une donnée n'est envoyée que si elle ne peut rien dire de la personne. Tout passe par les filtres ci-dessous,
/// qui ne laissent sortir que des valeurs d'une liste connue (catégories d'origine, types de documents reconnus,
/// noms de dossiers courants, logiciels réellement installés) ; le reste est remplacé par un mot générique
/// (« personnalisé », « autre ») ou n'est pas envoyé du tout. Jamais : nom de fichier, contenu de document, chemin
/// personnel, nom d'un dossier inventé par l'utilisateur, nom d'une catégorie ou d'un groupe inventé, raccourci vers
/// un site, script personnel. Dans le doute, rien.
/// </summary>
public static partial class Feedback
{
    // ------------------------------------------------------------------ filtres de confidentialité
    /// <summary>Valeurs génériques envoyées à la place d'une donnée qui pourrait être personnelle.</summary>
    public const string Custom = "personnalisé", Other = "autre";

    private static HashSet<string> Words(string s) =>
        Regex.Matches(s ?? "", @"[\p{L}\p{N}]{3,}").Select(m => m.Value.ToLowerInvariant()).ToHashSet();

    private static string Simple(string s) => new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Logiciel réellement installé, sous le nom donné par son éditeur : application du Microsoft Store, ou programme
    /// installé dans Program Files / ProgramData dont le nom partage un mot avec le programme, son dossier ou l'éditeur
    /// (un raccourci renommé « Le jeu de Léa » ne passe pas). Jamais : appli web, appli Android, script personnel.
    /// </summary>
    public static bool IsReportableApp(string name, string publisher, string target, bool packaged)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return false;
        // Nom d'une personne (prénom + nom de famille), ou listes de noms indisponibles : rien
        if (!PersonNames.Available || PersonNames.HasFullName(name)) return false;
        if (AppKinds.IsHosted(target) || AppKinds.IsPersonalScript(target)) return false;
        if (Regex.IsMatch(name, @"[\w.+-]+@[\w-]+\.|https?://|www\.", RegexOptions.IgnoreCase)) return false;
        if (packaged) return target == null || !target.Contains("_crx_", StringComparison.OrdinalIgnoreCase);   // applis web d'Edge et Chrome
        if (!IsInstalledProgram(target)) return false;
        var dir = Path.GetDirectoryName(target) ?? "";
        var evidence = Words(string.Join(" ", Path.GetFileNameWithoutExtension(target), Path.GetFileName(dir),
                                         Path.GetFileName(Path.GetDirectoryName(dir) ?? ""), publisher));
        evidence.ExceptWith(new[] { "program", "files", "programdata", "x86", "bin", "app", "application" });   // dossiers génériques : ne prouvent rien
        var nameWords = Words(name);
        if (nameWords.Overlaps(evidence)) return true;
        var flat = Simple(name);
        return evidence.Any(e => e.Length >= 4 && (flat.Contains(e) || e.Contains(flat) && flat.Length >= 4));
    }

    /// <summary>
    /// Éditeur : nom d'entreprise, nettoyé ; vide s'il ressemble à une personne (prénom + nom, ou un seul nom de famille
    /// qui n'est pas celui d'un éditeur connu) ou à un compte (adresse, chemin).
    /// </summary>
    public static string SafePublisher(string publisher)
    {
        var p = Clean(publisher ?? "");
        if (p.Contains('%') || p.Length > 80 || PersonNames.IsPersonPublisher(p)) return "";
        return p;
    }

    /// <summary>Catégorie d'origine de Centre PC (ou du catalogue) ; une catégorie créée par l'utilisateur devient « personnalisé ».</summary>
    public static string SafeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "";
        bool known = category == Settings.Current?.DefaultCategory
                     || DefaultThemes.Build().Any(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                     || DefaultThemes.CatalogEntry(category).cat != null;
        return known ? category : Custom;
    }

    /// <summary>
    /// Noms de dossiers courants, qui ne disent rien de la personne. Les noms des dossiers du plan de rangement
    /// (et leurs synonymes) s'y ajoutent.
    /// </summary>
    private static readonly string[] CommonFolderNames =
    {
        "Jeux", "Travail", "Personnel", "Perso", "Pro", "Professionnel", "Projets", "Cours", "École", "Ecole", "Collège", "Lycée", "Université", "Fac",
        "Recettes", "Cuisine", "Maison", "Jardin", "Bricolage", "Travaux", "Voiture", "Moto", "Animaux", "Sport", "Musique", "Vidéos", "Videos", "Photos",
        "Images", "Famille", "Vacances", "Voyages", "Archives", "Divers", "Autres", "Ancien", "Anciens", "À trier", "A trier", "Temp", "Temporaire",
        "Associations", "Association", "Loisirs", "Livres", "BD", "Mangas", "Films", "Séries", "Series", "Podcasts", "Wallpapers", "Screenshots",
        "Captures", "Scripts", "Outils", "Logiciels", "Programmes", "Pilotes", "Drivers", "Mods", "Sauvegardes", "Backup", "Backups", "Modèles",
        "Templates", "Graphisme", "Dessin", "Design", "Impression 3D", "Électronique", "Arduino", "Raspberry", "Domotique", "Papiers", "Administratif",
        "Banque", "Impôts", "Factures", "Devis", "Assurances", "Logement", "Santé", "Emploi", "Salaires", "Contrats", "Achats", "Garanties", "Notices",
        "Manuels", "Documentation", "Formations", "Diplômes", "CV", "Courriers", "Lettres", "Comptes", "Budget", "Retraite", "Enfants", "Bébé",
        "Mariage", "Anniversaires", "Noël", "Fêtes", "Souvenirs", "Scans", "Numérisations", "Téléchargements", "Downloads", "Installateurs", "ISO",
        "Fonds d'écran", "Icônes", "Logos", "Polices", "Fonts", "Code", "Dev", "Développement", "Programmation", "Streaming", "Montage", "Clips",
        "Enregistrements", "Audio", "Sons", "Samples", "Partitions", "Cartes", "GPS", "Randonnées", "Vélo", "Course", "Running", "Fitness", "Musculation"
    };

    private static readonly Lazy<HashSet<string>> FolderLexicon = new(() =>
        CommonFolderNames.Concat(DocPlanner.KnownFolderWords()).Select(Simple).Where(s => s.Length > 0).ToHashSet());

    /// <summary>
    /// Chemin d'un dossier par rapport à son grand dossier (« Jeux\Dofus »), seulement avec des noms courants ou des
    /// noms de logiciels installés ; chaque autre nom devient « autre ». Plus de trois niveaux : coupé.
    /// </summary>
    public static string SafeFolderPath(string relative, IEnumerable<string> installedApps = null)
    {
        var apps = (installedApps ?? Enumerable.Empty<string>()).Select(Simple).Where(a => a.Length >= 3).ToHashSet();
        var parts = (relative ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(p => FolderLexicon.Value.Contains(Simple(p)) || apps.Contains(Simple(p)) ? p.Trim() : Other);
        return string.Join("\\", parts);
    }

    /// <summary>Extension en minuscules si elle est courte et ordinaire (« .pdf »), sinon « autre ».</summary>
    public static string SafeExtension(string path)
    {
        var e = Path.GetExtension(path ?? "").ToLowerInvariant();
        return Regex.IsMatch(e, @"^\.[a-z0-9]{1,5}$") ? e : Other;
    }

    /// <summary>Clé d'un grand dossier (documents, images, musique, videos, telechargements, bureau) ou « autre ».</summary>
    private static string RootKeyOf(string path, out string relative)
    {
        relative = "";
        if (string.IsNullOrEmpty(path)) return Other;
        if (DocumentLibrary.IsUnder(path, AppScanner.UserDesktop) || path.TrimEnd('\\').Equals(AppScanner.UserDesktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            relative = Path.GetRelativePath(AppScanner.UserDesktop, path);
            return "bureau";
        }
        foreach (var r in DocumentLibrary.Roots.Where(r => !string.IsNullOrEmpty(r.Path)))
            if (DocumentLibrary.IsUnder(path, r.Path)) { relative = Path.GetRelativePath(r.Path, path); return r.Key; }
        return Other;
    }

    private static string YesNo(bool b) => b ? "oui" : "non";

    // ------------------------------------------------------------------ applications
    /// <summary>
    /// Catégorie choisie à la main pour une application installée. <paramref name="previous"/> : celle que Centre PC
    /// avait proposée (ce qui était faux).
    /// </summary>
    public static void RecordClassification(string app, string publisher, string target, bool packaged, string category, string previous)
    {
        if (!Enabled || category == null || !IsReportableApp(app, publisher, target, packaged)) return;
        Record("classement", new()
        {
            ["application"] = Clean(app), ["editeur"] = SafePublisher(publisher),
            ["categorie"] = SafeCategory(category), ["avant"] = SafeCategory(previous)
        });
    }

    /// <summary>
    /// Applications présentes et leur catégorie (une fois par application et par catégorie : pas un inventaire envoyé
    /// chaque jour). Logiciels installés uniquement, voir <see cref="IsReportableApp"/>.
    /// </summary>
    public static void RecordPresentApps(IEnumerable<(string name, string publisher, string target, bool packaged, string category)> apps)
    {
        if (!Enabled) return;
        try
        {
            var reported = Settings.Current.FeedbackReported ??= new();
            bool changed = false;
            int recorded = 0;
            foreach (var (name, publisher, target, packaged, category) in apps)
            {
                if (recorded >= 40) break;   // la suite à la prochaine fois : la file reste petite et les erreurs y gardent leur place
                if (!IsReportableApp(name, publisher, target, packaged)) continue;
                var clean = Clean(name);
                var cat = SafeCategory(category);
                var print = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("presente|" + clean.ToLowerInvariant() + "|" + cat)))[..16];
                if (reported.Contains(print)) continue;
                reported.Add(print);
                changed = true;
                recorded++;
                Record("presente", new() { ["application"] = clean, ["editeur"] = SafePublisher(publisher), ["categorie"] = cat });
            }
            if (reported.Count > 3000) reported.RemoveRange(0, reported.Count - 3000);
            if (changed) Settings.Save();
        }
        catch { }
    }

    /// <summary>
    /// Dossier d'applications créé, complété, vidé ou groupe automatique dissocié. Seuls les logiciels installés sont
    /// nommés (les autres sont comptés) ; le nom du dossier n'est envoyé que s'il est courant.
    /// </summary>
    public static void RecordGroup(string action, string folder, IEnumerable<(string name, string publisher, string target, bool packaged)> members, IEnumerable<string> installedApps = null)
    {
        if (!Enabled) return;
        var list = members?.ToList() ?? new();
        var named = list.Where(m => IsReportableApp(m.name, m.publisher, m.target, m.packaged)).Select(m => Clean(m.name)).Distinct().Take(12).ToList();
        int others = list.Count - named.Count;
        Record("groupe", new()
        {
            ["action"] = action,
            ["nom"] = folder == null ? "" : SafeFolderPath(folder, installedApps) is var n && n != Other ? n : Custom,
            ["applications"] = string.Join(" | ", named) + (others > 0 ? $" (+{others} autre{(others > 1 ? "s" : "")})" : "")
        });
    }

    /// <summary>
    /// Mots-clés ajoutés à une catégorie : seulement ceux qui figurent dans le nom ou l'éditeur d'un logiciel installé
    /// (c'est leur rôle : reconnaître des applications). Un autre mot n'est pas envoyé.
    /// </summary>
    public static void RecordKeywords(string category, IEnumerable<string> added, IEnumerable<(string name, string publisher, string target, bool packaged)> apps)
    {
        if (!Enabled) return;
        var installed = (apps ?? Enumerable.Empty<(string, string, string, bool)>())
            .Where(a => IsReportableApp(a.Item1, a.Item2, a.Item3, a.Item4))
            .Select(a => Simple(a.Item1) + " " + Simple(a.Item2)).ToList();
        foreach (var k in added ?? Enumerable.Empty<string>())
        {
            var s = Simple(k);
            if (s.Length < 3 || s.Length > 40) continue;
            if (!PersonNames.Available || PersonNames.HasFullName(k)) continue;   // jamais un nom de personne
            int hits = installed.Count(i => i.Contains(s));
            if (hits == 0) continue;
            Record("mot-cle", new() { ["categorie"] = SafeCategory(category), ["mot"] = k.Trim().ToLowerInvariant(), ["applications"] = hits.ToString() });
        }
    }

    /// <summary>Raccourci clavier de l'accès rapide choisi par l'utilisateur (touches seulement).</summary>
    public static void RecordHotkey(string keys, string conflict, bool isDefault)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(keys) || keys.Length > 40) return;
        Record("raccourci", new() { ["touches"] = keys, ["conflit"] = conflict ?? "aucun", ["defaut"] = YesNo(isDefault) });
    }

    // ------------------------------------------------------------------ documents
    /// <summary>
    /// Renommage depuis le plan de rangement. Rien du nom lui-même : seulement le type de document avant/après (liste
    /// connue), si le nom proposé a été utilisé, modifié ou ignoré, et ce qui en a été gardé (organisme, date).
    /// </summary>
    /// <param name="suggestion">Nom proposé à ce moment-là (null : aucun).</param>
    /// <param name="usage">utilise | modifie | ignore | aucun</param>
    public static void RecordRename(string newPath, bool isFolder, NameSuggestion suggestion, string usage, bool sortedAfter)
    {
        if (!Enabled || string.IsNullOrEmpty(newPath)) return;
        var newName = isFolder ? Path.GetFileName(newPath.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(newPath);
        string Kept(string part) => string.IsNullOrEmpty(part) ? "sans" : newName.Contains(part, StringComparison.CurrentCultureIgnoreCase) ? "garde" : "retire";
        Record("renommage", new()
        {
            ["element"] = isFolder ? "dossier" : "fichier",
            ["extension"] = isFolder ? "" : SafeExtension(newPath),
            ["propose"] = usage,
            ["type_propose"] = suggestion?.Type ?? "",
            ["type_final"] = DocNaming.TypeOfName(newName) ?? "non reconnu",
            ["organisme"] = suggestion == null ? "" : Kept(suggestion.Issuer),
            ["date"] = suggestion == null ? "" : Kept(suggestion.Date),
            ["range"] = YesNo(sortedAfter)
        });
    }

    /// <summary>
    /// Dossier créé ou dossier existant ajouté à l'arborescence par l'utilisateur. Le chemin ne garde que les noms
    /// courants ; pour un dossier ajouté, les types de fichiers qu'il contient (extensions et nombres) disent ce qu'il
    /// faudrait savoir reconnaître, et « vu comme appli » dit si Centre PC l'avait écarté à tort.
    /// </summary>
    /// <param name="action">creation | ajout</param>
    public static void RecordFolder(string action, string path, IEnumerable<string> installedApps = null)
    {
        if (!Enabled || string.IsNullOrEmpty(path)) return;
        var root = RootKeyOf(path, out var rel);
        var data = new Dictionary<string, string>
        {
            ["action"] = action,
            ["racine"] = root,
            ["chemin"] = root == Other ? Other : SafeFolderPath(rel, installedApps),
            ["profondeur"] = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length.ToString()
        };
        if (action == "ajout")
        {
            data["vu_comme_appli"] = YesNo(DocPlanner.IsAppFolder(path, installedApps));
            try
            {
                var top = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 })
                    .Take(500).GroupBy(SafeExtension).Where(g => g.Key != Other).OrderByDescending(g => g.Count()).Take(4)
                    .Select(g => $"{g.Key} {g.Count()}");
                data["types"] = string.Join(", ", top);
            }
            catch { data["types"] = ""; }
        }
        Record("dossier", data);
    }
}
