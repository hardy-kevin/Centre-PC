using System.IO;
using System.Text.RegularExpressions;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

// =====================================================================================
//  Conditions d'utilisation acceptées explicitement, raccourci du lanceur par défaut
// =====================================================================================
public class TermsTests
{
    [Fact]
    public void Premier_lancement_demande_les_conditions()
    {
        var cfg = new Config();
        Assert.True(Terms.NeedsAcceptance(cfg));
        Assert.False(Terms.IsUpdate(cfg));
    }

    [Fact]
    public void Conditions_acceptees_ne_sont_plus_demandees()
    {
        var cfg = new Config();
        Terms.Accept(cfg, new DateTime(2026, 11, 22, 9, 0, 0));
        Assert.False(Terms.NeedsAcceptance(cfg));
        Assert.Equal(Terms.Version, cfg.TermsAccepted);
        Assert.Equal(new DateTime(2026, 11, 22, 9, 0, 0), cfg.TermsAcceptedOn);
    }

    [Fact]
    public void Nouvelle_version_redemande_l_accord_comme_une_mise_a_jour()
    {
        var cfg = new Config { TermsAccepted = "2026-01-01" };
        Assert.True(Terms.NeedsAcceptance(cfg));
        Assert.True(Terms.IsUpdate(cfg));
        // Utilisateur d'une version où l'utilisation valait acceptation : c'est aussi une mise à jour
        Assert.True(Terms.IsUpdate(new Config { OnboardingDone = true }));
    }

    [Fact]
    public void La_version_correspond_a_l_en_tete_de_CONDITIONS_md()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CONDITIONS.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir.FullName, "CONDITIONS.md"));
        var m = Regex.Match(text, @"\*Version du (\d{1,2}) (\p{L}+) (\d{4})");
        Assert.True(m.Success, "En-tête « *Version du … » introuvable dans CONDITIONS.md");
        var months = new[] { "janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre" };
        var date = new DateTime(int.Parse(m.Groups[3].Value), Array.IndexOf(months, m.Groups[2].Value) + 1, int.Parse(m.Groups[1].Value));
        Assert.Equal(Terms.Version, date.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void Raccourci_par_defaut_Alt_Espace()
    {
        var hk = HotkeyCombo.Deserialize(new Config().QuickAccessHotkey);
        Assert.Equal(HotkeyCombo.Alt, hk.Mods);
        Assert.Equal(0x20, hk.Vk);
    }

    [Fact]
    public void Markdown_titres_listes_tableaux()
    {
        var md = "# Titre\n\n*Version du 1er.*\n\n## 1. Partie\n\n- un **point**\n  qui continue\n- deux\n\n| Quand | Quoi |\n|---|---|\n| Avant | `rien` |\n\nFin du\ntexte.";
        var b = MarkdownLite.Parse(md);
        Assert.Equal(new[] { MdKind.Heading, MdKind.Paragraph, MdKind.Heading, MdKind.Bullet, MdKind.Bullet, MdKind.Table, MdKind.Paragraph }, b.Select(x => x.Kind).ToArray());
        Assert.Equal(2, b[2].Level);
        Assert.Equal("un **point** qui continue", b[3].Text);
        Assert.Equal(2, b[5].Rows.Count);                       // en-tête + une ligne, sans la ligne |---|
        Assert.Equal("`rien`", b[5].Rows[1][1]);
        Assert.Equal("Fin du texte.", b[6].Text);
    }

    [Fact]
    public void Markdown_mise_en_forme_et_liens()
    {
        var spans = MarkdownLite.Inline("Voir **le détail** dans `config.json`, *ici* : [Confidentialité](CONFIDENTIALITE.md).");
        Assert.Contains(spans, s => s.Bold && s.Text == "le détail");
        Assert.Contains(spans, s => s.Code && s.Text == "config.json");
        Assert.Contains(spans, s => s.Italic && s.Text == "ici");
        Assert.Equal("Voir le détail dans config.json, ici : Confidentialité.", MarkdownLite.Plain("Voir **le détail** dans `config.json`, *ici* : [Confidentialité](CONFIDENTIALITE.md)."));
    }

    [Fact]
    public void Conditions_et_confidentialite_integrees_avec_section_6()
    {
        var conditions = MarkdownLite.Embedded("CONDITIONS.md");
        var privacy = MarkdownLite.Embedded("CONFIDENTIALITE.md");
        Assert.NotNull(conditions);
        Assert.NotNull(privacy);
        Assert.Contains(MarkdownLite.Parse(conditions), b => b.Kind == MdKind.Heading && b.Level == 2 && b.Text.StartsWith("6."));
        Assert.Contains(MarkdownLite.Parse(privacy), b => b.Kind == MdKind.Table);
        Assert.NotNull(MarkdownLite.Embedded("USAGE-PROFESSIONNEL.md"));
        // Les deux documents joints sont cités par un lien dans les conditions (c'est là qu'ils sont insérés)
        Assert.Contains("(CONFIDENTIALITE.md)", conditions);
        Assert.Contains("(USAGE-PROFESSIONNEL.md)", conditions);
    }
}
