using System.Text.Json;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Emplacement des données (Documents\Centre PC\<nom du PC>) et déplacement automatique
//  des données des versions précédentes (%LOCALAPPDATA%\CentrePC, Documents\Centre PC)
// =====================================================================================
public class PreviousInstallTests
{
    private static string ExeDir => PreviousInstall.ExeDataDir;
    private static string StoreRoot => PreviousInstall.StoreDataDir;

    /// <summary>Simule l'ancien dossier d'une version précédente : config.json et raccourcis rangés.</summary>
    private static void OldInstall(string dir, Action<Config> fill, params string[] storedNames)
    {
        var store = Path.Combine(dir, "Bureau rangé");
        Directory.CreateDirectory(store);
        var cfg = new Config { Categories = new() };
        foreach (var n in storedNames)
        {
            var p = Sandbox.Url(store, n, "https://example.org/" + Uri.EscapeDataString(n));
            cfg.Stored[p] = Path.Combine(Sandbox.Desktop, n + ".url");
        }
        fill?.Invoke(cfg);
        File.WriteAllText(Path.Combine(dir, "config.json"), JsonSerializer.Serialize(cfg));
    }

    [Fact]
    public void Les_donnees_sont_dans_un_dossier_propre_a_ce_PC()
    {
        Sandbox.Reset();
        Assert.Equal(StoreRoot, Path.GetDirectoryName(Settings.DataDir), ignoreCase: true);
        Assert.StartsWith(Sandbox.Root, Settings.CacheDir, StringComparison.OrdinalIgnoreCase);
        Assert.False(DocumentLibrary.IsUnder(Settings.CacheDir, StoreRoot));   // le cache n'est jamais synchronisé
        // Le dossier de Centre PC n'est jamais rangé ni proposé comme destination
        Assert.True(DocPlanner.IsAppFolder(StoreRoot));
    }

