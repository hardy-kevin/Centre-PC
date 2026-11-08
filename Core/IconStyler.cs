using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>Sauvegarde de l'état d'origine d'une icône, pour pouvoir revenir en arrière.</summary>
public class IconBackup
{
    public string Path { get; set; }
    public string Kind { get; set; }              // lnk, url, folder
    public string IconLocation { get; set; }      // .lnk : IconLocation d'origine
    public bool HadUrlIcon { get; set; }          // .url : IconFile / IconIndex d'origine
    public string UrlIconFile { get; set; }
    public string UrlIconIndex { get; set; }
    public bool HadDesktopIni { get; set; }       // dossier : desktop.ini d'origine
    public string DesktopIni { get; set; }
    public bool FolderWasReadOnly { get; set; }
}

public class StyleTarget
{
    public string Path { get; set; }
    public string Name { get; set; }
    public string Theme { get; set; }
    public bool IsFolder { get; set; }
}

public class PreviewItem
{
    public string Name { get; set; }
    public ImageSource Image { get; set; }
}

public record IconOp(string Path, string IconLocation);

/// <summary>Génère des icônes « tuile colorée par thème » et les applique aux raccourcis du bureau.</summary>
public static class IconStyler
{
    public static readonly string IconsDir = System.IO.Path.Combine(Settings.DataDir, "Icones");
    private static readonly string BackupFile = System.IO.Path.Combine(Settings.DataDir, "icones-sauvegarde.json");
    private const string FolderColor = "#E0A33A";

    public static List<IconBackup> Backups => Settings.Current.IconBackups;
    public static bool IsStyled => Backups.Count > 0;

