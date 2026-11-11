using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Réglages : chargement, enregistrement sûr, copie de secours
// =====================================================================================
public class SettingsTests
{
    private static string ConfigPath => Path.Combine(Settings.DataDir, "config.json");

    [Fact]
    public void Premier_lancement_cree_les_themes_par_defaut()
    {
        Sandbox.Reset();
        Assert.True(Settings.Current.Categories.Count >= 15);
        Assert.NotNull(Settings.Find("Jeux"));
        Assert.Contains("Centre PC", Settings.Current.KeepOnDesktop);
    }

    [Fact]
    public void Les_reglages_enregistres_sont_relus()
    {
        Sandbox.Reset();
        Settings.Current.Favorites.Add("Mon appli");
        Settings.Save();
        Settings.Flush();
        Settings.Load();
        Assert.Contains("Mon appli", Settings.Current.Favorites);
    }

    [Fact]
    public void Config_abimee_la_copie_de_secours_est_reprise()
    {
        Sandbox.Reset();
        Settings.Current.Favorites.Add("Version A");
        Settings.Save(); Settings.Flush();
        Settings.Current.Favorites.Add("Version B");
        Settings.Save(); Settings.Flush();             // config.bak = version A
        File.WriteAllText(ConfigPath, "{ fichier abîmé");
        Settings.Load();
        Assert.Contains("Version A", Settings.Current.Favorites);
    }

    [Fact]
    public void Une_instance_administrateur_ne_reecrit_jamais_la_config()
    {
        Sandbox.Reset();
        Settings.Save(); Settings.Flush();
        var before = File.ReadAllText(ConfigPath);
        try
        {
            Settings.ReadOnly = true;
            Settings.Current.Favorites.Add("Ne doit pas être écrit");
            Settings.Save(); Settings.Flush();
            Assert.Equal(before, File.ReadAllText(ConfigPath));
        }
        finally { Settings.ReadOnly = false; }
    }

    [Fact]
    public void Export_puis_import_restaure_choix_et_arborescence()
    {
        Sandbox.Reset();
        Settings.SetOverride("Mon outil", "Bureautique");
        DocumentLibrary.CreateFolder(Sandbox.Documents, @"Jeux\Dofus");
        var file = Path.Combine(Sandbox.Root, "export.json");
        Settings.Export(file);

        Sandbox.Reset();
        Assert.False(Directory.Exists(Path.Combine(Sandbox.Documents, "Jeux", "Dofus")));
        Settings.Import(file);
        Assert.Equal("Bureautique", Settings.Classify("Mon outil", ""));
        Assert.True(Directory.Exists(Path.Combine(Sandbox.Documents, "Jeux", "Dofus")));
    }
}

// =====================================================================================
//  Tri des applis par thème (mots-clés, choix manuels, sources en ligne)
// =====================================================================================
public class ClassificationTests
{
    [Theory]
    [InlineData("World of Warcraft", "Jeux")]
    [InlineData("Mozilla Firefox", "Internet")]
    [InlineData("Telegram", "Communication")]
    [InlineData("Steam", "Lanceurs de jeux")]
    public void Mots_cles_par_defaut(string app, string theme)
    {
        Sandbox.Reset();
        Assert.Equal(theme, Settings.Classify(app, ""));
    }

    [Fact]
    public void Un_choix_manuel_l_emporte_et_s_explique()
    {
        Sandbox.Reset();
        Settings.SetOverride("Steam", "Utilitaires");
        Assert.Equal("Utilitaires", Settings.Classify("Steam", ""));
        Assert.Equal("défini manuellement", Settings.Explain("Steam", ""));
    }

    [Fact]
    public void Appli_inconnue_dans_Autres_sauf_si_c_est_un_jeu()
    {
        Sandbox.Reset();
        Assert.Equal(Settings.Current.DefaultCategory, Settings.Classify("Zqxwv Outil", ""));
        Assert.Equal("Jeux", Settings.Classify("Zqxwv Outil", "", looksLikeGame: true));
    }

