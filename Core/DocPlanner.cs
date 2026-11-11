using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Un déplacement proposé par le plan de rangement.</summary>
public class DocProposal
{
    public string Source { get; set; }
    public bool IsFolder { get; set; }
    /// <summary>« Bureau », « Téléchargements », « Documents »… (d'où vient l'élément).</summary>
    public string Origin { get; set; }
    /// <summary>Dossier de destination (existant ou à créer).</summary>
    public string Destination { get; set; }
    public string DestinationLabel { get; set; }
    public bool DestinationExists { get; set; }
    /// <summary>Ce qui a permis de décider (« mot « facture » dans le nom », « photo d'appareil »…).</summary>
    public string Reason { get; set; }
    /// <summary>Coché par l'utilisateur (décoché : l'élément reste à sa place et ne sera plus proposé).</summary>
    public bool Selected { get; set; } = true;
    /// <summary>Aucune destination certaine : à ranger par l'utilisateur.</summary>
    public bool IsManual => string.IsNullOrEmpty(Destination);
    /// <summary>Élément de la file manuelle que l'utilisateur a choisi de laisser à sa place (plus proposé).</summary>
    public bool Keep { get; set; }
    /// <summary>À envoyer à la Corbeille de Windows au moment de valider (rien n'est supprimé avant).</summary>
    public bool Trash { get; set; }
    public string Name => Path.GetFileName(Source.TrimEnd('\\'));
}

/// <summary>Un dossier de l'arborescence proposée, avec ce qu'il recevra.</summary>
public class DocFolderPlan
{
    public string Path { get; set; }
    public string Label { get; set; }
    public string Description { get; set; }
    public bool Exists { get; set; }
    public List<DocProposal> Items { get; set; } = new();
    /// <summary>Choisi par l'utilisateur (décoché : ni créé, ni rempli).</summary>
    public bool Selected { get; set; } = true;
}

