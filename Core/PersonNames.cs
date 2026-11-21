using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>
/// Repère les noms de personnes (prénom + nom de famille) avant qu'un texte parte dans les retours d'expérience.
///
/// Listes : prénoms et noms de famille de l'Insee, plus les éditeurs et marques connus (fichier noms.gz fabriqué par
/// Outils\listes-noms.ps1, embarqué dans Centre PC ; une version plus récente et signée est reprise une fois par mois
/// depuis le catalogue). Les mêmes règles sont appliquées par le service des retours (Serveur\retours-worker.js).
///
/// Règles :
/// - un prénom suivi ou précédé d'un nom de famille (particules ignorées : « Jean de La Fontaine ») = un nom de personne ;
/// - un éditeur fait d'un seul mot qui est un nom de famille ou un prénom = une personne, sauf éditeur connu ou forme
///   d'entreprise (« Martin Software », « Dupont SARL »).
/// Sans listes (fichier absent ou illisible), <see cref="Available"/> est faux : les retours qui citent une application,
/// un éditeur ou un mot ne sont pas envoyés du tout. Dans le doute, rien.
/// </summary>
public static class PersonNames
{
    public const string Url = "https://raw.githubusercontent.com/" + AppUpdater.Owner + "/" + AppUpdater.Repo + "/main/Catalogue/noms.gz";
    private const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Particules : jamais dans les listes, ignorées entre un prénom et un nom (même liste que le script et le serveur).</summary>
    private static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
        { "de", "du", "des", "la", "le", "les", "van", "von", "der", "den", "del", "della", "dos", "das", "da", "di", "el", "al", "ben", "bin", "mac", "mc", "saint", "sainte", "st", "ste" };

    /// <summary>Mots qui désignent une entreprise : un éditeur qui en contient un n'est pas une personne.</summary>
    private static readonly HashSet<string> CompanyWords = new(StringComparer.Ordinal)
    {
        "inc", "ltd", "llc", "llp", "plc", "sa", "sas", "sasu", "sarl", "eurl", "sci", "gmbh", "ag", "kg", "bv", "nv", "srl", "spa", "oy", "ab", "as", "kk",
        "corp", "corporation", "co", "company", "limited", "group", "groupe", "holding", "software", "logiciels", "games", "game", "studio", "studios",
        "technologies", "technology", "tech", "labs", "lab", "interactive", "entertainment", "digital", "media", "systems", "solutions", "services",
        "foundation", "fondation", "association", "project", "team", "community", "electronics", "industries", "international", "network", "networks"
    };

    private static HashSet<string> _first = new(), _last = new(), _known = new();
    private static int _version;
    private static bool _loaded;
    private static readonly object Gate = new();

    /// <summary>Listes chargées et complètes : les filtres peuvent laisser passer un nom d'application ou d'éditeur.</summary>
    public static bool Available { get { EnsureLoaded(); return _first.Count > 0 && _last.Count > 0; } }
    public static int Version { get { EnsureLoaded(); return _version; } }

