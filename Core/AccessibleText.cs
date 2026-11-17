using System.Text;

namespace CentrePC.Core;

/// <summary>
/// Texte lu par les lecteurs d'écran (Narrateur, NVDA) pour un élément construit avec des icônes : les caractères d'icône
/// (police Segoe Fluent / MDL2, zone à usage privé d'Unicode) n'ont aucun sens à l'oral et sont retirés.
/// </summary>
public static class AccessibleText
{
    public static bool IsIcon(char c) => c >= '' && c <= '';

    /// <summary>Texte sans caractères d'icône, espaces regroupés ; vide s'il ne reste rien.</summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Séparateurs visuels lus naturellement : « Documents › Administratif · 1 élément » → « Documents, Administratif, 1 élément »
        text = text.Replace(" › ", ", ").Replace("›", ",").Replace(" · ", ", ").Replace("·", ",").Replace(" → ", " vers ").Replace("→", " vers ");
        text = Units(text).Replace("✓", ", réussi").Replace("▶", "").Replace("…", "");
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var c in text)
        {
            if (IsIcon(c)) continue;
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (c is ',' or '.' or ')') space = false;   // pas d'espace avant une virgule (« salaires , 1 » → « salaires, 1 »)
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Unités abrégées, lues en entier : « 30 s » → « 30 secondes », « 69 Go/s » → « 69 gigaoctets par seconde »
    private static readonly (string abbr, string one, string many)[] UnitWords =
    {
        ("tr/min", "tour par minute", "tours par minute"), ("Mbit/s", "mégabit par seconde", "mégabits par seconde"),
        ("Go/s", "gigaoctet par seconde", "gigaoctets par seconde"), ("Mo/s", "mégaoctet par seconde", "mégaoctets par seconde"),
        ("MT/s", "mégatransfert par seconde", "mégatransferts par seconde"), ("TFLOPS", "téraflops", "téraflops"), ("GFLOPS", "gigaflops", "gigaflops"),
        ("GHz", "gigahertz", "gigahertz"), ("MHz", "mégahertz", "mégahertz"), ("ms", "milliseconde", "millisecondes"),
        ("min", "minute", "minutes"), ("°C", "degré", "degrés"), ("To", "téraoctet", "téraoctets"), ("Go", "gigaoctet", "gigaoctets"),
        ("Mo", "mégaoctet", "mégaoctets"), ("Ko", "kilooctet", "kilooctets"), ("s", "seconde", "secondes"), ("h", "heure", "heures"), ("W", "watt", "watts"),
    };

    private static readonly System.Text.RegularExpressions.Regex UnitPattern = new(
        @"(?<n>\d+(?:[.,]\d+)?)\s?(?<u>" + string.Join("|", UnitWords.Select(u => System.Text.RegularExpressions.Regex.Escape(u.abbr))) + @")(?![\p{L}/])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string Units(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = UnitPattern.Replace(text, m =>
        {
            var u = UnitWords.First(x => x.abbr == m.Groups["u"].Value);
            double.TryParse(m.Groups["n"].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n);
            return $"{m.Groups["n"].Value} {(n >= 2 ? u.many : u.one)}";
        });
        // « 10 boucles × 30 secondes » → « 10 boucles de 30 secondes »
        return text.Replace(" × ", " de ");
    }

    private static readonly string[] GenericVerbs =
    {
        "ouvrir", "afficher", "annuler", "garder", "supprimer", "modifier", "renommer", "ignorer", "mettre", "désinstaller", "réactiver",
        "désactiver", "activer", "tester", "corriger", "lancer", "voir", "copier", "choisir", "autre", "ne", "retirer", "éjecter", "vérifier",
        "options", "installer", "ranger", "remettre", "masquer", "réafficher", "identifier", "exclure", "inclure", "détail", "plus"
    };

    /// <summary>
    /// Libellé d'action qui ne dit pas sur quoi il porte (« Ouvrir », « Afficher l'emplacement », « Désactiver ») : il faut
    /// lui ajouter le nom de l'élément de sa ligne pour qu'il ait un sens à l'oral.
    /// </summary>
    public static bool IsGenericAction(string label)
    {
        var words = Clean(label).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 4) return false;
        var first = words[0].ToLowerInvariant().TrimEnd('…', '.', ',');
        return GenericVerbs.Contains(first);
    }

    /// <summary>Nom à annoncer : les textes visibles de l'élément, dans l'ordre, sinon son infobulle.</summary>
    public static string Name(IEnumerable<string> texts, string tooltip)
    {
        var parts = texts.Select(Clean).Where(t => t.Length > 0).Distinct().ToList();
        if (parts.Any(p => p.Length > 1)) parts.RemoveAll(p => p.Length == 1 && char.IsLetter(p[0]));   // initiale affichée à la place d'une icône
        // Les nombres seuls (compteurs, badges) après le texte : « Office, 3 » plutôt que « 3, Office »
        parts = parts.Where(p => !p.All(char.IsDigit)).Concat(parts.Where(p => p.All(char.IsDigit))).ToList();
        var name = string.Join(", ", parts);
        if (name.Length == 0) name = Clean(tooltip);
        return name.Length > 200 ? name[..200] : name;
    }
}
