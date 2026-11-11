using System.Windows;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

public partial class App : Application
{
    /// <summary>Lancé par un clic sur la notification : ouvrir directement la page Mises à jour.</summary>
    public static bool OpenUpdatesPage;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Vérification quotidienne (tâche planifiée) : rien à afficher sauf une notification
        if (e.Args.Contains("--check"))
        {
            // Centre PC déjà ouvert : sa pastille suffit, pas de notification en double
            if (!Mutex.TryOpenExisting(@"Local\CentrePC_Instance", out var running))
            {
                Settings.Load();
                Settings.ReadOnly = true;
                Task.Run(() => BackgroundCheck.RunAsync()).GetAwaiter().GetResult();
            }
            else running.Dispose();
            Shutdown();
            return;
        }
        OpenUpdatesPage = e.Args.Any(a => a.StartsWith(BackgroundCheck.Protocol + ":", StringComparison.OrdinalIgnoreCase));

        // Instance élevée chargée seulement de déplacer des fichiers du bureau Public
        // Instances élevées (administrateur) lancées pour une action précise
        if (e.Args.Length >= 2 && e.Args[0].StartsWith("--"))
        {
            int code = 0;
            try
            {
                Settings.Load();
                Settings.ReadOnly = true;   // l'instance principale reste seule à écrire la configuration
                switch (e.Args[0])
                {
                    case "--move": DesktopManager.RunElevatedMoves(e.Args[1]); break;
                    case "--icons": IconStyler.RunElevatedIconOps(e.Args[1]); break;
                    case "--arrows": IconStyler.RunElevatedArrows(e.Args[1], e.Args.Length > 2 ? e.Args[2] : ""); break;
                }
            }
            catch (Exception ex) { code = 1; Log.Error("Action administrateur " + e.Args[0], ex); }
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
            try { EventWaitHandle.OpenExisting(OpenUpdatesPage ? @"Local\CentrePC_MisesAJour" : @"Local\CentrePC_Afficher").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CentrePC_Afficher");
        _updatesSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CentrePC_MisesAJour");
        new Thread(() =>
        {
            var handles = new WaitHandle[] { _showSignal, _updatesSignal };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                Dispatcher.Invoke(() =>
                {
                    BringToFront();
                    if (i == 1) (MainWindow as global::CentrePC.MainWindow)?.ShowUpdatesPage();
                });
            }
        }) { IsBackground = true }.Start();

        if (!Native.IsPackaged) AppUpdater.CleanupOld();
        DispatcherUnhandledException += OnError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Crash("Erreur non gérée", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Tâche en arrière-plan", a.Exception); a.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Settings.Flush();
        base.OnStartup(e);
        Settings.Load();
        var main = new MainWindow();
        // Ouverture de session : Centre PC démarre discrètement, seulement l'icône près de l'horloge
        if (e.Args.Contains("--tray") && Settings.Current.QuickAccess) main.StartInBackground();
        else main.Show();
    }

    private static Mutex _instance;

    /// <summary>Libère le verrou « une seule instance » avant de lancer la nouvelle version.</summary>
    public static void ReleaseInstanceLock()
    {
        try { _instance?.ReleaseMutex(); } catch { }
        try { _instance?.Dispose(); } catch { }
        _instance = null;
    }
    private static EventWaitHandle _showSignal, _updatesSignal;

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
        Settings.Flush();
        try { _instance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    private void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Crash("Erreur dans l'interface", e.Exception);
        MessageBox.Show("Une erreur est survenue :\n\n" + e.Exception.Message, "Centre PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
