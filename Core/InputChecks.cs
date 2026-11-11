using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>
/// Mauvais réglages fréquents du clavier, de la souris et du son, qui font croire à une panne :
/// clavier passé en QWERTY par un raccourci, touches rémanentes ou filtres, boutons de la souris inversés, molette à zéro,
/// casque Bluetooth en « mains libres »… Chaque point propose une correction en un clic.
/// Toute correction est réversible : l'état d'origine est gardé (« Annuler » le remet), jusqu'à ce que l'utilisateur la garde.
/// </summary>
public static class InputChecks
{
    // ================================================================== annulation
    private static readonly Dictionary<string, string> UndoText = new()
    {
        ["layouts"] = "dispositions de clavier modifiées", ["langtoggle"] = "raccourcis Alt+Maj et Ctrl+Maj désactivés",
        ["sticky"] = "touches rémanentes modifiées", ["filter"] = "touches filtres modifiées", ["mousekeys"] = "touches souris désactivées",
        ["kbrepeat"] = "répétition des touches rétablie", ["numlock"] = "Verr. Num activé au démarrage",
        ["swap"] = "bouton principal de la souris remis à gauche", ["clicklock"] = "verrouillage du clic désactivé",
        ["dblclick"] = "vitesse du double-clic rétablie", ["speed"] = "vitesse du pointeur rétablie", ["wheel"] = "défilement de la molette rétabli",
        ["trails"] = "traînées du pointeur désactivées", ["ducking"] = "son des applications conservé pendant les appels"
    };

    private static Dictionary<string, string> Backups => Settings.Current.DeviceFixBackups;

    /// <summary>Garde l'état d'origine avant la première correction (une seconde correction ne l'écrase pas).</summary>
    private static void Backup(string id, string original)
    {
        if (!Backups.ContainsKey(id)) { Backups[id] = original ?? ""; Settings.Save(); }
    }

    /// <summary>Corrections faites par Centre PC, avec « Annuler » (remet l'état d'origine) et « Garder ».</summary>
    public static IEnumerable<DeviceIssue> UndoIssues(params string[] ids)
    {
        foreach (var id in ids.Where(Backups.ContainsKey).ToList())
        {
            var key = id;
            yield return new DeviceIssue
            {
                Level = DeviceStatus.Info, Text = "Corrigé par Centre PC : " + UndoText[key] + ".",
                Actions =
                {
                    new DeviceAction { Label = "Annuler", Glyph = "", Tip = "Remet le réglage tel qu'il était avant", Run = () => Task.Run(() => Undo(key)) },
                    new DeviceAction { Label = "Garder", Glyph = "", Run = () => Task.Run(() => { Backups.Remove(key); Settings.Save(); return (string)null; }) }
                }
            };
        }
    }

    private static string Undo(string id)
    {
        if (!Backups.TryGetValue(id, out var v)) return null;
        bool ok = true;
        switch (id)
        {
            case "layouts": ok = RestoreLanguages(v); break;
            case "langtoggle": SetToggle(v.Split('|')); break;
            case "sticky": SetFlags(SPI_SETSTICKYKEYS, SPI_GETSTICKYKEYS, 2, uint.Parse(v)); break;
            case "filter": SetFlags(SPI_SETFILTERKEYS, SPI_GETFILTERKEYS, 6, uint.Parse(v)); break;
            case "mousekeys": SetFlags(SPI_SETMOUSEKEYS, SPI_GETMOUSEKEYS, 7, uint.Parse(v)); break;
            case "kbrepeat": { var p = v.Split(','); SetUi(SPI_SETKEYBOARDDELAY, uint.Parse(p[0])); SetUi(SPI_SETKEYBOARDSPEED, uint.Parse(p[1])); break; }
            case "numlock": SetReg(@"Control Panel\Keyboard", "InitialKeyboardIndicators", v); break;
            case "swap": SetUi(SPI_SETMOUSEBUTTONSWAP, v == "1" ? 1u : 0u); break;
            case "clicklock": SetPv(SPI_SETMOUSECLICKLOCK, v == "1" ? 1 : 0); break;
            case "dblclick": SetUi(SPI_SETDOUBLECLICKTIME, uint.Parse(v)); break;
            case "speed": SetPv(SPI_SETMOUSESPEED, int.Parse(v)); break;
            case "wheel": SetUi(SPI_SETWHEELSCROLLLINES, uint.Parse(v)); break;
            case "trails": SetUi(SPI_SETMOUSETRAILS, uint.Parse(v)); break;
            case "ducking": SetDucking(v); break;
        }
        if (!ok) return "Impossible de remettre le réglage d'origine : faites-le dans les paramètres de Windows.";
        Backups.Remove(id);
        Settings.Save();
        return "Réglage d'origine remis.";
    }

