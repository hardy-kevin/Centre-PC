using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>
/// Emplacement des données de Centre PC, le même pour la version GitHub (.exe) et la version Microsoft Store :
///
///   Documents\Centre PC\                 dossier commun (bilans, mode d'emploi)
///   Documents\Centre PC\&lt;nom du PC&gt;\    réglages, icônes rangées, historique, journaux… de CE PC
///   %LOCALAPPDATA%\CentrePC\Cache\       cache jetable (icônes affichées, installateurs téléchargés)
///
/// Pourquoi Documents : Windows isole AppData pour les applications du Store (copie privée, invisible pour l'Explorateur
/// et supprimée à la désinstallation) : les raccourcis rangés y seraient perdus. Documents reste visible et survit à
/// toute désinstallation.
///
/// Pourquoi un sous-dossier par PC : Documents est souvent sauvegardé par OneDrive. Avec un seul dossier, deux PC du
/// même compte partageraient leurs réglages et leurs raccourcis rangés (raccourcis d'un PC pris pour des applications
/// désinstallées sur l'autre, réglages écrasés…). Chaque PC ne lit et n'écrit que le sien ; la synchronisation devient
/// une simple sauvegarde. Le dossier est reconnu par un identifiant de la machine (fichier pc.json) : il est retrouvé
/// même si le PC est renommé.
/// </summary>
public static class DataLocation
{
    /// <summary>Documents\Centre PC (commun à tous les PC du compte).</summary>
    public static string Root => Path.Combine(Paths.Get(Environment.SpecialFolder.MyDocuments), "Centre PC");

    /// <summary>Cache jetable, propre au PC, jamais synchronisé (et vidé par Windows à la désinstallation de la version Store).</summary>
    public static string CacheDir => Path.Combine(Paths.Get(Environment.SpecialFolder.LocalApplicationData), "CentrePC", "Cache");

    public const string MarkerFile = "pc.json";

    private class Marker
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
    }

    /// <summary>Identifiant de ce PC (empreinte de l'identifiant Windows de la machine, jamais l'identifiant lui-même).</summary>
    public static string MachineId()
    {
        string raw = null;
        if (!Paths.IsTest)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                raw = k?.GetValue("MachineGuid") as string;
            }
            catch { }
        }
        raw ??= "nom:" + Environment.MachineName;   // tests, ou registre illisible : le nom du PC
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("CentrePC|" + raw.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash)[..24];
    }

    /// <summary>Le dossier est-il le dossier de données d'un PC (contient pc.json) ?</summary>
    public static bool IsPcFolder(string dir)
    {
        try { return File.Exists(Path.Combine(dir, MarkerFile)); } catch { return false; }
    }

    /// <summary>
    /// Dossier de données de ce PC : celui qui porte son identifiant (même s'il a été renommé), sinon un nouveau dossier
    /// au nom du PC (« DESKTOP-ABC123 », suivi de « (2) » si un autre PC du même nom en a déjà un).
    /// </summary>
    public static string Resolve()
    {
        var root = Root;
        var id = MachineId();
        try
        {
            if (Directory.Exists(root))
                foreach (var d in Directory.GetDirectories(root))
                    if (ReadId(d) == id) return d;
        }
        catch { }
        var name = SafeName(Environment.MachineName);
        var dir = Path.Combine(root, name);
        for (int i = 2; (IsPcFolder(dir) && ReadId(dir) != id) || File.Exists(dir); i++)
            dir = Path.Combine(root, $"{name} ({i})");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFile), JsonSerializer.Serialize(new Marker { Id = id, Name = Environment.MachineName }));
            WriteReadme(root);
        }
        catch { }
        return dir;
    }

    private static string ReadId(string dir)
    {
        try
        {
            var f = Path.Combine(dir, MarkerFile);
            return File.Exists(f) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(f))?.Id : null;
        }
        catch { return null; }
    }

    private static string SafeName(string s)
    {
        var name = new string((s ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "Ce PC" : name;
    }

    /// <summary>Mode d'emploi du dossier, pour qui l'ouvre sans connaître Centre PC (ou après sa désinstallation).</summary>
    private static void WriteReadme(string root)
    {
        var f = Path.Combine(root, "Lisez-moi.txt");
        if (File.Exists(f)) return;
        File.WriteAllText(f,
            "Dossier de Centre PC\r\n" +
            "====================\r\n\r\n" +
            "Chaque sous-dossier porte le nom d'un PC et contient les réglages de Centre PC pour ce PC.\r\n\r\n" +
            "Le dossier « Bureau rangé » d'un PC contient les icônes que Centre PC a retirées du bureau.\r\n" +
            "Si Centre PC n'est plus installé, vous pouvez les remettre sur le bureau en les faisant glisser.\r\n\r\n" +
            "Le dossier « Bilans » contient les rapports de bilan du PC.\r\n\r\n" +
            "Vous pouvez supprimer le dossier d'un PC que vous n'utilisez plus.\r\n",
            new UTF8Encoding(true));
    }
}
