using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CentrePC.Core;

/// <summary>
/// Aperçu en grand, net : chaque type de fichier par le meilleur moyen disponible.
/// 1) Images : le fichier lui-même, en pleine résolution ;
/// 2) Textes (txt, csv, scripts…) : le texte ;
/// 3) Documents (PDF, Word, Excel…) : le gestionnaire d'aperçu de Windows, celui du volet de l'Explorateur (rendu réel, défilable) ;
/// 4) Fichiers OpenDocument (LibreOffice) sans gestionnaire : conversion en PDF par LibreOffice, puis aperçu du PDF ;
/// 5) Sinon : la miniature de l'Explorateur, jamais agrandie au-delà de sa netteté.
/// Rien n'est modifié : les conversions se font dans un dossier temporaire.
/// </summary>
public static class DocPreview
{
    public static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico", ".jfif" };
    public static readonly string[] TextExt =
    {
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".ps1", ".bat", ".cmd", ".sh", ".py", ".cs",
        ".js", ".ts", ".html", ".htm", ".css", ".sql", ".java", ".c", ".cpp", ".h", ".php", ".ahk", ".reg", ".srt", ".gpx", ".kml"
    };
    private static readonly string[] OpenDocExt = { ".odt", ".ods", ".odp", ".odg", ".ott", ".ots", ".otp", ".rtf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };

    public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsText(string path) => TextExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Image en pleine résolution (limitée à <paramref name="maxSize"/> pixels de large), ou null.</summary>
    public static Task<ImageSource> LoadImageAsync(string path, int maxSize = 2600) => Task.Run<ImageSource>(() =>
    {
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (frame.PixelWidth > maxSize) bmp.DecodePixelWidth = maxSize;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }   // format sans décodeur installé : la miniature prend le relais
    });

    /// <summary>Début du texte (200 000 caractères au plus), en UTF-8 ou dans l'encodage Windows habituel.</summary>
    public static Task<string> LoadTextAsync(string path) => Task.Run(() =>
    {
        try
        {
            var bytes = new byte[Math.Min(new FileInfo(path).Length, 400_000)];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fs.ReadExactly(bytes);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding(1252).GetString(bytes); }
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            return text.Length > 200_000 ? text[..200_000] + "\n…" : text;
        }
        catch (Exception ex) { return "Lecture impossible : " + ex.Message; }
    });

    // ------------------------------------------------------------------ gestionnaires d'aperçu de Windows
    private const string PreviewHandlerGuid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    /// <summary>Gestionnaire d'aperçu enregistré pour ce type de fichier (volet d'aperçu de l'Explorateur), ou null.</summary>
    public static Guid? HandlerFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        try
        {
            uint size = 64;
            var sb = new StringBuilder((int)size);
            // ASSOCF_INIT_DEFAULTTOSTAR (0x4), ASSOCSTR_SHELLEXTENSION (16)
            if (AssocQueryString(0x4, 16, ext, PreviewHandlerGuid, sb, ref size) == 0 && Guid.TryParse(sb.ToString(), out var g)) return g;
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ LibreOffice
    /// <summary>soffice.exe de LibreOffice, s'il est installé.</summary>
    public static string LibreOffice()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var p = Path.Combine(root, @"LibreOffice\program\soffice.exe");
            if (File.Exists(p)) return p;
        }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\soffice.exe");
            if (k?.GetValue(null) is string s && File.Exists(s.Trim('"'))) return s.Trim('"');
        }
        catch { }
        return null;
    }

    // Une seule conversion LibreOffice à la fois ; le résultat est gardé d'une session à l'autre (dossier temporaire)
    private static readonly SemaphoreSlim LibreGate = new(1, 1);
    private static string CacheRoot => Path.Combine(Path.GetTempPath(), "CentrePC-apercus");

    /// <summary>A besoin de LibreOffice pour un aperçu net (OpenDocument ou bureautique sans gestionnaire de Windows).</summary>
    public static bool NeedsConversion(string path) =>
        OpenDocExt.Contains(Path.GetExtension(path).ToLowerInvariant()) && HandlerFor(path) == null;

    /// <summary>PDF déjà préparé pour ce fichier (même version), ou null.</summary>
    public static string CachedPdf(string path)
    {
        var pdf = PdfPathFor(path);
        return pdf != null && File.Exists(pdf) ? pdf : null;
    }

    /// <summary>Emplacement stable du PDF (même nom d'une session à l'autre : chemin + date de modification).</summary>
    private static string PdfPathFor(string path)
    {
        try
        {
            var key = path.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            return Path.Combine(CacheRoot, hash, Path.GetFileNameWithoutExtension(path) + ".pdf");
        }
        catch { return null; }
    }

    /// <summary>
    /// Fichier OpenDocument / bureautique sans gestionnaire d'aperçu : LibreOffice le convertit en PDF (copie temporaire,
    /// profil séparé pour ne pas gêner un LibreOffice ouvert). Null si impossible.
    /// </summary>
    public static async Task<string> ToPdfAsync(string path)
    {
        if (!OpenDocExt.Contains(Path.GetExtension(path).ToLowerInvariant())) return null;
        var ready = CachedPdf(path);
        if (ready != null) return ready;
        await ConvertAsync(new[] { path });
        return CachedPdf(path);
    }

    /// <summary>
    /// Prépare à l'avance les aperçus nets des fichiers qui en ont besoin, par lots de 4 (un seul démarrage de LibreOffice
    /// par lot, le plus coûteux), en commençant par les premiers de la liste. Ne bloque rien.
    /// </summary>
    public static async Task PrewarmAsync(IEnumerable<string> paths)
    {
        try
        {
            var todo = await Task.Run(() => paths.Where(p => File.Exists(p) && NeedsConversion(p) && CachedPdf(p) == null).ToList());
            if (todo.Count == 0 || LibreOffice() == null) return;
            foreach (var batch in todo.Chunk(4)) await ConvertAsync(batch);
        }
        catch (Exception ex) { Log.Error("Aperçus préparés à l'avance", ex); }
    }

    private static async Task ConvertAsync(IReadOnlyCollection<string> files)
    {
        var soffice = LibreOffice();
        if (soffice == null) return;
        await LibreGate.WaitAsync();
        try
        {
            // Déjà fait pendant l'attente (par la préparation à l'avance) ?
            var todo = files.Where(f => CachedPdf(f) == null).ToList();
            while (todo.Count > 0)
            {
                // Deux fichiers de même nom donneraient le même PDF : un seul par nom dans un lot, les autres au tour suivant
                var batch = todo.GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                await RunBatch(soffice, batch);
                todo = todo.Except(batch).ToList();
            }
        }
        catch (Exception ex) { Log.Error("Aperçu LibreOffice", ex); }
        finally { LibreGate.Release(); }
    }

    /// <summary>Un seul démarrage de LibreOffice pour tout le lot ; chaque PDF est rangé à son emplacement stable.</summary>
    private static async Task RunBatch(string soffice, List<string> batch)
    {
        var outDir = Path.Combine(CacheRoot, "lot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        var profile = new Uri(Path.Combine(CacheRoot, "profil-libreoffice")).AbsoluteUri;
        var psi = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", $"-env:UserInstallation={profile}", "--convert-to", "pdf", "--outdir", outDir })
            psi.ArgumentList.Add(a);
        foreach (var f in batch) psi.ArgumentList.Add(f);
        using (var p = Process.Start(psi))
        {
            var exited = await Task.Run(() => p.WaitForExit(20000 + 10000 * batch.Count));
            if (!exited) { try { p.Kill(true); } catch { } }
        }
        foreach (var f in batch)
        {
            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".pdf");
            var target = PdfPathFor(f);
            if (!File.Exists(produced) || target == null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Move(produced, target, true);
        }
        try { Directory.Delete(outDir, true); } catch { }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, int str, string assoc, string extra, StringBuilder outBuf, ref uint size);
}

