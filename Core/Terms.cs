namespace CentrePC.Core;

/// <summary>
/// Conditions d'utilisation : version en vigueur (la même que l'en-tête de CONDITIONS.md) et acceptation enregistrée.
/// Elles sont acceptées explicitement au premier lancement, puis de nouveau quand la version change. L'acceptation
/// reste sur le PC (config.json) : elle n'est envoyée nulle part.
/// </summary>
public static class Terms
{
    /// <summary>Version des conditions (date de CONDITIONS.md). La changer redemande l'accord à tous les utilisateurs.</summary>
    public const string Version = "2026-11-22";

    /// <summary>Résumé des changements depuis la version précédente, montré aux utilisateurs qui avaient déjà accepté.</summary>
    public static readonly string[] Changes =
    {
        "Les conditions sont maintenant affichées en entier dans Centre PC, confidentialité et usage professionnel compris, et acceptées une fois, explicitement, au lieu de l'être par la simple utilisation.",
        "Nouvelles fonctions décrites : renommage des documents avec un nom proposé d'après leur contenu (lu sur votre PC), "
            + "lanceur d'applications au raccourci clavier, démarrage avec Windows en arrière-plan.",
        "Retours anonymes : protection renforcée (un prénom suivi d'un nom n'est jamais envoyé), détail dans la page Confidentialité.",
    };

    /// <summary>Vrai si l'utilisateur doit (encore) accepter les conditions : jamais acceptées, ou version différente.</summary>
    public static bool NeedsAcceptance(Config cfg) => cfg == null || cfg.TermsAccepted != Version;

    /// <summary>Vrai si une version précédente avait déjà été acceptée, ou utilisée avant l'acceptation explicite (mise à jour).</summary>
    public static bool IsUpdate(Config cfg) => cfg != null && (cfg.TermsAccepted != null || cfg.OnboardingDone);

    public static void Accept(Config cfg, DateTime now)
    {
        cfg.TermsAccepted = Version;
        cfg.TermsAcceptedOn = now;
    }
}
