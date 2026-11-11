using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CentrePC.Core;

public enum HotkeyState
{
    /// <summary>Libre.</summary>
    Free,
    /// <summary>Utilisable, avec une remarque (remplace le menu de fenêtre de Windows…).</summary>
    Note,
    /// <summary>Utilisé par une application connue dont Centre PC sait changer le réglage (réversible).</summary>
    Freeable,
    /// <summary>Probablement utilisé par une application installée, réglage non modifiable par Centre PC.</summary>
    LikelyUsed,
    /// <summary>Réservé par Windows, sert à écrire, ou déjà pris par une application inconnue : non proposé.</summary>
    Blocked
}

/// <summary>Raccourci trouvé dans les réglages d'une autre application.</summary>
public sealed class HotkeyOwner
{
    public string App { get; set; }
    public string File { get; set; }
    /// <summary>Chemin dans le JSON (« properties/open_powerlauncher »).</summary>
    public List<string> Path { get; set; } = new();
    public bool IsObject { get; set; }
    public string Separator { get; set; } = " + ";
    public string[] Processes { get; set; } = Array.Empty<string>();
    public HotkeyCombo Combo { get; set; }
    /// <summary>Faux : Centre PC sait qui l'utilise mais ne modifie pas ce réglage (script, fichier avec commentaires…).</summary>
    public bool CanFree { get; set; } = true;
    /// <summary>Où le changer soi-même, quand Centre PC ne le fait pas.</summary>
    public string Where { get; set; }
}

public sealed class HotkeyCheck
{
    public HotkeyState State { get; set; }
    public string Text { get; set; }
    public List<HotkeyOwner> Owners { get; set; } = new();
    /// <summary>Windows refuse le raccourci sans dire qui l'a : l'utilisateur peut l'identifier en appuyant dessus.</summary>
    public bool UnknownOwner { get; set; }
}

/// <summary>Réglage d'une autre application modifié pour libérer un raccourci (remis à l'identique ensuite).</summary>
public sealed class HotkeyBackup
{
    public string App { get; set; }
    public string File { get; set; }
    public List<string> Path { get; set; } = new();
    /// <summary>Valeur d'origine (JSON) et valeur écrite par Centre PC.</summary>
    public string Original { get; set; }
    public string Written { get; set; }
    public string[] Processes { get; set; } = Array.Empty<string>();
    /// <summary>Raccourci de Centre PC pour lequel ce réglage a été libéré.</summary>
    public string For { get; set; }
}

/// <summary>
/// Qui utilise un raccourci ? Windows n'en tient pas de liste : on recoupe
/// 1) les raccourcis réservés par Windows et ceux qui servent à écrire (AltGr) ;
/// 2) les réglages des lanceurs et outils connus (PowerToys, Command Palette, Flow Launcher, Wox), modifiables de façon réversible ;
/// 3) les raccourcis par défaut d'outils installés (NVIDIA, AMD) ;
/// 4) un essai auprès de Windows (pris par une application inconnue).
/// </summary>
public static class HotkeyConflicts
{
    private const int A = HotkeyCombo.Alt, C = HotkeyCombo.Ctrl, S = HotkeyCombo.Shift, W = HotkeyCombo.Win;

    /// <summary>Suggestions proposées dans Réglages (le choix libre reste possible).</summary>
    public static readonly HotkeyCombo[] Suggestions =
    {
        new(A, 0x20), new(C | A, 0x20), new(C | S, 0x20), new(W | A, 0x20), new(A | S, 0x20), new(C | A | S, 0x20),
        new(C, 0x20), new(A, 0xC0), new(A, 0xDE), new(C | A, 0x0D), new(C | S, 0x0D), new(A, 0x51), new(0, 0x7C), new(0, 0x13)
    };