/// <summary>
/// Zone qui affiche un fichier avec le gestionnaire d'aperçu de Windows (comme le volet d'aperçu de l'Explorateur) :
/// rendu réel et défilable des PDF, documents Word, classeurs, présentations…
/// </summary>
public sealed class PreviewHost : HwndHost
{
    private IntPtr _hwnd;
    private object _handler;
    private IStream _stream;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        // WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN
        _hwnd = CreateWindowEx(0, "static", "", 0x40000000 | 0x10000000 | 0x02000000, 0, 0, 10, 10, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>Affiche le fichier ; faux si le gestionnaire refuse (on se rabat alors sur la miniature).</summary>
    public bool Load(string path, Guid clsid)
    {
        Unload();
        if (_hwnd == IntPtr.Zero) return false;
        try
        {
            var type = Type.GetTypeFromCLSID(clsid, true);
            _handler = Activator.CreateInstance(type);
            if (_handler is IInitializeWithStream ws)
            {
                // STGM_READ | STGM_SHARE_DENY_NONE
                if (SHCreateStreamOnFileEx(path, 0x00000000 | 0x00000040, 0, false, null, out _stream) != 0) return Fail();
                ws.Initialize(_stream, 0);
            }
            else if (_handler is IInitializeWithFile wf) wf.Initialize(path, 0);
            else return Fail();
            if (_handler is not IPreviewHandler ph) return Fail();
            var r = PixelRect();
            ph.SetWindow(_hwnd, ref r);
            ph.DoPreview();
            return true;
        }
        catch (Exception ex) { Log.Error("Aperçu Windows " + path, ex); return Fail(); }
    }

    private bool Fail() { Unload(); return false; }

    public void Unload()
    {
        try { (_handler as IPreviewHandler)?.Unload(); } catch { }
        if (_handler != null) { try { Marshal.FinalReleaseComObject(_handler); } catch { } _handler = null; }
        if (_stream != null) { try { Marshal.FinalReleaseComObject(_stream); } catch { } _stream = null; }
    }

    private RECT PixelRect()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new RECT { Left = 0, Top = 0, Right = (int)(ActualWidth * dpi.DpiScaleX), Bottom = (int)(ActualHeight * dpi.DpiScaleY) };
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_handler is IPreviewHandler ph)
        {
            var r = PixelRect();
            try { ph.SetRect(ref r); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(IntPtr pmsg);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IStream template, out IStream stream);
}
