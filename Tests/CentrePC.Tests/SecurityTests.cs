using System.IO;
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
}