    [Theory]
    [InlineData("web browser based on Chromium", "Internet")]
    [InlineData("2019 video game", "Jeux")]
    [InlineData("instant messaging client", "Communication")]
    [InlineData("integrated development environment", "Développement")]
    public void Description_en_ligne_vers_theme(string text, string theme)
    {
        Sandbox.Reset();
        Assert.Equal(theme, OnlineClassifier.Decide(text, out _));
    }

    [Fact]
    public void Description_sans_indice_ne_classe_pas()
    {
        Sandbox.Reset();
        Assert.Null(OnlineClassifier.Decide("", out _));
        Assert.Null(OnlineClassifier.Decide("Lorem ipsum dolor", out _));
    }

    [Fact]
    public void Categories_Flathub_et_genres_Steam()
    {
        Sandbox.Reset();
        Assert.Equal("Communication", OnlineClassifier.ThemeFromFlathub(new[] { "Network", "InstantMessaging" }));
        Assert.Equal("Jeux", OnlineClassifier.ThemeFromFlathub(new[] { "Game" }));
        Assert.Null(OnlineClassifier.ThemeFromFlathub(new[] { "Inconnue" }));
        Assert.Equal("Jeux", OnlineClassifier.ThemeFromSteam("game", Array.Empty<string>()));
        Assert.Equal("Utilitaires", OnlineClassifier.ThemeFromSteam("game", new[] { "57" }));
        Assert.Null(OnlineClassifier.ThemeFromSteam("music", Array.Empty<string>()));
    }
}

// =====================================================================================
//  Rangement du bureau (raccourcis) et historique « Annuler »
// =====================================================================================
public class DesktopTidyTests
{
    private static AppItem Item(string path) =>
        AppScanner.ScanDesktop().First(i => i.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Ranger_puis_remettre_sur_le_bureau()
    {
        Sandbox.Reset();
        var lnk = Sandbox.Url(Sandbox.Desktop, "Mon jeu");
        Assert.Equal(1, DesktopManager.Store(new[] { Item(lnk) }));
        var stored = Path.Combine(Settings.StoreDir, "Mon jeu.url");
        Assert.False(File.Exists(lnk));
        Assert.True(File.Exists(stored));
        Assert.Equal(lnk, Settings.Current.Stored[stored], ignoreCase: true);

        Assert.Equal(1, DesktopManager.Restore(new[] { Item(stored) }));
        Assert.True(File.Exists(lnk));
        Assert.False(Settings.Current.Stored.ContainsKey(stored));
    }

    [Fact]
    public void Les_fichiers_ne_vont_jamais_dans_le_rangement_cache()
    {
        Sandbox.Reset();
        var doc = Sandbox.File_(Sandbox.Desktop, "Notes.txt");
        Assert.Equal(0, DesktopManager.Store(new[] { Item(doc) }));
        Assert.True(File.Exists(doc));
    }

    [Fact]
    public void Historique_annuler_un_rangement_des_jours_apres()
    {
        Sandbox.Reset();
        var lnk = Sandbox.Url(Sandbox.Desktop, "Discord");
        DesktopManager.Store(new[] { Item(lnk) });
        History.ResetCache();                       // comme après un redémarrage de Centre PC
        var entry = History.Recent().First(e => e.Kind == "rangement");
        Assert.True(entry.CanUndo);
        Assert.Null(History.Undo(entry));
        Assert.True(File.Exists(lnk));
        Assert.Empty(Settings.Current.Stored);
        Assert.False(History.Recent().First().CanUndo);   // déjà annulé
    }

    [Fact]
    public void Historique_n_ecrase_jamais_un_fichier()
    {
        Sandbox.Reset();
        var lnk = Sandbox.Url(Sandbox.Desktop, "Spotify");
        DesktopManager.Store(new[] { Item(lnk) });
        Sandbox.Url(Sandbox.Desktop, "Spotify", "https://autre.example");   // un autre fichier a pris la place
        var entry = History.Recent().First();
        Assert.False(entry.CanUndo);
        Assert.NotNull(History.Undo(entry));
    }
}

// =====================================================================================
//  Remise en état du bureau après les mises à jour
// =====================================================================================
public class ShortcutGuardTests
{
    [Fact]
    public void Icone_remise_sur_le_bureau_par_un_installateur_est_retiree()
    {
        Sandbox.Reset();
        Sandbox.Url(Settings.StoreDir, "Telegram", "https://telegram.org");
        var snap = ShortcutGuard.Take();
        var dup = Sandbox.Url(Sandbox.Desktop, "Telegram", "https://telegram.org");   // l'installateur la recrée

        var r = ShortcutGuard.Reconcile(snap);
        Assert.Equal(1, r.Removed);
        Assert.False(File.Exists(dup));
        Assert.True(File.Exists(Path.Combine(Settings.StoreDir, "Telegram.url")));
        Assert.NotEmpty(Directory.GetFiles(ShortcutGuard.ReplacedDir));   // mise de côté, pas supprimée
    }

