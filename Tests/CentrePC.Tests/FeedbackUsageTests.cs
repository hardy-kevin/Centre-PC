using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Retours sur les modifications : rien de personnel ne doit pouvoir sortir
// =====================================================================================
public class FeedbackUsageTests
{
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [Fact]
    public void Seuls_les_logiciels_installes_sous_leur_vrai_nom_peuvent_etre_cites()
    {
        var lghub = Path.Combine(ProgramFiles, @"LGHUB\lghub.exe");
        Assert.True(Feedback.IsReportableApp("Logitech G HUB", "Logitech", lghub, false));
        Assert.True(Feedback.IsReportableApp("Spotify", "Spotify AB", null, true));                        // Microsoft Store
        Assert.False(Feedback.IsReportableApp("Le jeu de Léa", "Logitech", lghub, false));                 // raccourci renommé
        Assert.False(Feedback.IsReportableApp("Compta Durand", "", @"C:\Users\Camille\Outils\compta.exe", false));   // dossier personnel
        Assert.False(Feedback.IsReportableApp("Intranet", "", "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome_proxy.exe\" --profile-directory=Default --app-id=abc", false));
        Assert.False(Feedback.IsReportableApp("Mon site", "", "MSEdge._crx_abcdef", true));               // appli web d'Edge
    }

    [Theory]
    [InlineData("Jeux", "Jeux")]
    [InlineData(@"Administratif\Factures", @"Administratif\Factures")]
    [InlineData(@"Jeux\Dofus", @"Jeux\Dofus")]                    // nom d'un logiciel installé
    [InlineData("Dossier médical de Léa", "autre")]
    [InlineData(@"Clients\Durand SARL\Devis", @"autre\autre\Devis")]
    public void Noms_de_dossiers_courants_seulement(string relative, string expected)
    {
        Sandbox.Reset();
        Assert.Equal(expected, Feedback.SafeFolderPath(relative, new[] { "Dofus" }));
    }

    [Fact]
    public void Categorie_inventee_par_l_utilisateur_jamais_envoyee()
    {
        Sandbox.Reset();
        Assert.Equal("Jeux", Feedback.SafeCategory("Jeux"));
        Assert.Equal("Outils pour les jeux", Feedback.SafeCategory("Outils pour les jeux"));   // catalogue
        Assert.Equal(Feedback.Custom, Feedback.SafeCategory("Travail chez Dupont"));
    }

    [Fact]
    public void Renommage_rien_du_nom_seulement_le_type_et_l_usage()
    {
        Sandbox.Reset();
        Feedback.SetConsent(true);
        var sug = DocNaming.Suggest("EDF Facture n° 12 du 12/03/2024");
        Feedback.RecordRename(Path.Combine(Sandbox.Desktop, "Facture EDF Léa Martin 2024-03-12.pdf"), false, sug, "modifie", true);
        var e = Feedback.Queue().Single(x => x.Type == "renommage");
        var all = string.Join(" ", e.Donnees.Values);
        Assert.DoesNotContain("Léa", all);
        Assert.DoesNotContain("Martin", all);
        Assert.Equal("Facture", e.Donnees["type_final"]);
        Assert.Equal("modifie", e.Donnees["propose"]);
        Assert.Equal("garde", e.Donnees["organisme"]);
        Assert.Equal(".pdf", e.Donnees["extension"]);
        Feedback.SetConsent(false);
    }

    [Fact]
    public void Mot_cle_envoye_seulement_s_il_designe_un_logiciel_installe()
    {
        Sandbox.Reset();
        Feedback.SetConsent(true);
        var apps = new[] { ("Dofus", "Ankama", Path.Combine(ProgramFiles, @"Ankama\Dofus\Dofus.exe"), false) };
        Feedback.RecordKeywords("Jeux", new[] { "dofus", "Léa", "client Durand" }, apps);
        var words = Feedback.Queue().Where(x => x.Type == "mot-cle").Select(x => x.Donnees["mot"]).ToList();
        Assert.Equal(new[] { "dofus" }, words);
        Feedback.SetConsent(false);
    }

    [Fact]
    public void Dossier_ajoute_noms_courants_et_types_de_fichiers()
    {
        Sandbox.Reset();
        Feedback.SetConsent(true);
        var dir = Directory.CreateDirectory(Path.Combine(Sandbox.Documents, @"Sorties vélo de Léa")).FullName;
        File.WriteAllText(Path.Combine(dir, "a.gpx"), "x");
        File.WriteAllText(Path.Combine(dir, "b.gpx"), "x");
        Feedback.RecordFolder("ajout", dir);
        var e = Feedback.Queue().Single(x => x.Type == "dossier");
        Assert.Equal("documents", e.Donnees["racine"]);
        Assert.Equal("autre", e.Donnees["chemin"]);
        Assert.Equal(".gpx 2", e.Donnees["types"]);
        Assert.DoesNotContain("Léa", string.Join(" ", e.Donnees.Values));
        Feedback.SetConsent(false);
    }

    [Fact]
    public void Nom_de_fichier_cite_dans_un_message_d_erreur_retire()
    {
        var t = Feedback.Clean("Impossible d'ouvrir 'Facture Léa Martin.pdf' : accès refusé", @"C:\Users\Camille", "Camille", "PC-SALON");
        Assert.DoesNotContain("Léa", t);
        Assert.Contains("%FICHIER%", t);
    }
}
