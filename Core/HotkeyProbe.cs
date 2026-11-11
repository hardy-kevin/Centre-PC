using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CentrePC.Core;

/// <summary>
/// « Identifier en appuyant » : l'utilisateur déclenche lui-même le raccourci ; Centre PC regarde quelle fenêtre
/// s'ouvre ou passe au premier plan juste après (événements de fenêtres de Windows, sans rien injecter dans les autres
/// applications). L'action de l'autre application a lieu normalement.
/// </summary>
public static class HotkeyProbe
{
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    /// <summary>
    /// Attend que l'utilisateur appuie sur <paramref name="k"/> (jusqu'à <paramref name="timeoutMs"/>), puis observe 1,5 s.
    /// pressed = faux si rien n'a été pressé ; app = application qui a réagi (null : rien de visible).
    /// </summary>
    public static async Task<(bool pressed, string app)> IdentifyAsync(HotkeyCombo k, int timeoutMs = 12000)
    {
        var events = new List<(DateTime at, IntPtr hwnd, bool foreground)>();
        WinEventProc proc = (_, ev, hwnd, idObject, _, _, _) =>
        {
            if (idObject == 0 && hwnd != IntPtr.Zero) events.Add((DateTime.UtcNow, hwnd, ev == 0x0003));
        };
        // EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_SHOW ; WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        var h1 = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, proc, 0, 0, 0x0000 | 0x0002);
        var h2 = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, proc, 0, 0, 0x0000 | 0x0002);
        try
        {
            var start = DateTime.UtcNow;
            DateTime? pressedAt = null;
            while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
            {
                if (pressedAt == null && IsDown(k)) pressedAt = DateTime.UtcNow;
                if (pressedAt != null && (DateTime.UtcNow - pressedAt.Value).TotalMilliseconds > 1500) break;
                await Task.Delay(25);   // laisse passer les messages : les événements de fenêtres arrivent ici
            }
            if (pressedAt == null) return (false, null);

            int own = Environment.ProcessId;
            var after = events.Where(e => e.at >= pressedAt.Value.AddMilliseconds(-80)).ToList();
            foreach (var e in after.Where(e => e.foreground).Concat(after.Where(e => !e.foreground)))
            {
                var root = GetAncestor(e.hwnd, 2);   // GA_ROOT
                if (root == IntPtr.Zero || !IsWindowVisible(root)) continue;
                GetWindowThreadProcessId(root, out var pid);
                if (pid == 0 || pid == own) continue;
                return (true, AppName((int)pid));
            }
            return (true, null);
        }
        finally
        {
            if (h1 != IntPtr.Zero) UnhookWinEvent(h1);
            if (h2 != IntPtr.Zero) UnhookWinEvent(h2);
            GC.KeepAlive(proc);
        }
    }

    private static bool IsDown(HotkeyCombo k)
    {
        static bool D(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (!D(k.Vk)) return false;
        if (k.Has(HotkeyCombo.Ctrl) && !D(0x11)) return false;
        if (k.Has(HotkeyCombo.Alt) && !D(0x12)) return false;
        if (k.Has(HotkeyCombo.Shift) && !D(0x10)) return false;
        if (k.Has(HotkeyCombo.Win) && !D(0x5B) && !D(0x5C)) return false;
        return true;
    }

    /// <summary>« Discord (Discord.exe) » : description du programme, sinon le nom du processus.</summary>
    private static string AppName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string exe = p.ProcessName + ".exe", desc = null;
            try
            {
                var info = p.MainModule?.FileVersionInfo;
                desc = !string.IsNullOrWhiteSpace(info?.FileDescription) ? info.FileDescription : info?.ProductName;
            }
            catch { }   // application lancée en administrateur : seul le nom du processus est lisible
            if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "Windows (Explorateur)";
            return string.IsNullOrWhiteSpace(desc) ? exe : $"{desc.Trim()} ({exe})";
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
}