    [Fact]
    public void Nouvelle_icone_rangee_si_l_utilisateur_range_son_bureau()
    {
        Sandbox.Reset();
        Sandbox.Url(Settings.StoreDir, "Déjà rangé");
        var snap = ShortcutGuard.Take();
        var fresh = Sandbox.Url(Sandbox.Desktop, "Nouvelle appli");

        var r = ShortcutGuard.Reconcile(snap);
        Assert.Equal(1, r.Stored);
        Assert.False(File.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(Settings.StoreDir, "Nouvelle appli.url")));
    }

    [Fact]
    public void Bureau_jamais_range_les_nouvelles_icones_restent()
    {
        Sandbox.Reset();
        var snap = ShortcutGuard.Take();
        var fresh = Sandbox.Url(Sandbox.Desktop, "Nouvelle appli");
        ShortcutGuard.Reconcile(snap);
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Raccourci_range_casse_remplace_par_la_nouvelle_version()
    {
        Sandbox.Reset();
        var oldExe = Sandbox.FakeExe("Editeur 2025.2", "editeur64.exe");
        Sandbox.Lnk(Settings.StoreDir, "Editeur 2025.2", oldExe);
        Settings.SetOverride("Editeur 2025.2", "Développement");
        var snap = ShortcutGuard.Take();

        Directory.Delete(Path.GetDirectoryName(oldExe), true);                 // l'ancienne version est désinstallée
        var newExe = Sandbox.FakeExe("Editeur 2025.3", "editeur64.exe");
        Sandbox.Lnk(Sandbox.Desktop, "Editeur 2025.3", newExe);                // la nouvelle crée son icône

        var r = ShortcutGuard.Reconcile(snap);
        Assert.Equal(1, r.Replaced);
        Assert.True(File.Exists(Path.Combine(Settings.StoreDir, "Editeur 2025.3.lnk")));
        Assert.False(File.Exists(Path.Combine(Settings.StoreDir, "Editeur 2025.2.lnk")));
        Assert.Empty(Directory.GetFiles(Sandbox.Desktop, "*.lnk"));
        Assert.Equal("Développement", Settings.Classify("Editeur 2025.3", ""));   // le thème choisi suit
    }

    [Fact]
    public void Appli_desinstallee_son_raccourci_range_disparait()
    {
        Sandbox.Reset();
        var exe = Sandbox.FakeExe("Ancien Jeu");
        Sandbox.Lnk(Settings.StoreDir, "Ancien Jeu", exe);
        Directory.Delete(Path.GetDirectoryName(exe), true);

        var removed = ShortcutGuard.RemoveOrphans();
        Assert.Contains("Ancien Jeu", removed);
        Assert.False(File.Exists(Path.Combine(Settings.StoreDir, "Ancien Jeu.lnk")));
    }

    [Fact]
    public void Doublons_anciens_nettoyes_au_demarrage()
    {
        Sandbox.Reset();
        Sandbox.Url(Settings.StoreDir, "Ubisoft Connect", "uplay://launch");
        var dup = Sandbox.Url(Sandbox.Desktop, "Ubisoft Connect", "uplay://launch");
        var r = ShortcutGuard.CleanDuplicates();
        Assert.Equal(1, r.Removed);
        Assert.False(File.Exists(dup));
    }

    [Fact]
    public void Mise_a_jour_terminee_appli_fermee_la_photo_est_reprise()
    {
        Sandbox.Reset();
        var log = Path.Combine(Sandbox.Root, "maj.log");
        File.WriteAllText(log, "Démarrage\n");
        var snap = ShortcutGuard.Take();
        snap.LogPath = log;
        ShortcutGuard.Save(snap);
        Assert.Null(ShortcutGuard.Pending());      // mise à jour encore en cours
        File.AppendAllText(log, "##FIN|0\n");
        Assert.NotNull(ShortcutGuard.Pending());   // terminée : la remise en état peut se faire
    }
}

// =====================================================================================
//  Documents : arborescence et rangement des fichiers du bureau
// =====================================================================================
public class DocumentTests
{
    [Fact]
    public void Fichiers_du_bureau_a_ranger_sans_les_raccourcis()
    {
        Sandbox.Reset();
        Sandbox.File_(Sandbox.Desktop, "Parchemin métier.md");
        Sandbox.Url(Sandbox.Desktop, "Jeu");
        Directory.CreateDirectory(Path.Combine(Sandbox.Desktop, "Sauvegarde ancien PC"));
        Sandbox.File_(Settings.StoreDir, "Craft alchimiste.ods");               // ancien rangement caché

        var pending = DocumentLibrary.Pending();
        Assert.Contains(pending, p => p.Name == "Parchemin métier.md" && p.Origin.StartsWith("sur le bureau"));
        Assert.Contains(pending, p => p.Name == "Sauvegarde ancien PC" && p.IsFolder);
        Assert.Contains(pending, p => p.Name == "Craft alchimiste.ods" && p.Origin.StartsWith("ancien"));
        Assert.DoesNotContain(pending, p => p.Name == "Jeu.url");
    }

