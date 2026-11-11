using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>Un programme inscrit dans « Applications installées » de Windows (clé Uninstall du Registre).</summary>
public class ArpEntry
{
    public string KeyName { get; set; }
    public string RegistryPath { get; set; }
    public bool CurrentUser { get; set; }
    public RegistryView View { get; set; }
    public string DisplayName { get; set; }
    public string Publisher { get; set; }
    public string InstallLocation { get; set; }
    public string DisplayIcon { get; set; }
    public string UninstallString { get; set; }
    public bool SystemComponent { get; set; }
    public string Comments { get; set; }
    public string UrlInfoAbout { get; set; }
    public string HelpLink { get; set; }
    /// <summary>Dossiers qui identifient le programme (calculés une fois).</summary>
    internal string[] Dirs;
}

public enum UninstallKind { None, Program, Store, Steam, WindowsFeature }

/// <summary>Comment désinstaller une application donnée.</summary>
public class UninstallPlan
{
    public UninstallKind Kind { get; set; }
    public ArpEntry Entry { get; set; }
    /// <summary>Paquet du Microsoft Store (nom de famille, ex. Microsoft.WindowsMaps_8wekyb3d8bbwe).</summary>
    public string PackageFamily { get; set; }
    /// <summary>Fonctionnalité facultative de Windows (début du nom de la capacité, ex. App.StepsRecorder).</summary>
    public string Capability { get; set; }
    /// <summary>Pourquoi l'application ne peut pas être désinstallée ici (outil intégré à Windows…).</summary>
    public string Reason { get; set; }
    public bool CanUninstall => Kind != UninstallKind.None;
    public string Label => Kind switch
    {
        UninstallKind.Program => Entry?.DisplayName,
        UninstallKind.Store => "Application du Microsoft Store",
        UninstallKind.Steam => "Jeu Steam",
        UninstallKind.WindowsFeature => "Fonctionnalité facultative de Windows",
        _ => Reason
    };
}