    /// <summary>Raccourci de Centre PC lui-même : son icône est déjà une tuile, on ne le stylise pas.</summary>
    public static bool IsOwnShortcut(string path)
    {
        if (System.IO.Path.GetFileNameWithoutExtension(path).Equals("Centre PC", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        return Shortcuts.GetTargetPath(path).EndsWith("CentrePC.exe", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ Aperçu
    public static Task<List<PreviewItem>> PreviewAsync(List<StyleTarget> targets) => Native.RunSta(() =>
        targets.Select(t => new PreviewItem { Name = t.Name, Image = Render(t, null, 112) }).ToList());

    /// <summary>Raccourci : tuile colorée. Dossier : dossier à la couleur du thème avec son symbole.</summary>
    private static BitmapSource Render(StyleTarget t, ImageSource src, int size) => t.IsFolder
        ? RenderFolder(ColorOf(t), FolderGlyph(t), size)
        : RenderTile(src ?? SourceIcon(t), ColorOf(t), size, t.Name);

    // ------------------------------------------------------------------ Appliquer
    public static Task<int> ApplyAsync(List<StyleTarget> targets) => Native.RunSta(() =>
    {
        Directory.CreateDirectory(IconsDir);
        var elevated = new List<IconOp>();
        int n = 0;
        foreach (var t in targets)
        {
            try
            {
                var src = t.IsFolder ? null : SourceIcon(t);
                var ico = System.IO.Path.Combine(IconsDir, $"{Math.Abs(t.Path.ToLowerInvariant().GetHashCode()):x8}_{DateTime.Now.Ticks:x}.ico");
                WriteIco(ico, new[] { 256, 64, 48, 32, 24, 16 }.Select(s => Render(t, src, s)).ToList());

                var kind = t.IsFolder ? "folder" : System.IO.Path.GetExtension(t.Path).ToLowerInvariant() == ".url" ? "url" : "lnk";
                var backup = Backups.FirstOrDefault(b => b.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase));
                bool isNew = backup == null;
                backup ??= new IconBackup { Path = t.Path, Kind = kind };

                bool ok = kind switch
                {
                    "lnk" => ApplyLnk(t.Path, ico + ",0", backup, isNew, elevated),
                    "url" => ApplyUrl(t.Path, ico, backup, isNew),
                    _ => ApplyFolder(t.Path, ico, backup, isNew)
                };
                if (ok) { if (isNew) Backups.Add(backup); n++; }
            }
            catch { }
        }
        if (elevated.Count > 0 && RunElevatedIconOps(elevated)) n += elevated.Count;
        SaveBackups();
        NotifyShell(targets.Select(t => t.Path));
        CleanIcons();
        return n;
    });

    // ------------------------------------------------------------------ Revenir en arrière
    public static Task<int> RevertAsync(Func<IconBackup, bool> filter = null) => Native.RunSta(() =>
    {
        int n = 0;
        var elevated = new List<IconOp>();
        var paths = new List<string>();
        foreach (var b in Backups.Where(b => filter == null || filter(b)).ToList())
        {
            var path = Locate(b);
            if (path == null) { Backups.Remove(b); continue; }   // fichier supprimé entre-temps
            try
            {
                switch (b.Kind)
                {
                    case "lnk":
                        try { Shortcuts.SetIconLocation(path, b.IconLocation ?? ""); }
                        catch { elevated.Add(new IconOp(path, b.IconLocation ?? "")); }
                        break;
                    case "url":
                        RevertUrl(path, b);
                        break;
                    case "folder":
                        RevertFolder(path, b);
                        break;
                }
                Backups.Remove(b); paths.Add(path); n++;
            }
            catch { }
        }
        if (elevated.Count > 0) RunElevatedIconOps(elevated);
        SaveBackups();
        NotifyShell(paths);
        CleanIcons();
        return n;
    });

    /// <summary>Suit les déplacements (rangement / remise sur le bureau) pour pouvoir toujours revenir en arrière.</summary>
    public static void OnMoved(string from, string to)
    {
        foreach (var b in Backups.Where(b => b.Path.Equals(from, StringComparison.OrdinalIgnoreCase))) b.Path = to;
    }

    public static void SaveBackups()
    {
        Settings.Save();
        try { File.WriteAllText(BackupFile, JsonSerializer.Serialize(Backups, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    private static string Locate(IconBackup b)
    {
        if (File.Exists(b.Path) || Directory.Exists(b.Path)) return b.Path;
        var name = System.IO.Path.GetFileName(b.Path);
        foreach (var dir in new[] { AppScanner.UserDesktop, AppScanner.PublicDesktop, Settings.StoreDir })
        {
            var p = System.IO.Path.Combine(dir, name);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        return null;
    }

    // ------------------------------------------------------------------ .lnk
    private static bool ApplyLnk(string path, string location, IconBackup backup, bool isNew, List<IconOp> elevated)
    {
        if (isNew) backup.IconLocation = Shortcuts.GetIconLocation(path);
        try { Shortcuts.SetIconLocation(path, location); return true; }
        catch
        {
            // Raccourci « pour tous les utilisateurs » : nécessite les droits admin
            elevated.Add(new IconOp(path, location));
            if (isNew) Backups.Add(backup);
            return false;
        }
    }

    private static bool RunElevatedIconOps(List<IconOp> ops)
    {
        var json = System.IO.Path.Combine(Settings.DataDir, "icones-admin.json");
        File.WriteAllText(json, JsonSerializer.Serialize(ops));
        return Elevated.Run($"--icons \"{json}\"");
    }

    /// <summary>Appelé par l'instance élevée (« CentrePC.exe --icons fichier.json »).</summary>
    public static void RunElevatedIconOps(string jsonPath)
    {
        var ops = JsonSerializer.Deserialize<List<IconOp>>(File.ReadAllText(jsonPath));
        foreach (var op in ops) { try { Shortcuts.SetIconLocation(op.Path, op.IconLocation); } catch { } }
    }

    // ------------------------------------------------------------------ .url
    private static bool ApplyUrl(string path, string ico, IconBackup backup, bool isNew)
    {
        var lines = File.ReadAllLines(path).ToList();
        if (isNew)
        {
            backup.UrlIconFile = Ini.Get(lines, "InternetShortcut", "IconFile");
            backup.UrlIconIndex = Ini.Get(lines, "InternetShortcut", "IconIndex");
            backup.HadUrlIcon = backup.UrlIconFile != null;
        }
        Ini.Set(lines, "InternetShortcut", "IconFile", ico);
        Ini.Set(lines, "InternetShortcut", "IconIndex", "0");
        WriteKeepingAttributes(path, lines);
        return true;
    }

    private static void RevertUrl(string path, IconBackup b)
    {
        var lines = File.ReadAllLines(path).ToList();
        Ini.Set(lines, "InternetShortcut", "IconFile", b.HadUrlIcon ? b.UrlIconFile : null);
        Ini.Set(lines, "InternetShortcut", "IconIndex", b.HadUrlIcon ? b.UrlIconIndex : null);
        WriteKeepingAttributes(path, lines);
    }

    // ------------------------------------------------------------------ Dossiers (desktop.ini)
    private static bool ApplyFolder(string dir, string ico, IconBackup backup, bool isNew)
    {
        var ini = System.IO.Path.Combine(dir, "desktop.ini");
        var di = new DirectoryInfo(dir);
        if (isNew)
        {
            backup.HadDesktopIni = File.Exists(ini);
            backup.DesktopIni = backup.HadDesktopIni ? File.ReadAllText(ini) : null;
            backup.FolderWasReadOnly = (di.Attributes & FileAttributes.ReadOnly) != 0;
        }
        var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>();
        Ini.Set(lines, ".ShellClassInfo", "IconResource", ico + ",0");
        if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
        File.WriteAllLines(ini, lines, System.Text.Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        di.Attributes |= FileAttributes.ReadOnly;   // requis pour que Windows lise desktop.ini
        return true;
    }

    private static void RevertFolder(string dir, IconBackup b)
    {
        var ini = System.IO.Path.Combine(dir, "desktop.ini");
        if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
        if (b.HadDesktopIni)
        {
            File.WriteAllText(ini, b.DesktopIni ?? "", System.Text.Encoding.Unicode);
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        }
        else if (File.Exists(ini)) File.Delete(ini);
        if (!b.FolderWasReadOnly)
        {
            var di = new DirectoryInfo(dir);
            di.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    private static void WriteKeepingAttributes(string path, List<string> lines)
    {
        var attr = File.GetAttributes(path);
        if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        File.WriteAllLines(path, lines);
        File.SetAttributes(path, attr);
    }

    // ------------------------------------------------------------------ Flèches de raccourci
    private const string ShellIconsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    public static bool ArrowsHidden()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ShellIconsKey);
            return k?.GetValue("29") is string v && v.Contains("CentrePC", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Masque ou rétablit les flèches (droits admin requis), puis redémarre l'Explorateur.</summary>
    public static bool SetArrows(bool hide)
    {
        string arg;
        if (hide)
        {
            // Sauvegarde de la valeur d'origine (souvent absente)
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(ShellIconsKey);
                var prev = k?.GetValue("29") as string;
                if (prev == null || !prev.Contains("CentrePC", StringComparison.OrdinalIgnoreCase))
                    Settings.Current.ArrowPreviousValue = prev ?? "";
            }
            catch { }
            Settings.Save();
            var blank = System.IO.Path.Combine(Settings.DataDir, "vide.ico");
            Native.RunSta(() => { WriteIco(blank, new[] { 48, 32, 16 }.Select(Transparent).ToList()); return 0; }).Wait();
            arg = $"--arrows hide \"{blank}\"";
        }
        else arg = $"--arrows restore \"{Settings.Current.ArrowPreviousValue ?? ""}\"";

        if (!Elevated.Run(arg)) return false;
        RestartExplorer();
        return true;
    }

    /// <summary>Appelé par l'instance élevée.</summary>
    public static void RunElevatedArrows(string mode, string value)
    {
        using var k = Registry.LocalMachine.CreateSubKey(ShellIconsKey);
        if (mode == "hide") k.SetValue("29", value + ",0", RegistryValueKind.String);
        else if (string.IsNullOrEmpty(value)) k.DeleteValue("29", false);
        else k.SetValue("29", value, RegistryValueKind.String);
    }

    public static void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } }
            Thread.Sleep(1500);
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { }
    }

    // ------------------------------------------------------------------ Rendu des tuiles
    /// <summary>Thème d'un dossier : son nom s'il correspond à un thème (Jeux, Bureautique…), sinon aucun.</summary>
    public static string FolderTheme(string name)
    {
        if (name.Equals(Settings.FilesCategory, StringComparison.OrdinalIgnoreCase)) return Settings.FilesCategory;
        return Settings.AllCategoryNames.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string FolderGlyph(StyleTarget t)
    {
        var theme = FolderTheme(t.Name);
        if (theme == null) return null;
        if (theme == Settings.FilesCategory) return "\uE8A5";
        if (theme == Settings.Current.DefaultCategory) return "\uE712";
        return Settings.GlyphOf(theme);
    }

    private static Color ColorOf(StyleTarget t)
    {
        string hex;
        if (t.IsFolder)
        {
            var theme = FolderTheme(t.Name);
            hex = theme == null ? FolderColor
                : theme == Settings.FilesCategory ? "#64748B"
                : theme == Settings.Current.DefaultCategory ? "#6B7280"
                : Settings.Find(theme)?.Color ?? FolderColor;
        }
        else hex = Settings.Find(t.Theme)?.Color ?? "#6B7280";
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Colors.SlateGray; }
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static BitmapSource RenderTile(ImageSource icon, Color color, int size, string name)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            double s = size, m = Math.Max(1, s * 0.035), r = s * 0.23;
            var rect = new Rect(m, m, s - 2 * m, s - 2 * m);
            var fill = new LinearGradientBrush(Mix(color, Colors.White, 0.22), Mix(color, Colors.Black, 0.30), new Point(0, 0), new Point(1, 1));
            dc.DrawRoundedRectangle(fill, null, rect, r, r);

            // reflet doux en haut
            dc.PushClip(new RectangleGeometry(rect, r, r));
            var gloss = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawRectangle(gloss, null, new Rect(rect.X, rect.Y, rect.Width, rect.Height * 0.55));
            dc.Pop();
            if (s >= 32)
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), Math.Max(1, s / 110)), rect, r, r);

            if (icon != null)
            {
                double isz = s * 0.60;
                var ir = new Rect((s - isz) / 2, (s - isz) / 2, isz, isz);
                if (s >= 32)
                {
                    var sh = ir; sh.Offset(0, s * 0.03);
                    dc.PushOpacityMask(new ImageBrush(icon) { Stretch = Stretch.Uniform });
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, sh);
                    dc.Pop();
                }
                dc.DrawImage(icon, ir);
            }
            else
            {
                var letter = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
                var ft = new FormattedText(letter, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    s * 0.46, Brushes.White, 1.0);
                dc.DrawText(ft, new Point((s - ft.Width) / 2, (s - ft.Height) / 2));
            }
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Dossier dessiné à la couleur du thème, sans contour, avec le symbole du thème sur la face avant.</summary>
    public static BitmapSource RenderFolder(Color color, string glyph, int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size;
            Rect R(double x1, double y1, double x2, double y2) => new(x1 * s, y1 * s, (x2 - x1) * s, (y2 - y1) * s);
            double r = s * 0.07;

            // Ombre douce sous le dossier
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(55, 0, 0, 0)), null, R(0.07, 0.24, 0.95, 0.91), r, r);

            // Dos du dossier + onglet (teinte plus foncée)
            var back = new CombinedGeometry(GeometryCombineMode.Union,
                new RectangleGeometry(R(0.05, 0.20, 0.95, 0.86), r, r),
                new RectangleGeometry(R(0.05, 0.13, 0.43, 0.27), r * 0.8, r * 0.8));
            dc.DrawGeometry(new LinearGradientBrush(Mix(color, Colors.Black, 0.18), Mix(color, Colors.Black, 0.38), 90), null, back);

            // Feuille qui dépasse (donne du relief)
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 245, 245, 250)), null, R(0.11, 0.24, 0.89, 0.50), r * 0.6, r * 0.6);