    [Fact]
    public void Garder_sur_le_bureau_n_est_plus_propose()
    {
        Sandbox.Reset();
        Sandbox.File_(Sandbox.Desktop, "A garder.txt");
        Settings.Current.KeepOnDesktop.Add("A garder.txt");
        Assert.DoesNotContain(DocumentLibrary.Pending(), p => p.Name == "A garder.txt");
    }

    [Fact]
    public void Arborescence_de_depart()
    {
        Sandbox.Reset();
        DocumentLibrary.CreateStarter();
        var labels = DocumentLibrary.Folders().Select(f => f.Label).ToList();
        Assert.Contains("Documents › Jeux", labels);
        Assert.Contains("Images › Photos", labels);
        Assert.True(Directory.Exists(Path.Combine(Sandbox.Documents, "Administratif")));
    }

    [Fact]
    public void Ranger_un_fichier_puis_annuler_depuis_l_historique()
    {
        Sandbox.Reset();
        var src = Sandbox.File_(Sandbox.Desktop, "Parchemin métier.md");
        var dofus = DocumentLibrary.CreateFolder(Sandbox.Documents, @"Jeux\Dofus");
        var item = DocumentLibrary.Pending().First(p => p.Path == src);

        var dest = DocumentLibrary.Move(item, dofus);
        Assert.Equal(Path.Combine(dofus, "Parchemin métier.md"), dest);
        Assert.False(File.Exists(src));
        Assert.Contains(dest, DocumentLibrary.Recent());

        var entry = History.Recent().First(e => e.Kind == "document");
        Assert.Null(History.Undo(entry));
        Assert.True(File.Exists(src));
        Assert.DoesNotContain(dest, DocumentLibrary.Recent());
    }

    [Fact]
    public void Un_doublon_de_nom_ne_remplace_jamais_un_fichier()
    {
        Sandbox.Reset();
        var dir = DocumentLibrary.CreateFolder(Sandbox.Documents, "Travail");
        Sandbox.File_(dir, "Rapport.txt", "ancien");
        var src = Sandbox.File_(Sandbox.Desktop, "Rapport.txt", "nouveau");
        var dest = DocumentLibrary.Move(DocumentLibrary.Pending().First(p => p.Path == src), dir);
        Assert.Equal("ancien", File.ReadAllText(Path.Combine(dir, "Rapport.txt")));
        Assert.Equal("nouveau", File.ReadAllText(dest));
    }

