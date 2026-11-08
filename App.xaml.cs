using System.Windows;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Instance élevée chargée seulement de déplacer des fichiers du bureau Public
        // Instances élevées (administrateur) lancées pour une action précise
        if (e.Args.Length >= 2 && e.Args[0].StartsWith("--"))
        {
            int code = 0;
            try
            {
                Settings.Load();
                switch (e.Args[0])
                {
                    case "--move": DesktopManager.RunElevatedMoves(e.Args[1]); break;
                    case "--icons": IconStyler.RunElevatedIconOps(e.Args[1]); break;
                    case "--arrows": IconStyler.RunElevatedArrows(e.Args[1], e.Args.Length > 2 ? e.Args[2] : ""); break;
                }
            }
            catch { code = 1; }
            Shutdown(code);
            return;
        }
        // Désinstallation (lancée depuis « Applications installées »)
        if (e.Args.Contains("--uninstall"))
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("CentrePC").Where(p => p.Id != Environment.ProcessId))
                try { p.Kill(); } catch { }
            SelfInstall.Uninstall();
            Shutdown();
            return;
        }

        // Copie lancée depuis une clé USB, Téléchargements… : propose de s'installer
        if (!SelfInstall.HandleStartup(e.Args)) { Shutdown(); return; }

        // Une seule instance : si Centre PC est déjà ouvert, on ramène sa fenêtre au premier plan
        bool first;
        if (e.Args.Contains("--updated"))
        {
            // Juste après une mise à jour : on attend que l'ancienne version se ferme
            _instance = new Mutex(false, @"Local\CentrePC_Instance");
            try { first = _instance.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { first = true; }
        }
        else _instance = new Mutex(true, @"Local\CentrePC_Instance", out first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(@"Local\CentrePC_Afficher").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CentrePC_Afficher");
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.Invoke(BringToFront);
        }) { IsBackground = true }.Start();

        AppUpdater.CleanupOld();
        DispatcherUnhandledException += OnError;
        base.OnStartup(e);
        Settings.Load();
        new MainWindow().Show();
    }

    private static Mutex _instance;

    /// <summary>Libère le verrou « une seule instance » avant de lancer la nouvelle version.</summary>
    public static void ReleaseInstanceLock()
    {
        try { _instance?.ReleaseMutex(); } catch { }
        try { _instance?.Dispose(); } catch { }
        _instance = null;
    }
    private static EventWaitHandle _showSignal;

    private void BringToFront()
    {
        var w = MainWindow;
        if (w == null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;    // contourne la protection de Windows contre le vol du premier plan
        w.Topmost = false;
        w.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    private void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(Settings.DataDir, "erreurs.log"), $"{DateTime.Now:G}\n{e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show("Oups, une erreur est survenue :\n\n" + e.Exception.Message, "Centre PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
