using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
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
    /// <summary>Fichier (.lnk, .url, .exe, document...) ou null pour une application du Store.</summary>
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

    /// <summary>Sous-dossier du menu Démarrer (« Git », « LibreOffice 7.6 »…), ou null.</summary>
    public string StartFolder { get; set; }
    /// <summary>Éditeur (exécutable, programme d'installation ou paquet du Store), ou null.</summary>
    public string Publisher { get; set; }
    /// <summary>Dossier d'installation du programme (C:\Program Files\Git…), ou null.</summary>
    public string InstallRoot { get; set; }
    /// <summary>Site de l'application (programme d'installation, package.json), utilisé pour l'identifier en ligne.</summary>
    public string Homepage { get; set; }
    /// <summary>Élément annexe d'une application (désinstalleur, aide, contenu bonus, bande-son…) : rangé avec elle.</summary>
    public bool IsCompanion { get; set; }

    /// <summary>Suite d'applications (Git Bash, Git CMD, Git GUI…) : les applications regroupées sous cette tuile.</summary>
    public List<AppItem> Members { get; set; }
    public bool IsSuite => Members?.Count > 1 || (IsUserGroup && Members?.Count > 0);
    /// <summary>Dossier créé par l'utilisateur (glisser-déposer).</summary>
    public bool IsUserGroup { get; set; }
    /// <summary>Nom affiché, sans le suffixe « - Raccourci » ajouté par Windows.</summary>
    public string Label => AppKinds.StripShortcutSuffix(Name);
    public string SuiteCount => IsSuite ? Members.Count.ToString() : null;

    /// <summary>Application retirée ou plus mise à jour par son éditeur (voir ObsoleteApps).</summary>
    public ObsoleteInfo Obsolete { get; set; }
    public bool IsObsolete => Obsolete != null;
    public string ObsoleteTip => Obsolete == null ? null : $"Application obsolète : {Obsolete.Reason}";
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

    /// <summary>
    /// Cette même mise à jour (même version actuelle → même version proposée) a déjà été installée avec succès,
    /// mais winget continue de la proposer (il lit mal la version de l'application) : l'application est à jour, elle n'est pas affichée.
    /// </summary>
    public bool Phantom { get; set; }
    /// <summary>Clé de la fausse alerte : version actuelle → version proposée.</summary>
    public string Pair => $"{Version}→{Available}";
}

/// <summary>Mise à jour de pilote proposée par Windows Update, présentée comme une application (version actuelle → nouvelle).</summary>
public class DriverUpdate : Observable
{
    /// <summary>Identifiant Windows Update (pour installer exactement ce pilote).</summary>
    public string UpdateId { get; set; }
    public string Title { get; set; }
    /// <summary>Nom du périphérique tel que Windows l'affiche (« Realtek Audio », « Intel(R) Wi-Fi 6 AX201 »…).</summary>
    public string Device { get; set; }
    /// <summary>Type en clair : Audio, Réseau, Bluetooth…</summary>
    public string Kind { get; set; }
    public string Manufacturer { get; set; }
    public string HardwareId { get; set; }
    public string CurrentVersion { get; set; }
    public DateTime? CurrentDate { get; set; }
    public string NewVersion { get; set; }
    public DateTime? NewDate { get; set; }
    public string Size { get; set; }
    /// <summary>Pilote géré par le logiciel du fabricant (AMD Software, NVIDIA App) : affiché mais pas installé.</summary>
    public string ManagedBy { get; set; }
    /// <summary>Firmware (BIOS, contrôleur…) : décoché par défaut, à installer en connaissance de cause.</summary>
    public bool IsFirmware { get; set; }

    private bool _selected = true;
    public bool Selected { get => _selected && CanSelect; set => Set(ref _selected, value && CanSelect); }
    public bool CanSelect => ManagedBy == null;