    [Fact]
    public void Ancien_rangement_le_fichier_quitte_le_dossier_cache()
    {
        Sandbox.Reset();
        var old = Sandbox.File_(Settings.StoreDir, "combo_cartes.png");
        Settings.Current.Stored[old] = Path.Combine(Sandbox.Desktop, "combo_cartes.png");
        var item = DocumentLibrary.Pending().First(p => p.Path == old);
        var dest = DocumentLibrary.Move(item, DocumentLibrary.Roots.First(r => r.Key == "images").Path);
        Assert.True(File.Exists(dest));
        Assert.False(Settings.Current.Stored.ContainsKey(old));
    }

    [Fact]
    public void Renommer_et_retirer_des_dossiers_sans_perte()
    {
        Sandbox.Reset();
        var jeux = DocumentLibrary.CreateFolder(Sandbox.Documents, @"Jeux\Dofus");
        var renamed = DocumentLibrary.RenameFolder(Path.GetDirectoryName(jeux), "Mes jeux");
        Assert.Contains(DocumentLibrary.Folders(), f => f.Label == "Documents › Mes jeux › Dofus");

        var plein = Path.Combine(renamed, "Dofus");
        Sandbox.File_(plein, "important.txt");
        Assert.False(DocumentLibrary.RemoveFolder(plein));                 // pas vide : reste sur le disque
        Assert.True(File.Exists(Path.Combine(plein, "important.txt")));
        Assert.DoesNotContain(DocumentLibrary.Folders(), f => f.Path == plein);

        var vide = DocumentLibrary.CreateFolder(Sandbox.Documents, "Vide");
        Assert.True(DocumentLibrary.RemoveFolder(vide));                   // vide : supprimé
        Assert.False(Directory.Exists(vide));
    }
}

// =====================================================================================
//  Mises à jour : lancement sécurisé et déroulé complet avec un winget simulé
// =====================================================================================
public class UpdateScriptTests
{
    private const string FakeWinget = @"
$cmd = $args[0]; $id = ''; $dir = ''; $loc = ''
for ($i = 1; $i -lt $args.Count; $i++) {
    if ($args[$i] -eq '--id') { $id = $args[$i + 1] }
    elseif ($args[$i] -eq '--download-directory') { $dir = $args[$i + 1] }
    elseif ($args[$i] -eq '--location') { $loc = $args[$i + 1] }
}
$S = '__STATE__'
if ($cmd -eq 'list') {
    if (Test-Path ""$S\removed_$id"") { 'Aucun package installé'; exit -1978335212 }
    ""Nom   $id   1.0""; exit 0
}
if ($cmd -eq 'download') {
    New-Item -ItemType Directory $dir -Force | Out-Null
    [IO.File]::WriteAllBytes(""$dir\setup.exe"", (New-Object byte[] 5000))
    'UpgradeBehavior: uninstallPrevious' | Set-Content ""$dir\manifeste.yaml""
    exit 0
}
if ($cmd -eq 'install') { Remove-Item ""$S\removed_$id"" -ErrorAction SilentlyContinue; 'Installé correctement'; exit 0 }
if ($cmd -eq 'upgrade') {
    if ($id -eq 'JetBrains.CentrePCTest') { 'Début de la désinstallation du paquet...'; New-Item ""$S\removed_$id"" -ItemType File -Force | Out-Null; exit 1 }
    if ($id -eq 'CentrePCTest.Emplacement') { exit -1978335137 }
    if ($id -eq 'CentrePCTest.Techno') { exit -1978335090 }
    if ($id -eq 'CentrePCTest.Hachage') { exit -1978335215 }
    if ($id -eq 'CentrePCTest.Redemarrage') { 'Installé correctement'; exit -1978334967 }
    'Installé correctement'; exit 0
}
exit 0
";

    private static string ScriptText()
    {
        using var res = typeof(Updates).Assembly.GetManifestResourceStream("maj.ps1");
        using var sr = new StreamReader(res);
        return sr.ReadToEnd();
    }

