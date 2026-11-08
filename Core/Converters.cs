using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CentrePC.Core;

public class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class ThemeGlyphConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Settings.GlyphOf(value as string);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class ThemeBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Settings.BrushOf(value as string);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Charge les icônes une par une sur un thread STA dédié, sans bloquer l'interface.</summary>
public static class IconLoader
{
    private static readonly System.Collections.Concurrent.BlockingCollection<AppItem> Queue = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Windows.Media.ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Thread _worker;

    public static void ClearCache() => Cache.Clear();

    public static void Enqueue(IEnumerable<AppItem> items)
    {
        foreach (var it in items)
        {
            var key = it.IconSource;
            if (key == null || it.Icon != null) continue;
            if (Cache.TryGetValue(key, out var img)) { it.Icon = img; continue; }
            Queue.Add(it);
        }
        if (_worker != null) return;
        _worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>
    /// 1) icône grand format du shell si elle est propre (pas de cadre)
    /// 2) sinon l'icône du programme lue directement dans son fichier (exe, ico…) — jamais encadrée
    /// 3) sinon le shell en petit format, puis l'icône classique
    /// </summary>
    private static System.Windows.Media.ImageSource Best(AppItem it)
    {
        var key = it.IconSource;
        // 1) Le raccourci désigne son fichier d'icône (.ico, .exe…) : on le lit directement, sans passer
        //    par le shell qui encadre les petites icônes agrandies (cause du « carré blanc »)
        if (it.FilePath != null)
        {
            var native = NativeFromFile(it.FilePath);
            if (native != null) return native;
        }
        // 2) Sinon le shell (applis du Store, dossiers…), sans cadre si possible
        return Native.GetIcon(key, 96, strict: true) ?? Native.GetIcon(key, 48);
    }

    private static System.Windows.Media.Imaging.BitmapSource NativeFromFile(string file)
    {
        try
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            string loc = null;
            if (ext == ".lnk")
            {
                loc = Shortcuts.GetIconLocation(file);
                if (string.IsNullOrWhiteSpace(loc) || !File.Exists(Environment.ExpandEnvironmentVariables(loc.Split(',')[0].Trim('"'))))
                    loc = Shortcuts.GetTargetPath(file);
            }
            else if (ext == ".url")
            {
                var lines = File.ReadAllLines(file).ToList();
                var f = Ini.Get(lines, "InternetShortcut", "IconFile");
                if (f != null) loc = $"{f},{Ini.Get(lines, "InternetShortcut", "IconIndex") ?? "0"}";
            }
            else if (ext == ".exe") loc = file;
            if (string.IsNullOrWhiteSpace(loc)) return null;

            int comma = loc.LastIndexOf(',');
            var path = Environment.ExpandEnvironmentVariables((comma > 1 ? loc[..comma] : loc).Trim().Trim('"'));
            int idx = 0;
            if (comma > 1) int.TryParse(loc[(comma + 1)..].Trim(), out idx);
            if (!File.Exists(path) || !System.Text.RegularExpressions.Regex.IsMatch(path, @"\.(exe|dll|ico|icl|cpl)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
            // 64 px : les tuiles affichent 46 px (69 px à 150 %) ; les icônes plus grandes sont réduites proprement
            return Native.ExtractIcon(path, idx, 64);
        }
        catch { return null; }
    }

    private static void Work()
    {
        foreach (var it in Queue.GetConsumingEnumerable())
        {
            var key = it.IconSource;
            if (key == null) continue;
            if (!Cache.TryGetValue(key, out var img))
            {
                img = Best(it);
                if (img != null) Cache[key] = img;
            }
            if (img != null)
                Application.Current?.Dispatcher.InvokeAsync(() => it.Icon = img);
        }
    }
}
