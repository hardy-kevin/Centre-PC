using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Nom proposé pour un document, et ce qui a permis de le trouver.</summary>
public class NameSuggestion
{
    /// <summary>Nom proposé, sans l'extension (« Facture EDF 2024-03-12 »).</summary>
    public string Name { get; set; }
    /// <summary>Type de document reconnu (« Facture »).</summary>
    public string Type { get; set; }
    /// <summary>Organisme reconnu (liste connue) ou immatriculation, sinon null.</summary>
    public string Issuer { get; set; }
    /// <summary>Date mise dans le nom (« 2024-03-12 »), sinon null.</summary>
    public string Date { get; set; }
    /// <summary>Explication pour l'utilisateur (« le document parle de facture, EDF, 12 mars 2024 »).</summary>
    public string Why { get; set; }
}

/// <summary>
/// Proposition de nom d'après le contenu d'un document : on lit le début du texte (Word, LibreOffice, texte), ou le
/// texte d'une photo, d'une numérisation ou de la première page d'un PDF (reconnaissance de texte de Windows, sur ce PC).
///
/// Le nom est proposé seulement si le type de document est reconnu avec certitude (facture, avis d'imposition, bulletin
/// de paie…) : sinon rien n'est deviné, l'utilisateur renomme lui-même. Le nom suit toujours la même forme, triable :
///   Type [Organisme] [AAAA-MM-JJ ou AAAA-MM]      ex. « Facture EDF 2024-03-12 », « Bulletin de paie 2023-09 »
/// Le type figure en toutes lettres dans le nom : le plan de rangement sait ensuite où ranger le document.
/// Rien n'est renommé ici : c'est seulement une proposition.
/// </summary>
public static class DocNaming
{
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Taille maximale d'un fichier lu (au-delà, ce n'est presque jamais un papier administratif).</summary>
    private const long MaxBytes = 40L * 1024 * 1024;
    /// <summary>Seul le début compte : en-tête, titre, date, émetteur.</summary>
    private const int MaxChars = 4000;

    private static readonly string[] PlainExt = { ".txt", ".md", ".csv", ".rtf", ".htm", ".html", ".eml" };
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif", ".heic", ".heif", ".webp", ".jfif" };
    private static readonly Dictionary<string, string[]> ZipParts = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = new[] { "word/document.xml" },
        [".xlsx"] = new[] { "xl/sharedStrings.xml" },
        [".pptx"] = new[] { "ppt/slides/slide1.xml" },
        [".odt"] = new[] { "content.xml" },
        [".ods"] = new[] { "content.xml" },
        [".odp"] = new[] { "content.xml" },
    };

    /// <summary>Ce type de fichier peut-il être lu pour proposer un nom ?</summary>
    public static bool CanRead(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (PlainExt.Contains(ext) || ZipParts.ContainsKey(ext)) return true;
        return (ext == ".pdf" || ImageExt.Contains(ext)) && Ocr.Available;
    }

    // ------------------------------------------------------------------ lecture
    /// <summary>Début du texte du document (null si illisible).</summary>
    public static async Task<string> ReadTextAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0 || fi.Length > MaxBytes) return null;
            var ext = fi.Extension.ToLowerInvariant();
            string text = null;
            if (PlainExt.Contains(ext)) text = await DocPreview.LoadTextAsync(path);
            else if (ZipParts.TryGetValue(ext, out var parts)) text = await Task.Run(() => ReadZipXml(path, parts), ct);
            else if (ext == ".pdf") text = await Ocr.FromPdfAsync(path, ct);
            else if (ImageExt.Contains(ext)) text = await Ocr.FromImageAsync(path, ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (ext is ".rtf") text = Regex.Replace(text, @"\\[a-z]+-?\d* ?|[{}]", " ");
            if (ext is ".htm" or ".html") text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
            return text.Length > MaxChars ? text[..MaxChars] : text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Info($"Lecture du contenu impossible : {Path.GetFileName(path)} ({ex.Message})"); return null; }
    }

    /// <summary>Texte d'un document Office ou OpenDocument (fichier zip contenant du XML).</summary>
    private static string ReadZipXml(string path, string[] parts)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        foreach (var part in parts)
        {
            var entry = zip.GetEntry(part);
            if (entry == null) continue;
            using var r = new StreamReader(entry.Open());
            var buf = new char[200_000];
            int n = r.ReadBlock(buf, 0, buf.Length);
            var xml = new string(buf, 0, n);
            // Fins de paragraphe et cellules : retours à la ligne ; balises retirées
            xml = Regex.Replace(xml, @"</(w:p|text:p|text:h|a:p|si)>|<(w:br|w:tab|text:tab|text:line-break)[^>]*/>", "\n");
            return WebUtility.HtmlDecode(Regex.Replace(xml, "<[^>]+>", ""));
        }
        return null;
    }

    // ------------------------------------------------------------------ reconnaissance
    private record DocType(string Label, Regex Rx);

    /// <summary>
    /// Types reconnus. Parmi les types précis, celui cité le plus tôt l'emporte (le titre est en haut du document : une
    /// facture qui parle plus bas de « bon de commande » reste une facture). Les trois derniers, généraux, ne servent
    /// que si aucun type précis n'est trouvé.
    /// </summary>
    private static readonly DocType[] Types =
    {
        new("RIB", Rx(@"relev[ée] d.identit[ée] bancaire")),
        new("Avis d'imposition", Rx(@"avis d.(imp[oô]t|imposition)|imp[oô]t sur le revenu|taxe fonci[eè]re|taxe d.habitation|avis de taxe")),
        new("Déclaration de revenus", Rx(@"d[ée]claration (des |de )?revenus")),
        new("Bulletin de paie", Rx(@"bulletin de (paie|salaire)|fiche de paie|net [àa] payer avant imp[oô]t|salaire de base")),
        new("Relevé de compte", Rx(@"relev[ée] (de |des )?(compte|vos comptes|op[ée]rations)|extrait de compte")),
        new("Tableau d'amortissement", Rx(@"tableau d.amortissement")),
        new("Quittance de loyer", Rx(@"quittance de loyer")),
        new("Bail", Rx(@"contrat de (bail|location)|bail d.habitation")),
        new("État des lieux", Rx(@"[ée]tat des lieux")),
        new("Ordonnance", Rx(@"\bordonnance\b")),
        new("Attestation d'assurance", Rx(@"attestation d.assurance|m[ée]mo v[ée]hicule assur[ée]|carte verte")),
        new("Contrat de travail", Rx(@"contrat de travail|contrat [àa] dur[ée]e (in)?d[ée]termin[ée]e")),
        new("Certificat de cession", Rx(@"certificat de cession|d[ée]claration de cession")),
        new("Certificat d'immatriculation", Rx(@"certificat d.immatriculation|carte grise")),
        new("Contrôle technique", Rx(@"contr[ôo]le technique")),
        new("Carte d'identité", Rx(@"carte nationale d.identit[ée]|identity card")),
        new("Passeport", Rx(@"\bpasseport\b|\bpassport\b")),
        new("Permis de conduire", Rx(@"permis de conduire|driving licen[cs]e")),
        new("Acte de naissance", Rx(@"acte de naissance")),
        new("Relevé de notes", Rx(@"relev[ée] de notes|bulletin (de notes|scolaire|trimestriel)")),
        new("Lettre de motivation", Rx(@"lettre de motivation")),
        new("CV", Rx(@"curriculum vitae")),
        new("Billet", Rx(@"e-?billet|carte d.embarquement|boarding pass|billet de (train|bus|car|transport)")),
        new("Réservation", Rx(@"confirmation de (votre )?r[ée]servation|votre r[ée]servation (est|a bien)")),
        new("Devis", Rx(@"\bdevis\b")),
        new("Bon de commande", Rx(@"bon de commande|confirmation de (votre )?commande|r[ée]capitulatif de (votre )?commande")),
        new("Facture", Rx(@"\bfacture\b|\binvoice\b|n[°o]\s*de\s*facture")),
        new("Reçu", Rx(@"re[çc]u (de paiement|n[°o])|ticket de caisse|\breceipt\b")),
        new("Attestation", Rx(@"\battestation\b")),
        new("Contrat", Rx(@"\bcontrat\b|conditions particuli[eè]res")),
        new("Courrier", Rx(@"\bmadame,|\bmonsieur,|veuillez agr[ée]er|nous vous prions d.agr[ée]er")),
    };

    /// <summary>
    /// Organismes courants (nom affiché ← motif dans le texte). Liste générale (fournisseurs d'énergie, opérateurs,
    /// banques, assurances, administrations, transport, commerce) : un organisme inconnu n'empêche pas la proposition.
    /// </summary>
    private static readonly (string Name, Regex Rx)[] Issuers =
    {
        ("Impôts", Rx(@"impots\.gouv|direction g[ée]n[ée]rale des finances publiques|\bdgfip\b")),
        ("CAF", Rx(@"\bcaf\b|caisse d.allocations familiales")),
        ("Assurance Maladie", Rx(@"\bameli\b|\bcpam\b|assurance maladie")),
        ("France Travail", Rx(@"france travail|p[ôo]le emploi")),
        ("URSSAF", Rx(@"\burssaf\b")),
        ("ANTS", Rx(@"\bants\b|agence nationale des titres")),
        ("EDF", Rx(@"\bedf\b")), ("Engie", Rx(@"\bengie\b")), ("TotalEnergies", Rx(@"totalenergies|total direct energie")),
        ("Orange", Rx(@"\borange\b(?! bank)")), ("SFR", Rx(@"\bsfr\b")), ("Free", Rx(@"\bfree mobile\b|\bfreebox\b|\bfree\.fr\b")),
        ("Bouygues Telecom", Rx(@"bouygues telecom")), ("Sosh", Rx(@"\bsosh\b")), ("Red by SFR", Rx(@"red by sfr")),
        ("Veolia", Rx(@"\bveolia\b")), ("Suez", Rx(@"\bsuez\b")),
        ("Crédit Agricole", Rx(@"cr[ée]dit agricole")), ("BNP Paribas", Rx(@"bnp paribas")), ("Société Générale", Rx(@"soci[ée]t[ée] g[ée]n[ée]rale")),
        ("LCL", Rx(@"\blcl\b")), ("La Banque Postale", Rx(@"banque postale")), ("Caisse d'Épargne", Rx(@"caisse d.[ée]pargne")),
        ("Crédit Mutuel", Rx(@"cr[ée]dit mutuel")), ("CIC", Rx(@"\bcic\b")), ("Boursorama", Rx(@"boursorama|boursobank")),
        ("Fortuneo", Rx(@"\bfortuneo\b")), ("Hello bank", Rx(@"hello ?bank")), ("Revolut", Rx(@"\brevolut\b")), ("PayPal", Rx(@"\bpaypal\b")),
        ("MAIF", Rx(@"\bmaif\b")), ("MACIF", Rx(@"\bmacif\b")), ("MAAF", Rx(@"\bmaaf\b")), ("MMA", Rx(@"\bmma\b")), ("AXA", Rx(@"\baxa\b")),
        ("Allianz", Rx(@"\ballianz\b")), ("Groupama", Rx(@"\bgroupama\b")), ("Matmut", Rx(@"\bmatmut\b")), ("GMF", Rx(@"\bgmf\b")),
        ("Generali", Rx(@"\bgenerali\b")), ("Harmonie Mutuelle", Rx(@"harmonie mutuelle")), ("MGEN", Rx(@"\bmgen\b")),
        ("SNCF", Rx(@"\bsncf\b|ouigo|\btgv\b")), ("RATP", Rx(@"\bratp\b")), ("Air France", Rx(@"air france")), ("easyJet", Rx(@"\beasyjet\b")),
        ("Ryanair", Rx(@"\bryanair\b")), ("Booking", Rx(@"booking\.com")), ("Airbnb", Rx(@"\bairbnb\b")), ("BlaBlaCar", Rx(@"blablacar")),
        ("Amazon", Rx(@"\bamazon\b")), ("Fnac", Rx(@"\bfnac\b")), ("Darty", Rx(@"\bdarty\b")), ("Cdiscount", Rx(@"\bcdiscount\b")),
        ("Boulanger", Rx(@"\bboulanger\b")), ("Leroy Merlin", Rx(@"leroy merlin")), ("IKEA", Rx(@"\bikea\b")), ("Decathlon", Rx(@"\bdecathlon\b")),
        ("Apple", Rx(@"\bapple\b")), ("Google", Rx(@"\bgoogle\b")), ("Microsoft", Rx(@"\bmicrosoft\b")), ("Steam", Rx(@"\bsteam\b|valve corporation")),
        ("Netflix", Rx(@"\bnetflix\b")), ("Spotify", Rx(@"\bspotify\b")), ("Canal+", Rx(@"canal\+")), ("Uber", Rx(@"\buber\b")),
    };

    /// <summary>Documents officiels : pas d'organisme dans le nom.</summary>
    private static readonly HashSet<string> OfficialForms = new()
        { "Carte d'identité", "Passeport", "Permis de conduire", "Acte de naissance", "Certificat d'immatriculation", "Certificat de cession", "Contrôle technique" };
    /// <summary>Papiers d'un véhicule : l'immatriculation remplace l'organisme.</summary>
    private static readonly HashSet<string> VehicleTypes = new() { "Certificat d'immatriculation", "Certificat de cession", "Contrôle technique" };
    /// <summary>Immatriculation française (système SIV : AB-123-CD), lettres I, O et U exclues.</summary>
    private static readonly Regex Plate = new(@"(?<![A-Z0-9])([A-HJ-NP-TV-Z]{2})[\s-]?(\d{3})[\s-]?([A-HJ-NP-TV-Z]{2})(?![A-Z0-9])", RegexOptions.CultureInvariant);

    /// <summary>
    /// Formulaires à cases (cerfa) : la reconnaissance lit souvent un caractère par case (« H,E,-,1,6,1,-,H,Y »).
    /// Les suites d'au moins quatre caractères isolés sont recollées (« HE-161-HY », « 27062025 »).
    /// </summary>
    private static string Compact(string t) =>
        Regex.Replace(t, @"(?<![\w-])(?:[\w-][\s,|.]{1,3}){3,}[\w-](?![\w-])", m => Regex.Replace(m.Value, @"[\s,|.]+", ""));

    /// <summary>Nombre de types généraux en fin de liste (attestation, contrat, courrier).</summary>
    private const int GeneralTypes = 3;

    /// <summary>Noms des organismes reconnus (intégrés et ajoutés par le catalogue signé) : éditeurs connus, jamais des personnes.</summary>
    public static IEnumerable<string> IssuerNames => Issuers.Select(i => i.Name).Concat(RemoteCatalog.Organisations.Keys);

    private static readonly string[] Months = { "janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre" };
    private static readonly Regex NumericDate = Rx(@"\b(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{4})\b");
    private static readonly Regex IsoDate = Rx(@"\b(\d{4})-(\d{2})-(\d{2})\b");
    private static readonly Regex WordDate = Rx(@"\b(\d{1,2})(?:er)?\s+(janvier|f[ée]vrier|mars|avril|mai|juin|juillet|ao[uû]t|septembre|octobre|novembre|d[ée]cembre)\s+(\d{4})\b");
    /// <summary>Date saisie dans des cases, recollée : JJMMAAAA.</summary>
    private static readonly Regex BoxedDate = Rx(@"(?<!\d)(\d{2})(\d{2})(\d{4})(?!\d)");
    /// <summary>Date du document lui-même (émission, cession, signature) plutôt qu'une autre date citée.</summary>
    private static readonly Regex DateAnchor = Rx(@"(fait (à|a) [^,\d]{1,40},?|\bfait|date( et heure)? (de la |de l.|d.)?(cession|vente|facture|[ée]mission|[ée]tablissement|signature)|[ée]mise?|[ée]tablie?|sign[ée]e?)\s*:?\s*(le\s*)?:?\s*$");
    private static readonly Regex MonthYear = Rx(@"\b(janvier|f[ée]vrier|mars|avril|mai|juin|juillet|ao[uû]t|septembre|octobre|novembre|d[ée]cembre)\s+(\d{4})\b");
    /// <summary>Documents mensuels : le mois concerné compte plus que le jour d'émission.</summary>
    private static readonly HashSet<string> Monthly = new() { "Bulletin de paie", "Relevé de compte", "Quittance de loyer" };

    /// <summary>Propose un nom d'après le texte, ou null si le type de document n'est pas reconnu.</summary>
    public static NameSuggestion Suggest(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Compact(Regex.Replace(text, @"\s+", " "));
        var type = FindType(t);
        if (type == null) return null;

        // Organisme : le premier cité (en-tête du document)
        // Formulaire officiel (pièce d'identité, papiers du véhicule) : l'organisme cité est l'administration, pas
        // l'intérêt du document ; pour un véhicule, l'immatriculation le désigne
        string issuer = OfficialForms.Contains(type.Label) ? null
            : Issuers.Concat(RemoteCatalog.OrganisationPatterns).Select(i => (i.Name, m: i.Rx.Match(t))).Where(x => x.m.Success)
                     .OrderBy(x => x.m.Index).Select(x => x.Name).FirstOrDefault();
        if (VehicleTypes.Contains(type.Label)) issuer = Plate.Match(t) is { Success: true } pm ? $"{pm.Groups[1].Value}-{pm.Groups[2].Value}-{pm.Groups[3].Value}".ToUpperInvariant() : null;
        var date = FindDate(t, Monthly.Contains(type.Label));

        var parts = new List<string> { type.Label };
        if (issuer != null) parts.Add(issuer);
        if (date != null) parts.Add(date.Value.text);
        var why = new List<string> { $"« {type.Label.ToLowerInvariant()} »" };
        if (issuer != null) why.Add(VehicleTypes.Contains(type.Label) ? "immatriculation " + issuer : issuer);
        if (date != null) why.Add(date.Value.spoken);
        return new NameSuggestion
        {
            Name = Clean(string.Join(" ", parts)),
            Type = type.Label,
            Issuer = issuer,
            Date = date?.text,
            Why = "lu dans le document : " + string.Join(", ", why)
        };
    }

    /// <summary>Type précis cité le plus tôt, sinon un type général, sinon null.</summary>
    private static DocType FindType(string t) =>
        Types.Take(Types.Length - GeneralTypes).Select(d => (d, m: d.Rx.Match(t))).Where(x => x.m.Success)
             .OrderBy(x => x.m.Index).Select(x => x.d).FirstOrDefault()
        ?? Types.Skip(Types.Length - GeneralTypes).FirstOrDefault(d => d.Rx.IsMatch(t));

    /// <summary>Type de document que désigne un nom de fichier (« Facture EDF mars » → « Facture »), ou null.</summary>
    public static string TypeOfName(string name) => string.IsNullOrWhiteSpace(name) ? null : FindType(name)?.Label;

    /// <summary>Lit le document et propose un nom (null : illisible ou type non reconnu).</summary>
    public static async Task<NameSuggestion> SuggestAsync(string path, CancellationToken ct = default)
    {
        if (!CanRead(path)) return null;
        var s = Suggest(await ReadTextAsync(path, ct));
        // Déjà nommé ainsi : rien à proposer
        if (s != null && string.Equals(s.Name, Path.GetFileNameWithoutExtension(path), StringComparison.CurrentCultureIgnoreCase)) return null;
        return s;
    }

    /// <summary>Date du document : jour (AAAA-MM-JJ) ou mois (AAAA-MM) ; la première date plausible du texte.</summary>
    private static (string text, string spoken)? FindDate(string t, bool monthly)
    {
        var found = new List<(int index, DateTime date, bool dayKnown)>();
        int maxYear = DateTime.Now.Year + 1;
        void Add(int index, int y, int m, int d, bool dayKnown)
        {
            if (y < 1980 || y > maxYear || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return;
            found.Add((index, new DateTime(y, m, d), dayKnown));
        }
        foreach (Match m in NumericDate.Matches(t)) Add(m.Index, int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), true);
        foreach (Match m in IsoDate.Matches(t)) Add(m.Index, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), true);
        foreach (Match m in WordDate.Matches(t)) Add(m.Index, int.Parse(m.Groups[3].Value), MonthIndex(m.Groups[2].Value), int.Parse(m.Groups[1].Value), true);
        foreach (Match m in BoxedDate.Matches(t)) Add(m.Index, int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), true);
        var monthYears = MonthYear.Matches(t).Cast<Match>().Select(m => (m.Index, y: int.Parse(m.Groups[2].Value), mo: MonthIndex(m.Groups[1].Value))).ToList();
        if (monthly && monthYears.Count > 0)
        {
            // « Période : septembre 2023 » : le mois concerné
            var my = monthYears.First();
            if (my.y >= 1980 && my.y <= maxYear && my.mo > 0) return ($"{my.y:0000}-{my.mo:00}", $"{Months[my.mo - 1]} {my.y}");
        }
        foreach (var my in monthYears) Add(my.Index, my.y, my.mo, 1, false);
        if (found.Count == 0) return null;
        // Une date annoncée (« Fait à Lyon, le … », « Date de la cession : … ») l'emporte ; sinon la première du texte
        bool Anchored(int index) => DateAnchor.IsMatch(t[Math.Max(0, index - 60)..index]);
        var first = found.Where(f => Anchored(f.index)).OrderBy(f => f.index).Concat(found.OrderBy(f => f.index)).First();
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        if (monthly || !first.dayKnown) return ($"{first.date:yyyy-MM}", first.date.ToString("MMMM yyyy", culture));
        return ($"{first.date:yyyy-MM-dd}", first.date.ToString("d MMMM yyyy", culture));
    }

    private static int MonthIndex(string month)
    {
        var m = month.ToLowerInvariant().Replace('é', 'e').Replace('û', 'u');
        for (int i = 0; i < Months.Length; i++)
            if (Months[i].Replace('é', 'e').Replace('û', 'u') == m) return i + 1;
        return 0;
    }

    // ------------------------------------------------------------------ nom de fichier
    /// <summary>Caractères interdits dans un nom de fichier de Windows (\ / : * ? " &lt; &gt; |).</summary>
    public static bool HasInvalidChars(string name) => name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

    /// <summary>Nom utilisable par Windows : caractères interdits remplacés, espaces et points de fin retirés.</summary>
    public static string Clean(string name)
    {
        var s = new string((name ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.', ' ');
        return s.Length > 120 ? s[..120].TrimEnd() : s;
    }

    /// <summary>Message quand le nom est déjà pris à cet endroit.</summary>
    public const string NameTaken = "Un autre élément porte déjà ce nom à cet endroit.";

    /// <summary>
    /// Vérifie un nouveau nom (sans extension) avant de renommer. Renvoie un message pour l'utilisateur, ou null si le nom convient.
    /// </summary>
    public static string Validate(string baseName, string currentPath, bool isFolder)
    {
        var n = (baseName ?? "").Trim();
        if (n.Length == 0) return "Saisissez un nom.";
        if (HasInvalidChars(n)) return "Ces caractères ne sont pas acceptés par Windows : \\ / : * ? \" < > |";
        if (n.EndsWith('.')) return "Le nom ne peut pas finir par un point.";
        if (Regex.IsMatch(n, @"^(con|prn|aux|nul|com\d|lpt\d)$", RegexOptions.IgnoreCase)) return "Ce nom est réservé par Windows.";
        var ext = isFolder ? "" : Path.GetExtension(currentPath);
        var target = Path.Combine(Path.GetDirectoryName(currentPath.TrimEnd('\\')) ?? "", n + ext);
        if (target.Length >= 250) return "Le nom est trop long pour cet emplacement.";
        bool sameItem = string.Equals(target, currentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (!sameItem && (File.Exists(target) || Directory.Exists(target))) return NameTaken;
        return null;
    }

    /// <summary>
    /// Renomme un fichier ou un dossier sur place (l'extension est gardée). Noté dans l'historique : annulable.
    /// Renvoie le nouveau chemin.
    /// </summary>
    public static string Rename(string path, string baseName, bool isFolder)
    {
        var err = Validate(baseName, path, isFolder);
        if (err != null) throw new IOException(err);
        var src = path.TrimEnd('\\');
        var ext = isFolder ? "" : Path.GetExtension(src);
        var target = Path.Combine(Path.GetDirectoryName(src) ?? "", baseName.Trim() + ext);
        if (string.Equals(target, src, StringComparison.Ordinal)) return src;
        if (isFolder)
        {
            // Changement de casse seulement : Windows demande un passage par un nom provisoire
            if (string.Equals(target, src, StringComparison.OrdinalIgnoreCase))
            {
                var tmp = src + ".centrepc-renommage";
                Directory.Move(src, tmp);
                Directory.Move(tmp, target);
            }
            else Directory.Move(src, target);
        }
        else File.Move(src, target);
        History.Record("renommage", src, target, isFolder);
        return target;
    }
}
