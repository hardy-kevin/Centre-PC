using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>
/// Nature d'une entrée, d'après son nom et sa cible (sans rien lancer) :
/// - raccourci renommé par Windows (« start_server - Raccourci ») ;
/// - application hébergée par un autre programme : appli Android lancée par un émulateur (BlueStacks, LDPlayer, MuMu,
///   Google Play Jeux, sous-système Android), application web installée par Chrome ou Edge. Elle n'hérite jamais
///   de l'éditeur ou du dossier de son hôte (PSApp n'est pas BlueStacks) ;
/// - script personnel (.bat, .ps1, .py, .ahk…) hors des dossiers de programmes ;
/// - composant intégré à Windows.
/// </summary>
public static class AppKinds
{
    public const string ScriptsCategory = "Scripts";

    private static readonly Regex ShortcutSuffix = new(
        @"\s+-\s+(raccourci|shortcut|verknüpfung|acceso directo|collegamento|atalho|snelkoppeling|skrót)(\s*\(\d+\))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>« start_server - Raccourci » → « start_server » (suffixe ajouté par Windows, toutes langues).</summary>
    public static string StripShortcutSuffix(string name) =>
        string.IsNullOrEmpty(name) ? name : ShortcutSuffix.Replace(name, "").Trim();

    // --package "com.x.y" (BlueStacks, LDPlayer), -package:com.x.y (Nox), wsa://com.x.y (Android sous Windows),
    // googleplaygames://launch/?id=com.x.y (Google Play Jeux), -p com.x.y (MuMu)
    private static readonly Regex AndroidPkg = new(
        @"(?:--?package[\s:=]+|-pkg\s+|wsa://|launch/\?id=|\s-p\s+)""?([a-z][a-z0-9_]*(?:\.[a-z0-9_]+){1,})""?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WebApp = new(@"--app-id=|_proxy\.exe""?\s.*--app|--app=https?://", RegexOptions.IgnoreCase);

    /// <summary>Identifiant Android (com.scee.psxandroid) d'un raccourci d'émulateur, ou null.</summary>
    public static string AndroidPackage(string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var m = AndroidPkg.Match(target);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static bool IsWebApp(string target) => !string.IsNullOrEmpty(target) && WebApp.IsMatch(target);

    public static bool IsHosted(string target) => AndroidPackage(target) != null || IsWebApp(target);

    /// <summary>Cible utilisée pour le classement : l'hôte (émulateur, navigateur) est ignoré.</summary>
    public static string ClassifyTarget(string target)
    {
        var pkg = AndroidPackage(target);
        if (pkg != null) return "android " + pkg;
        return IsWebApp(target) ? "application web" : target;
    }

    private static readonly Regex ScriptExt = new(
        @"([^""\s][^""]*?\.(bat|cmd|ps1|vbs|vbe|jse?|wsf|py|pyw|ahk|au3|sh))(""|\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Script lancé par le raccourci (directement ou via cmd, PowerShell, Python…), ou null.</summary>
    public static string ScriptPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var m = ScriptExt.Match(target);
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
    }

    private static readonly Regex PromptEnvironment = new(@"\bcmd(\.exe)?""?\s+/k\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Script lancé tel quel (« start_server.bat », « sauvegarde.ps1 ») : catégorie Scripts, même dans un dossier de jeu.
    /// Ne sont pas des scripts : les invites de commandes préparées par un programme (« cmd /k nodevars.bat » de Node.js,
    /// « Developer Command Prompt ») et les scripts de Windows.
    /// </summary>
    public static bool IsPersonalScript(string target)
    {
        var script = ScriptPath(target);
        if (script == null || PromptEnvironment.IsMatch(target)) return false;
        var full = AppScanner.NormalizePath(script) ?? script;
        bool Under(Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f);
            return !string.IsNullOrEmpty(p) && full.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        if (Under(Environment.SpecialFolder.Windows)) return false;
        // Script livré avec un programme installé (« runpsql.bat » de PostgreSQL) : fait partie du programme.
        // Exception : les bibliothèques de jeux (steamapps…), où l'utilisateur ajoute ses propres scripts de serveur
        if ((Under(Environment.SpecialFolder.ProgramFiles) || Under(Environment.SpecialFolder.ProgramFilesX86)) && !GameLibrary.IsMatch(full)) return false;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local) && full.StartsWith(Path.Combine(local, "Programs") + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static readonly Regex GameLibrary = new(@"\\(steamapps|SteamLibrary|XboxGames|Epic Games|GOG Games|Riot Games|Ubisoft Game Launcher\\games|EA Games)\\",
        RegexOptions.IgnoreCase);

    private static readonly Regex SystemRoots = new(
        @"[A-Za-z]:\\(Program Files( \(x86\))?|ProgramData|Users\\[^\\]+\\AppData\\(Local|Roaming|LocalLow)(\\Programs)?|Users\\[^\\]+|Windows(\\(System32|SysWOW64))?)\\",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Cible débarrassée des dossiers communs à tous les programmes (« C:\Program Files (x86)\ ») : leurs mots
    /// (« Files », « Program », « Local ») ne doivent pas servir au classement.
    /// </summary>
    public static string MatchableTarget(string target) => string.IsNullOrEmpty(target) ? target : SystemRoots.Replace(target, " ");

    /// <summary>« ASADedicatedManager » → « ASA Dedicated Manager », « SenaDeviceManager » → « Sena Device Manager ».</summary>
    public static string SplitWords(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var s = Regex.Replace(name, @"(?<=\p{Ll})(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})|(?<=\p{L})(?=\d)|[_]+", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Composant de Windows (dossier Windows, applications Microsoft du Store, raccourcis système).</summary>
    public static bool IsWindowsBuiltIn(AppItem a)
    {
        var aumid = a.Aumid ?? "";
        if (aumid.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase) || aumid.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
            || aumid.StartsWith("windows.", StringComparison.OrdinalIgnoreCase) || aumid.Contains("_8wekyb3d8bbwe!", StringComparison.OrdinalIgnoreCase)
            || aumid.Contains("_cw5n1h2txyewy!", StringComparison.OrdinalIgnoreCase))
            return true;
        var path = AppScanner.ResolveAppsFolderPath(aumid);
        if (path == null && a.FilePath != null && a.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) path = Shortcuts.GetTargetPath(a.FilePath);
        if (path == null && !string.IsNullOrEmpty(a.Target))
        {
            // Cible « C:\…\programme.exe arguments » : seul le programme compte
            var t = a.Target.Trim().Trim('"');
            int i = t.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = i > 0 ? t.Substring(0, i + 4) : null;
        }
        var full = AppScanner.NormalizePath(path);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return full != null && !string.IsNullOrEmpty(win) && full.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
