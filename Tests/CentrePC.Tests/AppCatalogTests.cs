using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Classement affiné, applications obsolètes, suites, doublons de langue, désinstallation
// =====================================================================================
public class RefinedClassificationTests
{
    [Theory]
    [InlineData("PlayStation App", "Lanceurs de jeux")]
    [InlineData("Power Automate", "Utilitaires")]
    [InlineData("Cartes", "Outils Windows")]
    [InlineData("Run", "Outils Windows")]
    [InlineData("Exécuter", "Outils Windows")]
    public void Applications_signalees_bien_classees(string app, string theme)
    {
        Sandbox.Reset();
        Assert.Equal(theme, Settings.Classify(app, ""));
    }

    [Fact]
    public void L_editeur_de_l_executable_suffit()
    {
        Sandbox.Reset();
        // « PSApp » ne dit rien par son nom : l'éditeur lu dans l'exécutable (Sony Interactive Entertainment) le classe
        Assert.Equal("Lanceurs de jeux", Settings.Classify("PSApp", @"C:\Program Files\PSApp\PSApp.exe", "Sony Interactive Entertainment Inc. | PlayStation"));
    }

    [Fact]
    public void Un_mot_general_ne_decide_qu_en_dernier_recours()
    {
        Sandbox.Reset();
        // « Launcher » ne fait pas d'Adobe Redeem Launcher un lanceur de jeux : l'éditeur Adobe l'emporte
        Assert.Equal("Photo et graphisme", Settings.Classify("Adobe Redeem Launcher", "", "Adobe Inc. | Adobe Redeem Launcher"));
        // Sans aucun autre indice, le mot général sert quand même (plutôt que « Autres »)
        Assert.Equal("Lanceurs de jeux", Settings.Classify("Zqx Launcher", ""));
        Assert.Contains("faute de mieux", Settings.Explain("Zqx Launcher", ""));
        // Un nom précis reste prioritaire
        Assert.Equal("Lanceurs de jeux", Settings.Classify("Epic Games Launcher", ""));
    }

    [Fact]
    public void Un_nom_exact_ne_capture_pas_les_autres_noms()
    {
        Sandbox.Reset();
        // « =Run » ne vise que « Run » : « Temple Run » n'est pas un outil Windows
        Assert.NotEqual("Outils Windows", Settings.Classify("Temple Run", ""));
        Assert.Equal("nom exact connu", Settings.Explain("Run", ""));
    }

    [Theory]
    [InlineData("Cyberpunk 2077 - Bonus Content")]
    [InlineData("Adobe Redeem Launcher")]
    [InlineData("Hollow Knight Soundtrack")]
    [InlineData("Elden Ring Digital Artbook")]
    [InlineData("Uninstall Git")]
    [InlineData("Aide de LibreOffice")]
    public void Elements_annexes_reconnus(string name) => Assert.True(AppScanner.IsCompanionName(name));

    [Theory]
    [InlineData("Cyberpunk 2077")]
    [InlineData("Wallpaper Engine")]
    [InlineData("Git Bash")]
    [InlineData("BlueStacks Services")]
    [InlineData("Ganymède")]
    public void Vraies_applications_non_annexes(string name) => Assert.False(AppScanner.IsCompanionName(name));

    [Fact]
    public void Desinstalleur_reconnu_par_sa_cible() =>
        Assert.True(AppScanner.IsCompanionName("Git", @"""C:\Program Files\Git\unins000.exe"""));

    [Fact]
    public void Mise_a_niveau_retire_les_anciens_mots_cles_et_garde_ceux_de_l_utilisateur()
    {
        Sandbox.Reset();
        var office = Settings.Find("Bureautique");
        office.Keywords.AddRange(new[] { "Cartes", "Power Automate", "Mon mot-clé" });
        Settings.Find("Utilitaires").Keywords.RemoveAll(k => k == "Power Automate");
        Settings.Current.DefaultsVersion = 3;
        Settings.Save();
        Settings.Flush();
        Settings.Load();
        office = Settings.Find("Bureautique");
        Assert.DoesNotContain("Cartes", office.Keywords);
        Assert.DoesNotContain("Power Automate", office.Keywords);
        Assert.Contains("Mon mot-clé", office.Keywords);
        Assert.Equal("Utilitaires", Settings.Classify("Power Automate", ""));
        Assert.Equal(DefaultThemes.Version, Settings.Current.DefaultsVersion);
    }

    [Fact]
    public void Gestionnaires_de_mods_deplaces_vers_les_outils_pour_les_jeux()
    {
        Sandbox.Reset();
        Settings.Find("Lanceurs de jeux").Keywords.AddRange(new[] { "CurseForge", "Overwolf" });
        Settings.Current.DefaultsVersion = 4;
        Settings.Save();
        Settings.Flush();
        Settings.Load();
        Assert.DoesNotContain("CurseForge", Settings.Find("Lanceurs de jeux").Keywords);
        Assert.Equal(Settings.GameToolsCategory, Settings.Classify("CurseForge", ""));
    }

    [Theory]
    [InlineData("natural satellite\nGalilean moon\nmoon of Jupiter", false)]
    [InlineData("free and open-source software\nmod manager", true)]
    [InlineData("2020 video game", true)]
    public void Wikidata_ne_retient_que_les_logiciels(string text, bool expected) =>
        Assert.Equal(expected, OnlineClassifier.LooksLikeSoftware(text));
}

