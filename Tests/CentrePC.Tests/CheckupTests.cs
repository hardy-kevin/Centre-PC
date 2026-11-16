using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Bilan du PC : comparaison de deux instantanés, lecture des capteurs, mémoire
// =====================================================================================
public class SnapshotCompareTests
{
    private static MachineSnapshot Snap(DateTime date, params (string name, double value)[] metrics)
    {
        var s = new MachineSnapshot { Date = date, Machine = "PC", Windows = "Windows 11", Build = "26100", Bios = "1.0", Cpu = "CPU", Ram = "32 Go", Level = "Express", Verdict = "Bon" };
        foreach (var (n, v) in metrics) s.Metrics[n] = v;
        return s;
    }

    private static List<CheckupFinding> Compare(MachineSnapshot old, MachineSnapshot now)
    {
        var report = new CheckupReport();
        now.CompareTo(old, report);
        return report.Findings;
    }

    [Fact]
    public void Premier_bilan_sert_de_reference()
    {
        var f = Compare(null, Snap(DateTime.Now));
        Assert.Single(f);
        Assert.Equal("Suivi", f[0].Area);
    }

    [Fact]
    public void Mesures_stables_ou_en_hausse_sans_alerte()
    {
        var old = Snap(DateTime.Now.AddDays(-1), ("Processeur 1 cœur GFLOPS", 30), ("Processeur tous les cœurs GFLOPS", 793), ("Mémoire Go/s", 73), ("Carte graphique TFLOPS", 52.88));
        var now = Snap(DateTime.Now, ("Processeur 1 cœur GFLOPS", 70), ("Processeur tous les cœurs GFLOPS", 824), ("Mémoire Go/s", 69), ("Carte graphique TFLOPS", 54.25));
        Assert.DoesNotContain(Compare(old, now), x => x.Area == "Évolution");
    }

    [Theory]
    [InlineData("Disque C: 4K Q1 lecture Mo/s", 49.5, 38.4)]
    [InlineData("Internet téléchargement Mbit/s", 900, 300)]
    public void Mesures_trop_variables_ne_declenchent_pas_d_alerte(string name, double before, double after)
    {
        Assert.DoesNotContain(Compare(Snap(DateTime.Now.AddDays(-1), (name, before)), Snap(DateTime.Now, (name, after))), x => x.Area == "Évolution");
    }

    [Fact]
    public void Vraie_baisse_de_performance_signalee_et_nommee()
    {
        var f = Compare(Snap(DateTime.Now.AddDays(-30), ("Processeur tous les cœurs GFLOPS", 800)), Snap(DateTime.Now, ("Processeur tous les cœurs GFLOPS", 600)));
        var e = Assert.Single(f, x => x.Area == "Évolution");
        Assert.Equal(DeviceStatus.Warning, e.Level);
        Assert.Contains("Processeur tous les cœurs", e.Client);
    }

    [Theory]
    [InlineData(70, 75, false)]   // quelques degrés : variation normale (pièce plus chaude)
    [InlineData(70, 82, true)]
    public void Temperature_en_hausse_seulement_au_dela_de_8_degres(double before, double after, bool alert)
    {
        var f = Compare(Snap(DateTime.Now.AddDays(-30), ("Processeur °C en charge", before)), Snap(DateTime.Now, ("Processeur °C en charge", after)));
        Assert.Equal(alert, f.Any(x => x.Area == "Évolution"));
    }

    [Fact]
    public void Temperature_en_baisse_n_est_pas_une_degradation()
    {
        var f = Compare(Snap(DateTime.Now.AddDays(-30), ("Processeur °C en charge", 90)), Snap(DateTime.Now, ("Processeur °C en charge", 70)));
        Assert.DoesNotContain(f, x => x.Area == "Évolution");
    }

