using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CentrePC.Core;

/// <summary>
/// Aperçu d'un fichier ou d'un dossier tel que l'Explorateur l'affiche : miniature (photo, vidéo, PDF, document Office…)
/// ou, à défaut, l'icône du type de fichier. Rien n'est ouvert ni modifié.
/// </summary>
public static class ShellThumbnail
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public uint c0, c1, c2, c3;   // réserve pour la table de couleurs (non utilisée en 32 bits)
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                                                          [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private static readonly SemaphoreSlim Gate = new(3);
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aperçu calculé hors de l'interface (thread STA dédié, 3 à la fois). Null si Windows n'en fournit pas.</summary>
    public static async Task<ImageSource> GetAsync(string path, int size)
    {
        var key = size + "|" + path;
        lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;
        await Gate.WaitAsync();
        try
        {
            var tcs = new TaskCompletionSource<ImageSource>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(Get(path, size)); }
                catch (Exception ex) { Log.Error("Aperçu : " + path, ex); tcs.SetResult(null); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            var img = await tcs.Task;
            if (size <= 256) lock (Cache) Cache[key] = img;   // les aperçus agrandis (plusieurs Mo chacun) ne sont pas gardés
            return img;
        }
        finally { Gate.Release(); }
    }

    private static ImageSource Get(string path, int size)
    {
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) return null;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory) != 0 || factory == null) return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            // SIIGBF_RESIZETOFIT (0) : miniature si elle existe, sinon icône du type
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return ToImage(hbm);
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Bitmap Windows → image WPF, transparence conservée (pixels lus de haut en bas).</summary>
    private static ImageSource ToImage(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var info = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, px, ref info, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // Les miniatures de photos n'ont souvent aucune transparence renseignée (alpha à 0 partout) : on les rend opaques
        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var img = BitmapSource.Create(w, h, 96, 96, anyAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, px, w * 4);
        img.Freeze();
        return img;
    }
}