    // ================================================================== clavier
    /// <summary>Une disposition de clavier : « 040C:0000040C » (langue : disposition).</summary>
    private sealed record Layout(string Tip, string LanguageTag, string LanguageName)
    {
        public string Klid => Tip.Contains(':') ? Tip[(Tip.IndexOf(':') + 1)..] : "";
        public bool IsKeyboard => Klid.Length == 8 && !Klid.StartsWith("{");
    }

    public static IEnumerable<DeviceIssue> Keyboard(bool laptop)
    {
        var issues = new List<DeviceIssue>();
        try { issues.AddRange(LayoutIssues()); } catch (Exception ex) { Log.Error("Dispositions de clavier", ex); }

        var sticky = GetFlags(SPI_GETSTICKYKEYS, 2);
        var filter = GetFlags(SPI_GETFILTERKEYS, 6);
        if ((sticky & SKF_ON) != 0)
            issues.Add(Fix(DeviceStatus.Warning, "Les touches rémanentes sont activées : Maj, Ctrl et Alt restent enfoncées après un simple appui, ce qui déclenche des raccourcis par erreur.",
                           "Désactiver", "sticky", sticky.ToString(), () => SetFlags(SPI_SETSTICKYKEYS, SPI_GETSTICKYKEYS, 2, sticky & ~(SKF_ON | SKF_HOTKEY)), "Touches rémanentes désactivées."));
        if ((filter & FKF_ON) != 0)
            issues.Add(Fix(DeviceStatus.Warning, "Les touches filtres sont activées : les appuis brefs sont ignorés et les touches répondent avec retard.",
                           "Désactiver", "filter", filter.ToString(), () => SetFlags(SPI_SETFILTERKEYS, SPI_GETFILTERKEYS, 6, filter & ~(FKF_ON | FKF_HOTKEY)), "Touches filtres désactivées."));
        bool stickyHot = (sticky & (SKF_ON | SKF_HOTKEY)) == SKF_HOTKEY, filterHot = (filter & (FKF_ON | FKF_HOTKEY)) == FKF_HOTKEY;
        if (stickyHot || filterHot)
        {
            var i = new DeviceIssue
            {
                Level = DeviceStatus.Info,
                Text = "Appuyer 5 fois sur Maj, ou maintenir Maj droite 8 secondes, active les touches rémanentes ou filtres sans le vouloir (fréquent en jeu)."
            };
            i.Actions.Add(new DeviceAction
            {
                Label = "Désactiver ces raccourcis", Glyph = "",
                Run = () => Task.Run(() =>
                {
                    if (stickyHot) { Backup("sticky", sticky.ToString()); SetFlags(SPI_SETSTICKYKEYS, SPI_GETSTICKYKEYS, 2, sticky & ~SKF_HOTKEY); }
                    if (filterHot) { Backup("filter", filter.ToString()); SetFlags(SPI_SETFILTERKEYS, SPI_GETFILTERKEYS, 6, filter & ~FKF_HOTKEY); }
                    return "Raccourcis désactivés : les touches rémanentes et filtres ne s'activeront plus par erreur.";
                })
            });
            issues.Add(i);
        }
        var mouseKeys = GetFlags(SPI_GETMOUSEKEYS, 7);
        if ((mouseKeys & MKF_ON) != 0)
            issues.Add(Fix(DeviceStatus.Info, "Les touches souris sont activées : le pavé numérique déplace le pointeur au lieu de taper des chiffres.",
                           "Désactiver", "mousekeys", mouseKeys.ToString(), () => SetFlags(SPI_SETMOUSEKEYS, SPI_GETMOUSEKEYS, 7, mouseKeys & ~MKF_ON), "Touches souris désactivées."));

        if (IsToggled(VK_CAPITAL))
            issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Info, Text = "Verr. Maj est activé : tout s'écrit en majuscules (et les mots de passe sont refusés).",
                Actions = { new DeviceAction { Label = "Désactiver Verr. Maj", Glyph = "", Run = () => Task.Run(() => { PressKey(VK_CAPITAL); return "Verr. Maj désactivé."; }) } }
            });
        // Pavé numérique : seulement sur un PC de bureau (sur certains portables, Verr. Num transforme des lettres en chiffres)
        if (!laptop && !IsToggled(VK_NUMLOCK))
        {
            var initial = GetReg(@"Control Panel\Keyboard", "InitialKeyboardIndicators") ?? "";
            issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Info, Text = "Verr. Num est désactivé : le pavé numérique fait bouger le curseur au lieu de taper des chiffres.",
                Actions = { new DeviceAction { Label = "Activer (aussi au démarrage)", Glyph = "", Run = () => Task.Run(() =>
                {
                    PressKey(VK_NUMLOCK);
                    if (!long.TryParse(initial, out var n) || (n & 2) == 0)
                    {
                        Backup("numlock", initial);
                        SetReg(@"Control Panel\Keyboard", "InitialKeyboardIndicators", ((long.TryParse(initial, out n) ? n & ~0x80000000L : 0) | 2).ToString());
                    }
                    return "Verr. Num activé, et il le sera à chaque démarrage.";
                }) } }
            });
        }
        int delay = GetInt(SPI_GETKEYBOARDDELAY), speed = GetInt(SPI_GETKEYBOARDSPEED);
        if (speed >= 0 && delay >= 0 && (delay >= 3 && speed <= 10 || speed <= 3))
            issues.Add(Fix(DeviceStatus.Info, "Une touche maintenue enfoncée se répète très lentement (effacer avec Retour arrière, se déplacer avec les flèches).",
                           "Rétablir", "kbrepeat", $"{delay},{speed}", () => { SetUi(SPI_SETKEYBOARDDELAY, 1); SetUi(SPI_SETKEYBOARDSPEED, 31); }, "Répétition des touches rétablie."));
        issues.AddRange(UndoIssues("layouts", "langtoggle", "sticky", "filter", "mousekeys", "kbrepeat", "numlock"));
        return issues;
    }

    /// <summary>
    /// Disposition du clavier : passée en QWERTY par un raccourci, plusieurs dispositions installées (Alt+Maj bascule par erreur),
    /// ou disposition qui ne correspond pas à la région (lettres A/Q et Z/W inversées).
    /// </summary>
    private static IEnumerable<DeviceIssue> LayoutIssues()
    {
        var langs = Languages();
        var layouts = langs.Where(l => l.IsKeyboard).ToList();
        if (layouts.Count == 0) yield break;
        var main = layouts[0];
        var mainHkl = LoadKeyboardLayout(main.Klid, 0);
        string mainName = LayoutName(main.Klid);

        // 1. Clavier actuellement dans une autre disposition que la principale
        var fg = GetForegroundWindow();
        var current = GetKeyboardLayout(fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _));
        if (layouts.Count > 1 && mainHkl != IntPtr.Zero && current != IntPtr.Zero && current != mainHkl)
        {
            var now = layouts.FirstOrDefault(l => LoadKeyboardLayout(l.Klid, 0) == current);
            if (now != null)
                yield return new DeviceIssue
                {
                    Level = DeviceStatus.Warning,
                    Text = $"Votre clavier est passé en {LayoutName(now.Klid)} au lieu de {mainName} : certaines lettres et symboles ne sont plus à leur place.",
                    Actions = { new DeviceAction { Label = "Repasser en " + ShortName(main.Klid), Glyph = "", Primary = true,
                                                   Run = () => Task.Run(() => { Activate(mainHkl); return $"Clavier repassé en {mainName}."; }) } }
                };
        }

        // 2. Plusieurs dispositions : un raccourci bascule de l'une à l'autre par erreur
        if (layouts.Count > 1)
        {
            var names = string.Join(", ", layouts.Select(l => LayoutName(l.Klid)).Distinct());
            var toggle = GetToggle();
            bool toggleOn = toggle[0] != "3" || toggle[1] != "3";
            var i = new DeviceIssue
            {
                Level = DeviceStatus.Info,
                Text = $"Plusieurs dispositions de clavier sont installées ({names}). "
                     + (toggleOn ? "Alt+Maj, Ctrl+Maj ou Windows+Espace passent de l'une à l'autre, souvent par erreur." : "Windows+Espace passe de l'une à l'autre.")
            };
            var snapshot = Serialize(langs);
            i.Actions.Add(new DeviceAction
            {
                Label = "Ne garder que " + ShortName(main.Klid), Glyph = "", Tip = "Les autres dispositions sont retirées ; « Annuler » les remet",
                Run = () => Task.Run(() =>
                {
                    Backup("layouts", snapshot);
                    return KeepOnly(main) ? $"Seule la disposition {mainName} est gardée." : "Windows a refusé la modification : faites-la dans Heure et langue.";
                })
            });
            if (toggleOn)
                i.Actions.Add(new DeviceAction
                {
                    Label = "Désactiver Alt+Maj", Glyph = "", Tip = "Désactive Alt+Maj et Ctrl+Maj ; Windows+Espace reste disponible",
                    Run = () => Task.Run(() =>
                    {
                        Backup("langtoggle", string.Join("|", toggle));
                        SetToggle(new[] { "3", "3", "3" });
                        return "Alt+Maj et Ctrl+Maj ne changent plus la disposition du clavier.";
                    })
                });
            yield return i;
        }

        // 3. Une seule disposition, qui ne correspond pas à la région (ex. QWERTY en France)
        if (layouts.Count == 1)
        {
            var culture = CultureInfo.CurrentCulture;
            var regionKlid = culture.LCID.ToString("X8");
            if (culture.LCID != 0x1000 && regionKlid != main.Klid.ToUpperInvariant() && LayoutExists(regionKlid))
            {
                var regionHkl = LoadKeyboardLayout(regionKlid, 0);
                string kMain = Kind(mainHkl), kRegion = Kind(regionHkl);
                if (kMain != null && kRegion != null && kMain != kRegion)
                {
                    var snapshot = Serialize(langs);
                    string region; try { region = new RegionInfo(culture.Name).NativeName; } catch { region = "votre région"; }
                    yield return new DeviceIssue
                    {
                        Level = DeviceStatus.Info,
                        Text = $"Votre clavier est réglé en {mainName} alors que les claviers vendus en {region} sont en {kRegion}. "
                             + "Si des lettres ne correspondent pas à ce qui est imprimé sur les touches :",
                        Actions = { new DeviceAction { Label = "Passer en " + LayoutName(regionKlid), Glyph = "",
                                                       Tip = "Ajoutée comme disposition principale ; « Annuler » remet l'ancienne",
                                                       Run = () => Task.Run(() =>
                                                       {
                                                           Backup("layouts", snapshot);
                                                           if (!AddFirst(main, regionKlid)) return "Windows a refusé la modification : faites-la dans Heure et langue.";
                                                           Activate(LoadKeyboardLayout(regionKlid, 0));
                                                           return $"Clavier passé en {LayoutName(regionKlid)}.";
                                                       }) } }
                    };
                }
            }
        }
    }

    /// <summary>« AZERTY », « QWERTY » ou « QWERTZ » selon la lettre produite par la touche en haut à gauche des lettres.</summary>
    private static string Kind(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return null;
        uint a = MapVirtualKeyEx(0x10, 1, hkl), y = MapVirtualKeyEx(0x15, 1, hkl);
        return a == 'A' ? "AZERTY" : a == 'Q' ? (y == 'Z' ? "QWERTZ" : "QWERTY") : null;
    }

    /// <summary>« Français (AZERTY) » : nom traduit de la disposition et type de clavier.</summary>
    private static string LayoutName(string klid)
    {
        var n = ShortName(klid);
        var k = Kind(LoadKeyboardLayout(klid, 0));
        return k == null || n.Contains(k, StringComparison.OrdinalIgnoreCase) ? n : $"{n} ({k})";
    }

    private static string ShortName(string klid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}");
            if (key?.GetValue("Layout Display Name") is string res && res.StartsWith("@"))
            {
                var sb = new StringBuilder(256);
                if (SHLoadIndirectString(res, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
            }
            if (key?.GetValue("Layout Text") is string t) return t;
        }
        catch { }
        return klid;
    }

    private static bool LayoutExists(string klid)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}"); return k != null; }
        catch { return false; }
    }

    /// <summary>Change la disposition de toutes les fenêtres (comme Windows+Espace).</summary>
    private static void Activate(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return;
        PostMessage((IntPtr)0xFFFF, 0x0050, IntPtr.Zero, hkl);   // WM_INPUTLANGCHANGEREQUEST à toutes les fenêtres
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) PostMessage(fg, 0x0050, IntPtr.Zero, hkl);
    }

    // Raccourcis de changement de disposition : « 1 » Alt+Maj, « 2 » Ctrl+Maj, « 3 » aucun, « 4 » touche ²
    private static string[] GetToggle() => new[]
    {
        GetReg(@"Keyboard Layout\Toggle", "Language Hotkey") ?? GetReg(@"Keyboard Layout\Toggle", "Hotkey") ?? "1",
        GetReg(@"Keyboard Layout\Toggle", "Layout Hotkey") ?? "2",
        GetReg(@"Keyboard Layout\Toggle", "Hotkey") ?? ""
    };

    private static void SetToggle(string[] v)
    {
        SetReg(@"Keyboard Layout\Toggle", "Language Hotkey", v[0]);
        SetReg(@"Keyboard Layout\Toggle", "Layout Hotkey", v[1]);
        SetReg(@"Keyboard Layout\Toggle", "Hotkey", v.Length > 2 ? v[2] : "");
        SystemParametersInfo(SPI_SETLANGTOGGLE, 0, IntPtr.Zero, 0);
    }

    // ------------------------------------------------------------------ liste des langues (Windows PowerShell, module International)
    private static List<Layout> _languages;
    private static string _languagesKey;

    /// <summary>Liste des langues (PowerShell, environ une seconde) gardée tant que les dispositions chargées ne changent pas.</summary>
    private static List<Layout> Languages()
    {
        var n = GetKeyboardLayoutList(0, null);
        var list = new IntPtr[Math.Max(n, 1)];
        GetKeyboardLayoutList(list.Length, list);
        var key = string.Join(",", list.Select(h => h.ToInt64()).OrderBy(h => h));
        if (_languages != null && key == _languagesKey) return _languages;
        var fresh = LoadLanguages();
        if (fresh.Count > 0) { _languages = fresh; _languagesKey = key; }
        return fresh;
    }

    private static List<Layout> LoadLanguages()
    {
        var output = PowerShell("Get-WinUserLanguageList | ForEach-Object { $t = $_.LanguageTag; $n = $_.LocalizedName; "
                              + "$_.InputMethodTips | ForEach-Object { $t + '|' + $n + '|' + $_ } }");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(l => l.Split('|')).Where(p => p.Length == 3)
                     .Select(p => new Layout(p[2], p[0], p[1])).ToList();
    }

    /// <summary>« fr-FR=040C:0000040C,040C:00000409;en-US=0409:00000409 »</summary>
    private static string Serialize(List<Layout> langs) =>
        string.Join(";", langs.GroupBy(l => l.LanguageTag).Select(g => g.Key + "=" + string.Join(",", g.Select(l => l.Tip))));

    private static bool SetLanguages(IEnumerable<(string tag, string[] tips)> list)
    {
        var sb = new StringBuilder("$l = $null; ");
        foreach (var (tag, tips) in list)
        {
            sb.Append($"if ($l -eq $null) {{ $l = New-WinUserLanguageList '{Esc(tag)}' }} else {{ $l.Add('{Esc(tag)}') }}; $x = $l[$l.Count - 1]; $x.InputMethodTips.Clear(); ");
            foreach (var t in tips) sb.Append($"$x.InputMethodTips.Add('{Esc(t)}'); ");
        }
        sb.Append("Set-WinUserLanguageList $l -Force; 'ok'");
        _languages = null;
        return PowerShell(sb.ToString()).Contains("ok");
    }

    private static bool RestoreLanguages(string snapshot) =>
        SetLanguages(snapshot.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('='))
                             .Where(p => p.Length == 2).Select(p => (p[0], p[1].Split(',', StringSplitOptions.RemoveEmptyEntries))));

    /// <summary>Garde la langue principale avec sa seule disposition principale (les autres langues restent installées dans Windows).</summary>
    private static bool KeepOnly(Layout main) => SetLanguages(new[] { (main.LanguageTag, new[] { main.Tip }) });

    /// <summary>Ajoute la disposition de la région en tête de la langue principale (devient celle par défaut), sans retirer les autres.</summary>
    private static bool AddFirst(Layout main, string klid)
    {
        var langs = Languages();
        var lang = main.Tip.Split(':')[0];
        return SetLanguages(langs.GroupBy(l => l.LanguageTag).Select(g =>
            (g.Key, g.Key == main.LanguageTag ? new[] { $"{lang}:{klid}" }.Concat(g.Select(l => l.Tip)).ToArray() : g.Select(l => l.Tip).ToArray())));
    }

    private static string Esc(string s) => s.Replace("'", "''");

    private static string PowerShell(string script)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
            });
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return ""; }
            return read.Result;
        }
        catch (Exception ex) { Log.Error("PowerShell (clavier)", ex); return ""; }
    }

    // ================================================================== souris
    public static IEnumerable<DeviceIssue> Mouse(bool laptop)
    {
        var issues = new List<DeviceIssue>();
        if (GetSystemMetrics(SM_SWAPBUTTON) != 0)
            issues.Add(Fix(DeviceStatus.Info, "Les boutons de la souris sont inversés : le clic principal se fait avec le bouton droit (réglage pour gaucher). Si ce n'est pas voulu :",
                           "Remettre le clic à gauche", "swap", "1", () => SetUi(SPI_SETMOUSEBUTTONSWAP, 0), "Le clic principal est de nouveau le bouton gauche."));
        if (GetBool(SPI_GETMOUSECLICKLOCK))
            issues.Add(Fix(DeviceStatus.Warning, "Le verrouillage du clic est activé : garder le bouton appuyé un instant bloque le clic, comme si la souris restait enfoncée.",
                           "Désactiver", "clicklock", "1", () => SetPv(SPI_SETMOUSECLICKLOCK, 0), "Verrouillage du clic désactivé."));
        uint dbl = GetDoubleClickTime();
        if (dbl >= 900 || dbl <= 200)
            issues.Add(Fix(DeviceStatus.Info, dbl >= 900 ? "Le double-clic est réglé très lent : deux clics espacés ouvrent des fichiers sans le vouloir."
                                                          : "Le double-clic est réglé très rapide : il faut cliquer très vite pour ouvrir un fichier.",
                           "Rétablir", "dblclick", dbl.ToString(), () => SetUi(SPI_SETDOUBLECLICKTIME, 500), "Vitesse du double-clic rétablie."));
        int speed = GetInt(SPI_GETMOUSESPEED);
        if (speed is > 0 and <= 3 || speed >= 18)
            issues.Add(Fix(DeviceStatus.Info, speed <= 3 ? "Le pointeur de la souris est réglé très lent." : "Le pointeur de la souris est réglé très rapide : difficile de viser.",
                           "Rétablir", "speed", speed.ToString(), () => SetPv(SPI_SETMOUSESPEED, 10), "Vitesse du pointeur rétablie."));
        int lines = GetInt(SPI_GETWHEELSCROLLLINES);
        if (lines == 0)
            issues.Add(Fix(DeviceStatus.Warning, "La molette est réglée pour ne rien faire défiler.",
                           "Rétablir", "wheel", "0", () => SetUi(SPI_SETWHEELSCROLLLINES, 3), "La molette fait de nouveau défiler 3 lignes."));
        int trails = GetInt(SPI_GETMOUSETRAILS);
        if (trails > 1)
            issues.Add(Fix(DeviceStatus.Info, "Le pointeur laisse une traînée derrière lui.",
                           "Désactiver", "trails", trails.ToString(), () => SetUi(SPI_SETMOUSETRAILS, 0), "Traînée du pointeur désactivée."));
        if (laptop && GetRegDword(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status", "Enabled") == 0)
            issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Info, Text = "Le pavé tactile est désactivé (raccourci du clavier ou réglage de Windows).",
                Actions = { new DeviceAction { Label = "Réactiver", Glyph = "", Uri = "ms-settings:devices-touchpad" } }
            });
        issues.AddRange(UndoIssues("swap", "clicklock", "dblclick", "speed", "wheel", "trails"));
        return issues;
    }

    // ================================================================== son
    private static readonly System.Text.RegularExpressions.Regex HandsFree = new(@"hands-?free|mains[ -]libres|\bHFP\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Nom de l'appareil Bluetooth dans « Casque (WH-1000XM4 Hands-Free AG Audio) » → « wh-1000xm4 ».</summary>
    private static string BaseName(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\(([^)]*)\)");
        var s = m.Success ? m.Groups[1].Value : name ?? "";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"hands-?free( ag)?( audio)?|mains[ -]libres|st[ée]r[ée]o|\bHFP\b|\bA2DP\b|audio", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s.Trim().ToLowerInvariant();
    }

    public static IEnumerable<DeviceIssue> Audio()
    {
        var issues = new List<DeviceIssue>();
        var outs = AudioDevices.List(render: true);
        var ins = AudioDevices.List(render: false);
        var output = outs.FirstOrDefault(d => d.IsDefault);
        var mic = ins.FirstOrDefault(d => d.IsDefault);

        // Casque Bluetooth en « mains libres » : son de qualité téléphone
        if (output != null && HandsFree.IsMatch(output.Name))
        {
            var stereo = outs.FirstOrDefault(d => d != output && !HandsFree.IsMatch(d.Name) && BaseName(d.Name) == BaseName(output.Name) && BaseName(d.Name).Length > 0);
            var i = new DeviceIssue
            {
                Level = DeviceStatus.Warning,
                Text = $"Le son passe par {output.Name}, le mode « mains libres » du casque Bluetooth : qualité téléphone, sourde et en mono."
            };
            if (stereo != null)
                i.Actions.Add(new DeviceAction { Label = "Passer en stéréo", Glyph = "", Primary = true,
                                                 Run = () => Task.Run(() => AudioDevices.SetDefault(stereo) ? $"Le son passe maintenant par {stereo.Name}." : "Windows a refusé ce changement.") });
            else i.Actions.Add(new DeviceAction { Label = "Choisir la sortie", Glyph = "", Goto = "test:speakers" });
            issues.Add(i);
        }
        // Micro du casque Bluetooth : dès qu'une application l'ouvre, le casque bascule en qualité téléphone
        if (mic != null && HandsFree.IsMatch(mic.Name) && ins.Count > 1)
        {
            var other = ins.FirstOrDefault(d => !HandsFree.IsMatch(d.Name));
            var i = new DeviceIssue
            {
                Level = DeviceStatus.Info,
                Text = $"Le micro utilisé est celui du casque Bluetooth ({mic.Name}) : dès qu'une application l'ouvre, le son du casque passe en qualité téléphone."
            };
            if (other != null)
                i.Actions.Add(new DeviceAction { Label = "Utiliser " + other.Name, Glyph = "",
                                                 Run = () => Task.Run(() => AudioDevices.SetDefault(other) ? $"{other.Name} est maintenant le micro de Windows." : "Windows a refusé ce changement.") });
            issues.Add(i);
        }
        // Son des autres applications baissé pendant les appels (réglage « Communications »)
        var duck = GetRegDword(@"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference");
        if (duck is null or 0 or 1)
        {
            var original = duck?.ToString() ?? "";
            issues.Add(Fix(DeviceStatus.Info, duck == 0 ? "Pendant un appel (Discord, Teams…), Windows coupe le son de toutes les autres applications."
                                                        : "Pendant un appel (Discord, Teams…), Windows baisse de 80 % le son des autres applications (jeu, musique).",
                           "Ne plus baisser le son", "ducking", original, () => SetDucking("3"), "Le son des applications ne baissera plus pendant les appels."));
        }
        if (GetRegDword(@"Software\Microsoft\Multimedia\Audio", "AccessibilityMonoMixState") == 1)
            issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Info, Text = "Le son est en mono : la gauche et la droite sont mélangées (utile avec une seule oreillette).",
                Actions = { new DeviceAction { Label = "Rétablir la stéréo", Glyph = "", Uri = "ms-settings:easeofaccess-audio" } }
            });
        issues.AddRange(UndoIssues("ducking"));
        return issues;
    }

    private static void SetDucking(string v)
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Multimedia\Audio");
        if (string.IsNullOrEmpty(v)) k.DeleteValue("UserDuckingPreference", false);
        else k.SetValue("UserDuckingPreference", int.Parse(v), RegistryValueKind.DWord);
    }

    // ================================================================== outils
    /// <summary>Point avec une correction réversible : l'état d'origine est gardé avant d'appliquer.</summary>
    private static DeviceIssue Fix(DeviceStatus level, string text, string label, string id, string original, Action apply, string done) => new()
    {
        Level = level, Text = text,
        Actions = { new DeviceAction { Label = label, Glyph = "", Primary = level == DeviceStatus.Warning,
                                       Run = () => Task.Run(() => { Backup(id, original); apply(); return done; }) } }
    };

    /// <summary>Ordinateur portable (une batterie est présente) ?</summary>
    public static bool IsLaptop() => GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255;

    private static string GetReg(string path, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(path); return k?.GetValue(name)?.ToString(); } catch { return null; }
    }

    private static int? GetRegDword(string path, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(path); return k?.GetValue(name) is int i ? i : null; } catch { return null; }
    }

    private static void SetReg(string path, string name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(path);
        if (string.IsNullOrEmpty(value)) k.DeleteValue(name, false); else k.SetValue(name, value, RegistryValueKind.String);
    }

    private static void PressKey(byte vk)
    {
        keybd_event(vk, 0x45, 0x1, UIntPtr.Zero);       // KEYEVENTF_EXTENDEDKEY
        keybd_event(vk, 0x45, 0x1 | 0x2, UIntPtr.Zero); // + KEYEVENTF_KEYUP
        Thread.Sleep(300);                               // le temps que Windows traite la touche avant la nouvelle analyse
    }

    // ================================================================== état en direct
    /// <summary>
    /// Lecture de Verr. Maj / Verr. Num fournie par l'interface (thread de la fenêtre) : l'état des touches à bascule n'est
    /// fiable que lu depuis un thread qui reçoit le clavier.
    /// </summary>
    public static Func<int, bool> ToggleReader { get; set; }

    private static bool IsToggled(int vk)
    {
        try { if (ToggleReader != null) return ToggleReader(vk); } catch { }
        return ReadToggle(vk);
    }

    /// <summary>
    /// État d'une touche à bascule vu par le thread appelant (à lire depuis le thread de la fenêtre, à jour quand elle est active).
    /// Volontairement sans AttachThreadInput : se rattacher à la fenêtre d'une autre application perturbe son focus (jeux, saisie).
    /// </summary>
    public static bool ReadToggle(int vk) => (GetKeyState(vk) & 1) != 0;

    /// <summary>
    /// Empreinte de tous les réglages vérifiés (touches à bascule, accessibilité, souris, disposition active) : lecture instantanée,
    /// comparée régulièrement pour réanalyser dès que quelque chose change, quelle qu'en soit l'origine.
    /// </summary>
    public static string KeyboardMouseFingerprint(bool caps, bool num)
    {
        var fg = GetForegroundWindow();
        var hkl = GetKeyboardLayout(fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _));
        return string.Join("|", caps, num, GetFlags(SPI_GETSTICKYKEYS, 2), GetFlags(SPI_GETFILTERKEYS, 6), GetFlags(SPI_GETMOUSEKEYS, 7),
                           GetInt(SPI_GETKEYBOARDDELAY), GetInt(SPI_GETKEYBOARDSPEED), GetSystemMetrics(SM_SWAPBUTTON), GetBool(SPI_GETMOUSECLICKLOCK),
                           GetDoubleClickTime(), GetInt(SPI_GETMOUSESPEED), GetInt(SPI_GETWHEELSCROLLLINES), GetInt(SPI_GETMOUSETRAILS),
                           hkl, GetKeyboardLayoutList(0, null), string.Join(",", GetToggle()), string.Join(",", Backups.Keys.OrderBy(k => k)),
                           GetRegDword(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status", "Enabled"));
    }

    /// <summary>Empreinte des réglages du son vérifiés (atténuation pendant les appels, mono).</summary>
    public static string AudioFingerprint() =>
        $"{GetRegDword(@"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference")}|{GetRegDword(@"Software\Microsoft\Multimedia\Audio", "AccessibilityMonoMixState")}|{Backups.ContainsKey("ducking")}";

    private static uint GetFlags(uint action, int size)
    {
        var a = new uint[size]; a[0] = (uint)(size * 4);
        return SystemParametersInfo(action, a[0], a, 0) ? a[1] : 0;
    }

    private static void SetFlags(uint set, uint get, int size, uint flags)
    {
        var a = new uint[size]; a[0] = (uint)(size * 4);
        SystemParametersInfo(get, a[0], a, 0);
        a[1] = flags;
        SystemParametersInfo(set, a[0], a, SPIF);
    }

    private static int GetInt(uint action) { int v = 0; return SystemParametersInfo(action, 0, ref v, 0) ? v : -1; }
    private static bool GetBool(uint action) { int v = 0; return SystemParametersInfo(action, 0, ref v, 0) && v != 0; }
    private static void SetUi(uint action, uint value) => SystemParametersInfo(action, value, IntPtr.Zero, SPIF);
    private static void SetPv(uint action, int value) => SystemParametersInfo(action, 0, (IntPtr)value, SPIF);

    private const uint SPIF = 3;   // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
    private const uint SPI_GETKEYBOARDSPEED = 0x000A, SPI_SETKEYBOARDSPEED = 0x000B, SPI_GETKEYBOARDDELAY = 0x0016, SPI_SETKEYBOARDDELAY = 0x0017,
                       SPI_SETDOUBLECLICKTIME = 0x0020, SPI_SETMOUSEBUTTONSWAP = 0x0021, SPI_GETFILTERKEYS = 0x0032, SPI_SETFILTERKEYS = 0x0033,
                       SPI_GETMOUSEKEYS = 0x0036, SPI_SETMOUSEKEYS = 0x0037, SPI_GETSTICKYKEYS = 0x003A, SPI_SETSTICKYKEYS = 0x003B,
                       SPI_SETLANGTOGGLE = 0x005B, SPI_SETMOUSETRAILS = 0x005D, SPI_GETMOUSETRAILS = 0x005E, SPI_GETWHEELSCROLLLINES = 0x0068,
                       SPI_SETWHEELSCROLLLINES = 0x0069, SPI_GETMOUSESPEED = 0x0070, SPI_SETMOUSESPEED = 0x0071,
                       SPI_GETMOUSECLICKLOCK = 0x101E, SPI_SETMOUSECLICKLOCK = 0x101F;
    private const uint SKF_ON = 0x1, SKF_HOTKEY = 0x4, FKF_ON = 0x1, FKF_HOTKEY = 0x4, MKF_ON = 0x1;
    private const int SM_SWAPBUTTON = 23;
    private const byte VK_CAPITAL = 0x14, VK_NUMLOCK = 0x90;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint ui, [In, Out] uint[] pv, uint winIni);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint ui, ref int pv, uint winIni);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint ui, IntPtr pv, uint winIni);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayout(string klid, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr hkl);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source, StringBuilder buffer, int size, IntPtr reserved);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] list);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