    [Fact]
    public void Changements_d_installation_resumes()
    {
        var old = Snap(DateTime.Now.AddDays(-10));
        old.Apps["Outil"] = "1.0"; old.Apps["Ancien"] = "2.0"; old.Drivers["Carte graphique"] = "1 (2026-01-01)";
        var now = Snap(DateTime.Now);
        now.Apps["Outil"] = "1.1"; now.Apps["Nouveau"] = "3.0"; now.Drivers["Carte graphique"] = "2 (2026-06-01)"; now.Bios = "1.1";
        var report = new CheckupReport();
        now.CompareTo(old, report);
        var since = Assert.Single(report.Findings, x => x.Area == "Depuis le dernier bilan");
        Assert.Contains("BIOS changé", since.Client);
        Assert.Contains("1 application(s) installée(s)", since.Client);
        Assert.Contains("1 application(s) supprimée(s)", since.Client);
        Assert.Contains("1 pilote(s)", since.Client);
        Assert.Contains(report.Tables, t => t.Rows.Any(r => r[0] == "Application mise à jour"));
    }
}

public class LibreHardwareMonitorJsonTests
{
    // Arbre réduit, au format de LibreHardwareMonitor 0.9.6 (valeurs en texte, virgule décimale selon la langue de Windows)
    private const string Recent = """
    {"id":0,"Text":"Sensor","Min":"Min","Value":"Value","Max":"Max","ImageURL":"","Children":[
     {"id":1,"Text":"PC","Min":"","Value":"","Max":"","ImageURL":"images_icon/computer.png","Children":[
      {"id":2,"Text":"Carte mère","Min":"","Value":"","Max":"","ImageURL":"images_icon/mainboard.png","Children":[
       {"id":3,"Text":"Nuvoton NCT6687D","Min":"","Value":"","Max":"","ImageURL":"images_icon/chip.png","Children":[
        {"id":4,"Text":"Fans","Min":"","Value":"","Max":"","ImageURL":"images_icon/fan.png","Children":[
         {"id":5,"Text":"CPU Fan","Min":"900 RPM","Value":"1220 RPM","Max":"1500 RPM","SensorId":"/lpc/nct6687d/0/fan/0","Type":"Fan","ImageURL":"images/transparent.png","Children":[]}]}]}]},
      {"id":10,"Text":"Intel Core i7-14700KF","Min":"","Value":"","Max":"","ImageURL":"images_icon/cpu.png","Children":[
       {"id":11,"Text":"Temperatures","Min":"","Value":"","Max":"","ImageURL":"images_icon/temperature.png","Children":[
        {"id":12,"Text":"CPU Core #1","Min":"30,0 °C","Value":"44,0 °C","Max":"80,0 °C","SensorId":"/intelcpu/0/temperature/0","Type":"Temperature","ImageURL":"images/transparent.png","Children":[]},
        {"id":13,"Text":"CPU Package","Min":"31,0 °C","Value":"48,5 °C","Max":"82,0 °C","SensorId":"/intelcpu/0/temperature/20","Type":"Temperature","ImageURL":"images/transparent.png","Children":[]}]},
       {"id":14,"Text":"Powers","Min":"","Value":"","Max":"","ImageURL":"images_icon/power.png","Children":[
        {"id":15,"Text":"CPU Package","Min":"10,0 W","Value":"65,3 W","Max":"253,0 W","SensorId":"/intelcpu/0/power/0","Type":"Power","ImageURL":"images/transparent.png","Children":[]}]}]},
      {"id":20,"Text":"NVIDIA GeForce RTX","Min":"","Value":"","Max":"","ImageURL":"images_icon/nvidia.png","Children":[
       {"id":21,"Text":"Temperatures","Min":"","Value":"","Max":"","ImageURL":"images_icon/temperature.png","Children":[
        {"id":22,"Text":"GPU Core","Min":"40,0 °C","Value":"51,0 °C","Max":"62,0 °C","SensorId":"/gpu-nvidia/0/temperature/0","Type":"Temperature","ImageURL":"images/transparent.png","Children":[]}]},
       {"id":23,"Text":"Fans","Min":"","Value":"","Max":"","ImageURL":"images_icon/fan.png","Children":[
        {"id":24,"Text":"GPU Fan 1","Min":"0 RPM","Value":"1100 RPM","Max":"1800 RPM","SensorId":"/gpu-nvidia/0/fan/1","Type":"Fan","ImageURL":"images/transparent.png","Children":[]}]}]}]}]}
    """;

