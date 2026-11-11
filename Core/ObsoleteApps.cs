using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Pourquoi une application est obsolète et par quoi la remplacer.</summary>
public record ObsoleteInfo(string Reason, string Replacement);

/// <summary>
/// Applications retirées ou abandonnées par leur éditeur : elles ne reçoivent plus de correctifs (failles de sécurité
/// comprises) et peuvent être désinstallées. Reconnues par leur nom exact ou par leur paquet du Microsoft Store.
/// </summary>
public static class ObsoleteApps
{
    private record Rule(Regex Name, string Package, Regex Target, ObsoleteInfo Info);

    private static Rule R(string namePattern, string package, string reason, string replacement, string targetPattern = null) => new(
        namePattern == null ? null : new Regex("^(" + namePattern + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        package,
        targetPattern == null ? null : new Regex(targetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new ObsoleteInfo(reason, replacement));

    private static readonly Rule[] Rules =
    {
        R("Cartes|Maps|Windows Maps|Cartes Windows", "Microsoft.WindowsMaps_",
          "Microsoft a retiré l'application Cartes en juillet 2025 : elle n'est plus mise à jour.", "Bing Maps ou Google Maps dans le navigateur"),
        R("Cortana", "Microsoft.549981C3F5F10_",
          "Microsoft a abandonné l'application Cortana en 2023.", "Copilot"),
        R("Paint 3D", "Microsoft.MSPaint_",
          "Microsoft a retiré Paint 3D du Microsoft Store en novembre 2024.", "Paint"),
        R("Visionneuse 3D|3D Viewer|Visionneuse de réalité mixte|Mixed Reality Viewer", "Microsoft.Microsoft3DViewer_",
          "Microsoft a abandonné la Visionneuse 3D.", "Blender ou le logiciel 3D de votre choix"),
        R("WordPad", null,
          "Microsoft a retiré WordPad de Windows 11 (version 24H2).", "Bloc-notes ou Word"),
        R("Courrier et calendrier|Mail and Calendar", "microsoft.windowscommunicationsapps_",
          "Microsoft a arrêté Courrier et Calendrier le 31 décembre 2024.", "Outlook"),
        R("Skype|Skype for Desktop|Skype pour bureau", "Microsoft.SkypeApp_",
          "Microsoft a fermé le service Skype le 5 mai 2025.", "Microsoft Teams"),
        R("Internet Explorer", null,
          "Microsoft a retiré Internet Explorer en juin 2022 : il n'est plus sécurisé.", "Microsoft Edge"),
        R("Microsoft Teams \\(classic\\)|Microsoft Teams classique|Teams classique|Teams \\(classic\\)", null,
          "La version classique de Teams a été retirée en 2024.", "Nouveau Microsoft Teams"),
        R(null, "Microsoft.RemoteDesktop_",
          "Microsoft a arrêté l'application Bureau à distance du Store le 27 mai 2025.", "Windows App (Microsoft Store)"),
        R("Origin", null,
          "EA a fermé Origin le 17 avril 2025.", "EA app", targetPattern: @"\\Origin\\Origin\.exe|Electronic Arts"),
        R("GeForce Experience|NVIDIA GeForce Experience", null,
          "NVIDIA a remplacé GeForce Experience par l'application NVIDIA.", "NVIDIA App"),
        R("Adobe Flash Player.*|Flash Player.*", null,
          "Flash Player n'est plus pris en charge depuis décembre 2020 : le conserver expose à des failles.", "Aucun (à désinstaller)"),
        R("Microsoft Silverlight", null,
          "Silverlight n'est plus pris en charge depuis octobre 2021.", "Aucun (à désinstaller)"),
        R("QuickTime Player|QuickTime", null,
          "Apple ne met plus à jour QuickTime pour Windows depuis 2016 (failles connues).", "VLC ou Lecteur multimédia"),
        R("Enregistreur d'actions|Steps Recorder|Enregistreur d'actions utilisateur", null,
          "Microsoft a abandonné l'Enregistreur d'actions.", "Outil Capture (enregistrement d'écran)"),
    };

    /// <summary>Renvoie la raison si l'application est obsolète, sinon null.</summary>
    public static ObsoleteInfo Find(string name, string aumid, string target)
    {
        var n = Settings.Normalize(name ?? "");
        foreach (var r in Rules)
        {
            if (r.Package != null && aumid != null && aumid.StartsWith(r.Package, StringComparison.OrdinalIgnoreCase)) return r.Info;
            if (r.Name == null || !r.Name.IsMatch(n)) continue;
            // Nom ambigu (« Origin » est aussi un logiciel scientifique) : la cible doit confirmer
            if (r.Target != null && !r.Target.IsMatch(target ?? "")) continue;
            // Une appli du Store au nom identique mais d'un autre paquet n'est pas concernée
            if (r.Package != null && aumid != null && aumid.Contains('!')) continue;
            return r.Info;
        }
        return null;
    }

    public static ObsoleteInfo Find(AppItem a) => a == null ? null : Find(a.Name, a.Aumid, a.Target);

    /// <summary>Marque les applications obsolètes d'une liste.</summary>
    public static void Mark(IEnumerable<AppItem> apps)
    {
        foreach (var a in apps) a.Obsolete = Find(a);
    }
}
