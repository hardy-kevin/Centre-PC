using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Plan de rangement des documents : propositions, dossiers des applications, retour en arrière
// =====================================================================================
public class DocPlannerTests
{
    private static string Touch(string dir, string name, string content = "x")
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static string Dest(List<DocFolderPlan> plan, string file) =>
        plan.FirstOrDefault(f => f.Items.Any(i => i.Name == file))?.Path;

    [Fact]
    public void Propositions_d_apres_le_nom_le_type_et_les_dossiers_existants()
    {
        Sandbox.Reset();
        var docs = Sandbox.Documents;
        var images = DocumentLibrary.Roots.First(r => r.Key == "images").Path;
        var downloads = DocumentLibrary.Roots.First(r => r.Key == "telechargements").Path;
        Directory.CreateDirectory(Path.Combine(docs, "Impôts"));                    // dossier déjà créé par l'utilisateur
        Touch(Sandbox.Desktop, "Facture EDF mars.pdf");
        Touch(Sandbox.Desktop, "IMG_20240501_123000.jpg");
        Touch(Sandbox.Desktop, "Capture d'écran 2025-01-01 101010.png");
        Touch(Sandbox.Desktop, "setup.exe");
        Touch(Sandbox.Desktop, "notes.txt");
        Touch(Path.Combine(Sandbox.Desktop, "Cours 2023"), "chapitre 1.pdf");
        Touch(docs, "avis d'impot 2024.pdf");
        Touch(docs, "lettre.docx");

        var plan = DocPlanner.BuildPlan();
        Assert.Equal(Path.Combine(docs, @"Administratif\Factures"), Dest(plan, "Facture EDF mars.pdf"));
        Assert.Equal(Path.Combine(images, "Photos"), Dest(plan, "IMG_20240501_123000.jpg"));
        Assert.Equal(Path.Combine(images, "Captures d'écran"), Dest(plan, "Capture d'écran 2025-01-01 101010.png"));
        Assert.Equal(Path.Combine(downloads, "Programmes d'installation"), Dest(plan, "setup.exe"));
        Assert.Null(Dest(plan, "notes.txt"));                                         // aucun cas prévu : rien n'est deviné
        var manual = DocPlanner.BuildPlanAndManual().manual;
        Assert.Contains(manual, m => m.Name == "notes.txt" && m.IsManual && !m.Selected); // … il attend dans la file « à ranger vous-même »
        Assert.Contains(manual, m => m.Name == "lettre.docx");
        Assert.Equal(Path.Combine(docs, "Études et formations"), Dest(plan, "Cours 2023"));
        Assert.Equal(Path.Combine(docs, "Impôts"), Dest(plan, "avis d'impot 2024.pdf"));   // le dossier de l'utilisateur est réutilisé
        Assert.Null(Dest(plan, "lettre.docx"));                                        // déjà dans Documents, rien de plus précis : laissé en place
        Assert.True(plan.Single(f => f.Path.EndsWith("Impôts")).Exists);
    }

    [Fact]
    public void Dossiers_des_applications_et_des_jeux_jamais_touches()
    {
        Sandbox.Reset();
        var docs = Sandbox.Documents;
        Assert.True(DocPlanner.IsAppFolder(Directory.CreateDirectory(Path.Combine(docs, "My Games")).FullName));
        Assert.True(DocPlanner.IsAppFolder(Directory.CreateDirectory(Path.Combine(docs, "Call of Duty")).FullName));
        var project = Directory.CreateDirectory(Path.Combine(docs, "MonProjet")).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Assert.True(DocPlanner.IsAppFolder(project));
        var saves = Path.Combine(docs, "Zqx Studio");
        foreach (var n in new[] { "a.json", "b.ini", "c.sav", "d.cfg", "photo.jpg" }) Touch(saves, n);
        Assert.True(DocPlanner.IsAppFolder(saves));
        Assert.True(DocPlanner.IsAppFolder(Directory.CreateDirectory(Path.Combine(docs, "Discord")).FullName, new[] { "Discord" }));
        var mine = Path.Combine(docs, "Recettes");
        Touch(mine, "tarte.pdf");
        Assert.False(DocPlanner.IsAppFolder(mine));
        Assert.False(DocPlanner.IsAppFolder(Directory.CreateDirectory(Path.Combine(docs, "Enregistrements audio")).FullName));
        // Le contenu d'un dossier d'application n'est jamais proposé, même s'il ressemble à un document
        Touch(Path.Combine(docs, "My Games"), "facture.pdf");
        Assert.Null(Dest(DocPlanner.BuildPlan(), "facture.pdf"));
    }

