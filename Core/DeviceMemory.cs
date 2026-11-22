using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Mémoire vive (RAM) et carte son.</summary>
public static partial class DeviceHealth
{
    // ------------------------------------------------------------------ mémoire vive
    /// <summary>
    /// Mémoire vive : quantité, utilisation, barrettes et emplacements, et les problèmes fréquents :
    /// profil XMP / EXPO non activé (RAM moins rapide que prévu), une seule barrette ou deux sur le même canal (bande passante réduite),
    /// barrettes différentes, mémoire presque pleine, mémoire réservée anormalement, erreurs matérielles enregistrées par Windows.
    /// </summary>
    private static DeviceCard Memory()
    {
        var c = new DeviceCard
        {
            Key = "memory", Title = "Mémoire vive (RAM)", Glyph = "", Color = "#8B5CF6",
            Software = new[] { "Corsair iCUE::corsair", "G.SKILL Trident Z Lighting Control::g.skill,f4-,f5-", "Kingston FURY CTRL::kingston,kf", "TeamGroup T-FORCE BLITZ::team,tf",
                               "CPU-Z", "Ryzen Master::amd", "Intel Extreme Tuning Utility::intel" },
            Actions =
            {
                new DeviceAction { Label = "Voir ce qui l'utilise", Glyph = "", Uri = "taskmgr.exe", Tip = "Gestionnaire des tâches : applications classées par mémoire utilisée" },
                new DeviceAction { Label = "Tester la mémoire", Glyph = "", Uri = "mdsched.exe",
                                   Tip = "Diagnostic de mémoire de Windows : il redémarre le PC pour tester la RAM (Windows vous demande quand)" }
            }
        };

        // Utilisation actuelle
        var st = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref st);
        double total = st.TotalPhys, used = st.TotalPhys - st.AvailPhys;
        var usage = new DeviceGroup { Title = "Utilisation", Subtitle = $"{Gb(total)} utilisables par Windows", Glyph = "", Level = DeviceStatus.Ok };
        double pct = total > 0 ? used / total : 0;
        usage.Gauges.Add(new DeviceGauge { Label = "Utilisée maintenant", Detail = $"{Gb(used)} sur {Gb(total)} ({pct * 100:0} %)", Fraction = pct,
                                           Level = pct > 0.92 ? DeviceStatus.Warning : DeviceStatus.Ok });
        // Mémoire virtuelle : comparée à ce que Windows peut atteindre, pas seulement à la taille actuelle du fichier
        // d'échange. Géré automatiquement, il grandit tout seul (jeux, montage vidéo) : 94 % de sa taille du moment
        // n'est alors pas un risque. Seuls comptent un fichier d'échange bridé ou un disque plein, et les alertes de Windows.
        double committed = st.TotalPageFile - st.AvailPageFile;
        var commit = CommitCapacity(committed, st.TotalPageFile, total, PageFiles(out bool automatic), automatic);
        double commitPct = commit.Potential > 0 ? committed / commit.Potential : 0;
        var lowMemory = LowVirtualMemoryEvent();
        usage.Gauges.Add(new DeviceGauge
        {
            Label = "Mémoire virtuelle engagée",
            Detail = commit.Growth > 0 ? $"{Gb(committed)} sur {Gb(commit.Potential)} possibles ({commitPct * 100:0} %)" : $"{commitPct * 100:0} %",
            Fraction = commitPct, Level = commit.Problem != null || lowMemory != null ? DeviceStatus.Warning : DeviceStatus.Ok
        });
        c.Groups.Add(usage);
        if (pct > 0.92)
            c.Issues.Add(new DeviceIssue
            {
                Text = $"La mémoire est presque pleine ({pct * 100:0} %) : le PC ralentit. Fermez les applications ou onglets inutiles.",
                Actions = { new DeviceAction { Label = "Voir ce qui l'utilise", Glyph = "", Uri = "taskmgr.exe", Primary = true } }
            });
        if (commit.Problem != null || lowMemory != null)
            c.Issues.Add(new DeviceIssue
            {
                Text = lowMemory != null
                    ? $"Windows a manqué de mémoire virtuelle le {lowMemory:d MMMM à HH:mm} : des applications ont pu planter. " + (commit.Problem ?? "Fermez les applications inutiles pendant les jeux ou logiciels lourds.")
                    : commit.Problem,
                Actions = { new DeviceAction { Label = "Mémoire virtuelle", Glyph = "\uE713", Uri = "SystemPropertiesPerformance.exe", Tip = "Onglet « Avancé », puis « Modifier » : cochez « Gestion automatique du fichier d'échange »" } }
            });