/// <summary>
/// Désinstallation des applications : le programme de désinstallation officiel de l'éditeur est lancé (jamais de
/// suppression de fichiers par Centre PC). Applications du Store : retrait pour l'utilisateur courant.
/// En cas de doute, la page « Applications installées » de Windows est ouverte.
/// </summary>
public static class AppUninstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    // ------------------------------------------------------------------ lecture du Registre
    public static List<ArpEntry> ReadInstalled()
    {
        var list = new List<ArpEntry>();
        void Read(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(UninstallKey);
                if (k == null) return;
                foreach (var name in k.GetSubKeyNames())
                {
                    try
                    {
                        using var e = k.OpenSubKey(name);
                        var dn = e?.GetValue("DisplayName") as string;
                        var us = e?.GetValue("UninstallString") as string;
                        if (string.IsNullOrWhiteSpace(dn) || string.IsNullOrWhiteSpace(us)) continue;
                        if (e.GetValue("ParentKeyName") != null) continue;                         // mises à jour d'un autre programme
                        var rt = e.GetValue("ReleaseType") as string;
                        if (rt != null && Regex.IsMatch(rt, "update|hotfix", RegexOptions.IgnoreCase)) continue;
                        list.Add(new ArpEntry
                        {
                            KeyName = name, RegistryPath = UninstallKey + "\\" + name, CurrentUser = hive == RegistryHive.CurrentUser, View = view,
                            DisplayName = dn.Trim(), Publisher = e.GetValue("Publisher") as string,
                            InstallLocation = e.GetValue("InstallLocation") as string, DisplayIcon = e.GetValue("DisplayIcon") as string,
                            UninstallString = us.Trim(), SystemComponent = (e.GetValue("SystemComponent") as int?) == 1,
                            Comments = e.GetValue("Comments") as string, UrlInfoAbout = Url(e.GetValue("URLInfoAbout") as string),
                            HelpLink = Url(e.GetValue("HelpLink") as string)
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64);
        Read(RegistryHive.CurrentUser, RegistryView.Registry32);
        // Une même clé lue par les deux vues (HKCU n'est pas redirigé) : une seule fois
        return list.GroupBy(e => (e.CurrentUser, e.KeyName, e.DisplayName)).Select(g => g.First()).ToList();
    }

    private static string Url(string s) =>
        !string.IsNullOrWhiteSpace(s) && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? s.Trim() : null;

    private static readonly object CacheGate = new();
    private static List<ArpEntry> _cached;
    private static DateTime _cachedAt;

    /// <summary>Programmes installés, gardés 2 minutes (lus pour chaque application lors de l'inventaire).</summary>
    public static List<ArpEntry> Installed()
    {
        lock (CacheGate)
        {
            if (_cached == null || DateTime.Now - _cachedAt > TimeSpan.FromMinutes(2))
            {
                try { _cached = ReadInstalled(); } catch { _cached = new(); }
                _cachedAt = DateTime.Now;
            }
            return _cached;
        }
    }

    public static void ForgetInstalled() { lock (CacheGate) _cached = null; }

    public static bool StillInstalled(ArpEntry e)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(e.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, e.View);
            using var k = root.OpenSubKey(e.RegistryPath);
            return k != null;
        }
        catch { return true; }
    }

    // ------------------------------------------------------------------ correspondance application → programme installé
    private static string Dir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        // « "C:\…\app.exe",0 » → C:\…\app.exe
        if (p.StartsWith("\"")) { var q = p.IndexOf('"', 1); p = q > 0 ? p.Substring(1, q - 1) : p.Trim('"'); }
        else { var c = p.LastIndexOf(','); if (c > 2 && !p.Substring(c + 1).Contains('\\')) p = p.Substring(0, c); }
        p = Environment.ExpandEnvironmentVariables(p).TrimEnd('\\');
        if (p.Length < 4 || p[1] != ':') return null;
        var ext = Path.GetExtension(p).ToLowerInvariant();
        return ext is ".exe" or ".ico" or ".dll" or ".bat" or ".cmd" ? Path.GetDirectoryName(p) : p;
    }

    /// <summary>Dossiers trop larges pour identifier un programme (Program Files, AppData, Windows…).</summary>
    private static readonly Lazy<HashSet<string>> TooBroad = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86, Environment.SpecialFolder.UserProfile
        })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) set.Add(p.TrimEnd('\\'));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) set.Add(Path.Combine(local, "Programs"));
        return set;
    });

    private static bool IsUnder(string file, string dir) =>
        dir != null && file != null && file.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Simple(string s) => Regex.Replace(Settings.Normalize(s ?? "").ToLowerInvariant(), @"[^\p{L}\d]+", " ").Trim();

    /// <summary>
    /// Programme installé correspondant à une application : d'abord par dossier (le plus précis l'emporte),
    /// puis par nom (« Git Bash » → « Git »).
    /// </summary>
    internal static ArpEntry Match(IEnumerable<ArpEntry> installed, string exePath, string name, bool byNameToo = true)
    {
        var list = installed.ToList();
        var exe = AppScanner.NormalizePath(exePath);
        if (exe != null)
        {
            ArpEntry best = null; int bestLen = 0;
            foreach (var e in list)
            {
                if (e.Dirs == null)
                {
                    var uninstallerExe = UninstallExe(e.UninstallString);
                    e.Dirs = new[] { Dir(e.InstallLocation), Dir(e.DisplayIcon), uninstallerExe == null ? null : Dir(uninstallerExe) }
                        .Where(d => d != null && d.Length >= 6 && !TooBroad.Value.Contains(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }
                foreach (var d in e.Dirs)
                {
                    if (!IsUnder(exe, d)) continue;
                    int len = d.Length + (e.SystemComponent ? 0 : 1);
                    if (len > bestLen) { best = e; bestLen = len; }
                }
            }
            if (best != null) return best;
        }
        if (!byNameToo) return null;
        var n = Simple(name);
        if (n.Length < 2) return null;
        var byName = list.Where(e => !e.SystemComponent).Select(e => (e, s: Simple(e.DisplayName))).ToList();
        return byName.FirstOrDefault(x => x.s == n).e
            ?? byName.Where(x => x.s.Length >= 3 && (n.StartsWith(x.s + " ") || x.s.StartsWith(n + " ")))
                     .OrderByDescending(x => x.s.Length).Select(x => x.e).FirstOrDefault();
    }

    private static string UninstallExe(string cmd)
    {
        var (exe, _) = ParseCommand(cmd);
        if (exe == null || Path.GetFileName(exe).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)) return null;
        return exe;
    }

    /// <summary>Sépare le programme et ses arguments. MsiExec /I{…} (réparation) devient /X{…} (désinstallation).</summary>
    internal static (string exe, string args) ParseCommand(string cmd, string keyName = null)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return (null, null);
        cmd = cmd.Trim();
        string exe, args;
        if (cmd.StartsWith("\""))
        {
            int q = cmd.IndexOf('"', 1);
            if (q < 0) return (cmd.Trim('"'), "");
            exe = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim();
        }
        else
        {
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0) { exe = cmd.Substring(0, i + 4); args = cmd.Substring(i + 4).Trim(); }
            else { int sp = cmd.IndexOf(' '); exe = sp > 0 ? cmd.Substring(0, sp) : cmd; args = sp > 0 ? cmd.Substring(sp + 1).Trim() : ""; }
        }
        exe = Environment.ExpandEnvironmentVariables(exe);
        if (Path.GetFileNameWithoutExtension(exe).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var guid = Regex.Match(args, @"\{[0-9A-Fa-f\-]{36}\}").Value;
            if (guid.Length == 0 && keyName != null && Regex.IsMatch(keyName, @"^\{[0-9A-Fa-f\-]{36}\}$")) guid = keyName;
            if (guid.Length > 0) args = "/X" + guid;
        }
        return (exe, args);
    }

    // ------------------------------------------------------------------ plan
    public static UninstallPlan Plan(AppItem a, List<ArpEntry> installed)
    {
        if (a == null) return new UninstallPlan { Reason = "Application introuvable" };
        // Application hébergée : c'est l'émulateur ou le navigateur qui la désinstalle
        if (AppKinds.AndroidPackage(a.Target) != null) return new UninstallPlan { Reason = "Application Android : à désinstaller depuis l'émulateur" };
        if (AppKinds.IsWebApp(a.Target)) return new UninstallPlan { Reason = "Application web : à désinstaller depuis le navigateur" };
        var aumid = a.Aumid ?? "";
        // Application du Store : « Famille!Appli »
        if (aumid.Contains('!') && !aumid.StartsWith("{"))
        {
            var family = aumid.Substring(0, aumid.IndexOf('!'));
            if (family.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) && !family.Contains('_'))
                return new UninstallPlan { Reason = "Intégrée à Windows" };
            return new UninstallPlan { Kind = UninstallKind.Store, PackageFamily = family };
        }
        // Jeu Steam : « steam://rungameid/1091500 » → entrée « Steam App 1091500 »
        var steam = Regex.Match(a.Target ?? "", @"steam://rungameid/(\d+)", RegexOptions.IgnoreCase);
        if (steam.Success)
        {
            var e = installed.FirstOrDefault(x => x.KeyName.Equals("Steam App " + steam.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            return e != null ? new UninstallPlan { Kind = UninstallKind.Steam, Entry = e } : new UninstallPlan { Reason = "Jeu Steam : à désinstaller depuis Steam" };
        }
        var exe = a.FilePath != null && a.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Shortcuts.GetTargetPath(a.FilePath)
                : AppScanner.ResolveAppsFolderPath(aumid) ?? a.Target;
        var full = AppScanner.NormalizePath(exe);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full != null && !string.IsNullOrEmpty(win) && IsUnder(full, win))
        {
            // Certains outils de Windows sont des « fonctionnalités facultatives » : Windows permet de les retirer
            return OptionalFeatures.TryGetValue(Path.GetFileName(full), out var cap)
                ? new UninstallPlan { Kind = UninstallKind.WindowsFeature, Capability = cap }
                : new UninstallPlan { Reason = "Intégrée à Windows" };
        }
        if (aumid.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)) return new UninstallPlan { Reason = "Intégrée à Windows" };
        var match = Match(installed, full, a.Name);
        return match != null ? new UninstallPlan { Kind = UninstallKind.Program, Entry = match }
                             : new UninstallPlan { Reason = "Programme de désinstallation introuvable" };
    }

    /// <summary>
    /// Outils de Windows livrés comme fonctionnalités facultatives (Paramètres › Système › Fonctionnalités facultatives) :
    /// exécutable → début du nom de la capacité Windows.
    /// </summary>
    internal static readonly Dictionary<string, string> OptionalFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["psr.exe"] = "App.StepsRecorder",
        ["wordpad.exe"] = "Microsoft.Windows.WordPad",
        ["iexplore.exe"] = "Browser.InternetExplorer",
        ["wmplayer.exe"] = "Media.WindowsMediaPlayer",
        ["WFS.exe"] = "Print.Fax.Scan",
        ["mip.exe"] = "MathRecognizer",
    };

    // ------------------------------------------------------------------ exécution
    /// <summary>
    /// Lance la désinstallation puis attend qu'elle soit terminée (le programme disparaît de la liste de Windows).
    /// Renvoie null si l'application a été désinstallée, sinon un message pour l'utilisateur.
    /// </summary>
    public static async Task<string> RunAsync(UninstallPlan plan, CancellationToken ct = default)
    {
        if (Settings.ReadOnly) return "Centre PC est en lecture seule.";
        switch (plan.Kind)
        {
            case UninstallKind.Store:
            {
                var family = plan.PackageFamily.Replace("'", "''");
                var name = family.Contains('_') ? family.Substring(0, family.LastIndexOf('_')) : family;
                var ps = "$ProgressPreference = 'SilentlyContinue'; " + $"$p = Get-AppxPackage -Name '{name}' | Where-Object {{ $_.PackageFamilyName -eq '{family}' }}; " +
                         "if (-not $p) { exit 0 }; if ($p.NonRemovable) { exit 3 }; $p | Remove-AppxPackage -ErrorAction Stop";
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
                var (_, err, code) = await Updates.RunAsync("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded);
                Log.Info($"Désinstallation {plan.PackageFamily} : code {code} {err?.Trim()}");
                if (code == 3) return "Windows ne permet pas de désinstaller cette application.";
                return code == 0 ? null : "La désinstallation a échoué. Vous pouvez la faire depuis les Paramètres de Windows.";
            }
            case UninstallKind.Steam:
                try { Process.Start(new ProcessStartInfo("steam://uninstall/" + plan.Entry.KeyName.Substring("Steam App ".Length)) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("Désinstallation Steam", ex); return "Steam n'a pas pu être ouvert."; }
                return await WaitGoneAsync(plan.Entry, ct);
            case UninstallKind.Program:
            {
                var (exe, args) = ParseCommand(plan.Entry.UninstallString, plan.Entry.KeyName);
                if (exe == null) return "Programme de désinstallation introuvable.";
                Log.Info($"Désinstallation de {plan.Entry.DisplayName} : {exe} {args}");
                try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
                {
                    try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" }); }
                    catch (System.ComponentModel.Win32Exception ex2) when (ex2.NativeErrorCode == 1223) { return "Autorisation refusée."; }
                    catch (Exception ex2) { Log.Error("Désinstallation " + plan.Entry.DisplayName, ex2); return "Le programme de désinstallation n'a pas pu être lancé."; }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Autorisation refusée."; }
                catch (Exception ex) { Log.Error("Désinstallation " + plan.Entry.DisplayName, ex); return "Le programme de désinstallation n'a pas pu être lancé."; }
                return await WaitGoneAsync(plan.Entry, ct);
            }
            case UninstallKind.WindowsFeature:
            {
                // Retrait par Windows lui-même (Remove-WindowsCapability), avec l'autorisation administrateur
                var cap = plan.Capability.Replace("'", "''");
                var ps = "try { $c = Get-WindowsCapability -Online | Where-Object { $_.Name -like '" + cap + "*' -and $_.State -eq 'Installed' }; " +
                         "if (-not $c) { exit 0 }; $c | Remove-WindowsCapability -Online -ErrorAction Stop | Out-Null; exit 0 } catch { exit 1 }";
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
                try
                {
                    using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded)
                    { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                    await p.WaitForExitAsync(ct);
                    Log.Info($"Retrait de la fonctionnalité {plan.Capability} : code {p.ExitCode}");
                    return p.ExitCode == 0 ? null : "Windows n'a pas pu retirer cette fonctionnalité. Essayez depuis Paramètres › Système › Fonctionnalités facultatives.";
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Autorisation refusée."; }
                catch (Exception ex) { Log.Error("Retrait de " + plan.Capability, ex); return "Le retrait n'a pas pu être lancé."; }
            }
            default:
                return plan.Reason ?? "Cette application ne peut pas être désinstallée ici.";
        }
    }

    /// <summary>Le programme de désinstallation de l'éditeur pose ses questions : on attend qu'il ait terminé (15 min au plus).</summary>
    private static async Task<string> WaitGoneAsync(ArpEntry e, CancellationToken ct)
    {
        var until = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < until && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (TaskCanceledException) { break; }
            if (!StillInstalled(e)) return null;
        }
        return "La désinstallation n'est pas terminée (annulée ou toujours en cours).";
    }

    /// <summary>Page « Applications installées » des Paramètres de Windows.</summary>
    public static void OpenWindowsSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); } catch { }
    }
}
