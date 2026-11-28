using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

/// <summary>Corrections tirées des retours d'expérience (novembre 2026).</summary>
public class RetoursCorrectionsTests
{
    [Fact]
    public void Identifiant_de_paquet_reconnu_seulement_s_il_remplace_le_nom()
    {
        const string aumid = "32739DavidXanatos.UpdateManagerforWindows_abcd1234efgh!App";
        Assert.True(AppScanner.LooksLikePackageId("32739DavidXanatos.UpdateManagerforWindows", aumid));
        // Un vrai nom contenant un point n'est jamais concerné
        Assert.False(AppScanner.LooksLikePackageId("Paint.NET", "dotPDN.PaintDotNet_h55e3w7q8jbva!dotPDN.PaintDotNet"));
        Assert.False(AppScanner.LooksLikePackageId("Update Manager for Windows", aumid));
        Assert.False(AppScanner.LooksLikePackageId("Calculatrice", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
    }

    [Theory]
    [InlineData("32739DavidXanatos.UpdateManagerforWindows", "Update Manager for Windows")]
    [InlineData("Microsoft.MixedReality.Portal", "Portal")]
    [InlineData("Contoso.PDFReader", "PDF Reader")]
    public void Identifiant_rendu_lisible(string id, string expected) => Assert.Equal(expected, AppScanner.ReadablePackageName(id));

    [Fact]
    public async Task Liste_des_cartes_reseau_refusee_par_Windows()
    {
        int calls = 0;
        // Windows refuse deux fois (carte qui change d'état), puis répond : la liste est obtenue
        var list = await DeviceHealth.ActiveAdaptersAsync(() =>
        {
            if (++calls < 3) throw new NetworkInformationException(1236);
            return Array.Empty<NetworkInterface>();
        });
        Assert.NotNull(list);
        Assert.Empty(list);
        Assert.Equal(3, calls);
        // Refus persistant : pas de plantage, la liste est simplement indisponible
        Assert.Null(await DeviceHealth.ActiveAdaptersAsync(() => throw new NetworkInformationException(1236)));
    }
}
