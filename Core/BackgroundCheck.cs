using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>
/// Vérification des mises à jour une fois par jour, même quand Centre PC est fermé :
/// une tâche planifiée Windows (créée pour l'utilisateur, sans droits administrateur) lance « CentrePC.exe --check »,
/// qui compte les mises à jour sans rien installer et affiche une notification Windows.
/// Un clic sur la notification ouvre Centre PC sur la page Mises à jour (lien « centrepc:updates »).
/// </summary>
public static class BackgroundCheck
{
    public const string TaskName = "Centre PC - verification des mises a jour";
    public const string Aumid = "CentrePC.App";
    public const string Protocol = "centrepc";
    private static string StatePath => Path.Combine(Settings.DataDir, "verification.json");

    private class State
    {
        public string Signature { get; set; }
        public DateTime? Notified { get; set; }
    }

    // ------------------------------------------------------------------ installation de la tâche
    /// <summary>Crée (ou retire) la tâche planifiée et l'inscription des notifications, selon le réglage.</summary>
    public static void Apply(bool enabled)
    {
        if (Native.IsPackaged) return;   // version Store : pas de tâche planifiée
        try
        {
            if (!enabled) { Remove(); return; }
            RegisterNotifications();
            var exe = Environment.ProcessPath;
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Centre PC : recherche quotidienne des mises à jour, avec notification. Aucune installation sans l'accord de l'utilisateur.</Description></RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>2026-01-01T12:41:00</StartBoundary>
      <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
    </CalendarTrigger>
  </Triggers>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
    <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions>
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
      <Arguments>--check</Arguments>
    </Exec>
  </Actions>
</Task>";
            var file = Path.Combine(Path.GetTempPath(), "centrepc-tache.xml");
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            var (_, err, code) = Updates.RunAsync("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F").GetAwaiter().GetResult();
            try { File.Delete(file); } catch { }
            if (code != 0) Log.Info("Tâche planifiée non créée : " + err.Trim());
        }
        catch (Exception ex) { Log.Error("Tâche planifiée", ex); }
    }

    public static void Remove()
    {
        try { Updates.RunAsync("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F").GetAwaiter().GetResult(); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{Aumid}", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Protocol}", false); } catch { }
    }

    /// <summary>Nom et icône des notifications + lien « centrepc: » qui ouvre l'application (pour l'utilisateur courant).</summary>
    public static void RegisterNotifications()
    {
        var exe = Environment.ProcessPath;
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}"))
        {
            k.SetValue("DisplayName", "Centre PC");
            var png = Path.Combine(Settings.DataDir, "notification.png");
            try
            {
                if (!File.Exists(png))
                {
                    var icon = Native.RunSta(() => Native.GetIcon(exe, 96)).Result;
                    if (icon != null)
                    {
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(icon));
                        using var fs = File.Create(png);
                        enc.Save(fs);
                    }
                }
                if (File.Exists(png)) k.SetValue("IconUri", png);
            }
            catch { }
        }
        using (var p = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}"))
        {
            p.SetValue("", "URL:Centre PC");
            p.SetValue("URL Protocol", "");
            using var cmd = p.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
    }

    // ------------------------------------------------------------------ vérification (CentrePC.exe --check)
    /// <summary>Compte les mises à jour et prévient si c'est nouveau (ou rappel tous les 3 jours). N'installe rien.</summary>
    public static async Task RunAsync()
    {
        try
        {
            if (!await Network.IsOnlineAsync(force: true)) return;
            var apps = await Updates.ScanWingetAsync();
            int system = 0, drivers = 0;
            try
            {
                var wu = await Updates.ScanWindowsUpdateAsync(null);
                system = wu.System.Count;
                drivers = wu.Drivers.Count(d => d.CanSelect && !d.IsFirmware);
            }
            catch (Exception ex) { Log.Error("Vérification Windows Update", ex); }

            int total = apps.Count + system + drivers;
            Log.Info($"Vérification quotidienne : {apps.Count} application(s), {system} Windows, {drivers} pilote(s)");
            var state = LoadState();
            var signature = string.Join("|", apps.Select(a => a.Id + "=" + a.Available).OrderBy(x => x)) + $"|wu{system}|drv{drivers}";
            bool isNew = signature != state.Signature;
            bool reminder = state.Notified == null || DateTime.Now - state.Notified > TimeSpan.FromDays(3);
            if (total == 0 || (!isNew && !reminder)) { SaveState(new State { Signature = signature, Notified = state.Notified }); return; }

            var parts = new List<string>();
            if (apps.Count > 0) parts.Add($"{apps.Count} application(s)");
            if (system > 0) parts.Add($"{system} mise(s) à jour Windows");
            if (drivers > 0) parts.Add($"{drivers} pilote(s)");
            var names = string.Join(", ", apps.Take(3).Select(a => a.Name)) + (apps.Count > 3 ? "…" : "");
            Notify(total == 1 ? "1 mise à jour disponible" : $"{total} mises à jour disponibles",
                   string.Join(", ", parts) + (names.Length > 0 ? $"\n{names}" : ""));
            SaveState(new State { Signature = signature, Notified = DateTime.Now });
        }
        catch (Exception ex) { Log.Error("Vérification quotidienne", ex); }
    }

    /// <summary>Notification Windows (via PowerShell, sans dépendance supplémentaire).</summary>
    public static void Notify(string title, string text)
    {
        string X(string s) => SecurityElement.Escape(s ?? "");
        var toast = $"<toast activationType=\"protocol\" launch=\"{Protocol}:updates\">" +
                    $"<visual><binding template=\"ToastGeneric\"><text>{X(title)}</text><text>{X(text)}</text></binding></visual>" +
                    $"<actions><action content=\"Voir les mises à jour\" activationType=\"protocol\" arguments=\"{Protocol}:updates\"/></actions></toast>";
        var ps = string.Join("\n",
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null",
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null",
            "$x = New-Object Windows.Data.Xml.Dom.XmlDocument",
            $"$x.LoadXml('{toast.Replace("'", "''")}')",
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{Aumid}').Show([Windows.UI.Notifications.ToastNotification]::new($x))");
        try
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
            var (_, err, code) = Updates.RunAsync("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}").GetAwaiter().GetResult();
            if (code != 0) Log.Info("Notification non affichée : " + err.Trim());
        }
        catch (Exception ex) { Log.Error("Notification", ex); }
    }

    private static State LoadState()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State(); } catch { return new State(); }
    }

    private static void SaveState(State s)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(s)); } catch { }
    }
}