            // Face avant
            var front = R(0.05, 0.31, 0.95, 0.88);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Mix(color, Colors.White, 0.18), Mix(color, Colors.Black, 0.12), 90), null, front, r, r);
            // reflet en haut de la face avant
            dc.PushClip(new RectangleGeometry(front, r, r));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, new Rect(front.X, front.Y, front.Width, Math.Max(1, s * 0.018)));
            dc.Pop();

            // Symbole du thème
            if (!string.IsNullOrEmpty(glyph) && s >= 20)
            {
                var face = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, s * 0.42, Brushes.White, 1.0);
                var pos = new Point(front.X + (front.Width - ft.Width) / 2, front.Y + (front.Height - ft.Height) / 2);
                if (s >= 48)
                {
                    var shadow = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, s * 0.42,
                        new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1.0);
                    dc.DrawText(shadow, new Point(pos.X, pos.Y + s * 0.015));
                }
                dc.DrawText(ft, pos);
            }
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource Transparent(int size)
    {
        var px = new byte[size * size * 4];
        var b = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        b.Freeze();
        return b;
    }

    /// <summary>Icône d'origine (jamais l'icône déjà stylisée), pour ne pas empiler les tuiles.</summary>
    private static ImageSource SourceIcon(StyleTarget t)
    {
        if (t.IsFolder) return Native.GetIcon(Settings.DataDir, 256);   // icône de dossier standard
        var backup = Backups.FirstOrDefault(b => b.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase));
        var ext = System.IO.Path.GetExtension(t.Path).ToLowerInvariant();
        try
        {
            if (ext == ".lnk")
            {
                var loc = backup != null ? backup.IconLocation : Shortcuts.GetIconLocation(t.Path);
                var fromLoc = FromLocation(loc);
                if (fromLoc != null) return fromLoc;
                var target = Shortcuts.GetTargetPath(t.Path);
                if (!string.IsNullOrEmpty(target) && File.Exists(target) && Regex.IsMatch(target, @"\.(exe|dll|ico)$", RegexOptions.IgnoreCase))
                    return Native.ExtractIcon(target, 0, 256) ?? Native.GetIcon(target, 256);
                if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target))) return Native.GetIcon(target, 256);
                return backup == null ? Native.GetIcon(t.Path, 256) : null;
            }
            if (ext == ".url")
            {
                if (backup != null) return backup.HadUrlIcon ? FromLocation($"{backup.UrlIconFile},{backup.UrlIconIndex}") : null;
                var lines = File.ReadAllLines(t.Path).ToList();
                var f = Ini.Get(lines, "InternetShortcut", "IconFile");
                return FromLocation($"{f},{Ini.Get(lines, "InternetShortcut", "IconIndex") ?? "0"}") ?? Native.GetIcon(t.Path, 256);
            }
        }
        catch { }
        return Native.GetIcon(t.Path, 256);
    }

    private static ImageSource FromLocation(string loc)
    {
        if (string.IsNullOrWhiteSpace(loc)) return null;
        int comma = loc.LastIndexOf(',');
        var file = Environment.ExpandEnvironmentVariables((comma > 0 ? loc[..comma] : loc).Trim().Trim('"'));
        int idx = 0;
        if (comma > 0) int.TryParse(loc[(comma + 1)..].Trim(), out idx);
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        return Native.ExtractIcon(file, idx, 256) ?? Native.GetIcon(file, 256);
    }

    // ------------------------------------------------------------------ Fichier .ico (images PNG)
    public static void WriteIco(string path, IList<BitmapSource> images)
    {
        var pngs = images.Select(img =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int sz = images[i].PixelWidth;
            w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    /// <summary>Supprime les icônes générées qui ne servent plus.</summary>
    private static void CleanIcons()
    {
        try
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in Backups)
            {
                var p = Locate(b);
                if (p == null) continue;
                try
                {
                    if (b.Kind == "lnk") used.Add((Shortcuts.GetIconLocation(p) ?? "").Split(',')[0]);
                    else if (b.Kind == "url") used.Add(Ini.Get(File.ReadAllLines(p).ToList(), "InternetShortcut", "IconFile") ?? "");
                    else
                    {
                        var ini = System.IO.Path.Combine(p, "desktop.ini");
                        if (File.Exists(ini)) used.Add((Ini.Get(File.ReadAllLines(ini).ToList(), ".ShellClassInfo", "IconResource") ?? "").Split(',')[0]);
                    }
                }
                catch { used.Add("*"); }
            }
            if (used.Contains("*")) return;
            foreach (var f in Directory.GetFiles(IconsDir, "*.ico"))
                if (!used.Contains(f)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    // ------------------------------------------------------------------ Rafraîchissement de l'Explorateur
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell(IEnumerable<string> paths)
    {
        try
        {
            foreach (var p in paths) SHChangeNotify(0x00002000 /*UPDATEITEM*/, 0x0005 /*PATHW*/, p, IntPtr.Zero);
            SHChangeNotify(0x08000000 /*ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }
}

/// <summary>Lecture / écriture minimale de fichiers .ini (desktop.ini, .url).</summary>
public static class Ini
{
    public static string Get(List<string> lines, string section, string key)
    {
        bool inSec = false;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith("[")) { inSec = l.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase); continue; }
            if (inSec && l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return l[(key.Length + 1)..];
        }
        return null;
    }

    /// <summary>Définit une clé (value null = supprime la clé).</summary>
    public static void Set(List<string> lines, string section, string key, string value)
    {
        int secStart = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (secStart < 0)
        {
            if (value == null) return;
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }
        int end = lines.FindIndex(secStart + 1, l => l.Trim().StartsWith("["));
        if (end < 0) end = lines.Count;
        for (int i = secStart + 1; i < end; i++)
        {
            if (lines[i].Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                if (value == null) lines.RemoveAt(i); else lines[i] = $"{key}={value}";
                return;
            }
        }
        if (value != null) lines.Insert(secStart + 1, $"{key}={value}");
    }
}

/// <summary>Relance Centre PC en administrateur pour une action précise.</summary>
public static class Elevated
{
    public static bool Run(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath, args) { UseShellExecute = true, Verb = "runas" });
            if (p == null) return true;
            p.WaitForExit();
            try { return p.ExitCode == 0; } catch { return true; }
        }
        catch { return false; }   // autorisation refusée
    }
}