        // Barrettes et emplacements
        var modules = CachedQuery("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, BankLabel, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        long slots = CachedQuery("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray WHERE Use = 3").Sum(o => Num(o, "MemoryDevices"));
        double installed = modules.Sum(o => (double)Num(o, "Capacity"));
        if (modules.Count > 0)
        {
            var type = MemoryType(Num(modules[0], "SMBIOSMemoryType"));
            long configured = modules.Select(o => Num(o, "ConfiguredClockSpeed")).Where(v => v > 0).DefaultIfEmpty(0).Min();
            c.Lines.Add($"{Gb(installed)} installés" + (type != null ? $" · {type}" : "") + (configured > 0 ? $" · {configured} MT/s" : "")
                        + $" · {Plural(modules.Count, "barrette")}" + (slots >= modules.Count && slots > 0 ? $" sur {Plural((int)slots, "emplacement")}" : ""));
            foreach (var m in modules)
            {
                var where = Str(m, "DeviceLocator")?.Trim();
                var maker = Clean(Str(m, "Manufacturer"));
                var part = Clean(Str(m, "PartNumber"));
                c.Lines.Add($"   {(string.IsNullOrEmpty(where) ? "Emplacement" : where)} : {Gb(Num(m, "Capacity"))}"
                            + (maker != null || part != null ? $" · {string.Join(" ", new[] { maker, part }.Where(x => x != null))}" : ""));
                c.Hardware += $" {maker} {part}";
            }

            // RAM plus lente que prévu : profil XMP (Intel) / EXPO (AMD) non activé dans le BIOS
            long rated = modules.Select(o => Math.Max(Num(o, "Speed"), RatedFromPart(Str(o, "PartNumber")))).DefaultIfEmpty(0).Min();
            if (configured > 0 && rated > configured + 150 && !InputChecks.IsLaptop())
                c.Issues.Add(new DeviceIssue
                {
                    Text = $"La RAM fonctionne à {configured} MT/s alors qu'elle est prévue pour {rated} MT/s : le profil XMP (Intel) ou EXPO (AMD) n'est sans doute pas activé dans le BIOS. "
                         + "Si la carte mère le permet, l'activer rend le PC plus rapide, surtout en jeu.",
                    Actions = { new DeviceAction { Label = "Redémarrer dans le BIOS", Glyph = "",
                                                   Tip = "Redémarre immédiatement le PC dans le BIOS (UEFI) : enregistrez votre travail avant. Cherchez « XMP », « EXPO » ou « DOCP ».",
                                                   Run = () => Elevated("shutdown.exe", "/r /fw /t 5", "Le PC redémarre dans le BIOS dans 5 secondes…") } }
                });

            // Canaux : une seule barrette, ou deux barrettes sur le même canal
            var channels = modules.Select(o => Channel(Str(o, "DeviceLocator"), Str(o, "BankLabel"))).ToList();
            bool laptopSoldered = slots <= 1;
            if (modules.Count == 1 && slots >= 2)
                c.Issues.Add(new DeviceIssue
                {
                    Level = DeviceStatus.Info,
                    Text = "Une seule barrette : la RAM fonctionne en simple canal, avec jusqu'à deux fois moins de débit (jeux, carte graphique intégrée). "
                         + "Une seconde barrette identique sur l'emplacement conseillé par le manuel de la carte mère double ce débit."
                });
            else if (modules.Count == 2 && !laptopSoldered && channels.All(ch => ch != null) && channels.Distinct().Count() == 1)
                c.Issues.Add(new DeviceIssue
                {
                    Text = $"Les deux barrettes sont sur le même canal ({string.Join(" et ", modules.Select(o => Str(o, "DeviceLocator")))}) : elles fonctionnent en simple canal. "
                         + "Placez-les sur deux canaux différents (en général A2 et B2, voir le manuel de la carte mère)."
                });

            // Barrettes différentes : source fréquente d'instabilité et de vitesse réduite
            var distinct = modules.Select(o => $"{Num(o, "Capacity")}|{Clean(Str(o, "PartNumber"))}").Distinct().Count();
            if (modules.Count > 1 && distinct > 1)
                c.Issues.Add(new DeviceIssue
                {
                    Level = DeviceStatus.Info,
                    Text = "Les barrettes ne sont pas identiques (taille ou modèle) : elles fonctionnent à la vitesse de la plus lente, et peuvent causer des plantages. "
                         + "En cas d'écran bleu, testez la mémoire."
                });

            // Mémoire réservée : forte différence entre installée et utilisable
            if (installed > 0 && total > 0 && installed - total > Math.Max(2.5e9, installed * 0.2))
                c.Issues.Add(new DeviceIssue
                {
                    Level = DeviceStatus.Info,
                    Text = $"{Gb(installed - total)} sur {Gb(installed)} ne sont pas utilisables par Windows. La carte graphique intégrée en réserve une partie ; "
                         + "au-delà, vérifiez que « Mémoire maximale » n'est pas cochée dans la configuration du système (Démarrer → Options avancées).",
                    Actions = { new DeviceAction { Label = "Configuration du système", Glyph = "", Uri = "msconfig.exe" } }
                });
        }
        if (installed > 0 && installed < 7.5e9)
            c.Issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Info,
                Text = $"Le PC a {Gb(installed)} de RAM : c'est juste pour Windows 11 avec un navigateur et quelques applications ouvertes. 16 Go sont recommandés."
            });

        // Erreurs matérielles de mémoire enregistrées par Windows (30 derniers jours) et dernier diagnostic
        if (_memEvents == null || DateTime.UtcNow - _memEventsAt > TimeSpan.FromMinutes(2)) { _memEvents = MemoryEvents(); _memEventsAt = DateTime.UtcNow; }
        var (errors, diag) = _memEvents.Value;
        if (errors > 0)
            c.Issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Warning,
                Text = $"Windows a enregistré {Plural(errors, "erreur matérielle")} de mémoire ces 30 derniers jours : une barrette peut être défectueuse ou mal enfoncée.",
                Actions = { new DeviceAction { Label = "Tester la mémoire", Glyph = "", Uri = "mdsched.exe", Primary = true } }
            });
        if (diag == false)
            c.Issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Problem,
                Text = "Le dernier diagnostic de mémoire de Windows a trouvé des erreurs : une barrette est probablement défectueuse. "
                     + "Testez-les une par une pour trouver laquelle remplacer."
            });
        else if (diag == true) c.Lines.Add("Dernier diagnostic de mémoire de Windows : aucune erreur");

        c.Headline = c.Issues.Any(i => i.Level == DeviceStatus.Problem) ? "La mémoire présente des erreurs."
                   : c.Issues.Any(i => i.Level == DeviceStatus.Warning) ? "La mémoire demande votre attention."
                   : c.Issues.Count > 0 ? "La mémoire fonctionne · des améliorations sont possibles."
                   : "La mémoire fonctionne normalement.";
        return c;
    }

    private static (int errors, bool? diag)? _memEvents;
    private static DateTime _memEventsAt;

    private static string Gb(double bytes) => bytes >= 1e9 ? $"{bytes / 1073741824.0:0.#} Go" : $"{bytes / 1048576.0:0} Mo";
    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {Regex.Replace(word, @"(\w+)", "$1s")}";
    private static string Clean(string s) => string.IsNullOrWhiteSpace(s) || Regex.IsMatch(s.Trim(), @"^(0+|unknown|undefined|[0-9A-F]{4})$", RegexOptions.IgnoreCase) ? null : s.Trim();

    private static string MemoryType(long smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => null
    };

    private static readonly long[] KnownSpeeds = { 2666, 2800, 2933, 3000, 3200, 3333, 3466, 3600, 3733, 3866, 4000, 4133, 4266, 4400, 4600, 4800,
                                                   5200, 5600, 6000, 6200, 6400, 6600, 6800, 7000, 7200, 7600, 8000, 8200, 8400 };

    /// <summary>Vitesse annoncée dans la référence (« CMK16GX4M2B3200C16 » → 3200, « F5-6000J3038F16G » → 6000), sinon 0.</summary>
    public static long RatedFromPart(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return 0;
        foreach (Match m in Regex.Matches(part, @"(?<!\d)(\d{4})(?!\d)"))
            if (long.TryParse(m.Groups[1].Value, out var v) && KnownSpeeds.Contains(v)) return v;
        return 0;
    }

    /// <summary>Canal d'un emplacement (« DIMM_A2 », « ChannelB-DIMM1 », « P0 CHANNEL A ») ; null si inconnu.</summary>
    private static string Channel(string locator, string bank)
    {
        foreach (var s in new[] { locator, bank })
        {
            if (string.IsNullOrEmpty(s)) continue;
            var m = Regex.Match(s, @"channel\s*([A-H])\b|chan(?:nel)?[\s_-]*([A-H])|DIMM[\s_-]*([A-H])\d", RegexOptions.IgnoreCase);
            if (m.Success) return (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value).ToUpperInvariant();
        }
        return null;
    }

    /// <summary>Fichier d'échange : taille actuelle, taille maximale réglée (0 = gérée par Windows), taille et place libre du disque.</summary>
    public readonly record struct PageFileInfo(double Allocated, double Maximum, double VolumeSize, double Free);

    /// <summary>Ce que la mémoire virtuelle peut encore gagner, et le problème réel s'il y en a un (null : aucun).</summary>
    public readonly record struct CommitInfo(double Potential, double Growth, string Problem);

    /// <summary>
    /// Limite de mémoire virtuelle atteignable : la limite actuelle plus ce que le fichier d'échange peut encore grandir.
    /// Géré par Windows : jusqu'à 3 fois la mémoire vive (4 Go au moins), sans dépasser le huitième du disque ni sa place
    /// libre (2 Go gardés). Taille fixée : jusqu'au maximum réglé. Un problème n'est signalé qu'au-delà de 90 % de cette
    /// limite atteignable, avec sa cause (fichier d'échange absent, bridé, disque plein).
    /// </summary>
    public static CommitInfo CommitCapacity(double committed, double currentLimit, double ram, IReadOnlyList<PageFileInfo> files, bool automatic)
    {
        const double Margin = 2.0 * 1024 * 1024 * 1024;
        double growth = 0;
        bool diskBound = false;
        foreach (var f in files)
        {
            double max = automatic || f.Maximum <= 0
                ? Math.Min(Math.Max(3 * ram, 4.0 * 1024 * 1024 * 1024), f.VolumeSize > 0 ? f.VolumeSize / 8 : double.MaxValue)
                : f.Maximum;
            double wanted = Math.Max(0, max - f.Allocated);
            double room = Math.Max(0, f.Free - Margin);
            if (room < wanted) diskBound = true;
            growth += Math.Min(wanted, room);
        }
        double potential = currentLimit + growth;
        if (potential <= 0 || committed / potential <= 0.9) return new CommitInfo(potential, growth, null);
        string problem = files.Count == 0
            ? "La mémoire virtuelle est presque épuisée et le fichier d'échange est désactivé : des applications peuvent planter. Laissez Windows le gérer automatiquement."
            : diskBound
                ? "La mémoire virtuelle est presque épuisée : le disque du fichier d'échange est presque plein et l'empêche de grandir. Libérez de la place sur ce disque."
                : !automatic
                    ? "La mémoire virtuelle est presque épuisée : le fichier d'échange a une taille limitée. Laissez Windows le gérer automatiquement."
                    : "La mémoire virtuelle est presque épuisée : des applications peuvent planter. Fermez les applications inutiles.";
        return new CommitInfo(potential, growth, problem);
    }

    /// <summary>Fichiers d'échange présents, avec leur réglage et le disque qui les porte.</summary>
    private static List<PageFileInfo> PageFiles(out bool automatic)
    {
        automatic = Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").Select(o => Str(o, "AutomaticManagedPagefile")).FirstOrDefault() == "True";
        const double Mb = 1024 * 1024;
        var settings = Query("SELECT Name, MaximumSize FROM Win32_PageFileSetting")
            .ToDictionary(o => Str(o, "Name") ?? "", o => Num(o, "MaximumSize") * Mb, StringComparer.OrdinalIgnoreCase);
        var list = new List<PageFileInfo>();
        foreach (var o in Query("SELECT Name, AllocatedBaseSize FROM Win32_PageFileUsage"))
        {
            var name = Str(o, "Name") ?? "";
            double volume = 0, free = 0;
            try { var d = new DriveInfo(System.IO.Path.GetPathRoot(name)); if (d.IsReady) { volume = d.TotalSize; free = d.AvailableFreeSpace; } } catch { }
            list.Add(new PageFileInfo(Num(o, "AllocatedBaseSize") * Mb, settings.TryGetValue(name, out var max) ? max : 0, volume, free));
        }
        return list;
    }

    /// <summary>Dernière alerte « mémoire virtuelle insuffisante » de Windows sur 7 jours (null : aucune).</summary>
    private static DateTime? LowVirtualMemoryEvent()
    {
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004 and TimeCreated[timediff(@SystemTime) <= 604800000]]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            using var e = r.ReadEvent();
            return e?.TimeCreated;
        }
        catch { return null; }
    }

    /// <summary>Erreurs mémoire signalées par le matériel (WHEA) en 30 jours, et résultat du dernier diagnostic (null : aucun).</summary>
    private static (int errors, bool? diag) MemoryEvents()
    {
        int errors = 0;
        bool? diag = null;
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=19 or EventID=47 or EventID=1 or EventID=46) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]");
            using var r = new EventLogReader(q);
            for (var e = r.ReadEvent(); e != null; e = r.ReadEvent())
                using (e)
                {
                    string text = null;
                    try { text = e.FormatDescription(); } catch { }
                    if (e.Id is 19 or 47 || Regex.IsMatch(text ?? "", "m[ée]moire|memory", RegexOptions.IgnoreCase)) errors++;
                }
        }
        catch { }
        try
        {
            var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            using var e = r.ReadEvent();
            if (e != null) diag = e.Id switch { 1101 or 1201 => true, 1102 or 1202 => false, _ => null };
        }
        catch { }
        return (errors, diag);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    // ------------------------------------------------------------------ état en direct
    /// <summary>
    /// Empreinte instantanée de ce qu'affiche une carte (sans WMI ni PowerShell) : comparée régulièrement, elle déclenche la
    /// réanalyse de la carte dès que l'état réel change (volume, place libre, charge, fréquence, connexion…).
    /// null : carte suivie seulement par les notifications de Windows (périphérique branché ou retiré).
    /// </summary>
    public static string Fingerprint(string key)
    {
        try
        {
            switch (key)
            {
                case "audio":
                {
                    var o = CoreAudio.Default(render: true);
                    var i = CoreAudio.Default(render: false);
                    return $"{o?.Name}|{Math.Round(o?.Volume ?? 0)}|{o?.Muted}|{i?.Name}|{Math.Round(i?.Volume ?? 0)}|{i?.Muted}|"
                         + $"{AudioDevices.List(true).Count}|{AudioDevices.List(false).Count}|{InputChecks.AudioFingerprint()}";
                }
                case "storage":
                    return string.Join("|", DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                                                     .Select(d => $"{d.Name}{d.VolumeLabel}:{Math.Round(d.AvailableFreeSpace / 2e8)}"));
                case "memory":
                {
                    var st = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    GlobalMemoryStatusEx(ref st);
                    double commit = st.TotalPageFile > 0 ? 1 - (double)st.AvailPageFile / st.TotalPageFile : 0;
                    return $"{st.MemoryLoad / 2}|{Math.Round(commit * 50)}|{st.TotalPhys}";
                }
                case "battery":
                    return GetSystemPowerStatus(out var p) ? $"{p.BatteryLifePercent}|{p.ACLineStatus}" : null;
                case "display":
                    return string.Join("|", DisplayModes.Active().Select(d => $"{d.Device}:{d.Width}x{d.Height}@{d.Hz}"));
                case "network":
                    return string.Join("|", System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up).Select(n => n.Id + n.Speed).OrderBy(x => x));
            }
        }
        catch { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out POWER_STATUS status);

    // ------------------------------------------------------------------ carte son
    /// <summary>
    /// Contrôleurs audio réels (les virtuels sont ignorés) : carte son dédiée (PCI Express), carte son ou DAC USB,
    /// puce audio de la carte mère, son HDMI de la carte graphique. Un contrôleur en erreur est signalé avec sa correction.
    /// </summary>
    private static void SoundCards(DeviceCard c)
    {
        foreach (var o in CachedQuery("SELECT Name, Manufacturer, PNPDeviceID, ConfigManagerErrorCode FROM Win32_SoundDevice"))
        {
            var id = Str(o, "PNPDeviceID") ?? "";
            var name = Str(o, "Name") ?? "Contrôleur audio";
            if (Regex.IsMatch(id, @"^(ROOT|SWD|BTHENUM|BTHHFENUM)\\", RegexOptions.IgnoreCase)) continue;   // virtuel ou Bluetooth (carte Bluetooth)
            bool hdmi = Regex.IsMatch(id, @"VEN_(10DE|1002|8086)", RegexOptions.IgnoreCase) && Regex.IsMatch(name, "hdmi|display|nvidia|amd|intel", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(name, @"high definition audio.*(nvidia|amd|ati)|(nvidia|amd) .*audio|display audio|hdmi", RegexOptions.IgnoreCase);
            var kind = hdmi ? "Son HDMI de la carte graphique"
                     : id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) ? "Carte son (PCI Express)"
                     : id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ? "Carte son USB"
                     : "Puce audio de la carte mère";
            c.Lines.Add($"{kind} : {name}");
            c.Hardware += $" {name} {Str(o, "Manufacturer")} {id}";
            var code = Num(o, "ConfigManagerErrorCode");
            if (code == 22)
                c.Issues.Add(new DeviceIssue { Text = $"{name} est désactivé : aucun son ne passe par cette carte.",
                                               Actions = { new DeviceAction { Label = "Activer", Glyph = "", Primary = true, Run = () => EnableAsync(id, name) } } });
            else if (code != 0 && code is not (24 or 45))
                c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Problem, Text = $"{name} ne fonctionne pas (code {code}) : son pilote est absent ou endommagé.",
                                               Actions = { new DeviceAction { Label = "Chercher un pilote", Glyph = "", Goto = "updates" } } });
        }
    }
}
