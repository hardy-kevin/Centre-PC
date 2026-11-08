using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>
/// Centre PC tient dans un seul fichier CentrePC.exe : copié sur un autre PC et lancé,
/// il propose de s'installer (raccourcis + entrée dans « Applications installées »)
/// et sa désinstallation remet le bureau et les icônes comme avant.
/// </summary>
public static class SelfInstall
{
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\CentrePC");
    public static string InstalledExe => Path.Combine(InstallDir, "CentrePC.exe");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CentrePC";

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>À appeler au démarrage. Renvoie false si l'appli doit s'arrêter (installation faite, nouvelle copie lancée).</summary>
    public static bool HandleStartup(string[] args)
    {
        if (Native.IsPackaged) return true;   // version Store : installée et désinstallée par le Store
        if (IsInstalledCopy) { Register(); return true; }
        if (args.Contains("--portable") || args.Contains("--updated")) return true;

        var installed = File.Exists(InstalledExe);
        var msg = installed
            ? "Une version de Centre PC est déjà installée sur ce PC.\n\nLa remplacer par celle-ci ?\n\n(Non = lancer cette copie sans l'installer)"
            : "Installer Centre PC sur ce PC ?\n\nCentre PC sera ajouté au menu Démarrer, au bureau et aux « Applications installées » de Windows.\n\n(Non = lancer sans installer)";
        var r = MessageBox.Show(msg, "Centre PC", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.No) return true;

        try
        {
            // Ferme une éventuelle version installée en cours d'exécution
            foreach (var p in Process.GetProcessesByName("CentrePC").Where(p => p.Id != Environment.ProcessId))
                try { p.Kill(); p.WaitForExit(5000); } catch { }

            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath, InstalledExe, true);
            CreateShortcuts();
            Register();
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Installation impossible : " + ex.Message + "\n\nCentre PC va démarrer sans être installé.", "Centre PC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
    }

    private static void CreateShortcuts()
    {
        var wsh = (dynamic)Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            dynamic l = wsh.CreateShortcut(Path.Combine(dir, "Centre PC.lnk"));
            l.TargetPath = InstalledExe;
            l.WorkingDirectory = InstallDir;
            l.Description = "Applications, bureau et mises à jour";
            l.Save();
        }
    }

    /// <summary>Entrée « Centre PC » dans Paramètres > Applications installées (pour l'utilisateur courant).</summary>
    public static void Register()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            var version = typeof(SelfInstall).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            k.SetValue("DisplayName", "Centre PC");
            k.SetValue("DisplayVersion", version);
            k.SetValue("Publisher", "Centre PC");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch { }
        }
        catch { }
    }

    /// <summary>Désinstallation : remet le bureau, les icônes et les flèches comme avant, puis supprime l'appli.</summary>
    public static void Uninstall()
    {
        if (MessageBox.Show("Désinstaller Centre PC ?\n\nTes icônes rangées seront remises sur le bureau et les icônes d'origine restaurées.",
                "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        Settings.Load();
        try
        {
            // 1) Icônes d'origine et flèches de raccourci
            IconStyler.RevertAsync().Wait();
            if (IconStyler.ArrowsHidden()) IconStyler.SetArrows(false);
            // 2) Éléments rangés → retour sur le bureau
            var stored = AppScanner.ScanDesktop().Where(i => i.Location == ItemLocation.Stored).ToList();
            DesktopManager.Restore(stored);
            DesktopManager.RemoveEmptyThemeFolders();
        }
        catch { }

        // 3) Raccourcis et entrée « Applications installées »
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            try { File.Delete(Path.Combine(dir, "Centre PC.lnk")); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        bool keepData = MessageBox.Show("Garder tes réglages (thèmes personnalisés, favoris) au cas où tu réinstalles Centre PC ?",
            "Centre PC", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Sécurité : si des éléments n'ont pas pu revenir sur le bureau, on ne supprime pas le dossier de rangement
        bool storeNotEmpty = false;
        try { storeNotEmpty = Directory.Exists(Settings.StoreDir) && Directory.EnumerateFileSystemEntries(Settings.StoreDir).Any(); } catch { }
        if (!keepData && storeNotEmpty) keepData = true;

        MessageBox.Show("Centre PC a été désinstallé." + (storeNotEmpty
                ? $"\n\nCertains éléments n'ont pas pu revenir sur le bureau : ils sont conservés dans\n{Settings.StoreDir}"
                : ""), "Centre PC", MessageBoxButton.OK, MessageBoxImage.Information);

        // 4) Suppression des fichiers une fois l'appli fermée
        var cmd = $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"";
        if (!keepData) cmd += $" & rmdir /s /q \"{Settings.DataDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }
}