// =====================================================================================
//  Outils pour les jeux et catégories créées à la demande
// =====================================================================================
public class GameToolsAndNewCategoriesTests
{
    [Theory]
    [InlineData("Dofus Guide", "")]
    [InlineData("WoW Addon Manager", "")]
    [InlineData("CurseForge", "")]
    [InlineData("Ganymède", "Ganymède | Application d'aide aux quêtes pour Dofus")]
    [InlineData("Mon Outil", "Companion app for Elden Ring")]
    public void Outils_pour_un_jeu(string name, string context)
    {
        Sandbox.Reset();
        Assert.Equal(Settings.GameToolsCategory, Settings.Classify(name, "", context));
    }

    [Theory]
    [InlineData("Dofus")]
    [InlineData("World of Warcraft")]
    public void Le_jeu_lui_meme_reste_un_jeu(string name)
    {
        Sandbox.Reset();
        Assert.Equal("Jeux", Settings.Classify(name, ""));
    }

    [Fact]
    public void Description_en_ligne_d_un_compagnon_de_jeu()
    {
        Sandbox.Reset();
        Assert.Equal(Settings.GameToolsCategory, OnlineClassifier.Decide("Guide de quêtes pour Dofus, gratuit", out _));
        Assert.Equal("Jeux", OnlineClassifier.Decide("2019 video game\nopen world", out _));
    }

    [Fact]
    public void Categorie_du_catalogue_creee_a_la_demande_puis_respectee_si_supprimee()
    {
        Sandbox.Reset();
        Assert.Null(Settings.Find(Settings.GameToolsCategory));
        Assert.True(Settings.IsAvailable(Settings.GameToolsCategory));
        var created = Settings.ActivateCategories(new[] { Settings.GameToolsCategory, "Jeux", null });
        Assert.Equal(new[] { Settings.GameToolsCategory }, created);
        // Placée juste après « Lanceurs de jeux »
        var names = Settings.Current.Categories.Select(c => c.Name).ToList();
        Assert.Equal(names.IndexOf("Lanceurs de jeux") + 1, names.IndexOf(Settings.GameToolsCategory));
        // Supprimée par l'utilisateur : plus jamais recréée automatiquement
        Settings.RemoveCategory(Settings.Find(Settings.GameToolsCategory));
        Assert.False(Settings.IsAvailable(Settings.GameToolsCategory));
        Assert.Empty(Settings.ActivateCategories(new[] { Settings.GameToolsCategory }));
        Assert.NotEqual(Settings.GameToolsCategory, Settings.Classify("CurseForge", ""));
    }

    [Fact]
    public void L_utilisateur_cree_sa_propre_categorie()
    {
        Sandbox.Reset();
        Assert.Null(Settings.CheckCategoryName("Radioamateur"));
        Settings.CreateCategory("Radioamateur");
        Assert.NotNull(Settings.CheckCategoryName("Radioamateur"));      // nom déjà pris
        Assert.NotNull(Settings.CheckCategoryName("A"));                 // trop court
        Settings.SetOverride("WSJT-X", "Radioamateur");
        Assert.Equal("Radioamateur", Settings.Classify("WSJT-X", ""));
    }

    [Fact]
    public void Page_web_resumee_par_ses_balises()
    {
        var html = "<html><head><title>Ganymède - Dofus</title><meta name=\"description\" content=\"Guide de quêtes &amp; outils\">" +
                   "<meta property='og:description' content='Companion app'></head><body>…</body></html>";
        var text = OnlineClassifier.PageSummary(html);
        Assert.Contains("Ganymède - Dofus", text);
        Assert.Contains("Guide de quêtes & outils", text);
        Assert.Contains("Companion app", text);
    }
}

// =====================================================================================
//  Catégorie déduite des applications voisines
// =====================================================================================
public class AffinityTests
{
    private static AppItem App(string name, string publisher = null, string root = null, string folder = null) =>
        new() { Name = name, Publisher = publisher, InstallRoot = root, StartFolder = folder, Location = ItemLocation.StartMenu, Target = "" };

    [Fact]
    public void Meme_editeur_meme_categorie()
    {
        Sandbox.Reset();
        var photoshop = App("Adobe Photoshop 2025", "Adobe Inc.");
        var redeem = App("Zqx Activation", "Adobe Inc.");
        var apps = new List<AppItem> { photoshop, redeem, App("Qwv Inconnu", "Autre éditeur") };
        Affinity.Compute(apps);
        Assert.NotEqual(Settings.Current.DefaultCategory, photoshop.Theme);
        Assert.Equal(photoshop.Theme, redeem.Theme);
        Assert.Contains("même éditeur", Settings.Explain(redeem.Name, ""));
        Assert.Equal(Settings.Current.DefaultCategory, apps[2].Theme);
    }

    [Fact]
    public void Meme_dossier_d_installation()
    {
        Sandbox.Reset();
        var apps = new List<AppItem> { App("Zqx Truc", root: @"C:\Program Files\Zqx"), App("Firefox", root: @"C:\Program Files\Zqx") };
        Affinity.Compute(apps);
        Assert.Equal("Internet", apps[0].Theme);
    }

    [Fact]
    public void Voisines_en_desaccord_pas_de_deduction()
    {
        Sandbox.Reset();
        var apps = new List<AppItem>
        {
            App("Zqx Truc", "Zqx SARL"), App("Firefox", "Zqx SARL"), App("Steam", "Zqx SARL")
        };
        Affinity.Compute(apps);
        Assert.Equal(Settings.Current.DefaultCategory, apps[0].Theme);
    }