    // ------------------------------------------------------------------ 1) Windows
    private static readonly HashSet<(int, int)> WindowsReserved = new()
    {
        (A, 0x09), (A | S, 0x09), (C | A, 0x09), (A, 0x73), (A, 0x1B), (C, 0x1B), (C | S, 0x1B), (C | A, 0x2E),
        (W | A, 0x42), (W | A, 0x44), (W | A, 0x47), (W | A, 0x48), (W | A, 0x4B), (W | A, 0x4D), (W | A, 0x52), (W | A, 0x54), (W | A, 0x2C), (W | A, 0x0D),
        (W | C, 0x44), (W | C, 0x73), (W | C, 0x25), (W | C, 0x27), (W | C, 0x0D), (W | C, 0x43), (W | C, 0x46), (W | C, 0x51), (W | C, 0x4F), (W | C, 0x4E),
        (W | C | S, 0x42), (W | C | S, 0x20)
    };

    private static string WindowsUse(HotkeyCombo k)
    {
        if (k.Has(W) && !k.Has(A) && !k.Has(C)) return "Réservé par Windows";          // Windows + lettre, Windows + Maj + …
        if (WindowsReserved.Contains((k.Mods, k.Vk))) return "Réservé par Windows";
        if (k.Mods == 0 && !(k.Vk is >= 0x7C and <= 0x87 or 0x13 or 0x91)) return "Touche utilisée seule partout (écrire, naviguer)";
        if (k.Mods == S) return "Sert à écrire (majuscules)";
        if (k.Mods is C or (C | S) && k.Vk is >= 0x30 and <= 0x5A) return "Raccourci courant des applications (copier, coller, onglets…)";
        var ch = k.TypedCharacter();
        if (ch != null && k.Has(C) && k.Has(A)) return $"Sert à taper « {ch} » (AltGr)";
        return null;
    }

    private static string Remark(HotkeyCombo k) => (k.Mods, k.Vk) switch
    {
        (A, 0x20) => "Remplace le menu de fenêtre de Windows (Restaurer, Déplacer…), qui reste accessible au clic droit sur la barre de titre",
        (C, 0x20) => "Aussi utilisé par les éditeurs de code (autocomplétion) et pour changer de clavier en chinois ou japonais",
        (A, _) when k.Vk is >= 0x41 and <= 0x5A => "Peut masquer un raccourci de menu (Alt + lettre) dans certaines applications",
        _ => null
    };