    [Fact]
    public void Rangement_puis_refus_remet_en_place_et_n_est_plus_propose()
    {
        Sandbox.Reset();
        var src = Touch(Sandbox.Desktop, "Facture box.pdf");
        var keep = Touch(Sandbox.Desktop, "IMG_0001.jpg");
        var (moved, errors) = DocPlanner.Apply(DocPlanner.BuildPlan());
        Assert.Equal(2, moved);
        Assert.Empty(errors);
        Assert.False(File.Exists(src));
        Assert.Equal(2, Settings.Current.DocReview.Count);

        var facture = Settings.Current.DocReview.Single(e => e.Name == "Facture box.pdf");
        Assert.Null(DocPlanner.Reject(facture));
        Assert.True(File.Exists(src));                                   // revenu exactement à sa place
        Assert.Contains(src, Settings.Current.DocRejected);
        Assert.Null(Dest(DocPlanner.BuildPlan(), "Facture box.pdf"));   // plus jamais proposé

        var photo = Settings.Current.DocReview.Single();
        DocPlanner.Accept(photo);
        Assert.Empty(Settings.Current.DocReview);
        Assert.True(File.Exists(photo.To));
        Assert.False(File.Exists(keep));
    }

    [Fact]
    public void Arborescence_conseillee_sans_doublon_avec_les_dossiers_existants()
    {
        Sandbox.Reset();
        Directory.CreateDirectory(Path.Combine(DocumentLibrary.Roots.First(r => r.Key == "images").Path, "Screenshots"));
        var suggested = DocPlanner.SuggestedEmptyFolders(new List<DocFolderPlan>());
        Assert.Contains(suggested, f => f.Path.EndsWith(@"Administratif\Factures"));
        Assert.DoesNotContain(suggested, f => f.Path.EndsWith("Captures d'écran"));   // « Screenshots » de Windows en tient lieu
        Assert.All(suggested, f => Assert.False(f.Selected));                          // rien n'est créé sans l'accord de l'utilisateur
    }

    [Fact]
    public void Destinations_existantes_seulement_si_ce_sont_des_categories()
    {
        Sandbox.Reset();
        var docs = Sandbox.Documents;
        var images = DocumentLibrary.Roots.First(r => r.Key == "images").Path;
        var downloads = DocumentLibrary.Roots.First(r => r.Key == "telechargements").Path;
        // Contenus particuliers : captures d'un jeu, sauvegarde d'un ancien PC
        Directory.CreateDirectory(Path.Combine(images, @"SimBin\Captures"));
        Directory.CreateDirectory(Path.Combine(docs, @"Sauvegarde ancien PC\Factures"));
        Directory.CreateDirectory(Path.Combine(images, "Screenshots"));
        Directory.CreateDirectory(Path.Combine(downloads, "7D96vAD"));
        Touch(downloads, "Screenshot 2023-08-12 at 14-24-52.png");
        Touch(downloads, "facture_313520339.pdf");
        Touch(downloads, "7D96vAD.zip");
        Touch(downloads, "JaxCoreSetup.bat");
        Touch(downloads, "2023_09_BP_septembre.pdf");
        Touch(downloads, "Certificat de cession.pdf");
        Touch(downloads, "F.gpx");

        var plan = DocPlanner.BuildPlan();
        Assert.Equal(Path.Combine(images, "Screenshots"), Dest(plan, "Screenshot 2023-08-12 at 14-24-52.png"));    // pas SimBin\Captures
        Assert.Equal(Path.Combine(docs, @"Administratif\Factures"), Dest(plan, "facture_313520339.pdf"));      // pas la sauvegarde
        Assert.Equal(Path.Combine(downloads, "Archives"), Dest(plan, "7D96vAD.zip"));                          // pas dans sa version extraite
        Assert.Equal(Path.Combine(downloads, "Programmes d'installation"), Dest(plan, "JaxCoreSetup.bat"));
        Assert.Equal(Path.Combine(docs, @"Administratif\Emploi et salaires"), Dest(plan, "2023_09_BP_septembre.pdf"));
        Assert.Equal(Path.Combine(docs, @"Administratif\Véhicule"), Dest(plan, "Certificat de cession.pdf"));
        Assert.Equal(Path.Combine(docs, "Cartes et traces GPS"), Dest(plan, "F.gpx"));
    }

    [Fact]
    public void Fichiers_audio_vers_Musique()
    {
        Sandbox.Reset();
        var music = DocumentLibrary.Roots.First(r => r.Key == "musique").Path;
        var downloads = DocumentLibrary.Roots.First(r => r.Key == "telechargements").Path;
        Touch(downloads, "En_ce_moment.m4a");
        Touch(downloads, "concert.flac");
        Touch(downloads, "playlist été.m3u8");
        Touch(downloads, "Le Petit Prince.m4b");
        Touch(Sandbox.Desktop, "Mémo 12.amr");
        Touch(music, "deja la.mp3");

        var (plan, manual) = DocPlanner.BuildPlanAndManual();
        Assert.Equal(music, Dest(plan, "En_ce_moment.m4a"));
        Assert.Equal(music, Dest(plan, "concert.flac"));
        Assert.Equal(music, Dest(plan, "playlist été.m3u8"));
        Assert.Equal(Path.Combine(music, "Livres audio"), Dest(plan, "Le Petit Prince.m4b"));
        Assert.Equal(Path.Combine(music, "Enregistrements"), Dest(plan, "Mémo 12.amr"));
        Assert.Null(Dest(plan, "deja la.mp3"));                                   // déjà dans Musique : à sa place
        Assert.DoesNotContain(manual, m => m.Name == "deja la.mp3");
    }
}