    [Fact]
    public void Laisser_dans_Autres_est_respecte()
    {
        Sandbox.Reset();
        Settings.SetSkipped("Zqx Truc", true);
        var apps = new List<AppItem> { App("Zqx Truc", "Zqx SARL"), App("Firefox", "Zqx SARL") };
        Affinity.Compute(apps);
        Assert.Equal(Settings.Current.DefaultCategory, apps[0].Theme);
    }
}

public class InstallFolderTests
{
    [Fact]
    public void Dossier_d_installation_d_un_programme()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(Path.Combine(pf, "Git"), AppScanner.InstallRootOf(Path.Combine(pf, "Git", "bin", "git.exe")), ignoreCase: true);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077",
            AppScanner.InstallRootOf(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"), ignoreCase: true);
        Assert.Null(AppScanner.InstallRootOf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")));
    }

    [Fact]
    public void Description_d_une_application_Electron()
    {
        Sandbox.Reset();
        var dir = Path.Combine(Sandbox.Programs, "Ganymede");
        Directory.CreateDirectory(Path.Combine(dir, "resources"));
        var json = "{\"name\":\"ganymede\",\"productName\":\"Ganymède\",\"description\":\"Aide aux quêtes de Dofus\",\"keywords\":[\"dofus\",\"guide\"],\"homepage\":\"https://example.org\"}";
        var data = System.Text.Encoding.UTF8.GetBytes(json);
        var header = System.Text.Encoding.UTF8.GetBytes("{\"files\":{\"package.json\":{\"size\":" + data.Length + ",\"offset\":\"0\"}}}");
        int padded = (header.Length + 3) / 4 * 4;
        using (var fs = File.Create(Path.Combine(dir, "resources", "app.asar")))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(4u); bw.Write((uint)(8 + padded)); bw.Write((uint)(4 + padded)); bw.Write(header.Length);
            bw.Write(header); bw.Write(new byte[padded - header.Length]); bw.Write(data);
        }
        var (text, home, _) = AppScanner.ReadPackageJson(dir);
        Assert.Contains("Aide aux quêtes de Dofus", text);
        Assert.Contains("dofus", text);
        Assert.Equal("https://example.org", home);
        Assert.Equal(Settings.GameToolsCategory, Settings.Classify("Ganymède", "", text));
    }
}

public class ObsoleteAppsTests
{
    [Theory]
    [InlineData("Cartes", null, null)]
    [InlineData("Maps", null, null)]
    [InlineData("N'importe quel nom", "Microsoft.WindowsMaps_8wekyb3d8bbwe!App", null)]
    [InlineData("WordPad", null, @"C:\Program Files\Windows NT\Accessories\wordpad.exe")]
    [InlineData("Origin", null, @"C:\Program Files (x86)\Origin\Origin.exe ")]
    [InlineData("Skype", "Microsoft.SkypeApp_kzf8qxf38zg5c!App", null)]
    public void Applications_retirees_reconnues(string name, string aumid, string target)
    {
        Sandbox.Reset();
        var info = ObsoleteApps.Find(name, aumid, target);
        Assert.NotNull(info);
        Assert.False(string.IsNullOrEmpty(info.Replacement));
    }

    [Theory]
    [InlineData("Origin 2024", null, @"C:\Program Files\OriginLab\Origin2024\Origin64.exe")]
    [InlineData("Origin", null, @"C:\Program Files\OriginLab\Origin64.exe")]       // logiciel scientifique homonyme
    [InlineData("Google Maps Tools", null, null)]
    [InlineData("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App", null)]               // le nouveau Paint n'est pas Paint 3D
    [InlineData("Microsoft Teams", "MSTeams_8wekyb3d8bbwe!MSTeams", null)]
    public void Applications_actuelles_non_signalees(string name, string aumid, string target)
    {
        Sandbox.Reset();
        Assert.Null(ObsoleteApps.Find(name, aumid, target));
    }
}

public class SuiteTests
{
    // Chaque test part d'une configuration vierge (dossiers créés par l'utilisateur, exclusions)
    public SuiteTests() => Sandbox.Reset();

    private static AppItem App(string name, string folder = null, string theme = "Développement", ItemLocation loc = ItemLocation.StartMenu,
                               string publisher = null, string root = null, string target = null, string aumid = null) =>
        new() { Name = name, StartFolder = folder, Theme = theme, Location = loc, Publisher = publisher, InstallRoot = root, Target = target, Aumid = aumid };

    [Fact]
    public void Git_LibreOffice_et_BlueStacks_regroupes()
    {
        var apps = new List<AppItem>
        {
            App("Git Bash", "Git"), App("Git CMD", "Git"), App("Git GUI", "Git"),
            App("LibreOffice", "LibreOffice 7.6", "Bureautique"), App("LibreOffice Writer", "LibreOffice 7.6", "Bureautique"),
            App("LibreOffice Calc", "LibreOffice 7.6", "Bureautique"),
            App("BlueStacks 5", "BlueStacks", "Lanceurs de jeux"), App("BlueStacks Multi-Instance Manager", "BlueStacks", "Lanceurs de jeux"),
            App("Firefox"),
        };
        var res = AppSuites.Group(apps);
        Assert.Equal(4, res.Count);
        Assert.Equal(3, res.Single(a => a.Name == "Git").Members.Count);
        var lo = res.Single(a => a.Name == "LibreOffice");
        Assert.Same(apps[3], lo.Members[0]);            // l'application principale en premier
        Assert.Equal("Bureautique", lo.Theme);
        Assert.Equal(2, res.Single(a => a.Name == "BlueStacks").Members.Count);
        Assert.Contains(res, a => a.Name == "Firefox" && !a.IsSuite);
    }

