using System.Globalization;

namespace CentrePC.Core;

/// <summary>Actions de Centre PC proposées dans la recherche du lanceur, à côté des applications.</summary>
public static class LauncherActions
{
    private static readonly CompareInfo Cmp = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;
    private const CompareOptions CmpOpts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Identifiant, libellé, icône, mots qui la font trouver (en plus du libellé).</summary>
    public static readonly (string id, string label, string glyph, string words)[] All =
    {
        ("checkup", "Bilan du PC", "\uE9D9", "checkup santé diagnostic rapport performance température vérifier test"),
        ("updates", "Mises à jour", "\uE895", "update maj mettre à jour windows pilotes"),
        ("capture", "Capture d'écran", "\uE722", "screenshot copie écran capturer image"),
        ("startup", "Démarrage de Windows", "\uE7E8", "startup programmes au démarrage lancement allumage lent"),
        ("space", "Espace disque", "\uEDA2", "place stockage plein gros fichiers dossiers nettoyer"),
        ("documents", "Ranger mes fichiers", "\uE8B7", "documents rangement trier téléchargements"),
        ("desktop", "Ranger le bureau", "\uE7F4", "icônes bureau"),
        ("devices", "Périphériques", "\uE772", "clavier souris écran son micro manette test"),
        ("settings", "Réglages de Centre PC", "\uE713", "paramètres options configuration raccourci"),
    };

    /// <summary>Actions correspondant à la recherche (tous les mots, sans tenir compte des accents ni de la casse).</summary>
    public static List<(string id, string label, string glyph)> Find(string query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.All(w => w.Length < 2)) return new();
        return All.Where(a => words.All(w => Cmp.IndexOf(a.label + " " + a.words, w, CmpOpts) >= 0))
                      .OrderBy(a => Cmp.IsPrefix(a.label, words[0], CmpOpts) ? 0 : Cmp.IndexOf(a.label, words[0], CmpOpts) >= 0 ? 1 : 2)
                      .Take(3).Select(a => (a.id, a.label, a.glyph)).ToList();
    }
}