    [Fact]
    public void Temperature_du_processeur_boitier_plutot_qu_un_coeur()
    {
        var s = Sensors.ReadLibreJson(Recent);
        Assert.Equal(48.5, s.CpuTemp);
        Assert.Contains("CPU Package", s.CpuTempSource);
    }

    [Fact]
    public void Ventilateurs_du_boitier_sans_ceux_de_la_carte_graphique()
    {
        var s = Sensors.ReadLibreJson(Recent);
        var fan = Assert.Single(s.Fans);
        Assert.Equal("CPU Fan", fan.Name);
        Assert.Equal(1220, fan.Rpm);
    }

    [Fact]
    public void Puissance_du_processeur()
    {
        Assert.Equal(65.3, Sensors.ReadLibreJson(Recent).CpuPowerW);
    }

    [Fact]
    public void Ancien_format_sans_identifiant_ni_type()
    {
        // Versions plus anciennes : ni « SensorId » ni « Type » ; le matériel et le groupe se lisent dans l'arbre
        const string old = """
        {"Text":"Sensor","Value":"Value","ImageURL":"","Children":[{"Text":"PC","Value":"","ImageURL":"images_icon/computer.png","Children":[
          {"Text":"AMD Ryzen 7","Value":"","ImageURL":"images_icon/cpu.png","Children":[
            {"Text":"Temperatures","Value":"","ImageURL":"images_icon/temperature.png","Children":[
              {"Text":"Core (Tctl/Tdie)","Value":"61.2 °C","ImageURL":"images/transparent.png","Children":[]}]}]}]}]}
        """;
        Assert.Equal(61.2, Sensors.ReadLibreJson(old).CpuTemp);
    }

    [Fact]
    public void Reponse_vide_ou_sans_capteur_ne_plante_pas()
    {
        var s = Sensors.ReadLibreJson("""{"Text":"Sensor","Children":[]}""");
        Assert.Null(s.CpuTemp);
        Assert.Empty(s.Fans);
    }
}

public class StartupProgramsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Outil x\\outil.exe\" /min", "C:\\Program Files\\Outil x\\outil.exe")]
    [InlineData("C:\\Outils\\a.exe -silent", "C:\\Outils\\a.exe")]
    [InlineData("C:\\Program Files\\Mon Outil\\b.EXE", "C:\\Program Files\\Mon Outil\\b.EXE")]
    [InlineData("C:\\Scripts\\demarrage.cmd arg", "C:\\Scripts\\demarrage.cmd")]
    public void Chemin_du_programme_dans_la_ligne_de_commande(string command, string expected)
    {
        Assert.Equal(expected, StartupPrograms.ExePath(command));
    }

    [Fact]
    public void Etat_absent_ou_pair_active_impair_desactive()
    {
        Assert.True(StartupPrograms.IsEnabled(null));
        Assert.True(StartupPrograms.IsEnabled(new byte[] { 2, 0, 0, 0 }));
        Assert.True(StartupPrograms.IsEnabled(new byte[] { 6, 0, 0, 0 }));
        Assert.False(StartupPrograms.IsEnabled(new byte[] { 3, 0, 0, 0 }));
        Assert.False(StartupPrograms.IsEnabled(new byte[] { 7, 0, 0, 0 }));
    }

    [Fact]
    public void Valeur_ecrite_au_format_de_Windows_et_relue()
    {
        var off = StartupPrograms.StateBytes(false, new DateTime(2026, 11, 16, 12, 0, 0));
        Assert.Equal(12, off.Length);
        Assert.Equal(3, off[0]);
        Assert.NotEqual(0L, BitConverter.ToInt64(off, 4));
        Assert.False(StartupPrograms.IsEnabled(off));
        var on = StartupPrograms.StateBytes(true, DateTime.Now);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, on);
        Assert.True(StartupPrograms.IsEnabled(on));
    }
}

