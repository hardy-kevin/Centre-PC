using System.Runtime.InteropServices;

namespace CentrePC.Core;

/// <summary>
/// Menu contextuel de Windows (celui de l'Explorateur : Propriétés, Épingler, Envoyer vers, options des logiciels installés…)
/// pour un fichier, un raccourci ou une application du Store (« shell:AppsFolder\… »).
/// </summary>
public static class ShellMenu
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr ppenum);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr commandString, uint cch);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr commandString, uint cch);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    private const uint CmdFirst = 1, CmdLast = 0x7FFF;

    /// <summary>
    /// Affiche le menu de Windows à la position de la souris. Renvoie faux si Windows ne fournit pas de menu pour cet élément.
    /// Les sous-menus dessinés par les logiciels (« Envoyer vers », « Ouvrir avec »…) sont alimentés via la fenêtre.
    /// </summary>
    public static bool Show(string parsingName, IntPtr hwnd)
    {
        if (string.IsNullOrEmpty(parsingName)) return false;
        IntPtr pidl = IntPtr.Zero, folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        IShellFolder folder = null;
        IContextMenu menu = null;
        IContextMenu3 menu3 = null;
        System.Windows.Interop.HwndSource source = null;
        System.Windows.Interop.HwndSourceHook hook = null;
        try
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
            if (SHBindToParent(pidl, typeof(IShellFolder).GUID, out folderPtr, out var child) != 0) return false;
            folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var iid = typeof(IContextMenu).GUID;
            if (folder.GetUIObjectOf(hwnd, 1, new[] { child }, ref iid, IntPtr.Zero, out menuPtr) != 0 || menuPtr == IntPtr.Zero) return false;
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            menu3 = menu as IContextMenu3;

            hmenu = CreatePopupMenu();
            // CMF_NORMAL | CMF_CANRENAME(0x10) retiré : pas de renommage depuis Centre PC ; CMF_EXTENDEDVERBS (0x100) avec Maj
            uint flags = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 0x100u : 0u;
            if (menu.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, flags) < 0) return false;

            if (menu3 != null)
            {
                source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    // WM_INITMENUPOPUP, WM_DRAWITEM, WM_MEASUREITEM, WM_MENUCHAR : sous-menus des logiciels
                    if (msg is 0x0117 or 0x002B or 0x002C or 0x0120)
                        if (menu3.HandleMenuMsg2((uint)msg, w, l, out var r) == 0) { handled = true; return r; }
                    return IntPtr.Zero;
                };
                source?.AddHook(hook);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd);
            // TPM_RETURNCMD (0x100) | TPM_RIGHTBUTTON (0x2)
            int cmd = TrackPopupMenuEx(hmenu, 0x0102, pt.X, pt.Y, hwnd, IntPtr.Zero);
            if (cmd >= CmdFirst)
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = 0x00004000 | 0x20000000,            // CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                    hwnd = hwnd,
                    lpVerb = (IntPtr)(cmd - CmdFirst),
                    lpVerbW = (IntPtr)(cmd - CmdFirst),
                    nShow = 1,                                    // SW_SHOWNORMAL
                    ptInvoke = pt
                };
                menu.InvokeCommand(ref info);
            }
            return true;
        }
        catch (Exception ex) { Log.Error("Menu Windows : " + parsingName, ex); return false; }
        finally
        {
            if (hook != null) source?.RemoveHook(hook);
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (menu != null) Marshal.ReleaseComObject(menu);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }
}
