using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Noms de personnes (prénoms et noms de l'Insee) : jamais dans les retours d'expérience
// =====================================================================================
public class PersonNamesTests
{
    [Theory]
    [InlineData("Jean Dupont", true)]
    [InlineData("DUPONT Jean", true)]                 // nom puis prénom
    [InlineData("Jean de La Fontaine", true)]         // particules ignorées
    [InlineData("Léa Martin", true)]
    [InlineData("Lea MARTIN", true)]                  // accents et casse
    [InlineData("Logitech G HUB", false)]
    [InlineData("Martin", false)]                     // un seul mot : pas une paire
    [InlineData("Maximum Durand", false)]             // « Max » n'est pas « Maximum »
    public void Paire_prenom_nom_reconnue(string text, bool expected)
    {
        Sandbox.Reset();
        Assert.Equal(expected, PersonNames.HasFullName(text));
    }

    [Fact]
    public void Message_d_erreur_garde_tout_sauf_le_nom()
    {
        Sandbox.Reset();
        Assert.Equal("Compte de %NOM% introuvable (code 5)", PersonNames.MaskFullNames("Compte de Pierre Durand introuvable (code 5)"));
        Assert.Equal("Rien à cacher", PersonNames.MaskFullNames("Rien à cacher"));
        Assert.DoesNotContain("Durand", Feedback.Clean("Échec pour Marie Durand", "", "", ""));
    }

    [Theory]
    [InlineData("Dupont", true)]                      // un seul nom de famille : une personne
    [InlineData("Kevin", true)]                       // un seul prénom aussi
    [InlineData("Jean Dupont", true)]
    [InlineData("Dupont SARL", false)]                // forme d'entreprise
    [InlineData("Martin Software", false)]
    [InlineData("Logitech", false)]
    [InlineData("SNCF", false)]                       // organisme connu
    [InlineData("", false)]
    public void Editeur_qui_designe_une_personne(string publisher, bool person)
    {
        Sandbox.Reset();
        Assert.Equal(person, PersonNames.IsPersonPublisher(publisher));
        Assert.Equal(person ? "" : publisher, Feedback.SafePublisher(publisher));
    }

    [Fact]
    public void Application_au_nom_d_une_personne_jamais_envoyee()
    {
        Sandbox.Reset();
        Assert.False(Feedback.IsReportableApp("Max Payne", "Rockstar Games", null, true));
        Assert.True(Feedback.IsReportableApp("Spotify", "Spotify AB", null, true));
    }

    [Fact]
    public void Sans_listes_rien_n_est_cite()
    {
        Sandbox.Reset();
        PersonNames.UseForTests(Array.Empty<string>(), Array.Empty<string>());
        Assert.False(PersonNames.Available);
        Assert.False(Feedback.IsReportableApp("Spotify", "Spotify AB", null, true));
        Assert.Equal("", Feedback.SafePublisher("Logitech"));
    }

    [Fact]
    public void Listes_de_l_Insee_embarquees()
    {
        // Échoue tant que Outils\listes-noms.ps1 n'a pas été lancé (Catalogue\noms.gz absent)
        try
        {
            Assert.True(PersonNames.ReloadEmbedded(), "Catalogue\\noms.gz absent ou illisible : lancez Outils\\listes-noms.ps1");
            Assert.True(PersonNames.HasFullName("Jean Dupont"));
            Assert.False(PersonNames.HasFullName("Microsoft Edge"));
            Assert.False(PersonNames.HasFullName("Visual Studio Code"));
        }
        finally { Sandbox.Reset(); }
    }
}