    [Fact]
    public void Dossier_d_editeur_avec_sous_dossier_de_documentation()
    {
        // Menu Démarrer réel : « PostgreSQL 16 » et son sous-dossier « Documentation »
        var docs = new[] { "pgAdmin documentation", "PostgreSQL documentation", "PostgreSQL release notes", "Installation notes" }
            .Select(n => { var a = App(n, @"PostgreSQL 16\Documentation"); a.IsCompanion = true; return a; }).ToList();
        var apps = new List<AppItem> { App("pgAdmin 4", "PostgreSQL 16"), App("SQL Shell (psql)", "PostgreSQL 16"), App("Application Stack Builder", "PostgreSQL 16") };
        apps.AddRange(docs);
        var res = AppSuites.Group(apps);
        var pg = Assert.Single(res);
        Assert.Equal("PostgreSQL", pg.Name);                 // « 16 » retiré du nom du dossier
        Assert.Equal(7, pg.Members.Count);
        Assert.All(pg.Members.Skip(3), m => Assert.True(m.IsCompanion));   // annexes à la fin
    }

    [Fact]
    public void Dossiers_d_editeurs_regroupes_dossiers_fourre_tout_jamais()
    {
        var apps = new List<AppItem>
        {
            App("Cyberpunk 2077", "Steam", "Jeux"), App("Elden Ring", "Steam", "Jeux"),
            App("Windows Media Player Legacy", "Accessoires Windows"), App("Windows Fax and Scan", "Accessoires Windows"),
            App("YouTube", "Applications Chrome", "Vidéo et streaming"), App("Gmail", "Applications Chrome", "Communication"),
            App("IntelliJ IDEA 2026.3", "JetBrains"), App("PhpStorm 2026.2.4", "JetBrains"),
            App("Assassin's Creed Mirage", "Ubisoft", "Jeux"), App("Far Cry 6", "Ubisoft", "Jeux"),
        };
        var res = AppSuites.Group(apps);
        var jb = res.Single(a => a.IsSuite);
        Assert.Equal("JetBrains", jb.Name);                   // dossier d'éditeur : ses applications réunies
        Assert.Equal(9, res.Count);                            // jeux différents, Steam, Accessoires, Chrome : séparés
    }

