using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

public class RecommendTests
{
    [Fact]
    public void Lien_suivi_du_Store()
    {
        Assert.Equal("https://apps.microsoft.com/detail/9PP71HDT2X5S?cid=partage", Recommend.StoreLink("partage"));
        Assert.Equal("https://apps.microsoft.com/detail/9PP71HDT2X5S", Recommend.StoreLink(null));
        // Caractères parasites retirés : rien ne peut s'ajouter à l'adresse
        Assert.Equal("https://apps.microsoft.com/detail/9PP71HDT2X5S?cid=forumxy", Recommend.StoreLink("forum&x=<y>"));
    }

    [Fact]
    public void Message_a_partager_contient_le_lien()
    {
        var text = Recommend.ShareText();
        Assert.Contains(Recommend.StoreLink("partage"), text);
        Assert.Contains("gratuite", text);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Invitation_une_seule_fois_apres_une_mise_a_jour_reussie(bool clean, bool shown, bool expected)
        => Assert.Equal(expected, Recommend.ShouldOffer(clean, shown));
}
