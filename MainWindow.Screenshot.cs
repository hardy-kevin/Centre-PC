using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Capture d'écran depuis la barre de gauche.
/// Clic : capture d'une zone avec l'outil de Windows (comme Windows+Maj+S) ; Centre PC se met de côté le temps de la capture
/// et revient dès que l'image est dans le presse-papiers. Clic droit : écran entier (copié et enregistré), zone avec retardateur,
/// dossier des captures.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer _captureTimeout;
    private bool _captureWaiting, _captureListening;
    private WindowState _captureRestoreState;
    private bool _captureWasShown;

    /// <summary>Depuis le lanceur (raccourci clavier) : mêmes captures ; Centre PC reste caché s'il l'était.</summary>
    private void CaptureFromLauncher(string mode)
    {
        switch (mode)
        {
            case "delay": _ = CaptureZoneAsync(5); break;
            case "full": _ = CaptureFullAsync(); break;
            case "folder": OpenTarget(CaptureFolder); break;
            default: _ = CaptureZoneAsync(0); break;
        }
    }

    /// <summary>Message après la capture : dans Centre PC s'il est affiché, sinon par l'icône près de l'horloge.</summary>
    private void CaptureMessage(string text)
    {
        if (_captureWasShown) ShowToast(text);
        else _tray?.ShowBalloon("Capture d'écran", text);
    }

    /// <summary>Dossier des captures de Windows (Images\Captures d'écran).</summary>
    private static string CaptureFolder
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e) => _ = CaptureZoneAsync(0);

    private void InitScreenshotMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Capturer une zone", "", () => _ = CaptureZoneAsync(0)));
        menu.Items.Add(MakeItem("Capturer une zone dans 5 secondes", "", () => _ = CaptureZoneAsync(5)));
        menu.Items.Add(MakeItem("Capturer l'écran entier", "", () => _ = CaptureFullAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Ouvrir le dossier des captures", "", () => OpenTarget(CaptureFolder)));
        NavScreenshot.ContextMenu = menu;
    }

    /// <summary>Met Centre PC de côté pour qu'il ne masque pas ce que l'on veut capturer.</summary>
    private void StepAside()
    {
        _captureWasShown = IsVisible && WindowState != WindowState.Minimized;
        if (!_captureWasShown) return;
        _captureRestoreState = WindowState;
        WindowState = WindowState.Minimized;
    }

    private void ComeBack()
    {
        if (!_captureWasShown) return;
        if (WindowState == WindowState.Minimized) WindowState = _captureRestoreState;
        Activate();
    }

    /// <summary>Zone : outil de capture de Windows (sélection, fenêtre, plein écran au choix dans sa barre).</summary>
    private async Task CaptureZoneAsync(int delaySeconds)
    {
        StepAside();
        if (delaySeconds > 0) await Task.Delay(delaySeconds * 1000);
        else await Task.Delay(300);   // le temps que la fenêtre disparaisse
        try
        {
            ListenClipboard();
            _captureWaiting = true;
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _captureWaiting = false;
            ComeBack();
            ShowToast("Impossible d'ouvrir l'outil de capture de Windows : " + ex.Message);
            return;
        }
        // Capture annulée (Échap) : Centre PC revient quand même au bout d'un moment
        _captureTimeout?.Stop();
        _captureTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _captureTimeout.Tick += (_, _) => { _captureTimeout.Stop(); if (_captureWaiting) { _captureWaiting = false; ComeBack(); } };
        _captureTimeout.Start();
    }

    /// <summary>Écran entier (tous les écrans) : copié dans le presse-papiers et enregistré en PNG.</summary>
    private async Task CaptureFullAsync()
    {
        StepAside();
        await Task.Delay(400);
        try
        {
            int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);   // écran virtuel
            var image = Grab(x, y, w, h);
            var path = Path.Combine(CaptureFolder, $"Capture {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            using (var f = File.Create(path))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(image));
                enc.Save(f);
            }
            Clipboard.SetImage(image);
            ComeBack();
            CaptureMessage("Capture de l'écran entier copiée (Ctrl+V pour la coller) et enregistrée dans Images\\Captures d'écran.");
        }
        catch (Exception ex)
        {
            ComeBack();
            ShowToast("Capture impossible : " + ex.Message);
            Log.Error("Capture d'écran", ex);
        }
    }

    private static BitmapSource Grab(int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);   // SRCCOPY | CAPTUREBLT (fenêtres superposées comprises)
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Le presse-papiers prévient Centre PC quand la capture y arrive : il revient alors au premier plan.</summary>
    private void ListenClipboard()
    {
        if (_captureListening) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !AddClipboardFormatListener(hwnd)) return;
        _captureListening = true;
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x031D && _captureWaiting)   // WM_CLIPBOARDUPDATE
                Dispatcher.InvokeAsync(() =>
                {
                    bool image = false;
                    try { image = Clipboard.ContainsImage(); } catch { }
                    if (!image || !_captureWaiting) return;
                    _captureWaiting = false;
                    _captureTimeout?.Stop();
                    ComeBack();
                    if (_captureWasShown) ShowToast("Capture copiée : collez-la avec Ctrl+V. La notification de Windows permet de l'annoter ou de l'enregistrer.");
                });
            return IntPtr.Zero;
        });
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