public class DiskUsageTests
{
    [Fact]
    public void Tailles_par_dossier_et_plus_gros_fichiers()
    {
        var root = Path.Combine(Path.GetTempPath(), "CentrePC-tests-espace-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Gros", "Sous"));
            Directory.CreateDirectory(Path.Combine(root, "Petit"));
            Directory.CreateDirectory(Path.Combine(root, "Vide"));
            File.WriteAllBytes(Path.Combine(root, "racine.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(root, "Gros", "a.bin"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(root, "Gros", "Sous", "b.bin"), new byte[20000]);
            File.WriteAllBytes(Path.Combine(root, "Petit", "c.bin"), new byte[300]);

            var r = DiskUsage.Scan(root, null, CancellationToken.None);

            Assert.Equal(26300, r.Root.Size);
            Assert.Equal(4, r.Root.Files);
            Assert.Equal(1000, r.Root.OwnSize);
            Assert.Equal(new[] { "Gros", "Petit", "Vide" }, r.Root.Children.Select(c => c.Name).ToArray());   // du plus gros au plus petit
            var gros = r.Root.Children[0];
            Assert.Equal(25000, gros.Size);
            Assert.Equal(5000, gros.OwnSize);
            Assert.Equal(20000, Assert.Single(gros.Children).Size);
            Assert.Same(r.Root, gros.Parent);
            Assert.Equal("b.bin", Path.GetFileName(r.BiggestFiles[0].Path));
            Assert.Equal(new long[] { 20000, 5000, 1000, 300 }, r.BiggestFiles.Select(f => f.Size).ToArray());
            Assert.False(r.Root.Partial);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Analyse_interrompue_proprement()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DiskUsage.Scan(Path.GetTempPath(), null, cts.Token));
    }

    [Theory]
    [InlineData(512L, "512 octets")]
    [InlineData(2048L, "2 Ko")]
    [InlineData(5L * 1024 * 1024, "5 Mo")]
    public void Tailles_lisibles(long bytes, string text) => Assert.Equal(text, DiskUsage.Size(bytes));
}

public class LauncherActionsTests
{
    [Theory]
    [InlineData("bilan", "checkup")]
    [InlineData("BILAN", "checkup")]
    [InlineData("capture", "capture")]
    [InlineData("screenshot", "capture")]
    [InlineData("mise a jour", "updates")]      // sans accent
    [InlineData("demarrage", "startup")]
    [InlineData("place", "space")]
    [InlineData("reglages", "settings")]
    public void Action_trouvee_par_son_nom_ou_un_mot_courant(string query, string id)
    {
        Assert.Equal(id, LauncherActions.Find(query).First().id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("firefox")]
    public void Rien_pour_une_recherche_vide_trop_courte_ou_sans_rapport(string query)
    {
        Assert.Empty(LauncherActions.Find(query));
    }

    [Fact]
    public void Trois_actions_au_plus_le_libelle_d_abord()
    {
        var found = LauncherActions.Find("ranger");
        Assert.True(found.Count is > 0 and <= 3);
        Assert.StartsWith("Ranger", found[0].label);
    }
}

public class SnapshotChoiceTests
{
    [Fact]
    public void Comparaison_de_deux_bilans_au_choix_quel_que_soit_l_ordre()
    {
        var old = new MachineSnapshot { Date = new DateTime(2026, 6, 1, 10, 0, 0), Machine = "PC", Level = "Express", Bios = "1.0", Windows = "W", Build = "1" };
        var now = new MachineSnapshot { Date = new DateTime(2026, 11, 1, 10, 0, 0), Machine = "PC", Level = "Standard", Bios = "1.2", Windows = "W", Build = "1" };
        old.Metrics["Processeur tous les cœurs GFLOPS"] = 800; now.Metrics["Processeur tous les cœurs GFLOPS"] = 500;
        foreach (var report in new[] { MachineSnapshot.Compare(old, now), MachineSnapshot.Compare(now, old) })
        {
            Assert.Contains("Comparaison", report.Title);
            Assert.InRange(report.Subtitle.IndexOf("niveau Express"), 0, report.Subtitle.IndexOf("niveau Standard"));   // le plus ancien d'abord
            Assert.Contains(report.Findings, f => f.Area == "Depuis le dernier bilan" && f.Client.Contains("BIOS changé"));
            Assert.Contains(report.Findings, f => f.Area == "Évolution");   // la baisse est lue de l'ancien vers le récent
            var html = report.Html("1.0");
            Assert.Contains("Comparaison de deux bilans", html);
            Assert.DoesNotContain("Bilan de la machine", html);
            Assert.DoesNotContain("class=\"verdict\"", html);
        }
    }