    [Fact]
    public void Composants_de_Windows_et_applications_obsoletes_jamais_regroupes()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var steps = App("Enregistreur d'actions utilisateur", "Accessories", "Outils Windows", target: Path.Combine(win, "System32", "psr.exe"));
        steps.Obsolete = new ObsoleteInfo("abandonné", "Outil Capture");
        var voice = App("Enregistreur vocal", null, "Musique et audio", ItemLocation.Store, aumid: "Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe!App");
        var res = AppSuites.Group(new List<AppItem> { steps, voice });
        Assert.Equal(2, res.Count);
        Assert.All(res, a => Assert.False(a.IsSuite));
        Assert.Contains(res, a => a.IsObsolete);               // l'application obsolète reste visible
    }

    [Fact]
    public void Meme_dossier_d_installation_meme_tuile()
    {
        var apps = new List<AppItem>
        {
            App("LGHUB", null, "Matériel et pilotes", root: @"C:\Program Files\LGHUB"),
            App("Logitech G HUB", "Logi", "Matériel et pilotes", root: @"C:\Program Files\LGHUB"),
        };
        var s = Assert.Single(AppSuites.Group(apps));
        Assert.Equal("Logitech G HUB", s.Name);                // le nom le plus complet
    }

    [Fact]
    public void Une_marque_installee_a_plusieurs_endroits_forme_une_suite_si_l_editeur_le_confirme()
    {
        var apps = new List<AppItem>
        {
            App("BlueStacks 5", null, "Lanceurs de jeux", publisher: "BlueStack Systems, Inc.", root: @"C:\Program Files\BlueStacks_nxt"),
            App("BlueStacks Multi-Instance Manager", null, "Lanceurs de jeux", publisher: "BlueStack Systems, Inc.", root: @"C:\Program Files\BlueStacks_nxt"),
            App("BlueStacks Store", "BlueStacks Store", "Lanceurs de jeux", publisher: "BlueStack Systems Inc", root: @"C:\Program Files (x86)\BlueStacks X"),
            App("BlueStacks Services", null, "Autres", publisher: "BlueStack Systems", root: @"C:\Users\K\AppData\Local\Programs\bluestacks-services"),
            // Application Android lancée par BlueStacks : ce n'est pas BlueStacks
            App("PSApp", null, "Lanceurs de jeux", target: @"C:\Program Files\BlueStacks_nxt\HD-Player.exe --instance Pie64 --cmd launchAppWithBsx --package ""com.scee.psxandroid"""),
            App("Need for Speed Heat", null, "Jeux"), App("Need for Speed Unbound", null, "Jeux"),
            App("Zqx Alpha", null, "Autres", publisher: "Zqx"), App("Zqx Beta", null, "Autres", publisher: "Autre éditeur"),
        };
        var res = AppSuites.Group(apps);
        var bs = res.Single(a => a.Name == "BlueStacks");
        Assert.Equal(4, bs.Members.Count);
        Assert.Equal("Lanceurs de jeux", bs.Theme);
        Assert.Contains(res, a => a.Name == "PSApp" && !a.IsSuite);
        Assert.Equal(2, res.Single(a => a.Name == "Need for Speed").Members.Count);
        Assert.DoesNotContain(res, a => a.Name == "Zqx");      // même premier mot, éditeurs différents
    }

    [Fact]
    public void Mots_courants_ne_rapprochent_pas_des_jeux_differents()
    {
        var apps = new List<AppItem>
        {
            App("World of Warcraft", null, "Jeux"), App("World of Tanks", null, "Jeux"), App("Star Citizen", null, "Jeux"), App("Star Wars Jedi", null, "Jeux"),
            App("Visual Studio 2022"), App("Visual Studio Code"), App("Google Chrome", null, "Internet"), App("Google Drive", null, "Cloud et sauvegarde")
        };
        Assert.All(AppSuites.Group(apps), a => Assert.False(a.IsSuite));
    }

    [Fact]
    public void Une_appli_du_bureau_rejoint_sa_suite()
    {
        var apps = new List<AppItem>
        {
            App("Git CMD", "Git"), App("Git GUI", "Git"), App("Git Bash", null, loc: ItemLocation.Desktop), App("GitHub Desktop")
        };
        var res = AppSuites.Group(apps);
        Assert.Equal(3, res.Single(a => a.IsSuite).Members.Count);
        Assert.Contains(res, a => a.Name == "GitHub Desktop" && !a.IsSuite);
    }

    [Fact]
    public void Les_elements_annexes_rejoignent_leur_application()
    {
        var game = App("Cyberpunk 2077", null, "Jeux", ItemLocation.Desktop);
        var bonus = App("Cyberpunk 2077 - Bonus Content", "Cyberpunk 2077", "Jeux");
        bonus.IsCompanion = true;
        var redeem = App("Adobe Redeem Launcher", null, "Autres");
        redeem.IsCompanion = true;
        var res = AppSuites.Group(new List<AppItem> { game, bonus, redeem, App("Firefox", null, "Internet") });
        var cp = res.Single(a => a.Name == "Cyberpunk 2077");
        Assert.True(cp.IsSuite);
        Assert.Same(game, cp.Members[0]);
        Assert.Same(bonus, cp.Members[1]);
        Assert.Contains(res, a => a == redeem);                 // annexe sans application connue : jamais cachée
    }

    [Fact]
    public void Dossiers_crees_par_l_utilisateur_et_groupes_defaits()
    {
        var apps = new List<AppItem> { App("Git Bash", "Git"), App("Git CMD", "Git"), App("Firefox", null, "Internet"), App("Discord", null, "Communication") };
        Settings.Current.AppFolders["Mes favoris"] = new List<string> { "Firefox", "Discord" };
        Settings.Current.SuiteExcluded.Add("Git CMD");
        var res = AppSuites.Group(apps);
        var mine = res.Single(a => a.Name == "Mes favoris");
        Assert.True(mine.IsUserGroup);
        Assert.Equal(2, mine.Members.Count);
        Assert.Equal(2, res.Count(a => !a.IsSuite));            // Git CMD sorti du groupe : Git Bash et Git CMD seuls
    }

    [Fact]
    public void Applications_identifiees_par_leur_identifiant_Windows()
    {
        // Entrées du dossier « Applications » sans dossier ni éditeur connus : l'identifiant Windows suffit
        var apps = new[] { "Writer", "Calc", "Impress", "Base" }
            .Select(n => App("LibreOffice " + n, null, "Bureautique", ItemLocation.Store, aumid: "TheDocumentFoundation.LibreOffice." + n)).ToList();
        apps.Add(App("LibreOffice", null, "Bureautique", ItemLocation.Store, aumid: "TheDocumentFoundation.LibreOffice.Startcenter"));
        var s = Assert.Single(AppSuites.Group(apps));
        Assert.Equal("LibreOffice", s.Name);
        Assert.Equal(5, s.Members.Count);
        Assert.Equal("TheDocumentFoundation.LibreOffice", AppSuites.AumidVendor(apps[0]));
        Assert.Null(AppSuites.AumidVendor(App("x", aumid: "Microsoft.WindowsMaps_8wekyb3d8bbwe!App")));
    }

    [Fact]
    public void Deux_groupes_du_meme_nom_fusionnes_et_editeurs_proches_reconnus()
    {
        var apps = new List<AppItem>
        {
            App("BlueStacks 5", null, "Lanceurs de jeux", publisher: "BlueStack Systems, Inc.", root: @"C:\Program Files\BlueStacks_nxt"),
            App("BlueStacks Multi-Instance Manager", null, "Lanceurs de jeux", publisher: "BlueStack Systems, Inc.", root: @"C:\Program Files\BlueStacks_nxt"),
            App("BlueStacks X", null, "Lanceurs de jeux", publisher: "BlueStack Systems, Inc.", root: @"C:\Program Files (x86)\BlueStacks X"),
            App("BlueStacks Store", "BlueStacks Store", "Lanceurs de jeux", root: @"C:\Program Files (x86)\BlueStacks X"),
            App("BlueStacks Services", null, "Autres", publisher: "BlueStacks", root: @"C:\Users\K\AppData\Local\Programs\bluestacks-services"),
        };
        var s = Assert.Single(AppSuites.Group(apps));
        Assert.Equal("BlueStacks", s.Name);
        Assert.Equal(5, s.Members.Count);
        Assert.True(AppSuites.SameMaker("bluestack", "bluestacks"));
        Assert.True(AppSuites.SameMaker("The Document Foundation", "Document Foundation"));
        Assert.False(AppSuites.SameMaker("zqx", "autre éditeur"));
    }

    [Fact]
    public void Noms_de_groupes_d_apres_tous_leurs_membres()
    {
        AppItem C(AppItem a) { a.IsCompanion = true; return a; }
        var res = AppSuites.Group(new List<AppItem>
        {
            App("7-Zip File Manager", "7-Zip", "Utilitaires"), C(App("7-Zip Help", "7-Zip", "Utilitaires")),
            App("Bitnami for XAMPP", "XAMPP"), C(App("Uninstall XAMPP", "XAMPP")), C(App("XAMPP htdocs folder", "XAMPP")),
            App("Wreckfest 2", null, "Jeux", root: @"D:\SteamLibrary\steamapps\common\Wreckfest 2"),
            App("start_server - Raccourci", null, "Scripts", root: @"D:\SteamLibrary\steamapps\common\Wreckfest 2",
                target: @"D:\SteamLibrary\steamapps\common\Wreckfest 2\start_server.bat"),
        });
        Assert.Contains(res, a => a.Name == "7-Zip" && a.IsSuite);                  // le dossier de l'éditeur, cité par ses applications
        Assert.Contains(res, a => a.Name == "XAMPP" && a.Members.Count == 3);
        var game = res.Single(a => a.Name == "Wreckfest 2");                         // le jeu, pas son script de serveur
        Assert.Equal("Jeux", game.Theme);
        Assert.Equal("Wreckfest 2", game.Members[0].Name);
    }

    [Fact]
    public void Annexes_rattachees_par_dossier_ou_dossier_d_installation()
    {
        var hamachi = App("LogMeIn Hamachi", null, "Administration système et réseau", ItemLocation.Stored);
        var uninstall = App("Uninstall", "LogMeIn Hamachi"); uninstall.IsCompanion = true;
        var meta = App("Meta Horizon Link", null, "Matériel et pilotes", root: @"C:\Program Files\Oculus");
        var oculus = App("Uninstall Oculus"); oculus.IsCompanion = true;
        var res = AppSuites.Group(new List<AppItem> { hamachi, uninstall, meta, oculus });
        Assert.Equal(2, res.Count);
        Assert.All(res, a => Assert.Equal(2, a.Members.Count));
    }

    [Fact]
    public void Editeur_fictif_ignore()
    {
        var res = AppSuites.Group(new List<AppItem>
        {
            App("Zqx One", null, "Autres", publisher: "COMPANY NAME"), App("Zqx Two", null, "Autres", publisher: "Vrai Éditeur"),
            App("Zqx Three", null, "Autres", publisher: "Vrai Éditeur"),
        });
        Assert.Equal(3, res.Single(a => a.IsSuite).Members.Count);
    }

    [Theory]
    [InlineData("PostgreSQL 16", "PostgreSQL")]
    [InlineData("OpenOffice 4.1.14", "OpenOffice")]
    [InlineData("Discord Inc", "Discord")]
    [InlineData("Python 3.12 (64-bit)", "Python")]
    [InlineData("7-Zip", "7-Zip")]
    public void Nom_de_dossier_nettoye(string folder, string expected) => Assert.Equal(expected, AppSuites.CleanFolderName(folder));

    [Fact]
    public void Prefixe_commun_en_mots_entiers() =>
        Assert.Equal("Cyberpunk 2077", AppSuites.CommonPrefix(new[] { "Cyberpunk 2077", "Cyberpunk 2077 - Bonus" }));
}