    // ------------------------------------------------------------------ règles
    /// <summary>Minuscules sans accents (« Éléonore » → « eleonore »), comme le script et le serveur.</summary>
    public static string Fold(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("Œ", "OE").Replace("œ", "oe").Replace("Æ", "AE").Replace("æ", "ae").Replace("ß", "ss");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Mots du texte (position, longueur, forme repliée), particules et mots de moins de 3 lettres écartés.</summary>
    private static List<(int index, int length, string word)> Words(string text)
    {
        var res = new List<(int, int, string)>();
        foreach (Match m in Regex.Matches(text ?? "", @"\p{L}+"))
        {
            var f = Regex.Replace(Fold(m.Value), "[^a-z]", "");
            if (f.Length >= 3 && !Particles.Contains(f)) res.Add((m.Index, m.Length, f));
        }
        return res;
    }

    private static bool IsPair(string a, string b) => (_first.Contains(a) && _last.Contains(b)) || (_last.Contains(a) && _first.Contains(b));

    /// <summary>Le texte contient-il un prénom suivi ou précédé d'un nom de famille ?</summary>
    public static bool HasFullName(string text)
    {
        if (!Available || string.IsNullOrWhiteSpace(text)) return false;
        var w = Words(text);
        for (int i = 0; i + 1 < w.Count; i++)
            if (IsPair(w[i].word, w[i + 1].word)) return true;
        return false;
    }

    /// <summary>Remplace chaque paire prénom + nom par %NOM% ; le reste du texte est gardé.</summary>
    public static string MaskFullNames(string text)
    {
        if (!Available || string.IsNullOrWhiteSpace(text)) return text;
        var w = Words(text);
        var spans = new List<(int start, int end)>();
        for (int i = 0; i + 1 < w.Count; i++)
            if (IsPair(w[i].word, w[i + 1].word))
            {
                spans.Add((w[i].index, w[i + 1].index + w[i + 1].length));
                i++;
            }
        if (spans.Count == 0) return text;
        var sb = new StringBuilder(text);
        foreach (var (start, end) in spans.OrderByDescending(s => s.start))
            sb.Remove(start, end - start).Insert(start, "%NOM%");
        return sb.ToString();
    }

    /// <summary>Éditeur connu (liste du fichier des noms, organismes des documents, catalogue signé).</summary>
    public static bool IsKnownPublisher(string publisher)
    {
        var f = Regex.Replace(Fold(publisher), "[^a-z0-9]+", " ").Trim();
        if (f.Length == 0) return false;
        return _known.Contains(f) || _known.Contains(f.Replace(" ", ""))
               || RemoteCatalog.Publishers.Any(p => Regex.Replace(Fold(p), "[^a-z0-9]+", " ").Trim() == f);
    }

    /// <summary>
    /// Éditeur qui désigne une personne : paire prénom + nom, ou un seul mot qui est un prénom ou un nom de famille
    /// (sauf éditeur connu ou forme d'entreprise). Sans listes : vrai (rien n'est envoyé).
    /// </summary>
    public static bool IsPersonPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;
        if (!Available) return true;
        if (HasFullName(publisher)) return true;
        var tokens = Regex.Matches(Fold(publisher), "[a-z0-9]+").Select(m => m.Value).ToList();
        if (tokens.Any(CompanyWords.Contains) || IsKnownPublisher(publisher)) return false;
        var words = tokens.Where(t => t.Length >= 3 && !Particles.Contains(t) && !t.All(char.IsDigit)).ToList();
        return words.Count == 1 && (_last.Contains(words[0]) || _first.Contains(words[0]));
    }