    /// <summary>Prépare le script et ses paramètres comme Centre PC, puis les lance (sans droits administrateur).</summary>
    private static (int code, string log) Run(Updates.InstallOptions opt, Action<string> tamper = null)
    {
        var dir = Path.Combine(Sandbox.Root, "maj-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        opt.Log = Path.Combine(dir, "journal.log");
        opt.DataDir = dir;
        var script = Path.Combine(dir, "maj.ps1");
        var scriptText = ScriptText();
        File.WriteAllText(script, scriptText, new UTF8Encoding(true));
        var paramsFile = Path.Combine(dir, "maj-params.json");
        var paramsText = JsonSerializer.Serialize(opt);
        File.WriteAllText(paramsFile, paramsText, new UTF8Encoding(false));
        var encoded = Updates.BuildBootstrap(script, paramsFile, scriptText, paramsText);
        tamper?.Invoke(script);

        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(180_000), "Le script n'a pas fini en 3 minutes");
        var log = File.Exists(opt.Log) ? File.ReadAllText(opt.Log) : "";
        return (p.ExitCode, log + "\n--- sortie ---\n" + outTask.Result + errTask.Result);
    }

    private static Updates.InstallOptions Nothing() => new()
    {
        RestorePoint = false, Apps = false, WindowsUpdate = false, Store = false, Defender = false, Drivers = false
    };

    [Fact]
    public void Le_script_verifie_se_lance_et_se_termine()
    {
        Sandbox.Reset();
        var (code, log) = Run(Nothing());
        Assert.True(code == 0, log);
        Assert.Contains("##FIN|", log);   // |1 si Windows attend déjà un redémarrage sur ce PC
    }

    [Fact]
    public void Un_script_modifie_par_un_autre_programme_n_est_pas_execute()
    {
        Sandbox.Reset();
        var (code, log) = Run(Nothing(), script => File.AppendAllText(script, "\r\nWrite-Host 'pirate'"));
        Assert.Equal(99, code);
        Assert.DoesNotContain("##FIN", log);
    }

    [Fact]
    public void Deroule_complet_des_applis_avec_winget_simule()
    {
        Sandbox.Reset();
        var state = Path.Combine(Sandbox.Root, "winget-etat");
        Directory.CreateDirectory(state);
        var fake = Path.Combine(Sandbox.Root, "faux-winget.ps1");
        File.WriteAllText(fake, FakeWinget.Replace("__STATE__", state), new UTF8Encoding(true));

        var opt = Nothing();
        opt.Apps = true;
        opt.WingetPath = fake;
        opt.Packages = new[]
        {
            "CentrePCTest.Normal", "JetBrains.CentrePCTest", "CentrePCTest.Emplacement",
            "CentrePCTest.Techno", "CentrePCTest.Hachage", "CentrePCTest.Redemarrage"
        }.Select(id => new Updates.PackageRef { Id = id, Name = "Zz " + id }).ToList();

        var (code, log) = Run(opt);
        string Status(string id) => log.Split('\n').Where(l => l.StartsWith($"##APP|{id}|")).Select(l => l.Split('|')[2]).LastOrDefault();

        Assert.True(code == 0, log);
        Assert.Equal("À jour", Status("CentrePCTest.Normal"));
        // Mise à jour qui désinstalle d'abord : vérifiée avant, puis réinstallée automatiquement quand l'installation échoue
        Assert.Contains("##APP|JetBrains.CentrePCTest|En cours|vérification", log);
        Assert.Equal("À jour", Status("JetBrains.CentrePCTest"));
        Assert.Contains("réinstallée automatiquement", log);
        Assert.Equal("À faire", Status("CentrePCTest.Emplacement"));
        Assert.Equal("À faire", Status("CentrePCTest.Techno"));
        Assert.Contains("##MANUEL|CentrePCTest.Techno|", log);
        Assert.Equal("Reporté", Status("CentrePCTest.Hachage"));
        Assert.Equal("À jour", Status("CentrePCTest.Redemarrage"));
        Assert.Contains("##ETAPE|apps|À VÉRIFIER|", log);
        Assert.Contains("##FIN|1", log);   // redémarrage demandé
        Assert.Contains("JetBrains.CentrePCTest", File.ReadAllText(Path.Combine(opt.DataDir, "maj-desinstallation.json")));
    }
}

// =====================================================================================
//  Divers : modèles, journal
// =====================================================================================
public class ModelAndLogTests
{
    [Fact]
    public void Pilote_gere_par_le_fabricant_jamais_coche()
    {
        var d = new DriverUpdate { ManagedBy = "AMD Software" };
        d.Selected = true;
        Assert.False(d.Selected);
        Assert.False(d.CanSelect);
        Assert.Contains("AMD Software", d.Note);
    }