public class LanguageDuplicateTests
{
    private static AppItem App(string name, ItemLocation loc = ItemLocation.StartMenu) => new() { Name = name, Location = loc };

    [Fact]
    public void Garde_le_nom_dans_la_langue_de_Windows()
    {
        var apps = new List<AppItem> { App("Explorateur de fichiers"), App("File Explorer", ItemLocation.Store), App("Narrator"), App("Narrateur") };
        var fr = AppScanner.MergeLanguageDuplicates(apps, "fr").Select(a => a.Name).ToList();
        Assert.Equal(new[] { "Explorateur de fichiers", "Narrateur" }, fr);
        var en = AppScanner.MergeLanguageDuplicates(apps, "en").Select(a => a.Name).ToList();
        Assert.Equal(new[] { "File Explorer", "Narrator" }, en);
    }

    [Fact]
    public void Un_raccourci_du_bureau_n_est_jamais_retire()
    {
        var apps = new List<AppItem> { App("Run", ItemLocation.Desktop), App("Exécuter") };
        var res = AppScanner.MergeLanguageDuplicates(apps, "fr");
        Assert.Single(res);
        Assert.Equal("Run", res[0].Name);
    }

    [Fact]
    public void Noms_traduits_du_desktop_ini()
    {
        Sandbox.Reset();
        var dir = Path.Combine(Sandbox.Root, "Outils système");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "desktop.ini"),
            "[.ShellClassInfo]\r\nLocalizedResourceName=Outils\r\n[LocalizedFileNames]\r\nFile Explorer.lnk=Explorateur de fichiers\r\n", System.Text.Encoding.Unicode);
        Assert.Equal("Explorateur de fichiers", AppScanner.DisplayName(Path.Combine(dir, "File Explorer.lnk")));
        Assert.Equal("Narrator", AppScanner.DisplayName(Path.Combine(dir, "Narrator.lnk")));
    }

    [Fact]
    public void Chemins_32_et_64_bits_de_Windows_identiques()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(AppScanner.NormalizePath(Path.Combine(win, "System32", "narrator.exe")),
                     AppScanner.NormalizePath(Path.Combine(win, "SysWOW64", "narrator.exe")));
        // {1AC14E77…} = dossier System32
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "narrator.exe"),
                     AppScanner.ResolveAppsFolderPath(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\narrator.exe"), ignoreCase: true);
        Assert.Null(AppScanner.ResolveAppsFolderPath("Microsoft.WindowsMaps_8wekyb3d8bbwe!App"));
    }
}