/// <summary>Un rangement effectué, en attente de validation par l'utilisateur.</summary>
public class DocReviewEntry
{
    public string From { get; set; }
    public string To { get; set; }
    public bool IsFolder { get; set; }
    public string Folder { get; set; }
    public string Reason { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public string Name => Path.GetFileName((To ?? From ?? "").TrimEnd('\\'));
    public string OriginLabel => DocPlanner.FriendlyDir(Path.GetDirectoryName(From));
}

/// <summary>
/// Plan de rangement des documents :
/// 1) Inventaire de ce qui n'est pas rangé : fichiers et dossiers du bureau, fichiers posés à la racine de Documents,
///    Images, Musique, Vidéos et Téléchargements. Tout ce qui est déjà dans un sous-dossier est considéré comme rangé
///    par l'utilisateur et n'est jamais déplacé.
/// 2) Les sous-dossiers existants de l'utilisateur sont repris dans l'arborescence et servent de destination en priorité
///    (« icones », « Enregistrements audio »…). Les dossiers créés par les applications et les jeux (« My Games »,
///    « Call of Duty », « WindowsPowerShell », projets de développement…) sont reconnus et laissés tranquilles.
/// 3) Chaque élément reçoit une destination d'après son nom (facture, avis d'imposition, capture d'écran, photo
///    d'appareil…) puis son type. Les catégories suivent les conseils habituels de classement : peu de dossiers
///    principaux, des sous-dossiers par sujet, trois niveaux au plus.
/// 4) Une proposition refusée n'est jamais refaite.
/// </summary>
public static class DocPlanner
{
    // ------------------------------------------------------------------ catégories
    private record Rule(string RootKey, string Path, string Description, string[] Synonyms, Regex Name, string[] Extensions, string Why);

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] DocExt = { ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".xls", ".xlsx", ".ods", ".csv", ".ppt", ".pptx", ".odp", ".jpg", ".jpeg", ".png", ".heic", ".tif", ".tiff" };
    private static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff" };
    private static readonly string[] RawExt = { ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2" };
    private static readonly string[] VidExt = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv" };
    private static readonly string[] AudExt = { ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".oga", ".aac", ".wma", ".opus", ".aiff", ".aif", ".aifc", ".alac", ".ape",
                                                ".wv", ".mka", ".mid", ".midi", ".dsf", ".dff", ".ac3", ".dts", ".mpc", ".tta" };
    private static readonly string[] PlaylistExt = { ".m3u", ".m3u8", ".pls", ".cue", ".xspf", ".wpl" };

    /// <summary>Règles par ordre de précision : les règles « par le nom » passent avant les règles « par le type ».</summary>
    private static readonly Rule[] Rules =
    {
        // Administratif (papiers à conserver)
        new("documents", @"Administratif\Identité", "Pièces d'identité, permis, actes", new[] { "identité", "papiers", "papiers d'identité" },
            Rx(@"carte d.identit|\bcni\b|passeport|passport|permis de conduire|acte de naissance|livret de famille|justificatif de domicile"), DocExt, "pièce d'identité"),
        new("documents", @"Administratif\Impôts", "Déclarations et avis d'imposition", new[] { "impôts", "impots", "fiscalité", "taxes" },
            Rx(@"imp[oô]ts?\b|avis d.imposition|d[ée]claration (de )?revenus|taxe fonci[eè]re|taxe d.habitation|tax return"), DocExt, "impôts"),
        new("documents", @"Administratif\Banque", "Relevés, RIB, prêts, épargne", new[] { "banque", "bancaire", "comptes", "finances" },
            Rx(@"relev[ée]s? (de )?(compte|bancaire)|\brib\b|\biban\b|bank statement|tableau d.amortissement|offre de pr[eê]t|livret a\b|assurance.vie|investissement|placements?\b|\bcrypto"), DocExt, "banque"),
        new("documents", @"Administratif\Factures", "Factures, devis, abonnements", new[] { "factures", "invoices", "achats" },
            Rx(@"factur|invoice|\bdevis\b|bon de commande|re[çc]u\b|receipt|ticket de caisse|[ée]ch[ée]ancier"), DocExt, "facture"),
        new("documents", @"Administratif\Emploi et salaires", "Fiches de paie, contrats, CV", new[] { "emploi", "salaires", "paie", "travail", "carrière", "cv" },
            Rx(@"bulletin de (paie|salaire)|fiche de paie|payslip|pay ?slip|contrat de travail|attestation (employeur|pole emploi|france travail)|\bcv\b|curriculum|lettre de motivation|cover letter|"
               + @"ordre de mission|^om\s|(^|[\W_])(bp|bs)[\W_]+(janvier|f[ée]vrier|mars|avril|mai|juin|juillet|ao[uû]t|septembre|octobre|novembre|d[ée]cembre)"), DocExt, "emploi"),
        new("documents", @"Administratif\Logement", "Bail, quittances, assurance habitation", new[] { "logement", "maison", "appartement", "habitation" },
            Rx(@"\bbail\b|quittance|loyer|[ée]tat des lieux|copropri[ée]t[ée]|syndic|acte de vente|compromis de vente|assurance habitation"), DocExt, "logement"),
        new("documents", @"Administratif\Santé", "Ordonnances, mutuelle, comptes rendus", new[] { "santé", "sante", "médical", "medical" },
            Rx(@"ordonnance|mutuelle|\bameli\b|carte vitale|compte.rendu (m[ée]dical|d.examen)|vaccin|certificat m[ée]dical|remboursement(s)? (de )?soins|r[ée]sultats? d.analyse"), DocExt, "santé"),
        new("documents", @"Administratif\Véhicule", "Carte grise, contrôle technique, entretien", new[] { "véhicule", "vehicule", "voiture", "auto", "moto" },
            Rx(@"carte grise|certificat d.immatriculation|contr[ôo]le technique|entretien (voiture|v[ée]hicule)|avis de contravention|certificat de cession|d[ée]claration de cession"), DocExt, "véhicule"),
        new("documents", "Voyages", "Billets, réservations, itinéraires", new[] { "voyages", "vacances", "billets", "travel" },
            Rx(@"\bbillets?\b|e-?billet|\bm-?ticket|\be-?ticket|boarding pass|carte d.embarquement|r[ée]servation|itin[ée]raire|\bsncf\b|\bouigo\b|booking\.com|airbnb"), DocExt, "voyage"),
        new("documents", @"Administratif\Assurances", "Contrats et attestations d'assurance", new[] { "assurances", "assurance" },
            Rx(@"assurance|sinistre|attestation d.assurance"), DocExt, "assurance"),
        // Études, lecture, créations
        new("documents", "Études et formations", "Cours, diplômes, exercices", new[] { "études", "etudes", "cours", "école", "ecole", "formation", "formations", "fac", "université" },
            Rx(@"\bcours\b|\btd\s?\d|\btp\s?\d|examen|partiel|dipl[ôo]me|certificat de scolarit|m[ée]moire de|th[èe]se|exercices?\b|homework|syllabus"), DocExt, "études"),
        new("documents", "Livres et BD", "Livres numériques et bandes dessinées", new[] { "livres", "ebooks", "bd", "lectures", "bibliothèque" },
            null, new[] { ".epub", ".mobi", ".azw", ".azw3", ".cbz", ".cbr", ".djvu", ".fb2" }, "livre numérique"),
        new("documents", "Modèles 3D", "Fichiers pour l'impression 3D et la modélisation", new[] { "3d", "impression 3d", "modèles 3d", "stl" },
            null, new[] { ".stl", ".3mf", ".obj", ".gcode", ".step", ".stp", ".f3d", ".blend", ".fbx" }, "fichier 3D"),
        new("documents", "Programmation", "Scripts et petits projets de code", new[] { "programmation", "code", "dev", "développement", "scripts" },
            null, new[] { ".py", ".js", ".ts", ".cs", ".java", ".cpp", ".c", ".h", ".html", ".css", ".php", ".sql", ".ipynb", ".ps1", ".bat", ".sh" }, "code"),
        new("documents", "Cartes et traces GPS", "Parcours, randonnées, sorties sportives", new[] { "gps", "traces gps", "parcours", "randonnées" },
            null, new[] { ".gpx", ".kml", ".kmz", ".fit", ".tcx" }, "trace GPS"),
        new("documents", "Polices", "Polices de caractères", new[] { "polices", "fonts" },
            null, new[] { ".ttf", ".otf", ".woff", ".woff2" }, "police de caractères"),
        // Images
        new("images", "Captures d'écran", "Captures d'écran et extraits", new[] { "captures d'écran", "captures d’écran", "screenshots", "captures", "capture d'écran" },
            Rx(@"capture d.[ée]cran|screenshot|screen ?shot|\bsnip|clipboard|image \(\d+\)"), ImgExt, "capture d'écran"),
        new("images", "Photos", "Photos d'appareil et de téléphone", new[] { "photos", "pellicule", "camera roll", "appareil photo" },
            Rx(@"^(img|dsc|dscn|dscf|pxl|mvimg|photo|whatsapp image|p\d{7}|\d{8}[_-]\d{6})"), ImgExt.Concat(RawExt).ToArray(), "photo d'appareil"),
        new("images", "Créations IA", "Images générées par une intelligence artificielle", new[] { "créations ia", "ia", "ai", "images ia" },
            Rx(@"chatgpt image|dall.?e|midjourney|gemini_generated|copilot|firefly|stable.?diffusion|leonardo"), ImgExt, "image générée par IA"),
        new("images", "Fonds d'écran", "Images pour le bureau", new[] { "fonds d'écran", "fond d'écran", "wallpapers", "wallpaper" },
            Rx(@"wallpaper|fond d.[ée]cran|background|\b(3840|2560|1920)x(2160|1440|1080)\b|\b4k\b"), ImgExt, "fond d'écran"),
        new("images", "Icônes et logos", "Icônes, logos, pictogrammes", new[] { "icônes", "icones", "icons", "logos", "logo" },
            Rx(@"\bic[oô]ne|\bicon|\blogo"), ImgExt.Concat(new[] { ".ico", ".svg" }).ToArray(), "icône"),
        new("images", "Numérisations", "Documents et photos numérisés", new[] { "numérisations", "scans", "scan", "numérisation" },
            Rx(@"\bscan|num[ée]ris|^\d{14}$"), ImgExt.Concat(new[] { ".pdf" }).ToArray(), "numérisation"),
        // Vidéos
        new("videos", "Films et séries", "Films et épisodes", new[] { "films", "séries", "series", "movies" },
            Rx(@"s\d{1,2}e\d{1,2}|\b(1080p|720p|2160p|x264|x265|hevc|bluray|web-?rip|web-?dl)\b"), VidExt, "film ou épisode"),
        new("videos", "Enregistrements de jeu", "Clips et captures de parties", new[] { "enregistrements de jeu", "clips", "captures", "gaming", "replays" },
            Rx(@"\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}|replay|highlight|\bclip\b|game ?bar"), VidExt, "enregistrement de jeu"),
        new("videos", "Vidéos personnelles", "Vidéos de téléphone et d'appareil photo", new[] { "vidéos personnelles", "souvenirs", "famille" },
            Rx(@"^(vid|pxl|mvi|mov|dji|gopr|gh\d{2})[_-]?\d"), VidExt, "vidéo d'appareil"),
        // Musique
        new("musique", "Enregistrements", "Mémos vocaux et enregistrements", new[] { "enregistrements", "enregistrements audio", "mémos vocaux", "recordings" },
            Rx(@"enregistrement|recording|m[ée]mo vocal|voice|rec_\d|audio \d"), AudExt, "enregistrement"),
        new("musique", "Podcasts", "Émissions et podcasts", new[] { "podcasts", "émissions" }, Rx(@"podcast|[ée]pisode|episode"), AudExt, "podcast"),
        new("musique", "Livres audio", "Livres audio", new[] { "livres audio", "audiobooks", "audible" }, null, new[] { ".m4b", ".aax", ".aa" }, "livre audio"),
        new("musique", "Enregistrements", "Mémos vocaux et enregistrements", new[] { "enregistrements", "enregistrements audio", "mémos vocaux", "recordings" },
            null, new[] { ".amr", ".3ga", ".m4r" }, "mémo vocal"),
        // Tout autre fichier audio va dans Musique, où l'utilisateur classe ensuite par artiste ou album comme il l'entend
        // (pas de synonymes : « Ma musique » dans Documents est un raccourci vers ce même dossier)
        new("musique", "", "Musique et fichiers audio", Array.Empty<string>(), null, AudExt.Concat(PlaylistExt).ToArray(), "fichier audio"),
        // Téléchargements (éléments techniques)
        // Script d'installation (« JaxCoreSetup.bat ») : c'est un installateur, pas du code de l'utilisateur
        new("telechargements", "Programmes d'installation", "Installateurs à garder ou supprimer plus tard", new[] { "programmes d'installation", "installateurs", "setups" },
            Rx(@"setup|install"), new[] { ".bat", ".cmd", ".ps1", ".vbs", ".sh" }, "programme d'installation"),
        new("telechargements", "Programmes d'installation", "Installateurs à garder ou supprimer plus tard", new[] { "programmes d'installation", "installateurs", "setups", "logiciels", "programmes" },
            null, new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appinstaller" }, "programme d'installation"),
        new("telechargements", "Images disque", "Fichiers ISO et images disque", new[] { "iso", "images disque" }, null, new[] { ".iso", ".img", ".vhd", ".vhdx" }, "image disque"),
        new("telechargements", "Archives", "Fichiers compressés", new[] { "archives", "zip", "compressés" }, null, new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" }, "fichier compressé"),
        new("telechargements", "Torrents", "Fichiers .torrent", new[] { "torrents" }, null, new[] { ".torrent" }, "fichier torrent"),
    };


    // ------------------------------------------------------------------ dossiers des applications
    /// <summary>Dossiers créés par Windows ou des applications à la racine de Documents, Images… : jamais déplacés ni proposés.</summary>
    private static readonly Regex AppFolderNames = Rx(
        @"^(my games|mes jeux|saved games|windowspowershell|powershell|custom office templates|mod[eè]les office personnalis[ée]s|outlook files|fichiers outlook|"
        + @"onenote notebooks|blocs-notes onenote|visual studio.*|sql server management studio.*|iisexpress|my web sites|mes sites web|zoom|adobe|arduino|github|"
        + @"unreal projects|unity projects|python scripts|snagit|camtasia|sharex|audacity|image-line|native instruments|bin|obj|node_modules|\.\w+|desktop\.ini|"
        + @"my received files|fichiers reçus|wechat files|tencent files|kingsoft|bluestacks.*|nvidia.*|amd|electronic arts|ea games|ubisoft|rockstar games|"
        + @"klei|paradox interactive|bioware|cd projekt red|steamvr|steam|battle\.net|riot games|epic games|gog\.com|xboxgames)$");

    /// <summary>Dossiers gérés par Windows mais qui sont du contenu de l'utilisateur : repris tels quels dans l'arborescence.</summary>
    private static readonly Regex WindowsContentFolders = Rx(
        @"^(screenshots|captures d.[ée]cran|captures|camera roll|pellicule|saved pictures|images enregistr[ée]es|enregistrements audio|sound recordings|"
        + @"enregistrements|playlists|listes de lecture)$");

    private static readonly string[] AppFileExt =
    {
        ".ini", ".cfg", ".conf", ".config", ".json", ".xml", ".yaml", ".yml", ".db", ".sqlite", ".sqlite3", ".sav", ".save", ".dat", ".log",
        ".dll", ".exe", ".bin", ".lock", ".ldb", ".pst", ".ost", ".profile", ".pak", ".bak", ".tmp", ".vdf", ".sln", ".csproj", ".gitignore"
    };

    private static readonly string[] ProjectMarkers = { ".git", ".svn", ".vs", ".idea", "package.json", "pom.xml", "Cargo.toml", "go.mod", "composer.json", "requirements.txt" };

    /// <summary>
    /// Dossier appartenant à une application, un jeu ou un projet de développement (à ne jamais déplacer) :
    /// nom connu, nom d'une application installée ou d'un jeu, fichiers de réglages ou de sauvegarde, dépôt de code.
    /// </summary>
    public static bool IsAppFolder(string dir, IEnumerable<string> installedNames = null)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\'));
        if (WindowsContentFolders.IsMatch(name)) return false;
        if (AppFolderNames.IsMatch(name)) return true;
        var n = Simple(name);
        if (n.Length >= 3 && installedNames != null && installedNames.Any(a => { var s = Simple(a); return s.Length >= 3 && (s == n || s.StartsWith(n) || n.StartsWith(s)); }))
            return true;
        // Nom d'un jeu ou d'un éditeur connu (« Call of Duty », « Assetto Corsa », « Overwatch »)
        var theme = Settings.ThemeFromName(name);
        if (theme is "Jeux" or "Lanceurs de jeux" or Settings.GameToolsCategory) return true;
        try
        {
            if (ProjectMarkers.Any(m => File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m)))) return true;
            int seen = 0, technical = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 }))
            {
                seen++;
                if (AppFileExt.Contains(Path.GetExtension(f).ToLowerInvariant())) technical++;
                if (seen >= 300) break;
            }
            // Réglages, sauvegardes de jeu, bases de données : c'est un dossier d'application
            return technical > 0 && technical * 4 >= seen;
        }
        catch { return true; }
    }

    // ------------------------------------------------------------------ inventaire
    private static string Simple(string s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static bool Rejected(string path) =>
        Settings.Current.DocRejected.Contains(path, StringComparer.OrdinalIgnoreCase)
        || Settings.Current.DocReview.Any(r => r.To.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static bool IsSkippable(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
        }
        catch { return true; }
        var name = Path.GetFileName(path).ToLowerInvariant();
        var ext = Path.GetExtension(name);
        // Raccourcis (rangés par la page Bureau), fichiers temporaires et téléchargements en cours
        return name == "desktop.ini" || name.StartsWith("~$") || ext is ".lnk" or ".url" or ".appref-ms" or ".tmp" or ".crdownload" or ".part" or ".partial" or ".download";
    }

    /// <summary>Sous-dossiers de l'utilisateur dans chaque grand dossier (hors dossiers d'applications), deux niveaux.</summary>
    public static List<string> UserFolders(IEnumerable<string> installedNames = null)
    {
        var list = new List<string>();
        foreach (var r in DocumentLibrary.Roots.Where(r => !string.IsNullOrEmpty(r.Path) && Directory.Exists(r.Path)))
        {
            foreach (var d in SafeDirs(r.Path))
            {
                if (IsSkippable(d) || IsAppFolder(d, installedNames)) continue;
                list.Add(d);
                foreach (var sub in SafeDirs(d).Where(x => !IsSkippable(x)).Take(50)) list.Add(sub);
            }
        }
        // Dossiers déjà connus de Centre PC (créés ou ajoutés par l'utilisateur)
        foreach (var p in Settings.Current.DocFolders.Where(Directory.Exists))
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        return list;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); }
    }

    /// <summary>Éléments non rangés : (chemin, dossier ?, origine).</summary>
    public static List<(string path, bool folder, string origin)> Unsorted(IEnumerable<string> installedNames = null)
    {
        var res = new List<(string, bool, string)>();
        var keep = Settings.Current.KeepOnDesktop;
        var desk = AppScanner.UserDesktop;
        if (Directory.Exists(desk))
        {
            foreach (var f in SafeFiles(desk).Where(f => !IsSkippable(f)))
                if (!keep.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)) res.Add((f, false, "Bureau"));
            foreach (var d in SafeDirs(desk).Where(d => !IsSkippable(d) && !DesktopManager.IsThemeFolder(d)))
                if (!keep.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase) && !IsAppFolder(d, installedNames)) res.Add((d, true, "Bureau"));
        }
        foreach (var r in DocumentLibrary.Roots.Where(r => !string.IsNullOrEmpty(r.Path) && Directory.Exists(r.Path)))
            foreach (var f in SafeFiles(r.Path).Where(f => !IsSkippable(f)))
                res.Add((f, false, r.Name));
        return res.Where(x => !Rejected(x.Item1)).ToList();
    }

    // ------------------------------------------------------------------ proposition
    /// <summary>Règle qui correspond à l'élément (nom d'abord, type ensuite), ou null.</summary>
    private static (Rule rule, string why) Match(string path, bool isFolder)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant();
        if (isFolder)
        {
            // Dossier : d'après son nom, sinon d'après la majorité de son contenu
            var byName = Rules.FirstOrDefault(r => r.Name != null && r.Name.IsMatch(name));
            if (byName != null) return (byName, $"« {byName.Why} » dans le nom");
            var exts = SafeFilesDeep(path).Select(f => Path.GetExtension(f).ToLowerInvariant()).ToList();
            if (exts.Count == 0) return (null, null);
            var top = Rules.Where(r => r.Name == null).Select(r => (r, n: exts.Count(e => r.Extensions.Contains(e)))).OrderByDescending(x => x.n).First();
            if (top.n * 2 > exts.Count) return (top.r, $"contient surtout des fichiers de type « {top.r.Why} »");
            return (null, null);
        }
        foreach (var r in Rules.Where(r => r.Name != null))
            if ((r.Extensions == null || r.Extensions.Contains(ext)) && r.Name.IsMatch(name)) return (r, $"« {r.Why} » d'après le nom");
        foreach (var r in Rules.Where(r => r.Name == null))
            if (r.Extensions.Contains(ext)) return (r, r.Why);
        return (null, null);
    }

    private static IEnumerable<string> SafeFilesDeep(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }).Take(500).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Dossier existant de l'utilisateur qui correspond à une catégorie (même nom ou synonyme) : dans le grand dossier
    /// de la catégorie d'abord (Images pour une capture d'écran), puis le moins profond.
    /// </summary>
    private static string ExistingFolderFor(Rule rule, List<string> userFolders)
    {
        var syn = rule.Synonyms.Select(Simple).Append(Simple(Path.GetFileName(rule.Path))).ToHashSet();
        var root = RootByKey(rule.RootKey)?.Path;
        return userFolders.Where(f => syn.Contains(Simple(Path.GetFileName(f.TrimEnd('\\')))) && IsGeneralDestination(f))
                          .OrderBy(f => root != null && DocumentLibrary.IsUnder(f, root) ? 0 : 1)
                          .ThenBy(f => f.Count(c => c == '\\')).FirstOrDefault();
    }

    /// <summary>Noms de dossiers « thématiques » : un sous-dossier n'est une destination générale que sous l'un d'eux.</summary>
    private static readonly Lazy<HashSet<string>> CategoryNames = new(() =>
    {
        var set = Rules.SelectMany(r => r.Synonyms.Concat(r.Path.Split('\\'))).Select(Simple).ToHashSet();
        foreach (var w in new[] { "administratif", "papiers", "documents administratifs", "perso", "personnel", "famille", "travail", "maison", "images", "photos", "documents" })
            set.Add(Simple(w));
        return set;
    });

    /// <summary>
    /// Un dossier existant peut recevoir des fichiers venus d'ailleurs s'il est à la racine d'un grand dossier
    /// (« Documents\Impôts »), ou rangé dans un dossier thématique (« Documents\Administratif\Factures »), ou créé
    /// via Centre PC. Pas s'il est dans un jeu, un projet ou une sauvegarde (« Images\SimBin\Captures »,
    /// « Documents\Sauvegarde ancien PC\Kevin ») : ce sont des contenus particuliers, pas des catégories.
    /// </summary>
    private static bool IsGeneralDestination(string folder)
    {
        if (Settings.Current.DocFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return true;
        var root = DocumentLibrary.Roots.FirstOrDefault(r => !string.IsNullOrEmpty(r.Path) && DocumentLibrary.IsUnder(folder, r.Path));
        if (root == null) return true;
        var parts = Path.GetRelativePath(root.Path, folder.TrimEnd('\\')).Split('\\');
        return parts.Length <= 1 || parts.Take(parts.Length - 1).All(p => CategoryNames.Value.Contains(Simple(p)));
    }

    /// <summary>Prénom et nom du compte Windows : présents dans beaucoup de noms de fichiers, ils ne désignent pas un sujet.</summary>
    private static readonly Lazy<HashSet<string>> OwnNames = new(() =>
        Simple2(Environment.UserName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToHashSet());

    /// <summary>Dossier existant dont le nom figure dans le nom du fichier (« Impôts 2023.pdf » → « Documents\Impôts »).</summary>
    private static string ExistingFolderNamedIn(string name, List<string> userFolders)
    {
        var simpleName = " " + Regex.Replace(Simple2(name), @"\s+", " ") + " ";
        return userFolders.Where(f =>
            {
                var w = Simple2(Path.GetFileName(f.TrimEnd('\\')));
                return w.Length >= 4 && !OwnNames.Value.Contains(w) && simpleName.Contains(" " + w + " ") && IsGeneralDestination(f);
            })
            .OrderByDescending(f => Path.GetFileName(f).Length).FirstOrDefault();
    }

    /// <summary>Minuscules sans accents, mots séparés par des espaces.</summary>
    private static string Simple2(string s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static DocRoot RootByKey(string key) => DocumentLibrary.Roots.FirstOrDefault(r => r.Key == key);

    public static string FriendlyDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        if (string.Equals(dir.TrimEnd('\\'), AppScanner.UserDesktop?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "Bureau";
        foreach (var r in DocumentLibrary.Roots.Where(r => !string.IsNullOrEmpty(r.Path)))
        {
            if (string.Equals(dir.TrimEnd('\\'), r.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return r.Name;
            if (DocumentLibrary.IsUnder(dir, r.Path)) return r.Name + " › " + Path.GetRelativePath(r.Path, dir).Replace("\\", " › ");
        }
        return dir;
    }

    /// <summary>
    /// Destination proposée pour un élément. Sans cas prévu proprement, la proposition n'a pas de destination
    /// (<see cref="DocProposal.IsManual"/>) : c'est l'utilisateur qui choisit. Null si l'élément est déjà à sa place.
    /// </summary>
    public static DocProposal Propose(string path, bool isFolder, string origin, List<string> userFolders)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        string dest = null, why = null;

        // 1) Un dossier de l'utilisateur est cité dans le nom (« facture EDF » → son dossier « EDF » s'il existe)
        var named = ExistingFolderNamedIn(name, userFolders);
        // Un dossier voisin du même nom (« 7D96vAD » à côté de « 7D96vAD.zip ») est sa version extraite, pas un sujet
        bool sibling = named != null && Simple2(Path.GetFileName(named)) == Simple2(name)
                       && string.Equals(Path.GetDirectoryName(named.TrimEnd('\\')), parent, StringComparison.OrdinalIgnoreCase);
        if (named != null && !sibling && !string.Equals(named, parent, StringComparison.OrdinalIgnoreCase) && !(isFolder && DocumentLibrary.IsUnder(named, path)))
        {
            dest = named;
            why = $"« {Path.GetFileName(named)} » figure dans le nom";
        }
        // 2) Catégorie d'après le nom ou le type ; un dossier existant équivalent est préféré
        if (dest == null)
        {
            var (rule, ruleWhy) = Match(path, isFolder);
            var root = rule == null ? null : RootByKey(rule.RootKey);
            if (rule != null && root != null && !string.IsNullOrEmpty(root.Path))
            {
                dest = ExistingFolderFor(rule, userFolders) ?? Path.Combine(root.Path, rule.Path);
                why = ruleWhy;
            }
        }
        // 3) Aucun cas prévu avec certitude : pas de supposition, l'élément part dans la file « à ranger vous-même »
        if (dest == null) return new DocProposal { Source = path, IsFolder = isFolder, Origin = origin, Selected = false };
        if (string.Equals(dest.TrimEnd('\\'), parent?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;   // déjà à sa place
        return new DocProposal
        {
            Source = path, IsFolder = isFolder, Origin = origin, Destination = dest, DestinationLabel = FriendlyDir(dest),
            DestinationExists = Directory.Exists(dest), Reason = why
        };
    }

    /// <summary>Plan complet : dossiers proposés (existants d'abord) avec les éléments qu'ils recevront.</summary>
    public static List<DocFolderPlan> BuildPlan(IEnumerable<string> installedNames = null) => BuildPlanAndManual(installedNames).plan;

    /// <summary>
    /// Plan (éléments dont la destination est certaine) et file « à ranger vous-même » (aucun cas prévu :
    /// rien n'est deviné, l'utilisateur choisit le dossier ou laisse l'élément à sa place).
    /// </summary>
    public static (List<DocFolderPlan> plan, List<DocProposal> manual) BuildPlanAndManual(IEnumerable<string> installedNames = null)
    {
        var names = installedNames?.ToList();
        var userFolders = UserFolders(names);
        var all = Unsorted(names).Select(u => Propose(u.path, u.folder, u.origin, userFolders)).Where(p => p != null).ToList();
        var manual = all.Where(p => p.IsManual).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var proposals = all.Where(p => !p.IsManual).ToList();
        var plan = proposals.GroupBy(p => p.Destination, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DocFolderPlan
            {
                Path = g.Key, Label = FriendlyDir(g.Key), Exists = Directory.Exists(g.Key), Items = g.ToList(),
                Description = Rules.FirstOrDefault(r => r.Path.Length > 0 && g.Key.EndsWith("\\" + r.Path, StringComparison.OrdinalIgnoreCase))?.Description
            }).ToList();
        return (plan.OrderBy(p => p.Exists ? 0 : 1).ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase).ToList(), manual);
    }

    /// <summary>
    /// Arborescence conseillée pour plus tard : catégories sans élément pour l'instant et sans dossier équivalent.
    /// L'utilisateur choisit celles qu'il veut créer.
    /// </summary>
    public static List<DocFolderPlan> SuggestedEmptyFolders(List<DocFolderPlan> plan, IEnumerable<string> installedNames = null)
    {
        var userFolders = UserFolders(installedNames);
        var res = new List<DocFolderPlan>();
        foreach (var r in Rules.Where(r => r.RootKey != "telechargements" && r.Path.Length > 0))
        {
            var root = RootByKey(r.RootKey);
            if (root == null || string.IsNullOrEmpty(root.Path)) continue;
            if (ExistingFolderFor(r, userFolders) != null) continue;
            var path = Path.Combine(root.Path, r.Path);
            if (plan.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            if (res.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;   // même dossier pour plusieurs règles
            res.Add(new DocFolderPlan { Path = path, Label = FriendlyDir(path), Description = r.Description, Selected = false });
        }
        return res;
    }

    // ------------------------------------------------------------------ exécution et retour en arrière
    /// <summary>
    /// Range les éléments des dossiers choisis. Chaque déplacement est noté (vérification, historique 30 jours) :
    /// rien n'est supprimé, tout peut être remis à sa place. Renvoie le nombre d'éléments rangés et les erreurs.
    /// </summary>
    /// <summary>
    /// Éléments décochés dans le plan : ils restent où ils sont et ne seront plus proposés
    /// (l'utilisateur considère qu'ils sont à leur place).
    /// </summary>
    public static void Decline(IEnumerable<DocProposal> items)
    {
        var cfg = Settings.Current;
        foreach (var p in items)
            if (!cfg.DocRejected.Contains(p.Source, StringComparer.OrdinalIgnoreCase)) cfg.DocRejected.Add(p.Source);
        Settings.Save();
    }

    public static (int moved, List<string> errors) Apply(IEnumerable<DocFolderPlan> folders)
    {
        int moved = 0;
        var errors = new List<string>();
        var cfg = Settings.Current;
        foreach (var f in folders.Where(f => f.Selected && f.Items.Any(i => i.Selected && !i.Trash)))
        {
            try { Directory.CreateDirectory(f.Path); DocumentLibrary.AddFolder(f.Path); }
            catch (Exception ex) { errors.Add($"{f.Label} : {ex.Message}"); continue; }
            foreach (var p in f.Items.Where(i => i.Selected && !i.Trash))
            {
                try
                {
                    if (!File.Exists(p.Source) && !Directory.Exists(p.Source)) continue;
                    var to = DocumentLibrary.Move(new DocItem { Path = p.Source, IsFolder = p.IsFolder, Origin = p.Origin }, f.Path);
                    cfg.DocReview.Add(new DocReviewEntry { From = p.Source, To = to, IsFolder = p.IsFolder, Folder = f.Label, Reason = p.Reason });
                    moved++;
                }
                catch (Exception ex) { errors.Add($"{p.Name} : {ex.Message}"); Log.Error("Plan de rangement " + p.Source, ex); }
            }
        }
        Settings.Save();
        return (moved, errors);
    }

    /// <summary>Crée seulement les dossiers choisis (arborescence préparée à l'avance).</summary>
    public static void CreateFolders(IEnumerable<DocFolderPlan> folders)
    {
        foreach (var f in folders.Where(f => f.Selected))
        {
            Directory.CreateDirectory(f.Path);
            DocumentLibrary.AddFolder(f.Path);
        }
    }

    /// <summary>Rangement validé : il ne sera plus présenté.</summary>
    public static void Accept(DocReviewEntry e)
    {
        Settings.Current.DocReview.Remove(e);
        Settings.Save();
    }

    /// <summary>
    /// Rangement refusé : l'élément retourne à son emplacement d'origine et ne sera plus jamais proposé.
    /// Renvoie un message d'erreur, ou null.
    /// </summary>
    public static string Reject(DocReviewEntry e)
    {
        try
        {
            if (File.Exists(e.To) || Directory.Exists(e.To))
                DocumentLibrary.Undo(e.To, e.From, e.IsFolder);
            var cfg = Settings.Current;
            if (!cfg.DocRejected.Contains(e.From, StringComparer.OrdinalIgnoreCase)) cfg.DocRejected.Add(e.From);
            cfg.DocReview.Remove(e);
            Settings.Save();
            return null;
        }
        catch (Exception ex) { Log.Error("Retour de " + e.To, ex); return ex.Message; }
    }

    /// <summary>Déplace un élément rangé vers un autre dossier choisi par l'utilisateur (rangement validé).</summary>
    public static string MoveElsewhere(DocReviewEntry e, string folder)
    {
        try
        {
            var to = DocumentLibrary.Move(new DocItem { Path = e.To, IsFolder = e.IsFolder, Origin = e.Folder }, folder);
            Settings.Current.DocReview.Remove(e);
            Settings.Save();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
