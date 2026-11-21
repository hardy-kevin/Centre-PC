using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CentrePC.Core;

/// <summary>
/// Lecture du texte d'une image (photo d'un document, numérisation) ou de la première page d'un PDF, par la
/// reconnaissance de texte intégrée à Windows. Tout se fait sur ce PC : rien n'est envoyé sur Internet.
/// Langue : celles de l'utilisateur dans Windows (le français est installé avec un Windows français).
/// </summary>
public static class Ocr
{
    private static OcrEngine _engine;
    private static bool _tried;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static OcrEngine Engine
    {
        get
        {
            if (_tried) return _engine;
            _tried = true;
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine == null)
                {
                    var lang = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
                    if (lang != null) _engine = OcrEngine.TryCreateFromLanguage(lang);
                }
            }
            catch (Exception ex) { Log.Error("Reconnaissance de texte de Windows indisponible", ex); }
            return _engine;
        }
    }

    /// <summary>La reconnaissance de texte de Windows est-elle disponible (Windows 10 ou 11, une langue installée) ?</summary>
    public static bool Available
    {
        get { try { return Engine != null; } catch { return false; } }
    }

    /// <summary>Texte d'une image (JPEG, PNG, TIFF, BMP, HEIC si l'extension est installée…), ou null.</summary>
    public static async Task<string> FromImageAsync(string path, CancellationToken ct = default)
    {
        if (!Available) return null;
        await Gate.WaitAsync(ct);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            return await RecognizeAsync(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info($"Lecture du texte impossible : {Path.GetFileName(path)} ({ex.Message})"); return null; }
        finally { Gate.Release(); }
    }

    /// <summary>Texte de la première page d'un PDF (rendue en image puis lue : marche aussi pour un PDF numérisé), ou null.</summary>
    public static async Task<string> FromPdfAsync(string path, CancellationToken ct = default)
    {
        if (!Available) return null;
        await Gate.WaitAsync(ct);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var pdf = await PdfDocument.LoadFromFileAsync(file);
            if (pdf.PageCount == 0) return null;
            using var page = pdf.GetPage(0);
            using var image = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(image, new PdfPageRenderOptions { DestinationWidth = Math.Min(OcrEngine.MaxImageDimension, 2000u) });
            image.Seek(0);
            return await RecognizeAsync(image);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info($"Lecture du PDF impossible : {Path.GetFileName(path)} ({ex.Message})"); return null; }
        finally { Gate.Release(); }
    }

    private static async Task<string> RecognizeAsync(IRandomAccessStream stream)
    {
        var engine = Engine;
        if (engine == null) return null;
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // Taille maximale acceptée par la reconnaissance : l'image est réduite si besoin, sens de la photo respecté
        double scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(1, Math.Max(decoder.PixelWidth, decoder.PixelHeight)));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                                                                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return result?.Text;
    }
}