public class UninstallerTests
{
    private static ArpEntry E(string name, string location = null, string uninstall = null, string icon = null, string key = null) =>
        new() { DisplayName = name, InstallLocation = location, UninstallString = uninstall ?? "", DisplayIcon = icon, KeyName = key ?? name };

    private static readonly List<ArpEntry> Installed = new()
    {
        E("Git", @"C:\Program Files\Git\", "\"C:\\Program Files\\Git\\unins000.exe\""),
        E("LibreOffice 7.6.4.1", null, "MsiExec.exe /I{3E4B1E2C-0000-4A6F-8A21-1234567890AB}", @"C:\Program Files\LibreOffice\program\soffice.exe,0",
          "{3E4B1E2C-0000-4A6F-8A21-1234567890AB}"),
        E("Outil sans dossier", @"C:\Program Files", "C:\\Program Files\\outil-uninstall.exe"),
        E("Discord", null, @"C:\Users\K\AppData\Local\Discord\Update.exe --uninstall", @"C:\Users\K\AppData\Local\Discord\app.ico"),
    };

    [Fact]
    public void Retrouve_le_programme_par_son_dossier()
    {
        Assert.Equal("Git", AppUninstaller.Match(Installed, @"C:\Program Files\Git\git-bash.exe", "Git Bash").DisplayName);
        Assert.Equal("LibreOffice 7.6.4.1", AppUninstaller.Match(Installed, @"C:\Program Files\LibreOffice\program\swriter.exe", "LibreOffice Writer").DisplayName);
        Assert.Equal("Discord", AppUninstaller.Match(Installed, @"C:\Users\K\AppData\Local\Discord\Update.exe", "Discord").DisplayName);
    }

    [Fact]
    public void Program_Files_seul_ne_suffit_pas()
    {
        // « C:\Program Files » est trop large : aucune correspondance par dossier ni par nom
        Assert.Null(AppUninstaller.Match(Installed, @"C:\Program Files\Autre\autre.exe", "Autre programme"));
    }

    [Fact]
    public void Retrouve_le_programme_par_son_nom()
    {
        Assert.Equal("Git", AppUninstaller.Match(Installed, null, "Git GUI").DisplayName);
        Assert.Null(AppUninstaller.Match(Installed, null, "GitHub Desktop"));
    }

    [Fact]
    public void Commandes_de_desinstallation()
    {
        Assert.Equal((@"C:\Program Files\Git\unins000.exe", ""), AppUninstaller.ParseCommand("\"C:\\Program Files\\Git\\unins000.exe\""));
        Assert.Equal("/X{3E4B1E2C-0000-4A6F-8A21-1234567890AB}", AppUninstaller.ParseCommand("MsiExec.exe /I{3E4B1E2C-0000-4A6F-8A21-1234567890AB}").args);
        Assert.Equal("/X{3E4B1E2C-0000-4A6F-8A21-1234567890AB}",
            AppUninstaller.ParseCommand("MsiExec.exe /qb", "{3E4B1E2C-0000-4A6F-8A21-1234567890AB}").args);
        var (exe, args) = AppUninstaller.ParseCommand(@"C:\Users\K\AppData\Local\Discord\Update.exe --uninstall");
        Assert.Equal(@"C:\Users\K\AppData\Local\Discord\Update.exe", exe);
        Assert.Equal("--uninstall", args);
    }

