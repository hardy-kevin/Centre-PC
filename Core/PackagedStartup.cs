using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace CentrePC.Core;

/// <summary>
/// Démarrage avec Windows de la version Microsoft Store : tâche de démarrage déclarée dans le manifeste
/// (Outils\store\AppxManifest.xml, TaskId « CentrePCStartup »). La version installée par le .exe utilise la clé
/// « Run » du registre (voir MainWindow.ApplyStartWithWindows). Tout est sans effet hors paquet.
/// </summary>
public static class PackagedStartup
{
    public const string TaskId = "CentrePCStartup";

    /// <summary>Lancé par la tâche de démarrage (ouverture de session) : Centre PC démarre sans fenêtre.</summary>
    public static bool LaunchedAtLogon()
    {
        if (!Native.IsPackaged) return false;
        try { return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask; }
        catch (Exception ex) { Log.Info("Démarrage : type d'activation illisible (" + ex.Message + ")"); return false; }
    }

    /// <summary>
    /// Active ou coupe la tâche. Renvoie un message à montrer si Windows refuse : l'utilisateur l'a désactivée dans
    /// Paramètres › Applications › Démarrage (seul lui peut la rallumer) ou une stratégie l'interdit ; null sinon.
    /// </summary>
    public static async Task<string> SetAsync(bool enabled)
    {
        if (!Native.IsPackaged) return null;
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (!enabled)
            {
                if (task.State is StartupTaskState.Enabled) task.Disable();
                return null;
            }
            var state = task.State;
            if (state is StartupTaskState.Disabled) state = await task.RequestEnableAsync();
            return state switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
                StartupTaskState.DisabledByUser => "Le démarrage de Centre PC a été désactivé dans les paramètres de Windows : "
                                                   + "rallumez-le dans Paramètres › Applications › Démarrage.",
                StartupTaskState.DisabledByPolicy => "Le démarrage automatique est interdit sur ce PC par une stratégie de l'organisation.",
                _ => null
            };
        }
        catch (Exception ex)
        {
            Log.Error("Démarrage avec Windows (Store)", ex);
            return "Le démarrage avec Windows n'a pas pu être réglé.";
        }
    }
}
