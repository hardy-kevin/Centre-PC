using System.IO;
using System.Linq;
using System.Text;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Sécurité : outils de Windows par chemin complet, commandes administrateur durcies, adresses web publiques
// =====================================================================================
public class SecurityTests
{
    [Theory]
    [InlineData("powershell.exe", @"WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("pnputil.exe", "pnputil.exe")]
    [InlineData("cmd.exe", "cmd.exe")]
    public void Outils_de_Windows_pris_dans_System32(string exe, string relative)
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, relative), Native.SystemTool(exe), ignoreCase: true);
    }

    [Fact]
    public void Outil_inconnu_garde_son_nom()
    {
        Assert.Equal("winget", Native.SystemTool("winget"));
    }

    [Fact]
    public void Commande_administrateur_sans_modules_ni_programmes_de_l_utilisateur()
    {
        var encoded = Updates.BuildBootstrap(@"C:\x\maj.ps1", @"C:\x\p.json", "Write-Output 1", "{}");
        var text = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        // Le durcissement passe avant toute lecture du script
        Assert.StartsWith("$env:PSModulePath=", text);
        Assert.Contains("GetEnvironmentVariable('Path','Machine')", text);
        Assert.True(text.IndexOf("$env:Path=", StringComparison.Ordinal) < text.IndexOf("ReadAllText", StringComparison.Ordinal));
        Assert.DoesNotContain("Documents", text);
    }

    [Theory]
    [InlineData("https://www.example.com/", true)]
    [InlineData("http://example.org/app", true)]
    [InlineData("https://93.184.216.34/", true)]
    [InlineData("http://localhost/", false)]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("http://192.168.1.1/admin", false)]
    [InlineData("http://10.0.0.2/", false)]
    [InlineData("http://172.20.0.1/", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("http://[::1]/", false)]
    [InlineData("http://imprimante/", false)]
    [InlineData("http://nas.local/", false)]
    [InlineData("https://www.example.com:8443/", false)]
    [InlineData("https://user:pass@www.example.com/", false)]
    public void Site_d_un_programme_seulement_s_il_est_public(string url, bool expected)
    {
        Assert.Equal(expected, OnlineClassifier.IsPublicWebHost(new Uri(url)));
    }

    [Fact]
    public void Mise_a_jour_installee_seulement_si_son_empreinte_est_signee()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        var pub = Convert.ToBase64String(p.Q.X.Concat(p.Q.Y).ToArray());
        var v = new Version(1, 2, 11);
        var sha = new string('a', 64);
        var sig = Convert.ToBase64String(key.SignData(AppUpdater.SignedText(v, sha), System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.True(AppUpdater.VerifyRelease(v, sha.ToUpperInvariant(), sig, new[] { pub }));
        // Signature d'une autre version ou d'un autre fichier : refusée (pas de réutilisation d'une ancienne signature)
        Assert.False(AppUpdater.VerifyRelease(new Version(1, 2, 12), sha, sig, new[] { pub }));
        Assert.False(AppUpdater.VerifyRelease(v, new string('b', 64), sig, new[] { pub }));
        Assert.False(AppUpdater.VerifyRelease(v, sha, null, new[] { pub }));
        Assert.False(AppUpdater.VerifyRelease(v, sha, sig, RemoteCatalog.PublicKeys));   // clé inconnue
    }

    [Fact]
    public void Fichier_venu_d_Internet_reconnu()
    {
        Sandbox.Reset();
        var f = Path.Combine(Sandbox.Root, "facture.docx");
        File.WriteAllText(f, "x");
        Assert.False(DocPreview.FromInternet(f));
        try { File.WriteAllText(f + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch { return; }   // système de fichiers sans flux secondaires : rien à vérifier
        Assert.True(DocPreview.FromInternet(f));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("192.168.0.10", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.1.2.3", false)]
    public void Adresses_publiques(string ip, bool expected) =>
        Assert.Equal(expected, OnlineClassifier.IsPublicAddress(System.Net.IPAddress.Parse(ip)));
}
