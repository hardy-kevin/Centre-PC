using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

/// <summary>Lecture du tableau affiché par « winget upgrade » (méthode de repli).</summary>
public class WingetParsingTests
{
    // Colonnes alignées comme dans la vraie sortie de winget (les noms et identifiants trop longs sont tronqués par « … »)
    private static string Row(string name, string id, string ver, string avail, string src = "winget") =>
        name.PadRight(34) + id.PadRight(34) + ver.PadRight(15) + avail.PadRight(15) + src;

    private static readonly string Sample = string.Join("\r\n",
        "   - ",
        Row("Nom", "ID", "Version", "Disponible", "Source"),
        new string('-', 104),
        Row("Ankama Launcher", "Ankama.AnkamaLauncher", "3.15.13", "3.15.14.20606"),
        Row("Microsoft Visual C++ 2013 Redis…", "Microsoft.VCRedist.2013.x64", "12.0.30501.0", "12.0.40664.0"),
        Row("IntelliJ IDEA", "JetBrains.IntelliJIDEA.Commun…", "2025.2", "2025.3"),
        "3 mises à niveau disponibles.",
        "");

    [Fact]
    public void Lit_les_identifiants_et_les_versions()
    {
        var list = Updates.ParseWinget(Sample);
        Assert.Contains(list, u => u.Id == "Ankama.AnkamaLauncher" && u.Available == "3.15.14.20606");
        Assert.Contains(list, u => u.Id == "Microsoft.VCRedist.2013.x64" && u.Version == "12.0.30501.0");
    }

    [Fact]
    public void Garde_les_identifiants_tronques_pour_la_recherche_partielle()
    {
        var list = Updates.ParseWinget(Sample);
        Assert.Contains(list, u => u.Id == "JetBrains.IntelliJIDEA.Commun…");
    }

    [Fact]
    public void Ignore_la_ligne_de_resume()
    {
        var list = Updates.ParseWinget(Sample);
        Assert.DoesNotContain(list, u => u.Name.StartsWith("3 mises"));
    }

    [Fact]
    public void Sortie_vide_ou_inattendue_ne_plante_pas()
    {
        Assert.Empty(Updates.ParseWinget(""));
        Assert.Empty(Updates.ParseWinget("Aucune mise à jour disponible."));
    }
}

/// <summary>Reconnaissance d'une même appli dans deux versions (remplacement des raccourcis après mise à jour).</summary>
public class ShortcutMatchingTests
{
    [Theory]
    [InlineData("IntelliJ IDEA Community Edition 2025.2", "IntelliJ IDEA Community Edition 2025.3")]
    [InlineData("PhpStorm 2023.2.3", "PhpStorm 2026.2.4")]
    [InlineData("LibreOffice 24.2", "LibreOffice 26.8")]
    [InlineData("Node.js 20 (x64)", "Node.js 22 (x64)")]
    public void Versions_differentes_meme_appli(string a, string b) =>
        Assert.Equal(ShortcutGuard.Norm(a), ShortcutGuard.Norm(b));

    [Theory]
    [InlineData("Telegram", "Discord")]
    [InlineData("Steam", "SteamVR")]
    public void Applis_differentes(string a, string b) =>
        Assert.NotEqual(ShortcutGuard.Norm(a), ShortcutGuard.Norm(b));
}