    // ------------------------------------------------------------------ 2) réglages des applications connues
    private record Source(string App, Func<IEnumerable<string>> Files, string[] Processes);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static IEnumerable<string> Glob(string dir, string pattern, SearchOption opt = SearchOption.TopDirectoryOnly)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, opt) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static readonly Source[] Sources =
    {
        // PowerToys : un fichier par module, relu automatiquement par PowerToys quand il change
        new("PowerToys", () => Directory.Exists(Path.Combine(Local, @"Microsoft\PowerToys"))
            ? Directory.GetDirectories(Path.Combine(Local, @"Microsoft\PowerToys")).Select(d => Path.Combine(d, "settings.json")).Where(System.IO.File.Exists)
            : Array.Empty<string>(), Array.Empty<string>()),
        new("Command Palette", () => Directory.Exists(Path.Combine(Local, "Packages"))
            ? Directory.GetDirectories(Path.Combine(Local, "Packages"), "Microsoft.CommandPalette*").Select(d => Path.Combine(d, @"LocalState\settings.json")).Where(System.IO.File.Exists)
            : Array.Empty<string>(), new[] { "Microsoft.CmdPal.UI" }),
        new("Flow Launcher", () => Glob(Path.Combine(Roaming, @"FlowLauncher\Settings"), "Settings.json")
            .Concat(Directory.Exists(Path.Combine(Local, "FlowLauncher"))
                ? Directory.GetDirectories(Path.Combine(Local, "FlowLauncher"), "app-*").Select(d => Path.Combine(d, @"UserData\Settings\Settings.json")).Where(System.IO.File.Exists)
                : Array.Empty<string>()), new[] { "Flow.Launcher" }),
        new("Wox", () => Glob(Path.Combine(Roaming, @"Wox\Settings"), "Settings.json"), new[] { "Wox" }),
        new("ShareX", () => Glob(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX"), "HotkeysConfig.json"), new[] { "ShareX" }),
    };

    /// <summary>Tous les raccourcis trouvés dans les réglages des applications connues.</summary>
    public static List<HotkeyOwner> ScanKnownApps()
    {
        var res = new List<HotkeyOwner>();
        var disabledModules = PowerToysDisabledModules();
        foreach (var src in Sources)
            foreach (var file in SafeFiles(src))
            {
                try
                {
                    var root = JsonNode.Parse(System.IO.File.ReadAllText(file));
                    var app = src.App;
                    if (src.App == "PowerToys")
                    {
                        var module = Path.GetFileName(Path.GetDirectoryName(file));
                        if (disabledModules.Contains(module)) continue;   // module désactivé : son raccourci ne sert pas
                        app = "PowerToys (" + module + ")";
                    }
                    int before = res.Count;
                    Walk(root, new List<string>(), app, file, src.Processes, res);
                    // ShareX écrit ses raccourcis dans son propre format : on les nomme, sans les modifier
                    if (src.App == "ShareX")
                        foreach (var o in res.Skip(before)) { o.CanFree = false; o.Where = "dans ShareX › Réglages des raccourcis"; }
                }
                catch (Exception ex) { Log.Error("Raccourcis : lecture de " + file, ex); }
            }
        try { ScanWindowsTerminal(res); } catch (Exception ex) { Log.Error("Raccourcis : Windows Terminal", ex); }
        try { ScanAutoHotkey(res); } catch (Exception ex) { Log.Error("Raccourcis : AutoHotkey", ex); }
        return res;
    }

    /// <summary>Windows Terminal : seuls les raccourcis globaux (« globalSummon », « quakeMode ») comptent.</summary>
    private static void ScanWindowsTerminal(List<HotkeyOwner> res)
    {
        var packages = Path.Combine(Local, "Packages");
        if (!Directory.Exists(packages)) return;
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var dir in Directory.GetDirectories(packages, "Microsoft.WindowsTerminal*"))
        {
            var file = Path.Combine(dir, @"LocalState\settings.json");
            if (!System.IO.File.Exists(file)) continue;
            var root = JsonNode.Parse(System.IO.File.ReadAllText(file), null, opts);
            static bool Global(JsonNode cmd) =>
                (cmd is JsonValue v && v.TryGetValue<string>(out var s) && s is "quakeMode" or "globalSummon")
                || (cmd is JsonObject o && o["action"] is JsonValue a && a.TryGetValue<string>(out var act) && act is "quakeMode" or "globalSummon");
            var ids = new HashSet<string>();
            var entries = new List<JsonObject>();
            foreach (var list in new[] { root?["actions"], root?["keybindings"] })
                if (list is JsonArray arr)
                    foreach (var e in arr.OfType<JsonObject>())
                    {
                        entries.Add(e);
                        if (Global(e["command"]) && e["id"] is JsonValue id && id.TryGetValue<string>(out var sid)) ids.Add(sid);
                    }
            foreach (var e in entries)
            {
                bool global = Global(e["command"]) || (e["id"] is JsonValue id && id.TryGetValue<string>(out var sid) && ids.Contains(sid));
                if (!global) continue;
                var keys = e["keys"] is JsonArray ka ? ka.Select(x => x?.ToString()) : new[] { e["keys"]?.ToString() };
                foreach (var key in keys)
                {
                    var c = HotkeyCombo.ParseText(key);
                    if (c != null) res.Add(new HotkeyOwner { App = "Windows Terminal (fenêtre rapide)", File = file, Combo = c, CanFree = false,
                                                             Where = "dans Windows Terminal › Paramètres › Actions" });
                }
            }
        }
    }

    /// <summary>Scripts AutoHotkey en cours d'exécution : leurs lignes « ^!Space:: » sont des raccourcis globaux.</summary>
    private static void ScanAutoHotkey(List<HotkeyOwner> res)
    {
        using var searcher = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name LIKE 'AutoHotkey%'");
        foreach (var p in searcher.Get())
        {
            var cmd = p["CommandLine"]?.ToString() ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(cmd, @"""([^""]+\.ahk)""|(\S+\.ahk)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var script = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
            if (script == null || !System.IO.File.Exists(script)) continue;
            foreach (var line in System.IO.File.ReadLines(script))
            {
                var h = System.Text.RegularExpressions.Regex.Match(line, @"^\s*[~*$]*([#!^+<>]*)([A-Za-z0-9]+|\S)(\s+up)?::");
                if (!h.Success) continue;
                int mods = 0;
                foreach (var ch in h.Groups[1].Value)
                    mods |= ch switch { '#' => W, '!' => A, '^' => C, '+' => S, _ => 0 };
                var key = HotkeyCombo.ParseText(h.Groups[2].Value);
                if (key == null) continue;
                res.Add(new HotkeyOwner { App = $"AutoHotkey ({Path.GetFileName(script)})", File = script, Combo = new HotkeyCombo(mods, key.Vk), CanFree = false,
                                          Where = "dans le script " + Path.GetFileName(script) });
            }
        }
    }

    private static IEnumerable<string> SafeFiles(Source s) { try { return s.Files().ToList(); } catch { return Array.Empty<string>(); } }

    private static HashSet<string> PowerToysDisabledModules()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var general = Path.Combine(Local, @"Microsoft\PowerToys\settings.json");
            if (System.IO.File.Exists(general) && JsonNode.Parse(System.IO.File.ReadAllText(general))?["enabled"] is JsonObject en)
                foreach (var kv in en)
                    if (kv.Value is JsonValue v && v.TryGetValue<bool>(out var on) && !on) set.Add(kv.Key);
        }
        catch { }
        return set;
    }

    /// <summary>Parcourt le JSON : objets {win, ctrl, alt, shift, code} (PowerToys) et textes « Alt + Space » des propriétés *Hotkey*.</summary>
    private static void Walk(JsonNode node, List<string> path, string app, string file, string[] processes, List<HotkeyOwner> res)
    {
        if (node is JsonObject o)
        {
            if (TryObjectCombo(o, out var combo))
            {
                res.Add(new HotkeyOwner { App = app, File = file, Path = path.ToList(), IsObject = true, Processes = processes, Combo = combo });
                return;
            }
            foreach (var kv in o)
            {
                path.Add(kv.Key);
                if (kv.Value is JsonValue v && kv.Key.Contains("hotkey", StringComparison.OrdinalIgnoreCase) && v.TryGetValue<string>(out var text))
                {
                    var c = HotkeyCombo.ParseText(text);
                    if (c != null)
                        res.Add(new HotkeyOwner { App = app, File = file, Path = path.ToList(), Processes = processes, Combo = c,
                                                  Separator = text.Contains(" + ") ? " + " : "+" });
                }
                else Walk(kv.Value, path, app, file, processes, res);
                path.RemoveAt(path.Count - 1);
            }
        }
        else if (node is JsonArray a)
            for (int i = 0; i < a.Count; i++)
            {
                path.Add("#" + i);
                Walk(a[i], path, app, file, processes, res);
                path.RemoveAt(path.Count - 1);
            }
    }

    private static bool TryObjectCombo(JsonObject o, out HotkeyCombo combo)
    {
        combo = null;
        if (!(o["code"] is JsonValue cv && cv.TryGetValue<int>(out var code)) || code == 0) return false;
        bool Flag(string n) => o[n] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        if (!o.ContainsKey("win") || !o.ContainsKey("alt")) return false;
        combo = new HotkeyCombo((Flag("alt") ? A : 0) | (Flag("ctrl") ? C : 0) | (Flag("shift") ? S : 0) | (Flag("win") ? W : 0), code);
        return true;
    }

    // ------------------------------------------------------------------ 3) raccourcis par défaut d'outils installés
    private static readonly (string[] names, string app, (int, int)[] keys)[] KnownDefaults =
    {
        (new[] { "NVIDIA App", "GeForce Experience" }, "NVIDIA (superposition, captures)",
            new[] { (A, 0x5A), (A, 0x52), (A, 0x70), (A, 0x78), (A, 0x79), (A, 0x7B) }),
        (new[] { "AMD Software" }, "AMD Software (superposition, captures)",
            new[] { (A, 0x52), (C | S, 0x4F), (C | S, 0x45), (C | S, 0x53), (C | S, 0x49), (C | S, 0x52) }),
        // Assistants d'IA : ils prennent leur raccourci à leur démarrage ; celui qui démarre en second ne l'a pas
        (new[] { "Claude" }, "Claude (saisie rapide)", new[] { (C | A, 0x20) }),
        (new[] { "ChatGPT" }, "ChatGPT (fenêtre rapide)", new[] { (A, 0x20) }),
        (new[] { "Gemini" }, "Gemini (fenêtre rapide)", new[] { (A, 0x20) }),
    };

    /// <summary>Application installée dont c'est le raccourci par défaut, ou null.</summary>
    public static string KnownDefaultOwner(HotkeyCombo k, IEnumerable<string> installedNames)
    {
        var names = installedNames?.ToList() ?? new();
        foreach (var (appNames, app, keys) in KnownDefaults)
            if (keys.Contains((k.Mods, k.Vk)) && names.Any(n => appNames.Any(a => n.Contains(a, StringComparison.OrdinalIgnoreCase))))
                return app;
        return null;
    }

    // ------------------------------------------------------------------ verdict
    /// <summary>
    /// État d'un raccourci. <paramref name="mine"/> : raccourci actuellement utilisé par Centre PC (pas d'essai auprès de Windows).
    /// <paramref name="installedNames"/> : noms des applications installées (raccourcis par défaut connus).
    /// </summary>
    public static HotkeyCheck Check(HotkeyCombo k, HotkeyCombo mine, List<HotkeyOwner> scanned, IEnumerable<string> installedNames)
    {
        if (k.IsNone) return new HotkeyCheck { State = HotkeyState.Free, Text = "Le lanceur s'ouvre seulement depuis l'icône près de l'horloge" };
        var win = WindowsUse(k);
        if (win != null) return new HotkeyCheck { State = HotkeyState.Blocked, Text = win };

        var owners = scanned.Where(o => o.Combo == k).ToList();
        if (owners.Count > 0)
        {
            var apps = string.Join(", ", owners.Select(o => o.App).Distinct());
            // Propriétaire connu mais réglage que Centre PC ne modifie pas : on dit qui, et où le changer
            var manual = owners.Where(o => !o.CanFree).ToList();
            if (manual.Count > 0)
                return new HotkeyCheck
                {
                    State = HotkeyState.Blocked, Owners = owners,
                    Text = $"Utilisé par {apps} · à changer {manual[0].Where} si vous voulez ce raccourci"
                };
            return new HotkeyCheck { State = HotkeyState.Freeable, Owners = owners, Text = $"Utilisé par {apps} · peut être libéré (réversible)" };
        }

        var known = KnownDefaultOwner(k, installedNames);
        var identified = Settings.Current.HotkeyIdentified;
        if (k != mine && !TrayHost.IsFree(k))
        {
            // Windows refuse sans dire qui : nommé seulement si identifié en appuyant, ou si c'est le raccourci par défaut
            // d'une application installée (pris, et attendu chez elle : on peut le dire)
            if (identified.TryGetValue(k.Serialize(), out var who))
                return new HotkeyCheck { State = HotkeyState.Blocked, Text = $"Utilisé par {who} (identifié en appuyant sur le raccourci)" };
            if (known != null)
                return new HotkeyCheck { State = HotkeyState.Blocked, Text = $"Utilisé par {known} : c'est son raccourci par défaut", UnknownOwner = true };
            return new HotkeyCheck { State = HotkeyState.Blocked, Text = "Déjà pris par une autre application", UnknownOwner = true };
        }
        identified.Remove(k.Serialize());   // le raccourci a été libéré entre-temps
        // Libre pour l'instant, mais attendu par une application installée : celle qui démarre en second le perd
        if (known != null && k != mine)
            return new HotkeyCheck
            {
                State = HotkeyState.LikelyUsed,
                Text = $"Raccourci par défaut de {known} : s'il est pris par Centre PC, {known.Split(' ')[0]} ne pourra plus l'utiliser"
            };
        if (known != null)
            return new HotkeyCheck { State = HotkeyState.LikelyUsed, Text = $"Aussi le raccourci par défaut de {known}, qui ne peut plus l'utiliser tant que Centre PC l'a" };

        var note = Remark(k);
        return note != null ? new HotkeyCheck { State = HotkeyState.Note, Text = note } : new HotkeyCheck { State = HotkeyState.Free, Text = "Disponible" };
    }

    // ------------------------------------------------------------------ libérer, puis remettre
    /// <summary>
    /// Change le raccourci des applications qui utilisent <paramref name="k"/> pour un raccourci libre, en sauvegardant
    /// le réglage d'origine. Renvoie (application, nouveau raccourci) pour chaque changement, ou l'erreur.
    /// </summary>
    public static (List<(string app, HotkeyCombo now)> done, string error) Free(HotkeyCheck check, HotkeyCombo k, List<HotkeyOwner> scanned)
    {
        var done = new List<(string, HotkeyCombo)>();
        var taken = scanned.Select(o => o.Combo).Append(k).ToHashSet();
        foreach (var group in check.Owners.GroupBy(o => o.File, StringComparer.OrdinalIgnoreCase))
        {
            var procs = group.First().Processes;
            var restart = StopProcesses(procs);
            try
            {
                var text = System.IO.File.ReadAllText(group.Key);
                var root = JsonNode.Parse(text);
                foreach (var o in group)
                {
                    var alt = Alternatives.FirstOrDefault(c => !taken.Contains(c) && WindowsUse(c) == null && TrayHost.IsFree(c));
                    if (alt == null) return (done, "aucun raccourci de remplacement disponible");
                    taken.Add(alt);
                    var original = Get(root, o.Path)?.ToJsonString();
                    JsonNode written = o.IsObject ? ObjectFor(Get(root, o.Path) as JsonObject, alt) : JsonValue.Create(alt.ToEnglish(o.Separator));
                    Set(root, o.Path, written);
                    Settings.Current.HotkeyBackups.Add(new HotkeyBackup
                    {
                        App = o.App, File = o.File, Path = o.Path, Original = original, Written = written.ToJsonString(),
                        Processes = procs, For = k.Serialize()
                    });
                    done.Add((o.App, alt));
                }
                BackupFile(group.Key, text);
                System.IO.File.WriteAllText(group.Key, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Error("Raccourcis : libération dans " + group.Key, ex); return (done, ex.Message); }
            finally { StartProcesses(restart); Settings.Save(); }
        }
        return (done, null);
    }

    /// <summary>Remet les réglages d'origine des autres applications (sauf ceux libérés pour <paramref name="keep"/>).</summary>
    public static int RestoreAll(HotkeyCombo keep = null)
    {
        var cfg = Settings.Current;
        var todo = cfg.HotkeyBackups.Where(b => keep == null || b.For != keep.Serialize()).ToList();
        int restored = 0;
        foreach (var group in todo.GroupBy(b => b.File, StringComparer.OrdinalIgnoreCase))
        {
            if (!System.IO.File.Exists(group.Key)) { foreach (var b in group) cfg.HotkeyBackups.Remove(b); continue; }
            var restart = StopProcesses(group.First().Processes);
            try
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(group.Key));
                foreach (var b in group)
                {
                    // Si l'utilisateur a lui-même changé ce raccourci depuis, on respecte son choix
                    if (Get(root, b.Path)?.ToJsonString() == b.Written && b.Original != null)
                    {
                        Set(root, b.Path, JsonNode.Parse(b.Original));
                        restored++;
                    }
                    cfg.HotkeyBackups.Remove(b);
                }
                System.IO.File.WriteAllText(group.Key, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Error("Raccourcis : remise en état de " + group.Key, ex); }
            finally { StartProcesses(restart); }
        }
        Settings.Save();
        return restored;
    }

    /// <summary>Raccourcis de remplacement donnés aux autres applications.</summary>
    private static readonly HotkeyCombo[] Alternatives =
    {
        new(C | A | S, 0x20), new(A | S, 0x20), new(C | S, 0x20), new(C | A, 0x20), new(W | C | A, 0x20), new(C | A | S, 0x0D)
    };

    private static JsonObject ObjectFor(JsonObject current, HotkeyCombo c)
    {
        var o = current?.DeepClone() as JsonObject ?? new JsonObject();
        o["win"] = c.Has(W); o["ctrl"] = c.Has(C); o["alt"] = c.Has(A); o["shift"] = c.Has(S); o["code"] = c.Vk;
        if (o.ContainsKey("key")) o["key"] = System.Windows.Input.KeyInterop.KeyFromVirtualKey(c.Vk).ToString();
        return o;
    }

    private static JsonNode Get(JsonNode root, List<string> path)
    {
        var n = root;
        foreach (var p in path)
        {
            if (n == null) return null;
            n = p.StartsWith('#') && n is JsonArray a ? (int.TryParse(p[1..], out var i) && i < a.Count ? a[i] : null) : (n as JsonObject)?[p];
        }
        return n;
    }

    private static void Set(JsonNode root, List<string> path, JsonNode value)
    {
        var parent = Get(root, path.Take(path.Count - 1).ToList());
        var last = path[^1];
        if (last.StartsWith('#') && parent is JsonArray a && int.TryParse(last[1..], out var i)) a[i] = value;
        else if (parent is JsonObject o) o[last] = value;
    }

    /// <summary>Copie intégrale du fichier avant toute modification (dans les réglages de Centre PC), par sécurité.</summary>
    private static void BackupFile(string file, string text)
    {
        try
        {
            var dir = Path.Combine(Settings.DataDir, "Sauvegardes raccourcis");
            Directory.CreateDirectory(dir);
            var name = string.Join("_", file.Split(Path.GetInvalidFileNameChars().Append(':').Append('\\').ToArray(), StringSplitOptions.RemoveEmptyEntries));
            var dest = Path.Combine(dir, name);
            if (!System.IO.File.Exists(dest)) System.IO.File.WriteAllText(dest, text);   // la première copie est l'état d'origine
        }
        catch { }
    }

    /// <summary>Ferme l'application le temps de modifier ses réglages (sinon elle les réécrirait en quittant). Renvoie de quoi la relancer.</summary>
    private static List<string> StopProcesses(string[] names)
    {
        var restart = new List<string>();
        foreach (var n in names ?? Array.Empty<string>())
            foreach (var p in Process.GetProcessesByName(n))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (!p.CloseMainWindow() || !p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
                    if (exe != null && !restart.Contains(exe, StringComparer.OrdinalIgnoreCase)) restart.Add(exe);
                }
                catch (Exception ex) { Log.Error("Raccourcis : fermeture de " + n, ex); }
            }
        return restart;
    }

    private static void StartProcesses(List<string> exes)
    {
        foreach (var exe in exes)
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Raccourcis : relance de " + exe, ex); }
    }
}