    [Fact]
    public void Version_de_pilote_par_date_si_inconnue()
    {
        var d = new DriverUpdate { CurrentVersion = "1.0", NewDate = new DateTime(2026, 3, 5) };
        Assert.Equal("1.0  →  05/03/2026", d.VersionText);
    }

    [Fact]
    public void Ligne_action_manuelle_du_journal()
    {
        var m = Updates.ManualAction.Parse("##MANUEL|Id.App|Mon appli|Ferme-la puis relance.|C:\\x\\setup.exe");
        Assert.Equal("Mon appli", m.Name);
        Assert.Equal("Ferme-la puis relance.", m.Message);
        Assert.Equal(@"C:\x\setup.exe", m.Installer);
        Assert.Null(Updates.ManualAction.Parse("##MANUEL|incomplet"));
    }

    [Fact]
    public void Le_journal_ecrit_dans_le_dossier_de_Centre_PC()
    {
        Sandbox.Reset();
        Log.Info("test du journal");
        Assert.Contains("test du journal", File.ReadAllText(Log.FilePath));
        Assert.StartsWith(Sandbox.Root, Log.FilePath, StringComparison.OrdinalIgnoreCase);
    }
}

// =====================================================================================
//  Fausses alertes de winget (mise à jour installée mais toujours proposée)
// =====================================================================================
public class PhantomUpdateTests
{
    private static WingetUpdate U(string id, string v, string a) => new() { Id = id, Name = id, Version = v, Available = a };

    [Fact]
    public void Deja_installee_dans_la_meme_version_fausse_alerte_decochee()
    {
        Sandbox.Reset();
        Updates.RememberInstalled(U("Ankama.AnkamaLauncher", "3.15.14", "3.15.14.20606"));
        var list = new List<WingetUpdate> { U("Ankama.AnkamaLauncher", "3.15.14", "3.15.14.20606"), U("Autre.Appli", "1.0", "2.0") };
        Updates.MarkPhantoms(list);
        Assert.True(list[0].Phantom);
        Assert.False(list[0].Selected);
        Assert.False(list[1].Phantom);
        Assert.True(list[1].Selected);
    }

    [Fact]
    public void Une_vraie_nouvelle_version_est_de_nouveau_proposee()
    {
        Sandbox.Reset();
        Updates.RememberInstalled(U("Blizzard.BattleNet", "Unknown", "2.0.0.4232"));
        var list = new List<WingetUpdate> { U("Blizzard.BattleNet", "Unknown", "2.0.0.4300") };
        Updates.MarkPhantoms(list);
        Assert.False(list[0].Phantom);
        Assert.False(Settings.Current.PhantomUpdates.ContainsKey("Blizzard.BattleNet"));
    }

    [Fact]
    public void Reconnues_depuis_les_journaux_des_dernieres_mises_a_jour()
    {
        Sandbox.Reset();
        var dir = Path.Combine(Settings.DataDir, "Journaux");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "MAJ_2026-11-08_21-37-53.log"), string.Join("\n",
            "> Mise à jour de Ankama.AnkamaLauncher",
            "##APP|Ankama.AnkamaLauncher|En cours|téléchargement et installation…",
            "Trouvé Ankama Launcher [Ankama.AnkamaLauncher] Version 3.15.14.20606",
            "Installé correctement",
            "##APP|Ankama.AnkamaLauncher|À jour|",
            "> Mise à jour de EpicGames.EpicOnlineServices",
            "##APP|EpicGames.EpicOnlineServices|À faire|…"));
        var list = new List<WingetUpdate>
        {
            U("Ankama.AnkamaLauncher", "3.15.14", "3.15.14.20606"),
            U("EpicGames.EpicOnlineServices", "2.0", "2.1")
        };
        Updates.MarkPhantoms(list);
        Assert.True(list[0].Phantom);
        Assert.False(list[1].Phantom);
    }
}
