using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CentrePC.Core;

/// <summary>Appels Windows bas niveau : icônes haute résolution, thread STA, fenêtre sombre.</summary>
public static class Native
{
    // ---------- Exécution sur un thread STA (requis par les objets COM du shell) ----------
    public static Task<T> RunSta<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // ---------- Icônes via IShellItemImageFactory ----------
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    /// <summary>
    /// Icône d'un fichier, raccourci ou application du Store (« shell:AppsFolder\AUMID »).
    /// Si le programme n'a pas d'icône assez grande, Windows la pose au centre d'un carré blanc :
    /// on le détecte et on prend alors l'icône native (48 px) ou l'icône classique du fichier.
    /// </summary>
    public static BitmapSource GetIcon(string parsingName, int size = 96, bool strict = false)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;
        var big = Render(parsingName, size, out bool alpha, out bool framed);
        if (big != null && alpha && !framed) return big;
        if (strict) return null;   // l'appelant a mieux à proposer (icône native du programme)
        if (size > 48)
        {
            var small = Render(parsingName, 48, out bool alpha2, out bool framed2);
            if (small != null && alpha2 && !framed2) return small;
        }
        // Dernier recours : icône classique avec son masque de transparence
        var classic = ClassicIcon(parsingName);
        return classic ?? big;
    }

    private static BitmapSource Render(string parsingName, int size, out bool hadAlpha, out bool framed)
    {
        hadAlpha = false; framed = false;
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
            return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            return HBitmapToSource(hbm, out hadAlpha, out framed);
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Détecte le cadre que Windows dessine autour des petites icônes agrandies :
    /// un trait fin presque entièrement opaque sur les quatre côtés, avec du vide juste à l'intérieur.
    /// (Un vrai logo carré plein, lui, est opaque aussi à l'intérieur.)
    /// </summary>
    private static bool IsFramed(byte[] px, int w, int h)
    {
        if (w < 32 || h < 32) return false;
        double Ring(int k, bool opaque)
        {
            int n = 0, hit = 0;
            for (int x = k; x < w - k; x++)
            {
                n += 2;
                if ((px[(k * w + x) * 4 + 3] > 24) == opaque) hit++;
                if ((px[((h - 1 - k) * w + x) * 4 + 3] > 24) == opaque) hit++;
            }
            for (int y = k + 1; y < h - k - 1; y++)
            {
                n += 2;
                if ((px[(y * w + k) * 4 + 3] > 24) == opaque) hit++;
                if ((px[(y * w + (w - 1 - k)) * 4 + 3] > 24) == opaque) hit++;
            }
            return n == 0 ? 0 : (double)hit / n;
        }
        for (int k = 0; k <= w / 6; k++)
        {
            if (Ring(k, true) < 0.85) continue;          // pas encore de trait opaque à ce niveau
            // Trait trouvé : l'intérieur proche doit être majoritairement vide
            for (int d = 2; d <= 4; d++)
                if (k + d < w / 2 && Ring(k + d, false) > 0.6) return true;
            return false;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);

    /// <summary>Icône classique (32 px) d'un fichier, avec sa transparence correcte.</summary>
    private static BitmapSource ClassicIcon(string path)
    {
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return null;
        var info = new SHFILEINFO();
        try
        {
            const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon); }
    }

    private static BitmapSource HBitmapToSource(IntPtr hbm, out bool hadAlpha, out bool framed)
    {
        hadAlpha = false; framed = false;
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,            // demande une image « de haut en bas »
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        var pixels = new byte[w * h * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        // Certaines anciennes icônes n'ont pas de canal alpha : on les rend opaques
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        hadAlpha = anyAlpha;
        framed = anyAlpha && IsFramed(pixels, w, h);

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        src.Freeze();
        return src;
    }

    // ---------- Extraction d'une icône précise d'un fichier (.exe, .dll, .ico) ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public static BitmapSource ExtractIcon(string file, int index, int size)
    {
        var icons = new IntPtr[1];
        var ids = new int[1];
        try
        {
            if (PrivateExtractIcons(file, index, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]); }
    }

    // ---------- Fenêtre : barre de titre sombre + coins arrondis (Windows 11) ----------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void StyleWindow(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));     // mode sombre
            int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // coins arrondis
        }
        catch { }
    }
}
