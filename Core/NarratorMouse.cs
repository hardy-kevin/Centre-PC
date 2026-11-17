using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>
/// La lecture au survol de Centre PC (élément entier, contexte, interruption propre) passe avant celle du Narrateur :
/// tant que la souris survole la fenêtre de Centre PC (au premier plan), l'option du Narrateur « Lire et interagir avec
/// l'écran à l'aide de la souris » est suspendue, puis remise dès que la souris en sort, que l'on passe à une autre
/// fenêtre ou que Centre PC se ferme. Rien n'est touché si l'option était désactivée, ou si la lecture au survol de
/// Centre PC est décochée.
///
/// L'option est toujours remise, même si Centre PC s'arrête brutalement :
/// - plantage, fermeture de session : remise aussitôt ;
/// - arrêt forcé (gestionnaire des tâches) : un petit processus de garde, lancé à la première suspension, attend la fin
///   de Centre PC et la remet ;
/// - coupure de courant, écran bleu : une entrée « RunOnce » de Windows la remet à l'ouverture de session suivante.
/// Cette entrée sert aussi de repère : elle n'existe que pendant la suspension, donc seulement si l'option était activée.
/// </summary>
public static class NarratorMouse
{
    private const string Key = @"Software\Microsoft\Narrator";
    private const string Value = "InteractionMouse";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Marker = "CentrePC-LectureSourisNarrateur";
    public const string GuardArg = "--garde-narrateur";
    private static bool _guardStarted;

    private static bool Suspended
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunOnceKey); return k?.GetValue(Marker) != null; }
    }

    /// <summary>Au démarrage : remet l'option si une suspension est restée en place.</summary>
    public static void RestoreIfPending() => Restore();

    public static void Attach(Window w)
    {
        RestoreIfPending();
        void Update() { if (w.IsActive && w.IsMouseOver) Suspend(); else Restore(); }
        w.MouseEnter += (_, _) => Update();
        w.MouseLeave += (_, _) => Restore();
        w.Activated += (_, _) => Update();
        w.Deactivated += (_, _) => Restore();
        w.Closed += (_, _) => Restore();
        Application.Current.SessionEnding += (_, _) => Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        Update();
    }

    public static void Suspend()
    {
        try
        {
            if (!Settings.Current.HoverSpeech || Suspended) return;
            if (Read() != 1) return;   // option du Narrateur non activée : rien à faire, rien à remettre
            // Filet de sécurité posé AVANT de toucher à l'option : remise à l'ouverture de session suivante si tout s'arrête
            using (var k = Registry.CurrentUser.CreateSubKey(RunOnceKey))
                k.SetValue(Marker, $"reg.exe add \"HKCU\\{Key}\" /v {Value} /t REG_DWORD /d 1 /f", RegistryValueKind.String);
            StartGuard();
            Write(0);
        }
        catch (Exception ex) { Log.Error("Lecture à la souris du Narrateur", ex); }
    }

    /// <summary>Remet l'option du Narrateur telle qu'elle était (seulement si Centre PC l'avait suspendue).</summary>
    public static void Restore()
    {
        try
        {
            if (!Suspended) return;
            Write(1);
            using var k = Registry.CurrentUser.OpenSubKey(RunOnceKey, true);
            k?.DeleteValue(Marker, false);
        }
        catch (Exception ex) { Log.Error("Lecture à la souris du Narrateur", ex); }
    }

    /// <summary>Processus de garde : attend la fin de Centre PC, même forcée, puis remet l'option si besoin.</summary>
    private static void StartGuard()
    {
        if (_guardStarted || Environment.ProcessPath is not { } exe) return;
        _guardStarted = true;
        try { Process.Start(new ProcessStartInfo(exe, $"{GuardArg} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Log.Error("Garde du Narrateur", ex); }
    }

    /// <summary>Exécuté par le processus de garde.</summary>
    public static void RunGuard(string pid)
    {
        try
        {
            if (int.TryParse(pid, out var id))
                try { using var p = Process.GetProcessById(id); p.WaitForExit(); } catch (ArgumentException) { }   // déjà terminé
            Restore();
        }
        catch { }
    }

    private static int? Read()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Value) is int v ? v : null;
    }

    private static void Write(int v)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(Value, v, RegistryValueKind.DWord);
    }
}
