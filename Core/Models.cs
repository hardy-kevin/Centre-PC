using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CentrePC.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(p); return true;
    }
}

public enum ItemLocation { StartMenu, Store, Desktop, PublicDesktop, Stored }

/// <summary>Une application (ou un fichier du bureau) affichée sous forme de tuile.</summary>
public class AppItem : Observable
{
    public string Name { get; set; }
    /// <summary>Fichier (.lnk, .url, .exe, document...) ou null pour une appli du Store.</summary>
    public string FilePath { get; set; }
    public string Aumid { get; set; }
    public string Target { get; set; }
    /// <summary>Indices de tri : dossier du menu Démarrer, éditeur, description.</summary>
    public string Context { get; set; }
    public bool LooksLikeGame { get; set; }
    public bool IsShortcut { get; set; } = true;

    private ItemLocation _location;
    public ItemLocation Location { get => _location; set { if (Set(ref _location, value)) { Raise(nameof(OnDesktop)); Raise(nameof(StateText)); } } }
    public bool OnDesktop => Location is ItemLocation.Desktop or ItemLocation.PublicDesktop;
    public string StateText => OnDesktop ? "Sur le bureau" : "Rangé";

    private string _theme;
    public string Theme
    {
        get => _theme;
        set { if (Set(ref _theme, value)) { Raise(nameof(ThemeOrder)); Raise(nameof(ThemeBrush)); } }
    }
    public int ThemeOrder => Settings.Order(Theme);
    public Brush ThemeBrush => Settings.BrushOf(Theme);

    private ImageSource _icon;
    public ImageSource Icon { get => _icon; set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

    private bool _keep;
    /// <summary>Doit toujours rester sur le bureau.</summary>
    public bool Keep { get => _keep; set => Set(ref _keep, value); }

    public string IconSource => FilePath ?? (Aumid != null ? @"shell:AppsFolder\" + Aumid : null);
    public string FileName => FilePath == null ? null : Path.GetFileName(FilePath);
}

public class WingetUpdate : Observable
{
    public string Name { get; set; }
    public string Id { get; set; }
    public string Version { get; set; }
    public string Available { get; set; }
    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string VersionText => $"{Version}  →  {Available}";
}

public class WuUpdate
{
    public string Title { get; set; }
    public bool IsDriver { get; set; }
    public bool Ignored { get; set; }
    public string IgnoredBy { get; set; }
    public string Size { get; set; }
    public string Kind => Ignored ? $"Ignoré (géré par {IgnoredBy ?? "le fabricant"})" : IsDriver ? "Pilote" : "Windows";
}

public class StepItem : Observable
{
    public string Key { get; set; }
    public string Title { get; set; }
    private string _status = "En attente";
    public string Status { get => _status; set { if (Set(ref _status, value)) { Raise(nameof(Glyph)); Raise(nameof(StatusBrush)); } } }
    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Glyph => Status switch
    {
        "OK" => "",
        "ERREUR" => "",
        "PARTIEL" or "À VÉRIFIER" => "",
        "En cours" => "",
        "IGNORÉ" => "",
        _ => ""
    };
    public Brush StatusBrush => Status switch
    {
        "OK" => Brushes.MediumSeaGreen,
        "ERREUR" => Brushes.IndianRed,
        "PARTIEL" or "À VÉRIFIER" => Brushes.Goldenrod,
        "En cours" => new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)),
        _ => Brushes.Gray
    };
}

public class ThemeGroup
{
    public string Name { get; set; }
    public List<AppItem> Items { get; set; }
    public int Count => Items.Count;
    public Brush Brush => Settings.BrushOf(Name);
    public string Glyph => Settings.GlyphOf(Name);

    public static List<ThemeGroup> Build(IEnumerable<AppItem> items) => items
        .GroupBy(i => i.Theme)
        .OrderBy(g => Settings.Order(g.Key))
        .Select(g => new ThemeGroup { Name = g.Key, Items = g.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList() })
        .ToList();
}

/// <summary>Un choix manuel enregistré (page Réglages).</summary>
public class ChoiceRow
{
    public string Name { get; set; }
    public string Kind { get; set; }    // theme, hidden, skipped
    public string Label { get; set; }
    public Brush Brush { get; set; }
}

/// <summary>Une ligne de la liste des thèmes (page Réglages).</summary>
public class ThemeRow
{
    public string Name { get; set; }
    public string Glyph { get; set; }
    public Brush Brush { get; set; }
    public string Detail { get; set; }
}

public class InfoLine
{
    public string Label { get; set; }
    public string Value { get; set; }
}
