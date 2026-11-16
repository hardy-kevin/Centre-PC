using System.Diagnostics;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>Un programme lancé à l'ouverture de session de Windows.</summary>
public sealed class StartupEntry
{
    /// <summary>Nom affiché : description du programme si elle est lisible dans son fichier, sinon le nom de l'entrée.</summary>
    public string Name { get; set; }
    /// <summary>Nom de l'entrée dans Windows (valeur du registre ou fichier du dossier Démarrage).</summary>
    public string Key { get; set; }
    public string Publisher { get; set; }
    public string Command { get; set; }
    public string Exe { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Vrai : entrée commune à tous les utilisateurs (la modifier demande les droits administrateur).</summary>
    public bool Machine { get; set; }
    /// <summary>Clé « StartupApproved » où Windows note l'état activé / désactivé de cette entrée.</summary>
    public string ApprovedKey { get; set; }
    /// <summary>Le programme n'existe plus sur le disque (entrée laissée par une désinstallation).</summary>
    public bool Missing { get; set; }
}

/// <summary>
/// Programmes au démarrage de Windows : mêmes sources et même mécanisme que l'onglet « Démarrage » du Gestionnaire des tâches
/// (clés Run et dossiers Démarrage ; l'état activé / désactivé est noté par Windows dans « StartupApproved »). Désactiver ne
/// supprime rien : l'entrée reste en place et se réactive d'un clic, ici ou dans Windows.
/// </summary>
public static class StartupPrograms
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        void FromRun(RegistryKey hive, string path, string approved, bool machine)
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name) || k.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
                    list.Add(Describe(new StartupEntry { Key = name, Command = cmd, Exe = ExePath(cmd), Machine = machine, ApprovedKey = approved,
                                                         Enabled = IsEnabled(ReadState(hive, approved, name)) }));
                }
            }
            catch (Exception ex) { Log.Error("Démarrage : " + path, ex); }
        }
        void FromFolder(Environment.SpecialFolder folder, RegistryKey hive, bool machine)
        {
            try
            {
                var dir = Environment.GetFolderPath(folder);
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var file = Path.GetFileName(f);
                    if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(f) : f;
                    list.Add(Describe(new StartupEntry { Key = file, Name = Path.GetFileNameWithoutExtension(f), Command = f, Exe = target, Machine = machine,
                                                         ApprovedKey = "StartupFolder", Enabled = IsEnabled(ReadState(hive, "StartupFolder", file)) }));
                }
            }
            catch (Exception ex) { Log.Error("Démarrage : dossier", ex); }
        }
        FromRun(Registry.CurrentUser, Run, "Run", false);
        FromRun(Registry.LocalMachine, Run, "Run", true);
        FromRun(Registry.LocalMachine, Run32, "Run32", true);
        FromFolder(Environment.SpecialFolder.Startup, Registry.CurrentUser, false);
        FromFolder(Environment.SpecialFolder.CommonStartup, Registry.LocalMachine, true);
        return list.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Nom et éditeur lus dans le fichier du programme (rien n'est deviné : sans information, le nom de l'entrée est gardé).</summary>
    private static StartupEntry Describe(StartupEntry e)
    {
        e.Name ??= e.Key;
        try
        {
            if (!string.IsNullOrEmpty(e.Exe) && File.Exists(e.Exe))
            {
                var v = FileVersionInfo.GetVersionInfo(e.Exe);
                // Programmes « hôtes » de Windows (rundll32, cmd…) : leur description ne dit rien de ce qui est lancé
                bool host = Path.GetFileName(e.Exe).ToLowerInvariant() is "rundll32.exe" or "cmd.exe" or "powershell.exe" or "wscript.exe" or "cscript.exe" or "msiexec.exe" or "explorer.exe";
                if (!host && !string.IsNullOrWhiteSpace(v.FileDescription)) e.Name = v.FileDescription.Trim();
                if (!host) e.Publisher = v.CompanyName?.Trim();
            }
            else if (!string.IsNullOrEmpty(e.Exe) && Path.IsPathRooted(e.Exe)) e.Missing = true;
        }
        catch { }
        return e;
    }

    /// <summary>Chemin du programme dans une ligne de commande (« "C:\x y\a.exe" /min », « C:\x\a.exe -b », « %windir%\a.exe »).</summary>
    public static string ExePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return c[..(exe + 4)];
        int space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    /// <summary>État noté par Windows : absent ou premier octet pair = activé ; impair (3, 7…) = désactivé.</summary>
    public static bool IsEnabled(byte[] state) => state == null || state.Length == 0 || (state[0] & 1) == 0;

    /// <summary>Valeur « StartupApproved » à écrire : 02 = activé ; 03 suivi de la date = désactivé (format de Windows).</summary>
    public static byte[] StateBytes(bool enabled, DateTime now)
    {
        var b = new byte[12];
        b[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(now.ToFileTimeUtc()).CopyTo(b, 4);
        return b;
    }

    private static byte[] ReadState(RegistryKey hive, string approved, string name)
    {
        try { using var k = hive.OpenSubKey(Approved + approved); return k?.GetValue(name) as byte[]; } catch { return null; }
    }

    /// <summary>
    /// Active ou désactive une entrée de l'utilisateur (sans droits administrateur). Renvoie un message d'erreur, ou null.
    /// Les entrées communes à tous les utilisateurs se règlent dans les paramètres de Windows.
    /// </summary>
    public static string SetEnabled(StartupEntry e, bool enabled)
    {
        if (e.Machine) return "Cette entrée concerne tous les utilisateurs du PC : elle se règle dans les paramètres de Windows.";
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Approved + e.ApprovedKey);
            k.SetValue(e.Key, StateBytes(enabled, DateTime.Now), RegistryValueKind.Binary);
            e.Enabled = enabled;
            Log.Info($"Démarrage : « {e.Key} » {(enabled ? "réactivé" : "désactivé")}");
            return null;
        }
        catch (Exception ex) { Log.Error("Démarrage : modification", ex); return "Modification impossible : " + ex.Message; }
    }

    private static string ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            try { return (string)shell.CreateShortcut(lnk).TargetPath; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }
}
