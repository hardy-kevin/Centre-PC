using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CentrePC.Core;

/// <summary>
/// Accès rapide en arrière-plan : icône dans la zone de notification (près de l'horloge) et raccourci clavier global.
/// Fenêtre de messages invisible ; rien n'apparaît dans la barre des tâches.
/// </summary>
public sealed class TrayHost : IDisposable
{
    public event Action LeftClick, OpenLauncher, OpenMain, Quit, Hotkey;

    private const int WM_APP_TRAY = 0x8001, WM_HOTKEY = 0x0312, HotkeyId = 0xC0DE;
    private readonly HwndSource _source;
    private readonly int _taskbarCreated;
    private IntPtr _icon;
    private bool _added, _hotkeyOn;
    private string _hotkeyLabel;

    public TrayHost()
    {
        _source = new HwndSource(new HwndSourceParameters("CentrePC_Acces_rapide") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var small = new IntPtr[1];
        try { if (ExtractIconEx(Environment.ProcessPath, 0, null, small, 1) > 0) _icon = small[0]; } catch { }
        if (_icon == IntPtr.Zero) _icon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
    }

    public IntPtr Handle => _source.Handle;

    public void Add()
    {
        var d = Data();
        d.uFlags = 1 | 2 | 4;   // NIF_MESSAGE | NIF_ICON | NIF_TIP
        _added = Shell_NotifyIcon(0, ref d);   // NIM_ADD
        if (!_added) Log.Info("Accès rapide : icône de la zone de notification non ajoutée");
    }

    /// <summary>Petite bulle d'information sous l'icône (respecte le mode Ne pas déranger de Windows).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var d = Data();
        d.uFlags = 0x10;                  // NIF_INFO
        d.szInfoTitle = title;
        d.szInfo = text;
        d.dwInfoFlags = 1 | 0x80;         // NIIF_INFO | NIIF_RESPECT_QUIET_TIME
        Shell_NotifyIcon(1, ref d);       // NIM_MODIFY
    }

    /// <summary>Active le raccourci global ; faux s'il est déjà pris par une autre application.</summary>
    public bool SetHotkey(HotkeyCombo hk)
    {
        if (_hotkeyOn) { UnregisterHotKey(Handle, HotkeyId); _hotkeyOn = false; }
        _hotkeyLabel = null;
        if (hk == null || hk.IsNone) return true;
        _hotkeyOn = RegisterHotKey(Handle, HotkeyId, hk.Mods | 0x4000, hk.Vk);   // MOD_NOREPEAT
        if (_hotkeyOn) _hotkeyLabel = hk.Label;
        return _hotkeyOn;
    }

    /// <summary>Suspend le raccourci (pendant que l'utilisateur en saisit un nouveau), puis le rétablit.</summary>
    public void PauseHotkey(bool pause, HotkeyCombo hk)
    {
        if (pause) { if (_hotkeyOn) UnregisterHotKey(Handle, HotkeyId); _hotkeyOn = false; }
        else SetHotkey(hk);
    }

    /// <summary>
    /// Essai auprès de Windows : vrai si aucune application n'a réservé ce raccourci.
    /// (Les logiciels qui surveillent directement le clavier n'apparaissent pas ici : voir <see cref="HotkeyConflicts"/>.)
    /// </summary>
    public static bool IsFree(HotkeyCombo hk)
    {
        if (hk == null || hk.IsNone) return true;
        try
        {
            using var probe = new HwndSource(new HwndSourceParameters("CentrePC_Essai") { Width = 0, Height = 0, WindowStyle = 0 });
            if (!RegisterHotKey(probe.Handle, 0xBEEF, hk.Mods | 0x4000, hk.Vk)) return false;
            UnregisterHotKey(probe.Handle, 0xBEEF);
            return true;
        }
        catch { return true; }
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = Handle, uID = 1, uCallbackMessage = WM_APP_TRAY, hIcon = _icon,
        szTip = "Centre PC · vos applications", szInfo = "", szInfoTitle = ""
    };

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)((long)lParam & 0xFFFF);
            if (ev == 0x0202) LeftClick?.Invoke();          // WM_LBUTTONUP
            else if (ev == 0x0205) ShowMenu();              // WM_RBUTTONUP
            handled = true;
        }
        else if (msg == WM_HOTKEY && (int)wParam == HotkeyId) { Hotkey?.Invoke(); handled = true; }
        else if (msg == _taskbarCreated && _added) Add();  // l'Explorateur a redémarré : l'icône est remise
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "Ouvrir le lanceur" + (_hotkeyLabel != null ? "\t" + _hotkeyLabel : ""));
            AppendMenu(menu, 0, 2, "Ouvrir Centre PC");
            AppendMenu(menu, 0x800, 0, null);   // MF_SEPARATOR
            AppendMenu(menu, 0, 3, "Quitter Centre PC");
            SetMenuDefaultItem(menu, 1, 0);
            GetCursorPos(out var pt);
            SetForegroundWindow(Handle);        // sinon le menu ne se referme pas en cliquant ailleurs
            int cmd = TrackPopupMenuEx(menu, 0x0100 | 0x0002, pt.X, pt.Y, Handle, IntPtr.Zero);   // TPM_RETURNCMD | TPM_RIGHTBUTTON
            PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
            switch (cmd)
            {
                case 1: OpenLauncher?.Invoke(); break;
                case 2: OpenMain?.Invoke(); break;
                case 3: Quit?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        try
        {
            if (_hotkeyOn) UnregisterHotKey(Handle, HotkeyId);
            if (_added) { var d = Data(); Shell_NotifyIcon(2, ref d); }   // NIM_DELETE : pas d'icône fantôme
            _added = _hotkeyOn = false;
            _source.RemoveHook(Hook);
            _source.Dispose();
        }
        catch (Exception ex) { Log.Error("Accès rapide : fermeture", ex); }
    }

    // ------------------------------------------------------------------ Windows
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPos);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}