    [Fact]
    public void Version_exe_precedente_raccourcis_et_reglages_deplaces_puis_remis_sur_le_bureau()
    {
        Sandbox.Reset();
        PreviousInstall.InstalledOverride = null;
        OldInstall(ExeDir, c => { c.Favorites.Add("Mon appli"); c.OnboardingDone = true; }, "Mon jeu", "Mon appli");
        Sandbox.File_(Path.Combine(ExeDir, "Journaux"), "centrepc.log", "ancien journal");
        Sandbox.File_(Path.Combine(ExeDir, "Installateurs", "app"), "setup.exe", "x");
        Assert.True(Settings.FirstRun);

        var r = PreviousInstall.Run();
        Assert.Equal(2, r.Shortcuts);
        Assert.True(r.Choices);
        Assert.Contains("Mon appli", Settings.Current.Favorites);    // réglages repris tels quels (premier lancement)
        Assert.True(Settings.Current.OnboardingDone);
        var stored = Path.Combine(Settings.StoreDir, "Mon jeu.url");
        Assert.True(File.Exists(stored));
        Assert.False(File.Exists(Path.Combine(ExeDir, "Bureau rangé", "Mon jeu.url")));
        Assert.Equal(Path.Combine(Sandbox.Desktop, "Mon jeu.url"), Settings.Current.Stored[stored], ignoreCase: true);
        // Installateurs dans le cache, journaux et ancienne configuration dans le dossier du PC
        Assert.True(File.Exists(Path.Combine(Settings.CacheDir, "Installateurs", "app", "setup.exe")));
        Assert.Contains(Directory.GetFiles(Path.Combine(Settings.DataDir, "Journaux")), f => File.ReadAllText(f).Contains("ancien journal"));
        Assert.True(File.Exists(Path.Combine(Settings.DataDir, "Ancienne installation", "Version GitHub", "config.json")));
        Assert.False(File.Exists(Path.Combine(ExeDir, "config.json")));
        // « Remettre sur le bureau » fonctionne comme avant
        var item = AppScanner.ScanDesktop().Single(i => i.FilePath.Equals(stored, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ItemLocation.Stored, item.Location);
        Assert.Equal(1, DesktopManager.Restore(new[] { item }));
        Assert.True(File.Exists(Path.Combine(Sandbox.Desktop, "Mon jeu.url")));
        // Plus rien à déplacer au démarrage suivant
        Assert.Null(PreviousInstall.Run());
    }

    [Fact]
    public void Ancienne_version_Store_deplacee_sans_toucher_aux_bilans_ni_aux_dossiers_des_PC()
    {
        Sandbox.Reset();
        PreviousInstall.InstalledOverride = null;
        OldInstall(StoreRoot, null, "Ubisoft Connect");
        Sandbox.File_(Path.Combine(StoreRoot, "Bilans"), "Bilan PC 2026-11-10.html");
        Sandbox.File_(Path.Combine(StoreRoot, "Cache", "Icones"), "a.png");
        var otherPc = Directory.CreateDirectory(Path.Combine(StoreRoot, "AUTRE-PC")).FullName;
        File.WriteAllText(Path.Combine(otherPc, DataLocation.MarkerFile), "{\"Id\":\"autre\"}");
        Sandbox.File_(otherPc, "config.json", "{}");

        Assert.Equal(1, PreviousInstall.Run().Shortcuts);
        Assert.True(File.Exists(Path.Combine(Settings.StoreDir, "Ubisoft Connect.url")));
        Assert.True(File.Exists(Path.Combine(StoreRoot, "Bilans", "Bilan PC 2026-11-10.html")));   // bilans : restent communs
        Assert.True(File.Exists(Path.Combine(otherPc, "config.json")));                             // l'autre PC n'est pas touché
        Assert.False(Directory.Exists(Path.Combine(StoreRoot, "Cache")));                          // cache jetable retiré
        Assert.False(File.Exists(Path.Combine(StoreRoot, "config.json")));
    }

    [Fact]
    public void Deux_anciennes_versions_la_plus_recente_est_reprise_l_autre_fusionnee()
    {
        Sandbox.Reset();
        PreviousInstall.InstalledOverride = null;
        OldInstall(ExeDir, c => { c.Overrides["Appli A"] = "Bureautique"; c.Favorites.Add("Ancienne"); }, "Mon jeu");
        File.SetLastWriteTime(Path.Combine(ExeDir, "config.json"), DateTime.Now.AddDays(-2));
        OldInstall(StoreRoot, c => { c.Overrides["Appli A"] = "Jeux"; c.TidyMode = "folders"; }, "Mon jeu", "Autre");

        var r = PreviousInstall.Run();
        Assert.Equal("Jeux", Settings.Current.Overrides["Appli A"]);   // la plus récente l'emporte
        Assert.Equal("folders", Settings.Current.TidyMode);
        Assert.Contains("Ancienne", Settings.Current.Favorites);       // l'autre complète
        // « Mon jeu » rangé par les deux versions, identique : un seul exemplaire
        Assert.Single(Directory.GetFiles(Settings.StoreDir, "Mon jeu*"));
        Assert.True(File.Exists(Path.Combine(Settings.StoreDir, "Autre.url")));
        Assert.Equal(2, r.Shortcuts);
    }

    [Fact]
    public void Rien_n_est_deplace_tant_que_l_ancienne_version_est_installee()
    {
        Sandbox.Reset();
        OldInstall(ExeDir, null, "Mon jeu");
        PreviousInstall.InstalledOverride = _ => "La version téléchargée sur GitHub";
        try
        {
            var r = PreviousInstall.Run();
            Assert.Equal(0, r.Shortcuts);
            Assert.Equal(1, r.BlockedShortcuts);
            Assert.True(File.Exists(Path.Combine(ExeDir, "Bureau rangé", "Mon jeu.url")));   // elle garde son rangement
        }
        finally { PreviousInstall.InstalledOverride = null; }
        // Mise à jour ou désinstallée : déplacement au démarrage suivant
        Assert.Equal(1, PreviousInstall.Run().Shortcuts);
    }

    [Fact]
    public void Documents_et_refus_de_l_ancienne_version_repris()
    {
        Sandbox.Reset();
        PreviousInstall.InstalledOverride = null;
        var factures = Directory.CreateDirectory(Path.Combine(Sandbox.Documents, @"Administratif\Factures")).FullName;
        var refused = Path.Combine(Sandbox.Desktop, "notes perso.pdf");
        OldInstall(ExeDir, c =>
        {
            c.DocFolders.Add(factures);
            c.DocRejected.Add(refused);
            c.Categories.Add(new Category { Name = "Ma catégorie", Keywords = new() { "truc" } });
        });
        PreviousInstall.Run();
        Assert.Contains(factures, Settings.Current.DocFolders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(refused, Settings.Current.DocRejected, StringComparer.OrdinalIgnoreCase);   // jamais reproposé
        Assert.NotNull(Settings.Find("Ma catégorie"));
    }

    [Fact]
    public void Les_dossiers_crees_par_une_installation_precedente_sont_reconnus()
    {
        Sandbox.Reset();
        var docs = Sandbox.Documents;
        var images = DocumentLibrary.Roots.First(r => r.Key == "images").Path;
        var downloads = DocumentLibrary.Roots.First(r => r.Key == "telechargements").Path;
        var factures = Directory.CreateDirectory(Path.Combine(docs, @"Administratif\Factures")).FullName;
        var gps = Directory.CreateDirectory(Path.Combine(docs, "Cartes et traces GPS")).FullName;
        var photos = Directory.CreateDirectory(Path.Combine(images, "Photos")).FullName;
        var setups = Directory.CreateDirectory(Path.Combine(downloads, "Programmes d'installation")).FullName;
        var mine = Directory.CreateDirectory(Path.Combine(docs, "Recettes")).FullName;   // dossier de l'utilisateur, inconnu de Centre PC

        var added = DocPlanner.AdoptOwnFolders();
        foreach (var p in new[] { factures, Path.Combine(docs, "Administratif"), gps, photos, setups })
            Assert.Contains(p, Settings.Current.DocFolders, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(mine, Settings.Current.DocFolders, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(added);
        Assert.Empty(DocPlanner.AdoptOwnFolders());   // une seule fois

        // « Photos » porte le nom d'une appli installée (Photos de Windows) : c'est pourtant un dossier de rangement
        Assert.False(DocPlanner.IsAppFolder(photos, new[] { "Photos" }));
        File.WriteAllText(Path.Combine(Sandbox.Desktop, "IMG_20240501_123000.jpg"), "x");
        var plan = DocPlanner.BuildPlan(new[] { "Photos" });
        Assert.Contains(plan, f => f.Path.Equals(photos, StringComparison.OrdinalIgnoreCase) && f.Exists);
        Assert.DoesNotContain(DocPlanner.SuggestedEmptyFolders(plan, new[] { "Photos" }), f => f.Path.Equals(photos, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Un_dossier_retire_par_l_utilisateur_n_est_jamais_remis()
    {
        Sandbox.Reset();
        var gps = Directory.CreateDirectory(Path.Combine(Sandbox.Documents, "Cartes et traces GPS")).FullName;
        File.WriteAllText(Path.Combine(gps, "sortie.gpx"), "x");
        DocPlanner.AdoptOwnFolders();
        Assert.False(DocumentLibrary.RemoveFolder(gps));   // pas vide : reste sur le disque
        DocPlanner.AdoptOwnFolders();
        Assert.DoesNotContain(gps, Settings.Current.DocFolders, StringComparer.OrdinalIgnoreCase);
        DocumentLibrary.AddFolder(gps);                    // remis à la main : de nouveau dans l'arborescence
        Assert.Contains(gps, Settings.Current.DocFolders, StringComparer.OrdinalIgnoreCase);
    }
}
