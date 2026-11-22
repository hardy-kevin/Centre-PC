using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Bloc d'un document Markdown simple : titre, paragraphe, élément de liste ou tableau.</summary>
public sealed class MdBlock
{
    public MdKind Kind { get; init; }
    /// <summary>Niveau du titre (1 à 3).</summary>
    public int Level { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Tableau : ligne d'en-tête puis lignes de données.</summary>
    public List<string[]> Rows { get; init; }
}

public enum MdKind { Heading, Paragraph, Bullet, Table }

/// <summary>Morceau de texte avec sa mise en forme (gras, italique, code).</summary>
public readonly record struct MdSpan(string Text, bool Bold, bool Italic, bool Code);

/// <summary>
/// Lecture du Markdown des documents de Centre PC (conditions, confidentialité) pour les afficher dans l'appli :
/// titres, paragraphes, listes, tableaux, gras, italique, code et liens (le texte du lien est gardé). Volontairement
/// limité à ce que ces documents utilisent.
/// </summary>
public static class MarkdownLite
{
    public static List<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count > 0) blocks.Add(new MdBlock { Kind = MdKind.Paragraph, Text = string.Join(" ", paragraph) });
            paragraph.Clear();
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var t = line.TrimStart();
            if (t.Length == 0) { Flush(); continue; }
            var h = Regex.Match(t, @"^(#{1,6})\s+(.*)$");
            if (h.Success)
            {
                Flush();
                blocks.Add(new MdBlock { Kind = MdKind.Heading, Level = Math.Min(3, h.Groups[1].Length), Text = h.Groups[2].Value.Trim() });
                continue;
            }
            if (t.StartsWith("- ") || t.StartsWith("* ") && !t.StartsWith("**"))
            {
                Flush();
                var item = t[2..].Trim();
                // Suite de l'élément sur les lignes suivantes (indentées, sans nouveau tiret)
                while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && !lines[i + 1].TrimStart().StartsWith("- ")) item += " " + lines[++i].Trim();
                blocks.Add(new MdBlock { Kind = MdKind.Bullet, Text = item });
                continue;
            }
            if (t.StartsWith("|"))
            {
                Flush();
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith("|"); i++)
                {
                    var cells = SplitRow(lines[i].Trim());
                    if (cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))) continue;   // ligne de séparation |---|
                    rows.Add(cells);
                }
                i--;
                if (rows.Count > 0) blocks.Add(new MdBlock { Kind = MdKind.Table, Rows = rows });
                continue;
            }
            paragraph.Add(t);
        }
        Flush();
        return blocks;
    }

    private static string[] SplitRow(string row)
    {
        var inner = row.Trim().Trim('|');
        // « \| » dans une cellule reste un caractère
        return Regex.Split(inner, @"(?<!\\)\|").Select(c => c.Replace("\\|", "|").Trim()).ToArray();
    }

    /// <summary>Mise en forme d'une ligne : **gras**, *italique*, `code`, [lien](adresse) → texte du lien.</summary>
    public static List<MdSpan> Inline(string text)
    {
        var spans = new List<MdSpan>();
        text = Regex.Replace(text ?? "", @"\[([^\]]+)\]\([^)]*\)", "$1");
        var token = new Regex(@"\*\*(.+?)\*\*|`([^`]+)`|(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])");
        int pos = 0;
        foreach (Match m in token.Matches(text))
        {
            if (m.Index > pos) spans.Add(new MdSpan(text[pos..m.Index], false, false, false));
            if (m.Groups[1].Success)
            {
                // Le gras peut contenir de l'italique ou du code : on garde le texte, en gras
                foreach (var inner in Inline(m.Groups[1].Value)) spans.Add(inner with { Bold = true });
            }
            else if (m.Groups[2].Success) spans.Add(new MdSpan(m.Groups[2].Value, false, false, true));
            else spans.Add(new MdSpan(m.Groups[3].Value, false, true, false));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) spans.Add(new MdSpan(text[pos..], false, false, false));
        return spans;
    }

    /// <summary>Texte brut (sans mise en forme), pour la lecture à voix haute et les tests.</summary>
    public static string Plain(string text) => string.Concat(Inline(text).Select(s => s.Text));

    /// <summary>Document intégré à Centre PC (CONDITIONS.md, CONFIDENTIALITE.md), le même que celui du dépôt.</summary>
    public static string Embedded(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
