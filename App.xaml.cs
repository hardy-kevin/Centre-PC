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
        // Ouverture par un lien « centrepc: » (notification, mais aussi n'importe quelle page web) : seul ce lien est pris en
        // compte. Une page malveillante qui glisserait d'autres arguments (« --uninstall »…) dans le lien est ignorée.
        var args = e.Args.Any(a => a.StartsWith(BackgroundCheck.Protocol + ":", StringComparison.OrdinalIgnoreCase))
            ? e.Args.Where(a => a.StartsWith(BackgroundCheck.Protocol + ":", StringComparison.OrdinalIgnoreCase)).Take(1).ToArray()
            : e.Args;
        // Garde de la lecture à la souris du Narrateur : attend la fin de Centre PC, même forcée, et remet l'option
        if (args.Length >= 2 && args[0] == NarratorMouse.GuardArg)
        {
            NarratorMouse.RunGuard(args[1]);
            Shutdown();
            return;
        }
        // Vérification quotidienne (tâche planifiée) : rien à afficher sauf une notification
        if (args.Contains("--check"))
        {
            // Centre PC déjà ouvert : sa pastille suffit, pas de notification en double
            if (!Mutex.TryOpenExisting(@"Local\CentrePC_Instance", out var running))
            {
                // Lecture seule avant le chargement : rien n'est écrit (un premier lancement reste un premier lancement,
                // et les données d'une version précédente sont déplacées à la prochaine ouverture de Centre PC)
                Settings.ReadOnly = true;
                Settings.Load();
                Task.Run(() => BackgroundCheck.RunAsync()).GetAwaiter().GetResult();
            }
            else running.Dispose();
            Shutdown();
            return;
        }
        OpenUpdatesPage = args.Any(a => a.StartsWith(BackgroundCheck.Protocol + ":", StringComparison.OrdinalIgnoreCase));

        // Désinstallation (lancée depuis « Applications installées »)
        if (args.Contains("--uninstall"))
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("CentrePC").Where(p => p.Id != Environment.ProcessId))
                try { p.Kill(); } catch { }
            SelfInstall.Uninstall();
            Shutdown();
            return;
        }

        // Copie lancée depuis une clé USB, Téléchargements… : propose de s'installer
        if (!SelfInstall.HandleStartup(args)) { Shutdown(); return; }

        // Une seule instance : si Centre PC est déjà ouvert, on ramène sa fenêtre au premier plan
        bool first;
        if (args.Contains("--updated"))
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
        Accessibility.Install();   // noms lisibles par les lecteurs d'écran, pour toute l'application
        Settings.Load();
        // Installation précédente (autre version, réinstallation) : raccourcis rangés, dossiers et choix repris
        PreviousInstall.RunAtStartup();
        RemoteCatalog.LoadCached();   // copie locale du catalogue signé, revérifiée à chaque lancement
        var main = new MainWindow();
        // Ouverture de session (clé « Run » avec --tray, ou tâche de démarrage de la version Store) : Centre PC démarre
        // discrètement, seulement l'icône près de l'horloge. Conditions d'utilisation à accepter : la fenêtre s'ouvre.
        bool atLogon = args.Contains("--tray") || PackagedStartup.LaunchedAtLogon();
        if (atLogon && Settings.Current.QuickAccess && !Terms.NeedsAcceptance(Settings.Current)) main.StartInBackground();
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
