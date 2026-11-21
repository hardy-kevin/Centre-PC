using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Contenu du catalogue distant : des données uniquement, jamais de code.</summary>
public sealed class CatalogData
{
    /// <summary>Numéro croissant : un catalogue plus ancien que celui déjà reçu est refusé.</summary>
    public int Version { get; set; }
    public string Date { get; set; }
    /// <summary>Classement : nom de l'application → catégorie.</summary>
    public Dictionary<string, string> Classement { get; set; } = new();
    /// <summary>Fonctions coupées à distance en attendant un correctif (voir <see cref="RemoteCatalog.Switches"/>).</summary>
    public List<string> Desactive { get; set; } = new();
    /// <summary>Organismes à reconnaître dans les documents : nom affiché → mots ou expressions à chercher (texte simple).</summary>
    public Dictionary<string, List<string>> Organismes { get; set; } = new();
    /// <summary>Mots-clés ajoutés aux catégories : catégorie → mots-clés (les choix de l'utilisateur restent prioritaires).</summary>
    public Dictionary<string, List<string>> MotsCles { get; set; } = new();
    /// <summary>Éditeurs et marques connus, à ne jamais prendre pour une personne (retours d'expérience).</summary>
    public List<string> Editeurs { get; set; } = new();
}

/// <summary>
/// Catalogue distant signé : corrige le classement des applications et peut couper une fonction qui pose problème, sans
/// publier une nouvelle version de Centre PC.
/// <list type="bullet">
/// <item>Fichier de données public (dépôt GitHub du projet) accompagné d'une signature ECDSA P-256 ; la clé publique est dans
/// l'application, la clé privée ne quitte pas le PC de l'auteur. Un fichier non signé, modifié ou trop gros est ignoré.</item>
/// <item>Un catalogue plus ancien que le dernier accepté est refusé (pas de retour en arrière forcé).</item>
/// <item>La copie gardée sur le PC est revérifiée à chaque lancement : la modifier à la main la rend inutilisable, sans autre effet.</item>
/// <item>Les choix de l'utilisateur passent toujours avant le catalogue.</item>
/// </list>
/// Sans clé publique dans <see cref="PublicKeys"/>, le catalogue serait inactif et rien ne serait téléchargé.
/// </summary>
public static class RemoteCatalog
{
    public const string Url = "https://raw.githubusercontent.com/" + AppUpdater.Owner + "/" + AppUpdater.Repo + "/main/Catalogue/catalogue.json";
    private const int MaxBytes = 512 * 1024;

    /// <summary>
    /// Clés publiques acceptées (points X et Y de la courbe P-256, 64 octets en base64). Plusieurs clés permettent d'en
    /// changer sans couper les versions déjà installées. Produites par Catalogue\cle.ps1.
    /// </summary>
    public static readonly string[] PublicKeys =
    {
        "+cy5Y5cw1XFUqxMSHVjmbzKHBR9ZfsAn1ym+MzgkhhypvZWFYqhs/o25JOB9e/e1sM3vGGlgtljuDZuEPKdxZw==",   // clé principale (16/11/2026)
        "Xa0Gn5gyXOy6LmQyq/HvKgS8hAoRNZisngSyai15IDsyT6z/fiPUvmIpBLVgTxZsrP+abtbdH2EimfImfcQlhA==",   // clé de secours, gardée hors ligne (16/11/2026)
    };

    /// <summary>Fonctions qu'un catalogue peut couper (identifiant → ce que cela arrête).</summary>
    public static readonly Dictionary<string, string> Switches = new()
    {
        ["capteurs-hwinfo"] = "installation et lancement de HWiNFO par Centre PC",
        ["capteurs-lhm"] = "installation et lancement de LibreHardwareMonitor par Centre PC",
        ["outils-expert"] = "installation temporaire des outils d'expert",
        ["retours"] = "envoi des retours d'expérience",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static CatalogData _data;
    private static Dictionary<string, string> _byName = new();

    public static bool Active => PublicKeys.Length > 0;
    public static CatalogData Current => _data;
    private static string FilePath => Path.Combine(Settings.DataDir, "catalogue.json");
    private static string SigPath => FilePath + ".sig";

    // ------------------------------------------------------------------ vérification
    /// <summary>Vrai si <paramref name="signature"/> (base64, r puis s) est celle de <paramref name="content"/> pour une des clés.</summary>
    public static bool Verify(byte[] content, string signature, IEnumerable<string> keys)
    {
        try
        {
            var sig = Convert.FromBase64String((signature ?? "").Trim());
            foreach (var key in keys)
            {
                var xy = Convert.FromBase64String(key);
                if (xy.Length != 64 || sig.Length != 64) continue;
                using var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = xy[..32], Y = xy[32..] }
                });
                if (ecdsa.VerifyData(content, sig, HashAlgorithmName.SHA256)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Catalogue lu seulement si sa signature est bonne, sa taille raisonnable et son format correct ; sinon null.</summary>
    public static CatalogData Parse(byte[] content, string signature, IEnumerable<string> keys)
    {
        if (content == null || content.Length == 0 || content.Length > MaxBytes || !Verify(content, signature, keys)) return null;
        try
        {
            var d = JsonSerializer.Deserialize<CatalogData>(content, Json);
            if (d == null || d.Version <= 0) return null;
            d.Classement ??= new(); d.Desactive ??= new(); d.Organismes ??= new(); d.MotsCles ??= new(); d.Editeurs ??= new();
            // Textes simples et courts seulement (jamais d'expression régulière : tout est échappé à l'usage)
            static List<string> Short(IEnumerable<string> l) => (l ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 60).Select(x => x.Trim()).Take(200).ToList();
            d.Organismes = d.Organismes.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Key.Length <= 60).Take(2000)
                            .ToDictionary(kv => kv.Key.Trim(), kv => Short(kv.Value), StringComparer.OrdinalIgnoreCase);
            d.MotsCles = d.MotsCles.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).Take(200)
                            .ToDictionary(kv => kv.Key.Trim(), kv => Short(kv.Value), StringComparer.OrdinalIgnoreCase);
            d.Editeurs = Short(d.Editeurs.Take(5000));
            return d;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ utilisation
    private static void Apply(CatalogData d)
    {
        _data = d;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (d != null)
            foreach (var (name, category) in d.Classement)
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(category)) map[Settings.Normalize(name)] = category.Trim();
        _byName = map;
        // Organismes : chaque expression est cherchée telle quelle (échappée), en mot entier
        _orgPatterns = (d?.Organismes ?? new()).Where(kv => kv.Value.Count > 0)
            .Select(kv => (kv.Key, new Regex(@"(?<![\p{L}\d])(" + string.Join("|", kv.Value.Select(Regex.Escape)) + @")(?![\p{L}\d])",
                                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))))
            .ToList();
        // Mots-clés ajoutés : pris en compte par le classement des applications
        if (Settings.Current != null) Settings.Rebuild();
    }

