using System.IO.Compression;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Renommage des documents depuis le plan de rangement, et nom proposé d'après le contenu
// =====================================================================================
public class DocNamingTests
{
    [Theory]
    [InlineData("EDF\nFACTURE n° 1234\nDate : 12/03/2024\nMontant TTC 54,20 €", "Facture EDF 2024-03-12")]
    [InlineData("Direction générale des Finances publiques\nAVIS D'IMPÔT 2023 sur les revenus de 2022\nÉtabli le 31/07/2023", "Avis d'imposition Impôts 2023-07-31")]
    [InlineData("BULLETIN DE PAIE\nPériode : septembre 2023\nSalaire de base 2 000,00\nPayé le 29/09/2023", "Bulletin de paie 2023-09")]
    [InlineData("SNCF Connect\nVotre e-billet\nParis → Lyon le 14 août 2024", "Billet SNCF 2024-08-14")]
    [InlineData("Relevé d'identité bancaire\nCrédit Agricole\nIBAN FR76 ...", "RIB Crédit Agricole")]
    public void Nom_propose_d_apres_le_texte(string text, string expected)
    {
        Assert.Equal(expected, DocNaming.Suggest(text)?.Name);
    }

    [Fact]
    public void Formulaire_a_cases_certificat_de_cession_avec_immatriculation_et_date_de_cession()
    {
        // Texte tel que le lit la reconnaissance de Windows sur la photo d'un cerfa 15776 (un caractère par case)
        var ocr = "MINISTÈRE DE L'INTÉRIEUR CERTIFICAT DE CESSION D'UN VÉHICULE D'OCCASION (à remplir par l'ancien propriétaire et le nouveau propriétaire) "
                + "LE VÉHICULE H,E,-,1,6,1,-,H,Y (A) Numéro d'immatriculation du véhicule L,C,E,P,E,Y,L,P,2,S,8,0,0,5,1,0,1 (E) Numéro d'identification "
                + "2,7,0,6,2,0,2,5 (B) Date de 1re immatriculation CF MOTO Présence du certificat d'immatriculation "
                + "Date et heure de la cession : le 20/08/2026 à 14 h ants.gouv.fr";
        var s = DocNaming.Suggest(ocr);
        Assert.Equal("Certificat de cession", s.Type);
        Assert.Equal("Certificat de cession HE-161-HY 2026-08-20", s.Name);   // pas « ANTS » : formulaire officiel
    }

    [Fact]
    public void Facture_qui_parle_plus_bas_de_bon_de_commande_reste_une_facture()
    {
        var s = DocNaming.Suggest("Amazon\nFacture\nN° de facture FR123\nBon de commande n° 405-1234 du 02/01/2024");
        Assert.Equal("Facture", s.Type);
        Assert.Equal("Facture Amazon 2024-01-02", s.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Vacances au bord de la mer, il faisait beau.")]
    [InlineData("Nous avons bien reçu votre message.")]   // « reçu » courant : pas un reçu de paiement
    public void Type_non_reconnu_rien_n_est_devine(string text)
    {
        Assert.Null(DocNaming.Suggest(text));
    }

    [Fact]
    public void Le_nom_propose_permet_ensuite_de_ranger_le_document()
    {
        Sandbox.Reset();
        foreach (var name in new[] { "Facture EDF 2024-03-12", "Avis d'imposition Impôts 2023-07-31", "Bulletin de paie 2023-09", "Billet SNCF 2024-08-14", "RIB Crédit Agricole" })
        {
            var f = Sandbox.File_(Sandbox.Desktop, name + ".pdf");
            var p = DocPlanner.Propose(f, false, "Bureau", DocPlanner.UserFolders());
            Assert.False(p.IsManual, name);
        }
    }

    [Fact]
    public void Texte_d_un_document_Word_lu_pour_proposer_un_nom()
    {
        Sandbox.Reset();
        var docx = Path.Combine(Sandbox.Desktop, "Document1.docx");
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open());
            w.Write("<w:document><w:body><w:p><w:r><w:t>Quittance de loyer</w:t></w:r></w:p><w:p><w:r><w:t>Mois de mars 2024</w:t></w:r></w:p></w:body></w:document>");
        }
        var s = DocNaming.SuggestAsync(docx).GetAwaiter().GetResult();
        Assert.Equal("Quittance de loyer 2024-03", s?.Name);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Facture: EDF", false)]
    [InlineData("con", false)]
    [InlineData("rapport.", false)]
    [InlineData("Facture EDF 2024-03", true)]
    public void Nom_verifie_avant_renommage(string name, bool ok)
    {
        Sandbox.Reset();
        var f = Sandbox.File_(Sandbox.Desktop, "scan001.pdf");
        Assert.Equal(ok, DocNaming.Validate(name, f, false) == null);
    }

    [Fact]
    public void Renommage_garde_l_extension_refuse_un_nom_pris_et_s_annule()
    {
        Sandbox.Reset();
        var f = Sandbox.File_(Sandbox.Desktop, "scan001.pdf", "contenu");
        Sandbox.File_(Sandbox.Desktop, "Déjà là.pdf");
        Assert.Equal(DocNaming.NameTaken, DocNaming.Validate("Déjà là", f, false));

        var renamed = DocNaming.Rename(f, "Facture EDF 2024-03", false);
        Assert.Equal(Path.Combine(Sandbox.Desktop, "Facture EDF 2024-03.pdf"), renamed);
        Assert.Equal("contenu", File.ReadAllText(renamed));
        // Annulable depuis l'historique
        var entry = History.Recent().First(e => e.Kind == "renommage" && e.To == renamed);
        Assert.Null(History.Undo(entry));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Renomme_puis_range_remettre_a_sa_place_rend_aussi_l_ancien_nom()
    {
        Sandbox.Reset();
        var f = Sandbox.File_(Sandbox.Desktop, "IMG_1077.pdf", "x");
        var renamed = DocNaming.Rename(f, "Facture EDF 2024-03-12", false);
        var p = DocPlanner.Propose(renamed, false, "Bureau", DocPlanner.UserFolders());
        p.OriginalSource = f;
        var plan = new List<DocFolderPlan> { new() { Path = p.Destination, Label = p.DestinationLabel, Items = new() { p } } };
        DocPlanner.Apply(plan);
        var review = Settings.Current.DocReview.Single();
        Assert.Equal("Facture EDF 2024-03-12.pdf", review.Name);
        Assert.Null(DocPlanner.Reject(review));
        Assert.True(File.Exists(f));   // retour exact : même emplacement, même nom qu'avant
        Assert.False(File.Exists(renamed));
    }
}