/// <summary>Rangement des documents.</summary>
public class DocumentLibraryTests
{
    [Theory]
    [InlineData("Jeux")]
    [InlineData("Banque et factures")]
    [InlineData("Captures d'écran")]
    public void Noms_valides(string name) => Assert.Null(DocumentLibrary.CheckName(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("quoi?")]
    [InlineData("fin.")]
    [InlineData("CON")]
    [InlineData("com1")]
    public void Noms_refuses(string name) => Assert.NotNull(DocumentLibrary.CheckName(name));

    [Theory]
    [InlineData(@"C:\x\photo.JPG", "images")]
    [InlineData(@"C:\x\son.m4a", "musique")]
    [InlineData(@"C:\x\film.mkv", "videos")]
    [InlineData(@"C:\x\Craft alchimiste.ods", "documents")]
    [InlineData(@"C:\x\Parchemin métier.md", "documents")]
    public void Dossier_conseille_selon_le_type(string path, string root) =>
        Assert.Equal(root, DocumentLibrary.SuggestRoot(path, isFolder: false));

    [Fact]
    public void Un_dossier_va_dans_Documents() => Assert.Equal("documents", DocumentLibrary.SuggestRoot(@"C:\x\Sauvegarde", isFolder: true));

    [Fact]
    public void IsUnder_ne_confond_pas_les_prefixes()
    {
        Assert.True(DocumentLibrary.IsUnder(@"C:\Users\K\Documents\Jeux", @"C:\Users\K\Documents"));
        Assert.False(DocumentLibrary.IsUnder(@"C:\Users\K\Documents2\Jeux", @"C:\Users\K\Documents"));
        Assert.False(DocumentLibrary.IsUnder(@"C:\Users\K\Documents", @"C:\Users\K\Documents"));
    }
}

/// <summary>Sécurité : emplacements acceptés par les actions lancées en administrateur.</summary>
public class ElevatedTests
{
    [Fact]
    public void Refuse_le_systeme()
    {
        Assert.False(Elevated.IsAllowedLocation(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.False(Elevated.IsAllowedLocation(@"C:\Program Files\App\app.exe"));
        Assert.False(Elevated.IsAllowedLocation(""));
    }

    [Fact]
    public void Refuse_les_detours_par_dossier_parent()
    {
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.False(Elevated.IsAllowedLocation(Path.Combine(desk, @"..\..\..\Windows\win.ini")));
    }

    [Fact]
    public void Accepte_le_bureau_public()
    {
        var pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (string.IsNullOrEmpty(pub)) return;
        Assert.True(Elevated.IsAllowedLocation(Path.Combine(pub, "Jeu.lnk")));
    }
}

/// <summary>Fichiers .ico : petites tailles en BMP (sinon carrés noirs sur le bureau).</summary>
public class IconFormatTests
{
    private static BitmapSource Blank(int size)
    {
        var b = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, new byte[size * size * 4], size * 4);
        b.Freeze();
        return b;
    }

    [Fact]
    public void Petites_tailles_sans_PNG()
    {
        var path = Path.Combine(Path.GetTempPath(), $"centrepc-test-{Guid.NewGuid():N}.ico");
        try
        {
            IconStyler.WriteIco(path, new[] { 48, 32, 16 }.Select(Blank).ToList());
            Assert.False(IconStyler.HasSmallPng(path));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(3, BitConverter.ToInt16(bytes, 4));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Grande_taille_en_PNG()
    {
        var path = Path.Combine(Path.GetTempPath(), $"centrepc-test-{Guid.NewGuid():N}.ico");
        try
        {
            IconStyler.WriteIco(path, new[] { Blank(256) });
            var bytes = File.ReadAllBytes(path);
            int offset = BitConverter.ToInt32(bytes, 6 + 12);
            Assert.Equal(0x89, bytes[offset]);   // signature PNG
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Le script de mise à jour doit être un PowerShell valide (sinon aucune mise à jour ne se lance).</summary>
public class ScriptTests
{
    [Fact]
    public void Maj_ps1_sans_erreur_de_syntaxe()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Scripts", "maj.ps1");
        Assert.True(File.Exists(script), script);
        var cmd = "$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('" + script.Replace("'", "''") +
                  "',[ref]$t,[ref]$e);$e | ForEach-Object { $_.Extent.StartLineNumber.ToString() + ': ' + $_.Message };exit $e.Count";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{cmd}\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "Erreurs dans maj.ps1 :\n" + output);
    }

    [Fact]
    public void Le_script_est_bien_embarque_dans_l_appli()
    {
        using var res = typeof(Updates).Assembly.GetManifestResourceStream("maj.ps1");
        Assert.NotNull(res);
    }
}