    // ------------------------------------------------------------------ chargement
    private static string CachePath => Path.Combine(Settings.DataDir, "noms.gz");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                using var s = typeof(PersonNames).Assembly.GetManifestResourceStream("noms.gz");
                if (s != null) { using var ms = new MemoryStream(); s.CopyTo(ms); Load(ms.ToArray()); }
            }
            catch (Exception ex) { Log.Info("Listes de noms embarquées illisibles : " + ex.Message); }
            // Version plus récente reçue du catalogue (signature revérifiée à chaque lancement)
            try
            {
                if (File.Exists(CachePath) && File.Exists(CachePath + ".sig")
                    && RemoteCatalog.Verify(File.ReadAllBytes(CachePath), File.ReadAllText(CachePath + ".sig"), RemoteCatalog.PublicKeys))
                {
                    var bytes = File.ReadAllBytes(CachePath);
                    if (ReadVersion(bytes) > _version) Load(bytes);
                }
            }
            catch (Exception ex) { Log.Info("Listes de noms reçues illisibles : " + ex.Message); }
        }
    }

    private static string Decompress(byte[] gz)
    {
        using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var r = new StreamReader(z, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static int ReadVersion(byte[] gz)
    {
        try
        {
            var head = Decompress(gz);
            if (!head.StartsWith("CentrePC-noms 1\n", StringComparison.Ordinal)) return 0;
            var m = Regex.Match(head[..Math.Min(200, head.Length)], @"\nversion=(\d{8})\n");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        catch { return 0; }
    }

    /// <summary>Lit un fichier noms.gz ; remplace les listes seulement s'il est complet.</summary>
    private static bool Load(byte[] gz)
    {
        var text = Decompress(gz);
        if (!text.StartsWith("CentrePC-noms 1\n", StringComparison.Ordinal)) return false;
        HashSet<string> first = new(StringComparer.Ordinal), last = new(StringComparer.Ordinal), known = new(StringComparer.Ordinal);
        HashSet<string> current = null;
        int version = 0;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("version=")) { int.TryParse(l[8..], out version); continue; }
            current = l switch { "[prenoms]" => first, "[noms]" => last, "[editeurs]" => known, _ => current };
            if (l[0] == '[' || current == null) continue;
            current.Add(l);
        }
        if (first.Count < 1000 || last.Count < 1000) return false;
        // Organismes reconnus dans les documents : éditeurs connus eux aussi
        foreach (var n in DocNaming.IssuerNames) known.Add(Regex.Replace(Fold(n), "[^a-z0-9]+", " ").Trim());
        (_first, _last, _known, _version) = (first, last, known, version);
        return true;
    }

    /// <summary>Tests : listes réduites.</summary>
    internal static void UseForTests(IEnumerable<string> first, IEnumerable<string> last, IEnumerable<string> known = null)
    {
        lock (Gate)
        {
            _first = first.Select(Fold).ToHashSet(StringComparer.Ordinal);
            _last = last.Select(Fold).ToHashSet(StringComparer.Ordinal);
            _known = (known ?? Enumerable.Empty<string>()).Concat(DocNaming.IssuerNames)
                .Select(k => Regex.Replace(Fold(k), "[^a-z0-9]+", " ").Trim()).ToHashSet(StringComparer.Ordinal);
            _version = 1;
            _loaded = true;
        }
    }

    /// <summary>Tests : recharge les listes embarquées (fichier réel).</summary>
    internal static bool ReloadEmbedded()
    {
        lock (Gate) { _first = new(); _last = new(); _known = new(); _version = 0; _loaded = false; }
        return Available;
    }

    // ------------------------------------------------------------------ mise à jour mensuelle
    /// <summary>
    /// Une fois par mois au plus (si le catalogue est activé) : reprend la liste publiée si elle est signée et plus récente.
    /// Simple lecture d'un fichier public : aucune donnée n'est envoyée.
    /// </summary>
    public static async Task<bool> RefreshAsync(DateTime now)
    {
        if (!RemoteCatalog.Active || !Settings.Current.RemoteCatalog) return false;
        if (Settings.Current.NamesChecked is DateTime last && (now - last).TotalDays < 30) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxBytes };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"CentrePC/{AppUpdater.Current}");
            var signature = (await http.GetStringAsync(Url + ".sig")).Trim();
            var content = await http.GetByteArrayAsync(Url);
            Settings.Current.NamesChecked = now;
            Settings.Save();
            if (!RemoteCatalog.Verify(content, signature, RemoteCatalog.PublicKeys)) { Log.Info("Listes de noms : signature refusée"); return false; }
            int v = ReadVersion(content);
            EnsureLoaded();
            if (v <= _version) return false;
            lock (Gate) { if (!Load(content)) return false; }
            File.WriteAllBytes(CachePath, content);
            File.WriteAllText(CachePath + ".sig", signature);
            Log.Info($"Listes de noms : version {v} reçue");
            return true;
        }
        catch (Exception ex) { Log.Info("Listes de noms : téléchargement impossible : " + ex.GetType().Name + " " + ex.Message); return false; }
    }
}