    [Fact]
    public void Machines_differentes_signalees()
    {
        var a = new MachineSnapshot { Date = DateTime.Now.AddDays(-1), Machine = "PC-A", Level = "Express" };
        var b = new MachineSnapshot { Date = DateTime.Now, Machine = "PC-B", Level = "Express" };
        Assert.Contains(MachineSnapshot.Compare(a, b).Findings, f => f.Area == "Machines");
    }

    [Fact]
    public void Coordonnees_du_technicien_dans_le_rapport()
    {
        var report = new CheckupReport { Level = "Express", Technician = "Alex", Contact = "Atelier Exemple · 01 00 00 00 00", End = DateTime.Now };
        var html = report.Html("1.0");
        Assert.Contains("Alex", html);
        Assert.Contains("Atelier Exemple", html);
    }
}

public class FeedbackTests
{
    [Fact]
    public void Chemin_personnel_et_nom_de_fichier_retires()
    {
        var t = Feedback.Clean(@"Accès refusé à 'C:\Users\Camille\Documents\Impôts 2025.pdf' pour Camille sur PC-SALON", @"C:\Users\Camille", "Camille", "PC-SALON");
        Assert.DoesNotContain("Camille", t, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Impôts", t);
        Assert.DoesNotContain("PC-SALON", t);
        Assert.Contains("%PROFIL%", t);
        Assert.StartsWith("Accès refusé", t);
    }

    [Theory]
    [InlineData(@"Lecture de C:\Users\Dominique\AppData\x.json", "Dominique")]      // autre compte du PC
    [InlineData(@"Fichier introuvable : D:\Clients\Durand\devis.xlsx", "Durand")]     // autre disque
    [InlineData(@"Partage \\nas\compta\bilan.xlsx inaccessible", "compta")]          // réseau
    [InlineData("Écrire à camille.durand@exemple.fr", "exemple.fr")]
    public void Autres_donnees_personnelles_retirees(string text, string secret)
    {
        Assert.DoesNotContain(secret, Feedback.Clean(text, @"C:\Users\Camille", "Camille", "PC-SALON"));
    }

    [Fact]
    public void Dossiers_de_Windows_et_des_programmes_gardes()
    {
        var t = Feedback.Clean(@"Échec de C:\Program Files\Outil\outil.exe", @"C:\Users\Camille", "Camille", "PC-SALON");
        Assert.Contains(@"C:\Program Files\Outil\outil.exe", t);
    }

    [Fact]
    public void Nom_d_utilisateur_court_ou_inclus_dans_un_mot_laisse_le_texte_lisible()
    {
        // « Max » ne doit pas abîmer « Maximum » ; un nom de deux lettres n'est pas remplacé partout
        Assert.Contains("Maximum", Feedback.Clean("Maximum atteint", @"C:\Users\Max", "Max", "PC-X"));
        Assert.Equal("Le format est invalide", Feedback.Clean("Le format est invalide", @"C:\Users\Al", "Al", "PC-X"));
    }

    [Fact]
    public void Texte_long_coupe()
    {
        Assert.True(Feedback.Clean(new string('a', 5000), "", "", "").Length < 700);
    }

    [Fact]
    public void Sans_accord_rien_n_est_enregistre_et_le_retrait_efface_tout()
    {
        Sandbox.Reset();
        Feedback.RecordError("Test", new InvalidOperationException("x"));
        Assert.False(File.Exists(Feedback.QueuePath));

        Feedback.SetConsent(true);
        Feedback.RecordError("Test", new InvalidOperationException("x"));
        Feedback.RecordError("Test", new InvalidOperationException("x"));   // même erreur le même jour : une seule fois
        Feedback.RecordClassification("Outil Exemple", "Utilitaires");
        var queue = Feedback.Queue();
        Assert.Equal(2, queue.Count);
        Assert.Contains(queue, e => e.Type == "erreur" && e.Donnees["type"] == "InvalidOperationException");
        Assert.Contains(queue, e => e.Type == "classement" && e.Donnees["categorie"] == "Utilitaires");
        Assert.All(queue, e => Assert.Equal(10, e.Jour.Length));   // le jour seulement, pas l'heure

        Feedback.SetConsent(false);
        Assert.False(File.Exists(Feedback.QueuePath));
        Assert.Null(Settings.Current.FeedbackInstallId);
    }

    [Fact]
    public void Identifiant_aleatoire_renouvele_chaque_mois()
    {
        Sandbox.Reset();
        var a = Feedback.InstallId(new DateTime(2026, 11, 3));
        Assert.Equal(a, Feedback.InstallId(new DateTime(2026, 11, 28)));
        Assert.NotEqual(a, Feedback.InstallId(new DateTime(2026, 12, 1)));
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public async Task Aucun_envoi_sans_accord_ni_point_d_entree()
    {
        Sandbox.Reset();
        var real = Feedback.Endpoint;
        try
        {
            // Sans accord : rien n'est tenté, même avec une adresse configurée (aucun accès réseau dans ce test)
            Feedback.Endpoint = "https://exemple.invalid/v1/retours";
            Assert.Equal(0, await Feedback.SendAsync(DateTime.Now));

            // Avec accord mais sans adresse : la file reste sur le PC
            Feedback.Endpoint = "";
            Feedback.SetConsent(true);
            Feedback.RecordClassification("Outil Exemple", "Utilitaires");
            Assert.False(Feedback.CanSend);
            Assert.Equal(0, await Feedback.SendAsync(DateTime.Now));
            Assert.Single(Feedback.Queue());

            // Adresse non chiffrée refusée
            Feedback.Endpoint = "http://exemple.invalid/v1/retours";
            Assert.False(Feedback.CanSend);
        }
        finally { Feedback.Endpoint = real; Feedback.SetConsent(false); }
    }

    [Fact]
    public void Adresse_du_service_officiel_en_https()
    {
        Assert.True(Feedback.CanSend);
        Assert.StartsWith("https://centre-pc-retours.centre-pc.workers.dev/", Feedback.Endpoint);
    }
}

public class RemoteCatalogTests
{
    private static (string key, Func<byte[], string> sign) NewKey()
    {
        var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(false);
        return (Convert.ToBase64String(p.Q.X.Concat(p.Q.Y).ToArray()),
                b => Convert.ToBase64String(ecdsa.SignData(b, System.Security.Cryptography.HashAlgorithmName.SHA256)));
    }

    private static byte[] Doc(int version, string app = "Outil Mystère", string category = "Utilitaires", string disabled = "") =>
        System.Text.Encoding.UTF8.GetBytes($"{{ \"Version\": {version}, \"Date\": \"2026-11-16\", \"Classement\": {{ \"{app}\": \"{category}\" }}, \"Desactive\": [{disabled}] }}");

    [Fact]
    public void Cles_publiques_inscrites_utilisables()
    {
        Assert.True(RemoteCatalog.Active);
        Assert.All(RemoteCatalog.PublicKeys, k =>
        {
            var xy = Convert.FromBase64String(k);
            Assert.Equal(64, xy.Length);
            // Un point qui n'est pas sur la courbe est refusé à la création : la clé serait inutilisable
            using var e = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
            {
                Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                Q = new System.Security.Cryptography.ECPoint { X = xy[..32], Y = xy[32..] }
            });
            Assert.NotNull(e);
        });
        Assert.Equal(RemoteCatalog.PublicKeys.Length, RemoteCatalog.PublicKeys.Distinct().Count());
    }

    [Fact]
    public void Premier_catalogue_reel_signe_par_PowerShell_accepte()
    {
        // Catalogue version 1 tel que signé le 16/11/2026 par Catalogue\signer.ps1 (PowerShell, magasin de clés de Windows) :
        // garantit que le format de signature produit par l'outil est bien celui que Centre PC vérifie.
        var content = System.Text.Encoding.UTF8.GetBytes("{\n  \"Version\": 1,\n  \"Date\": \"2026-11-16\",\n  \"Classement\": {\n    \"Adobe Redeem Launcher\": \"Photo et graphisme\"\n  },\n  \"Desactive\": []\n}\n");
        const string signature = "/ZZlFs1R45uGHt5f6NKWFDpoJPfKMoiKe622iKiA6XmjhpUHP+o71j4lLGce4mNm44CKx+04MTMfO0j9SOkqCQ==";
        var d = RemoteCatalog.Parse(content, signature, RemoteCatalog.PublicKeys);
        Assert.NotNull(d);
        Assert.Equal(1, d.Version);
        Assert.Equal("Photo et graphisme", d.Classement["Adobe Redeem Launcher"]);
        // Fins de ligne modifiées (ce que Git ferait sans .gitattributes) : refusé
        Assert.Null(RemoteCatalog.Parse(System.Text.Encoding.UTF8.GetBytes("{\r\n  \"Version\": 1,\r\n  \"Date\": \"2026-11-16\",\r\n  \"Classement\": {\r\n    \"Adobe Redeem Launcher\": \"Photo et graphisme\"\r\n  },\r\n  \"Desactive\": []\r\n}\r\n"), signature, RemoteCatalog.PublicKeys));
    }

    [Fact]
    public void Signature_valide_acceptee_fichier_modifie_ou_autre_cle_refuses()
    {
        var (key, sign) = NewKey();
        var (other, _) = NewKey();
        var doc = Doc(1);
        var sig = sign(doc);
        Assert.NotNull(RemoteCatalog.Parse(doc, sig, new[] { key }));
        Assert.NotNull(RemoteCatalog.Parse(doc, sig, new[] { other, key }));          // plusieurs clés : changement de clé possible
        Assert.Null(RemoteCatalog.Parse(Doc(1, category: "Jeux"), sig, new[] { key }));  // contenu modifié
        Assert.Null(RemoteCatalog.Parse(doc, sig, new[] { other }));
        Assert.Null(RemoteCatalog.Parse(doc, "pas une signature", new[] { key }));
        Assert.Null(RemoteCatalog.Parse(doc, sig, Array.Empty<string>()));
        Assert.Null(RemoteCatalog.Parse(new byte[600 * 1024], sig, new[] { key }));      // trop gros
    }

    [Fact]
    public void Classement_applique_apres_les_choix_de_l_utilisateur_et_sans_retour_en_arriere()
    {
        Sandbox.Reset();
        var (key, sign) = NewKey();
        var keys = new[] { key };
        try
        {
            var v2 = Doc(2);
            Assert.True(RemoteCatalog.Accept(v2, sign(v2), keys));
            Assert.Equal("Utilitaires", Settings.Classify("Outil Mystère", ""));

            // Le choix de l'utilisateur passe avant le catalogue
            Settings.SetOverride("Outil Mystère", "Jeux");
            Assert.Equal("Jeux", Settings.Classify("Outil Mystère", ""));
            Settings.SetOverride("Outil Mystère", null);

            // Un catalogue plus ancien, même bien signé, est refusé
            var v1 = Doc(1, category: "Jeux");
            Assert.False(RemoteCatalog.Accept(v1, sign(v1), keys));
            Assert.Equal("Utilitaires", Settings.Classify("Outil Mystère", ""));

            // La copie gardée sur le PC est relue, et ignorée si elle a été modifiée
            RemoteCatalog.LoadCached(keys);
            Assert.Equal(2, RemoteCatalog.Current.Version);
            File.WriteAllBytes(Path.Combine(Settings.DataDir, "catalogue.json"), Doc(3, category: "Jeux"));
            RemoteCatalog.LoadCached(keys);
            Assert.Null(RemoteCatalog.Current);
            Assert.Null(RemoteCatalog.Category(Settings.Normalize("Outil Mystère")));
        }
        finally { RemoteCatalog.LoadCached(Array.Empty<string>()); }
    }

    [Fact]
    public void Categorie_inconnue_ignoree_et_fonction_coupee_a_distance()
    {
        Sandbox.Reset();
        var (key, sign) = NewKey();
        try
        {
            var doc = Doc(5, category: "Catégorie qui n'existe pas", disabled: "\"outils-expert\"");
            Assert.True(RemoteCatalog.Accept(doc, sign(doc), new[] { key }));
            Assert.NotEqual("Catégorie qui n'existe pas", Settings.Classify("Outil Mystère", ""));
            Assert.True(RemoteCatalog.IsDisabled("outils-expert"));
            Assert.False(RemoteCatalog.IsDisabled("retours"));
            Assert.All(RemoteCatalog.Current.Desactive, d => Assert.Contains(d, RemoteCatalog.Switches.Keys));
        }
        finally { RemoteCatalog.LoadCached(Array.Empty<string>()); }
    }
}

public class MicrocodeTests
{
    private const string Raptor = "13th Gen Intel(R) Core(TM) i5-13600KF";

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13600KF", true)]
    [InlineData("Intel(R) Core(TM) i9-14900K", true)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", false)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", false)]
    public void Processeurs_concernes_par_le_defaut_Intel(string cpu, bool affected) => Assert.Equal(affected, CpuMicrocode.Affected(cpu));