    [Fact]
    public void Plans_Store_Steam_et_Windows()
    {
        Sandbox.Reset();
        var steam = new List<ArpEntry> { E("Cyberpunk 2077", key: "Steam App 1091500", uninstall: "\"C:\\Steam\\steam.exe\" steam://uninstall/1091500") };
        Assert.Equal(UninstallKind.Store, AppUninstaller.Plan(new AppItem { Name = "Cartes", Aumid = "Microsoft.WindowsMaps_8wekyb3d8bbwe!App" }, steam).Kind);
        Assert.Equal("Microsoft.WindowsMaps_8wekyb3d8bbwe",
            AppUninstaller.Plan(new AppItem { Name = "Cartes", Aumid = "Microsoft.WindowsMaps_8wekyb3d8bbwe!App" }, steam).PackageFamily);
        Assert.Equal(UninstallKind.Steam, AppUninstaller.Plan(new AppItem { Name = "Cyberpunk 2077", Target = "steam://rungameid/1091500" }, steam).Kind);
        var narrator = AppUninstaller.Plan(new AppItem { Name = "Narrateur", Aumid = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\narrator.exe" }, steam);
        Assert.False(narrator.CanUninstall);
        Assert.Equal("Intégrée à Windows", narrator.Reason);
        // Enregistreur d'actions : fonctionnalité facultative de Windows, que Windows sait retirer
        var steps = AppUninstaller.Plan(new AppItem { Name = "Enregistreur d'actions", Aumid = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\psr.exe" }, steam);
        Assert.Equal(UninstallKind.WindowsFeature, steps.Kind);
        Assert.Equal("App.StepsRecorder", steps.Capability);
    }
}

// =====================================================================================
//  Scripts, applications hébergées (Android, web), indices contenus dans un programme
// =====================================================================================
public class AppKindsTests
{
    [Fact]
    public void Mots_colles_et_dossiers_communs()
    {
        Assert.Equal("Sena Device Manager", AppKinds.SplitWords("SenaDeviceManager"));
        Assert.Equal("ASA Dedicated Manager", AppKinds.SplitWords("ASADedicatedManager"));
        Assert.DoesNotContain("Files", AppKinds.MatchableTarget(@"C:\Program Files (x86)\MSI\AdobeRedeemLauncher\AdobeRedeemLauncher.exe"));
        Sandbox.Reset();
        // « Program Files » ne fait plus classer dans Utilitaires (mot-clé « Files »)
        Assert.Equal(Settings.Current.DefaultCategory, Settings.Classify("Zqxwv", @"C:\Program Files (x86)\Zqxwv\Zqxwv.exe", ""));
        Assert.Equal("Utilitaires", Settings.Classify("Files", ""));
        Assert.Equal("Matériel et pilotes", Settings.Classify("SenaDeviceManager", ""));
        Assert.Equal(Settings.GameToolsCategory, Settings.Classify("Game Bar", ""));
    }

    [Fact]
    public void Script_livre_avec_un_programme_n_est_pas_personnel()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(AppKinds.IsPersonalScript(Path.Combine(pf, @"PostgreSQL\16\scripts\runpsql.bat")));
        Assert.True(AppKinds.IsPersonalScript(Path.Combine(pf, @"Steam\steamapps\common\Wreckfest 2\start_server.bat")));
        Assert.True(AppKinds.IsPersonalScript(@"D:\Scripts\sauvegarde.ps1"));
    }

    [Theory]
    [InlineData("start_server - Raccourci", "start_server")]
    [InlineData("Mon jeu - Shortcut (2)", "Mon jeu")]
    [InlineData("Git Bash", "Git Bash")]
    public void Suffixe_de_raccourci_retire(string name, string expected) => Assert.Equal(expected, AppKinds.StripShortcutSuffix(name));

    [Fact]
    public void Scripts_personnels_dans_la_categorie_Scripts()
    {
        Sandbox.Reset();
        Assert.Equal(AppKinds.ScriptsCategory,
            Settings.Classify("start_server - Raccourci", @"C:\Program Files (x86)\Steam\steamapps\common\ARK Survival Ascended Dedicated Server\start_server.bat", ""));
        Assert.Equal(AppKinds.ScriptsCategory,
            Settings.Classify("Sauvegarde", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -File ""D:\Outils\sauvegarde.ps1""", ""));
        Assert.Contains("script personnel", Settings.Explain("start_server - Raccourci", @"D:\Serveurs\start_server.bat"));
        // Invite de commandes préparée par un programme : ce n'est pas un script personnel
        Assert.NotEqual(AppKinds.ScriptsCategory,
            Settings.Classify("Node.js command prompt", @"C:\Windows\System32\cmd.exe /k ""C:\Program Files\nodejs\nodevars.bat""", ""));
    }

    [Fact]
    public void Application_Android_n_herite_pas_de_l_emulateur()
    {
        Sandbox.Reset();
        const string target = @"C:\Program Files\BlueStacks_nxt\HD-Player.exe --instance Pie64 --cmd launchAppWithBsx --package ""com.scee.psxandroid"" --source desktop_shortcut";
        Assert.Equal("com.scee.psxandroid", AppKinds.AndroidPackage(target));
        Assert.True(AppKinds.IsHosted(target));
        // Le mot-clé « BlueStacks » de la cible ne doit pas en faire un lanceur de jeux
        Assert.Equal(Settings.Current.DefaultCategory, Settings.Classify("PSApp", target, "Application Android com.scee.psxandroid"));
        Assert.Contains("application Android", Settings.Explain("PSApp", target, "Application Android com.scee.psxandroid"));
        Assert.False(AppUninstaller.Plan(new AppItem { Name = "PSApp", Target = target }, new List<ArpEntry>()).CanUninstall);
    }

    [Fact]
    public void Application_web_reconnue()
    {
        const string target = @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe  --profile-directory=Default --app-id=agimnkijcaahngcdmfeangaknmldooml";
        Assert.True(AppKinds.IsWebApp(target));
        Assert.Null(AppKinds.AndroidPackage(target));
        Assert.Equal("application web", AppKinds.ClassifyTarget(target));
    }

    [Fact]
    public void Fiche_Google_Play_vers_theme()
    {
        Sandbox.Reset();
        Assert.Equal("Lanceurs de jeux", Settings.ThemeFromName("PlayStation App"));
        Assert.Equal("Jeux", OnlineClassifier.ThemeFromPlayCategory("GAME_RACING"));
        Assert.Equal("Communication", OnlineClassifier.ThemeFromPlayCategory("COMMUNICATION"));
        Assert.Equal("Santé et sport", OnlineClassifier.ThemeFromPlayCategory("HEALTH_AND_FITNESS"));
        Assert.Null(OnlineClassifier.ThemeFromPlayCategory("LIFESTYLE"));
    }

    [Fact]
    public void Programme_qui_cite_les_sites_d_un_jeu_est_un_outil_pour_ce_jeu()
    {
        Sandbox.Reset();
        // Sites réellement cités par Ganymede (aucune description dans l'exécutable)
        var hosts = new List<string> { "api.dofusdb.fr", "dofuspourlesnoobs.com", "dofusbook.net", "ganymede-app.com", "huzounet.fr", "sentry.io" };
        var (homepage, result) = OnlineClassifier.AnalyzeHosts("Ganymede", hosts);
        Assert.Equal("https://ganymede-app.com", homepage);
        Assert.Equal(Settings.GameToolsCategory, result.Category);
        Assert.Contains("Dofus", result.Evidence);
        // Un seul site cité ne suffit pas
        Assert.Null(OnlineClassifier.AnalyzeHosts("Autre", new List<string> { "dofus.com" }).result);
    }
}