    public string Name => string.IsNullOrWhiteSpace(Device) ? Title : Device;
    public string Subtitle => string.Join(" · ", new[] { Kind, Manufacturer }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
    public string VersionText
    {
        get
        {
            string V(string v, DateTime? d) => !string.IsNullOrEmpty(v) ? v : d is DateTime x ? x.ToString("dd/MM/yyyy") : "?";
            return $"{V(CurrentVersion, CurrentDate)}  →  {V(NewVersion, NewDate)}";
        }
    }
    public string Note => ManagedBy != null ? $"Géré par {ManagedBy}" : IsFirmware ? "Firmware : à installer uniquement sur recommandation du fabricant" : "";
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Une mise à jour exclue (page Réglages).</summary>
public class ExclusionRow
{
    public string Name { get; set; }
    public string Label { get; set; }
    public string Kind { get; set; }   // app, driver
    public string Key { get; set; }
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
        "OK" or "À jour" => "",
        "ERREUR" or "Échec" => "",
        "PARTIEL" or "À VÉRIFIER" or "À faire" => "",
        "En cours" => "",
        "IGNORÉ" => "",
        "Reporté" => "",
        _ => ""
    };
    public Brush StatusBrush => Status switch
    {
        "OK" or "À jour" => Brushes.MediumSeaGreen,
        "ERREUR" or "Échec" => Brushes.IndianRed,
        "PARTIEL" or "À VÉRIFIER" or "À faire" => Brushes.Goldenrod,
        "En cours" => new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)),
        _ => Brushes.Gray
    };
}

/// <summary>Avancement d'une application pendant la mise à jour (une ligne de la liste « Applications »).</summary>
public class AppStep : StepItem
{
    public string Version { get; set; }
    public bool IsDone => Status is "À jour" or "À faire" or "Reporté" or "Échec";
}

/// <summary>Ce qui reste à faire après les mises à jour (carte « Rapport »).</summary>
public class ReportAction
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Message { get; set; }
    public string Installer { get; set; }
    public Visibility InstallerVisibility => string.IsNullOrEmpty(Installer) ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>« Ne plus proposer » : seulement pour les applications (les pilotes se masquent depuis leur liste).</summary>
    public Visibility ExcludeVisibility => string.IsNullOrEmpty(Id) ? Visibility.Collapsed : Visibility.Visible;
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

/// <summary>Une ligne du gestionnaire d'applications.</summary>
public class ManagedAppRow : Observable
{
    public AppItem App { get; set; }
    public string Name => App.Name;
    /// <summary>Applications de la suite (désinstallées ensemble), ou l'application seule.</summary>
    public List<AppItem> Items { get; set; }

    private UninstallPlan _plan;
    public UninstallPlan Plan { get => _plan; set { if (Set(ref _plan, value)) { Raise(nameof(Detail)); Raise(nameof(CanAct)); Raise(nameof(ActionText)); } } }

    private bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { Raise(nameof(CanAct)); Raise(nameof(ActionText)); } } }

    private bool _done;
    public bool Done { get => _done; set { if (Set(ref _done, value)) { Raise(nameof(CanAct)); Raise(nameof(ActionText)); } } }

    private string _status;
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (App.Obsolete != null) parts.Add($"{App.Obsolete.Reason} Remplacement conseillé : {App.Obsolete.Replacement}.");
            else parts.Add(App.Theme);
            if (App.IsSuite) parts.Add($"{App.Members.Count} applications : " + string.Join(", ", App.Members.Select(m => m.Name)));
            if (_plan == null) parts.Add("Recherche du programme de désinstallation…");
            else if (!_plan.CanUninstall) parts.Add(_plan.Reason);
            else if (_plan.Kind == UninstallKind.Program && !string.Equals(_plan.Entry.DisplayName, App.Name, StringComparison.CurrentCultureIgnoreCase))
                parts.Add("Programme : " + _plan.Entry.DisplayName);
            return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    public bool CanAct => !_busy && !_done && _plan?.CanUninstall == true;
    public string ActionText => _done ? "Désinstallée" : _busy ? "En cours…" : _plan == null ? "…" : _plan.CanUninstall ? "Désinstaller" : "Non disponible";
}
