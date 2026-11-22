using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Mémoire virtuelle : alerte seulement si le fichier d'échange ne peut plus grandir
// =====================================================================================
public class MemoryTests
{
    private const double Go = 1024.0 * 1024 * 1024;

    [Fact]
    public void Jeu_avec_fichier_d_echange_automatique_pas_d_alerte()
    {
        // Cas réel : 31,8 Go de RAM, fichier d'échange automatique de 2 Go, 32,3 Go engagés sur 33,8 Go (94 %), 368 Go libres
        var files = new[] { new DeviceHealth.PageFileInfo(2 * Go, 0, 931 * Go, 368 * Go) };
        var r = DeviceHealth.CommitCapacity(32.3 * Go, 33.8 * Go, 31.8 * Go, files, automatic: true);
        Assert.Null(r.Problem);
        Assert.True(r.Potential > 100 * Go);
    }

    [Fact]
    public void Fichier_d_echange_desactive_alerte()
    {
        var r = DeviceHealth.CommitCapacity(30.5 * Go, 31.8 * Go, 31.8 * Go, new DeviceHealth.PageFileInfo[0], automatic: false);
        Assert.Contains("désactivé", r.Problem);
    }

    [Fact]
    public void Taille_fixe_atteinte_alerte()
    {
        var files = new[] { new DeviceHealth.PageFileInfo(2 * Go, 2 * Go, 931 * Go, 368 * Go) };
        var r = DeviceHealth.CommitCapacity(32.3 * Go, 33.8 * Go, 31.8 * Go, files, automatic: false);
        Assert.Contains("taille limitée", r.Problem);
    }

    [Fact]
    public void Disque_plein_alerte()
    {
        var files = new[] { new DeviceHealth.PageFileInfo(2 * Go, 0, 256 * Go, 2.5 * Go) };
        var r = DeviceHealth.CommitCapacity(32.3 * Go, 33.8 * Go, 31.8 * Go, files, automatic: true);
        Assert.Contains("presque plein", r.Problem);
    }
}
