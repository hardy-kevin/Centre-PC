using Windows.Services.Store;

namespace CentrePC.Core;

/// <summary>
/// Invitation à noter ou recommander Centre PC : montrée une seule fois, dans le bilan d'une mise à jour réussie.
/// Aucune contrepartie, aucun rappel. Note : boîte de dialogue officielle du Microsoft Store (édition Store seulement).
/// Partage : texte copié avec un lien suivi (« cid ») pour savoir, dans l'Espace partenaires, d'où viennent les installations.
/// Avis à l'auteur : page des suggestions du projet sur GitHub (texte libre : jamais envoyé par le service des retours).
/// </summary>
public static class Recommend
{
    public const string StoreId = "9PP71HDT2X5S";
    public const string FeedbackUrl = "https://github.com/hardy-kevin/Centre-PC/issues/new";

    /// <summary>Lien vers la fiche du Store ; <paramref name="source"/> identifie le canal (lettres, chiffres, tiret).</summary>
    public static string StoreLink(string source)
    {
        var cid = new string((source ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40).ToArray());
        return cid.Length == 0 ? $"https://apps.microsoft.com/detail/{StoreId}"
                               : $"https://apps.microsoft.com/detail/{StoreId}?cid={cid}";
    }

    /// <summary>Message prêt à coller (e-mail, messagerie, réseau social).</summary>
    public static string ShareText() =>
        "Centre PC : une application gratuite et en français pour entretenir son PC Windows "
        + "(applications, Windows et pilotes à jour en un clic, bureau rangé, bilan du PC). "
        + "À télécharger sur le Microsoft Store : " + StoreLink("partage");

    /// <summary>Invitation à montrer : mise à jour sans erreur ni action restante, et jamais montrée avant.</summary>
    public static bool ShouldOffer(bool updateClean, bool alreadyShown) => updateClean && !alreadyShown;

    /// <summary>
    /// Ouvre la fenêtre de notation du Microsoft Store par-dessus Centre PC. Renvoie false hors édition Store ou en cas d'échec
    /// (l'appelant propose alors la fiche du Store).
    /// </summary>
    public static async Task<bool> RequestStoreRatingAsync(IntPtr hwnd)
    {
        if (!Native.IsPackaged || hwnd == IntPtr.Zero) return false;
        try
        {
            var ctx = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(ctx, hwnd);
            var r = await ctx.RequestRateAndReviewAppAsync();
            return r.Status is StoreRateAndReviewStatus.Succeeded or StoreRateAndReviewStatus.CanceledByUser;
        }
        catch (Exception ex)
        {
            Log.Error("Notation dans le Microsoft Store", ex);
            return false;
        }
    }
}