    private static List<(string Name, Regex Rx)> _orgPatterns = new();

    /// <summary>Organismes du catalogue (nom affiché → expressions).</summary>
    public static IReadOnlyDictionary<string, List<string>> Organisations => _data?.Organismes ?? new Dictionary<string, List<string>>();
    /// <summary>Organismes du catalogue, prêts à chercher dans un texte.</summary>
    public static IEnumerable<(string Name, Regex Rx)> OrganisationPatterns => _orgPatterns;
    /// <summary>Éditeurs connus du catalogue.</summary>
    public static IEnumerable<string> Publishers => _data?.Editeurs ?? Enumerable.Empty<string>();
    /// <summary>Mots-clés que le catalogue ajoute à une catégorie.</summary>
    public static IEnumerable<string> ExtraKeywords(string category) =>
        category != null && _data?.MotsCles != null && _data.MotsCles.TryGetValue(category, out var l) ? l : Enumerable.Empty<string>();

    /// <summary>Catégorie indiquée par le catalogue pour ce nom (déjà normalisé), ou null.</summary>
    public static string Category(string normalizedName) =>
        normalizedName != null && _byName.TryGetValue(normalizedName, out var c) ? c : null;

    /// <summary>Fonction coupée à distance ? (identifiants : voir <see cref="Switches"/>)</summary>
    public static bool IsDisabled(string feature) => _data?.Desactive?.Contains(feature, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Au lancement : reprend la copie gardée sur le PC, si elle est toujours valide.</summary>
    public static void LoadCached() => LoadCached(PublicKeys);

    public static void LoadCached(IEnumerable<string> keys)
    {
        try
        {
            if (!keys.Any() || !File.Exists(FilePath) || !File.Exists(SigPath)) { Apply(null); return; }
            Apply(Parse(File.ReadAllBytes(FilePath), File.ReadAllText(SigPath), keys));
        }
        catch (Exception ex) { Apply(null); Log.Info("Catalogue : copie locale illisible : " + ex.Message); }
    }

    /// <summary>
    /// Accepte un catalogue reçu : signature valide et version au moins égale à celle déjà connue. Il est alors gardé sur le PC
    /// et appliqué. Renvoie vrai si le catalogue a changé.
    /// </summary>
    public static bool Accept(byte[] content, string signature, IEnumerable<string> keys)
    {
        var d = Parse(content, signature, keys);
        if (d == null) { Log.Info("Catalogue : fichier refusé (signature ou format)"); return false; }
        int known = Math.Max(_data?.Version ?? 0, Settings.Current.CatalogVersion);
        if (d.Version < known) { Log.Info($"Catalogue : version {d.Version} refusée (version {known} déjà reçue)"); return false; }
        bool changed = d.Version != (_data?.Version ?? 0);
        File.WriteAllBytes(FilePath, content);
        File.WriteAllText(SigPath, signature.Trim());
        if (d.Version != Settings.Current.CatalogVersion) { Settings.Current.CatalogVersion = d.Version; Settings.Save(); }
        Apply(d);
        if (changed) Log.Info($"Catalogue : version {d.Version} du {d.Date} ({d.Classement.Count} application(s), {d.Organismes.Count} organisme(s), "
                              + $"{d.MotsCles.Sum(m => m.Value.Count)} mot(s)-clé(s), {d.Desactive.Count} fonction(s) coupée(s))");
        return changed;
    }

    /// <summary>
    /// Téléchargement, une fois par jour au plus, si le réglage est actif et qu'une clé est inscrite. Simple lecture d'un
    /// fichier public : aucune donnée n'est envoyée. Renvoie vrai si le catalogue a changé.
    /// </summary>
    public static async Task<bool> RefreshAsync(DateTime now, bool force = false)
    {
        if (!Active || !Settings.Current.RemoteCatalog) return false;
        if (!force && Settings.Current.CatalogChecked is DateTime last && (now - last).TotalHours < 24) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxBytes };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"CentrePC/{AppUpdater.Current}");
            var content = await http.GetByteArrayAsync(Url);
            var signature = await http.GetStringAsync(Url + ".sig");
            Settings.Current.CatalogChecked = now;
            Settings.Save();
            return Accept(content, signature, PublicKeys);
        }
        catch (Exception ex) { Log.Info("Catalogue : téléchargement impossible : " + ex.GetType().Name + " " + ex.Message); return false; }
    }
}