    [Theory]
    [InlineData(0x129u, DeviceStatus.Problem)]   // avant le correctif de septembre 2024
    [InlineData(0x12Bu, DeviceStatus.Warning)]   // corrigé, mais pas la dernière variante
    [InlineData(0x12Fu, DeviceStatus.Ok)]
    [InlineData(0x130u, DeviceStatus.Ok)]
    [InlineData(0u, DeviceStatus.Info)]          // illisible : on le dit, sans alarmer
    public void Jugement_selon_la_revision(uint revision, DeviceStatus expected) => Assert.Equal(expected, CpuMicrocode.Assess(Raptor, revision, 0).level);

    [Fact]
    public void Processeur_non_concerne_jamais_en_alerte()
    {
        Assert.Equal(DeviceStatus.Ok, CpuMicrocode.Assess("AMD Ryzen 7 7800X3D", 0x10, 0).level);
        Assert.Equal(DeviceStatus.Ok, CpuMicrocode.Assess("AMD Ryzen 7 7800X3D", 0, 0).level);
    }
}

public class FeedbackUnknownAppsTests
{
    [Fact]
    public void Seuls_les_logiciels_installes_sont_signales_et_une_seule_fois()
    {
        Sandbox.Reset();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var apps = new[]
        {
            ("Outil Inconnu", "Éditeur Exemple", Path.Combine(pf, "Outil", "outil.exe"), false),
            ("Appli du Store", "", (string)null, true),
            ("Mon script perso", "", Path.Combine(profile, "Documents", "sauvegarde.bat"), false),      // script
            ("Jeu portable", "", Path.Combine(profile, "Jeux", "jeu.exe"), false),                       // dossier personnel
            ("Site de ma banque", "", "https://exemple.fr", false),                                     // raccourci vers un site
        };
        Feedback.RecordUnknownApps(apps);
        Assert.Empty(Feedback.Queue());   // sans accord : rien

        Feedback.SetConsent(true);
        try
        {
            Feedback.RecordUnknownApps(apps);
            Feedback.RecordUnknownApps(apps);   // déjà signalées : pas de doublon, même un autre jour
            var names = Feedback.Queue().Where(e => e.Type == "non-reconnue").Select(e => e.Donnees["application"]).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Appli du Store", "Outil Inconnu" }, names);
            Assert.Equal(2, Settings.Current.FeedbackReported.Count);
        }
        finally { Feedback.SetConsent(false); }
        Assert.Empty(Settings.Current.FeedbackReported);
    }
}
