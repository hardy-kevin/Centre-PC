using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CentrePC.Core;

public class HardwareInfo
{
    // Carte graphique principale (la carte dédiée est préférée à la puce intégrée)
    public string GpuName, GpuDriver, GpuVendor = "Inconnu";
    public DateTime? GpuDriverDate;
    public List<string> OtherGpus = new();
    /// <summary>Logiciel du fabricant pour le pilote graphique.</summary>
    public string GpuToolName, GpuToolPath, GpuToolUrl, GpuToolVersion;
    /// <summary>Titres Windows Update à ne pas installer (pilote géré par le logiciel du fabricant).</summary>
    public string GpuDriverFilter = "";

    public string CpuName, CpuVendor;
    public string ChipsetName, ChipsetVersion, ChipsetUrl;

    public string SystemMaker, SystemModel, BoardMaker, BoardModel, BiosVersion;
    public DateTime? BiosDate;
    public bool IsLaptop;
    public string SupportName, SupportUrl;
    /// <summary>Numéro de série du PC (service tag) : ouvre directement la page du modèle chez les constructeurs qui le permettent.</summary>
    public string Serial;

    public List<string> PeripheralApps = new();
    public List<string> MakerTools = new();
}

public static class Updates
{

    // ------------------------------------------------------------------ winget
    /// <summary>
    /// Applis à mettre à jour. Méthode principale : le module PowerShell officiel de winget (Microsoft.WinGet.Client),
    /// qui renvoie des données structurées (identifiants complets, versions), quelle que soit la langue de Windows.
    /// Repli : lecture du tableau affiché par « winget upgrade » si le module n'est pas disponible.
    /// </summary>
    /// <summary>
    /// Page de support du modèle exact (section BIOS quand le site le permet) plutôt que l'accueil du support :
    /// carte mère pour un PC monté, numéro de série pour les PC de marque. null : on garde la page générale.
    /// </summary>
    public static string ModelSupportUrl(HardwareInfo h)
    {
        // Numéro de série inutilisable (valeurs par défaut des cartes mères vendues seules)
        bool serialOk = !string.IsNullOrWhiteSpace(h.Serial) && h.Serial.Length >= 5
                        && !Regex.IsMatch(h.Serial, @"^(to be filled|default|system serial|none|0+|1234567890|n/?a)", RegexOptions.IgnoreCase);
        string sys = h.SystemMaker ?? "", board = (h.BoardModel ?? "").Trim();
        // Modèle de carte mère sans le code interne : « MAG B760 TOMAHAWK WIFI (MS-7D96) » → « MAG B760 TOMAHAWK WIFI »
        var model = Regex.Replace(board, @"\s*\((MS|rev)[^)]*\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        if (Regex.IsMatch(sys, "Dell", RegexOptions.IgnoreCase) && serialOk)
            return $"https://www.dell.com/support/home/fr-fr/product-support/servicetag/{Uri.EscapeDataString(h.Serial)}/drivers";
        if (Regex.IsMatch(sys, "Lenovo", RegexOptions.IgnoreCase) && serialOk)
            return $"https://pcsupport.lenovo.com/fr/fr/search?query={Uri.EscapeDataString(h.Serial)}";
        if (h.IsLaptop || model.Length == 0) return null;   // portables d'autres marques : page générale (détection par le site)
        var maker = h.BoardMaker ?? "";
        if (Regex.IsMatch(maker, @"Micro-Star|\bMSI\b", RegexOptions.IgnoreCase))
            return $"https://fr.msi.com/Motherboard/{Uri.EscapeDataString(Regex.Replace(model, @"\s+", "-"))}/support#bios";
        if (Regex.IsMatch(maker, "ASUS", RegexOptions.IgnoreCase))
            return $"https://www.asus.com/supportonly/{Uri.EscapeDataString(model)}/HelpDesk_BIOS/";
        if (Regex.IsMatch(maker, "ASRock", RegexOptions.IgnoreCase))
            return $"https://www.asrock.com/mb/{(Regex.IsMatch(h.CpuName ?? "", "AMD|Ryzen", RegexOptions.IgnoreCase) ? "AMD" : "Intel")}/{Uri.EscapeDataString(model)}/index.fr.asp#BIOS";
        if (Regex.IsMatch(maker, "Gigabyte", RegexOptions.IgnoreCase))
            return $"https://www.gigabyte.com/fr/Search?kw={Uri.EscapeDataString(model)}";   // l'adresse exacte dépend de la révision de la carte
        return null;
    }

    public static async Task<List<WingetUpdate>> ScanWingetAsync()
    {
        var list = await WingetModuleAsync(updatesOnly: true);
        if (list == null)
        {
            var output = await RunCaptureAsync("winget",
                "upgrade --include-unknown --accept-source-agreements --disable-interactivity");
            list = ParseWinget(output);
        }
        list = list.Where(u => !Settings.Current.WingetExclusions.Contains(u.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        // Déjà installées mais mal lues par winget : l'application est à jour, on ne la montre pas
        MarkPhantoms(list);
        return list.Where(u => !u.Phantom).ToList();
    }

    /// <summary>
    /// Repère les fausses alertes : même mise à jour (même paire de versions) déjà installée avec succès.
    /// Si une vraie nouvelle version sort (version proposée différente), elle est de nouveau proposée normalement.
    /// </summary>
    public static void MarkPhantoms(List<WingetUpdate> list)
    {
        var known = Settings.Current.PhantomUpdates;
        bool changed = false;
        var installed = InstalledInRecentLogs();
        foreach (var u in list)
        {
            // Déjà installée avec succès lors d'une mise à jour récente, dans cette même version : fausse alerte
            if (!known.ContainsKey(u.Id) && installed.Contains($"{u.Id}|{u.Available}"))
            {
                known[u.Id] = u.Pair;
                changed = true;
            }
            if (!known.TryGetValue(u.Id, out var pair)) continue;
            if (pair == u.Pair) { u.Phantom = true; u.Selected = false; }
            else { known.Remove(u.Id); changed = true; }
        }
        // Applis qui ne sont plus proposées du tout : rien à retenir
        foreach (var id in known.Keys.Where(id => !list.Any(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList())
        { known.Remove(id); changed = true; }
        if (changed) Settings.Save();
    }

    /// <summary>
    /// « identifiant|version » installés avec succès d'après les journaux des 30 derniers jours
    /// (winget écrit « Trouvé … [Id] Version x » puis Centre PC « ##APP|Id|À jour »).
    /// </summary>
    internal static HashSet<string> InstalledInRecentLogs(string logDir = null)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            logDir ??= Path.Combine(Settings.DataDir, "Journaux");
            if (!Directory.Exists(logDir)) return res;
            foreach (var f in new DirectoryInfo(logDir).GetFiles("MAJ_*.log").Where(f => f.LastWriteTime > DateTime.Now.AddDays(-30)))
            {
                string id = null, version = null;
                foreach (var line in File.ReadLines(f.FullName))
                {
                    if (line.StartsWith("> Mise à jour de ")) { id = line[17..].Trim(); version = null; continue; }
                    var m = Regex.Match(line, @"\[(?<id>[^\]]+)\]\s+\S+\s+(?<v>\S+)\s*$");   // « Trouvé Nom [Id] Version x.y »
                    if (id != null && m.Success && m.Groups["id"].Value.Equals(id, StringComparison.OrdinalIgnoreCase)) version = m.Groups["v"].Value;
                    if (id != null && version != null && line.StartsWith($"##APP|{id}|À jour")) res.Add($"{id}|{version}");
                }
            }
        }
        catch (Exception ex) { Log.Error("Lecture des journaux de mise à jour", ex); }
        return res;
    }

    /// <summary>Après une installation réussie : si winget reproposait encore la même chose, ce sera une fausse alerte.</summary>
    public static void RememberInstalled(WingetUpdate u)
    {
        if (u == null || string.IsNullOrEmpty(u.Id)) return;
        Settings.Current.PhantomUpdates[u.Id] = u.Pair;
        Settings.Save();
    }

    /// <summary>Logiciels installés connus de winget (nom → identifiant), pour le tri en ligne.</summary>
    public static async Task<List<(string name, string id)>> ListWingetAsync()
    {
        var list = await WingetModuleAsync(updatesOnly: false);
        return list?.Select(u => (u.Name, u.Id)).ToList();
    }

    private static bool _moduleUnavailable;

    /// <summary>
    /// Interroge winget par son module PowerShell (installé une fois pour l'utilisateur, sans droits administrateur).
    /// Renvoie null si le module est indisponible (hors connexion la première fois, PowerShell bloqué…).
    /// </summary>
    private static async Task<List<WingetUpdate>> WingetModuleAsync(bool updatesOnly)
    {
        if (_moduleUnavailable) return null;
        var filter = updatesOnly ? "| Where-Object { $_.IsUpdateAvailable }" : "";
        var script = string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "$ProgressPreference = 'SilentlyContinue'",
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8",
            "if (-not (Get-Module -ListAvailable -Name Microsoft.WinGet.Client)) {",
            "  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12",
            "  if (-not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) { Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null }",
            "  Install-Module -Name Microsoft.WinGet.Client -Scope CurrentUser -Force -AllowClobber -Repository PSGallery | Out-Null",
            "}",
            "Import-Module Microsoft.WinGet.Client",
            $"$r = @(Get-WinGetPackage {filter} | ForEach-Object {{ [pscustomobject]@{{ Name = $_.Name; Id = $_.Id; Version = \"$($_.InstalledVersion)\"; Available = \"$(@($_.AvailableVersions)[0])\"; Source = $_.Source }} }})",
            "ConvertTo-Json -InputObject $r -Compress");
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var (stdout, stderr, code) = await RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}");
            var json = stdout.Trim();
            int start = json.IndexOf('['), end = json.LastIndexOf(']');
            if (code != 0 || start < 0 || end < start)
            {
                var msg = (stderr + " " + json).Trim();
                throw new InvalidDataException("réponse inattendue : " + (msg.Length > 300 ? msg[..300] : msg));
            }
            using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
            var res = new List<WingetUpdate>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                var id = S("Id");
                if (string.IsNullOrEmpty(id)) continue;
                res.Add(new WingetUpdate { Name = S("Name"), Id = id, Version = S("Version"), Available = S("Available") });
            }
            return res;
        }
        catch (Exception ex)
        {
            _moduleUnavailable = true;   // pas de nouvel essai pendant cette session : on passe par l'outil winget
            Log.Error("Module winget (repli sur la lecture du texte de winget)", ex);
            return null;
        }
    }

    /// <summary>Lit le tableau de « winget upgrade » (colonnes Nom / ID / Version / Disponible / Source).</summary>
    public static List<WingetUpdate> ParseWinget(string output)
    {
        var res = new List<WingetUpdate>();
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l.Substring(l.LastIndexOf('\r') + 1) : l).ToList();
        int dash = lines.FindIndex(l => Regex.IsMatch(l, @"^-{10,}\s*$"));
        if (dash < 1) return res;
        var header = lines[dash - 1];
        var cols = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) cols.Add(i);
        if (cols.Count < 4) return res;
        cols[0] = 0;   // le nom commence toujours en colonne 0 (parfois précédé d'un résidu d'animation)

        string Col(string line, int c)
        {
            int start = cols[c];
            if (start >= line.Length) return "";
            int end = c + 1 < cols.Count ? Math.Min(cols[c + 1], line.Length) : line.Length;
            return line.Substring(start, end - start).Trim();
        }

        for (int i = dash + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.Length < cols[3]) break;
            var id = Col(line, 1); var ver = Col(line, 2); var avail = Col(line, 3);
            if (id.Length == 0 || id.Contains(' ') || avail.Length == 0) continue;
            res.Add(new WingetUpdate { Name = Col(line, 0), Id = id, Version = ver, Available = avail });
        }
        return res;
    }

    // ------------------------------------------------------------------ Windows Update (recherche seule)
    public class WuScan
    {
        public List<WuUpdate> System = new();
        public List<DriverUpdate> Drivers = new();
    }

    /// <summary>Recherche Windows Update : mises à jour du système d'un côté, pilotes (détaillés) de l'autre.</summary>
    public static async Task<WuScan> ScanWindowsUpdateAsync(HardwareInfo hw)
    {
        var res = await Native.RunSta(() =>
        {
            var scan = new WuScan();
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            dynamic updates = result.Updates;
            int count = updates.Count;
            var gpuFilter = string.IsNullOrEmpty(hw?.GpuDriverFilter) ? null : new Regex(hw.GpuDriverFilter, RegexOptions.IgnoreCase);
            for (int i = 0; i < count; i++)
            {
                dynamic u = updates.Item(i);
                string title = u.Title;
                int type = u.Type;
                decimal size = 0;
                try { size = (decimal)u.MaxDownloadSize; } catch { }
                var sizeText = size > 0 ? $"{size / 1024m / 1024m:0} Mo" : "";
                if (type != 2)
                {
                    scan.System.Add(new WuUpdate { Title = title, Size = sizeText });
                    continue;
                }
                string Str(Func<object> get) { try { return Convert.ToString(get())?.Trim(); } catch { return null; } }
                DateTime? date = null;
                try { date = (DateTime)u.DriverVerDate; } catch { }
                var d = new DriverUpdate
                {
                    UpdateId = Str(() => u.Identity.UpdateID),
                    Title = title,
                    Kind = KindOf(Str(() => u.DriverClass), title),
                    Manufacturer = Str(() => u.DriverManufacturer) ?? Str(() => u.DriverProvider),
                    HardwareId = Str(() => u.DriverHardwareID),
                    Device = Str(() => u.DriverModel),
                    NewDate = date,
                    NewVersion = VersionInTitle(title),
                    Size = sizeText,
                    ManagedBy = gpuFilter != null && gpuFilter.IsMatch(title) ? hw?.GpuToolName ?? "le logiciel du fabricant" : null,
                };
                d.IsFirmware = Regex.IsMatch($"{Str(() => u.DriverClass)} {title}", @"\bfirmware\b", RegexOptions.IgnoreCase);
                if (d.IsFirmware) d.Selected = false;
                scan.Drivers.Add(d);
            }
            return scan;
        });

        // Version actuelle et nom du périphérique : d'après le matériel qui correspond à l'identifiant du pilote
        if (res.Drivers.Count > 0)
            await Task.Run(() =>
            {
                var installed = InstalledDrivers();
                foreach (var d in res.Drivers)
                    if (d.HardwareId != null && installed.TryGetValue(d.HardwareId, out var cur))
                    {
                        d.CurrentVersion = cur.version;
                        d.CurrentDate = cur.date;
                        if (!string.IsNullOrWhiteSpace(cur.name)) d.Device = cur.name;
                    }
            });
        res.Drivers = res.Drivers
            .Where(d => d.HardwareId == null || !Settings.Current.DriverExclusions.Contains(d.HardwareId, StringComparer.OrdinalIgnoreCase))
            .OrderBy(d => d.ManagedBy != null).ThenBy(d => d.IsFirmware).ThenBy(d => d.Kind).ThenBy(d => d.Name)
            .ToList();
        return res;
    }

    /// <summary>Identifiant matériel (et compatible) → périphérique, version et date de son pilote actuel.</summary>
    private static Dictionary<string, (string name, string version, DateTime? date)> InstalledDrivers()
    {
        var map = new Dictionary<string, (string, string, DateTime?)>(StringComparer.OrdinalIgnoreCase);
        var byDevice = new Dictionary<string, (string version, DateTime? date)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sd in Wmi("SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver"))
            if (sd["DeviceID"] is string id) byDevice[id] = ($"{sd["DriverVersion"]}", WmiDate(sd["DriverDate"]));
        foreach (var e in Wmi("SELECT DeviceID, Name, HardwareID, CompatibleID FROM Win32_PnPEntity"))
        {
            if (e["DeviceID"] is not string id || !byDevice.TryGetValue(id, out var drv)) continue;
            var ids = (e["HardwareID"] as string[] ?? Array.Empty<string>()).Concat(e["CompatibleID"] as string[] ?? Array.Empty<string>());
            foreach (var hid in ids) map.TryAdd(hid, ($"{e["Name"]}", drv.version, drv.date));
        }
        return map;
    }

    /// <summary>« Realtek - Audio - 6.0.9600.1 » → « 6.0.9600.1 ».</summary>
    private static string VersionInTitle(string title)
    {
        var m = Regex.Match(title ?? "", @"(\d+(?:\.\d+){1,4})\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string KindOf(string driverClass, string title)
    {
        var c = (driverClass ?? "").ToUpperInvariant();
        return c switch
        {
            "DISPLAY" => "Carte graphique",
            "MEDIA" or "AUDIOENDPOINT" or "AUDIOPROCESSINGOBJECT" or "SOFTWARECOMPONENT" when Regex.IsMatch(title ?? "", "audio|sound|son", RegexOptions.IgnoreCase) => "Audio",
            "MEDIA" or "AUDIOENDPOINT" => "Audio",
            "NET" or "NETSERVICE" => "Réseau",
            "BLUETOOTH" => "Bluetooth",
            "USB" or "USBDEVICE" => "USB",
            "HIDCLASS" or "KEYBOARD" or "MOUSE" => "Souris, clavier, manettes",
            "PRINTER" or "PRINTQUEUE" => "Imprimante",
            "IMAGE" or "CAMERA" => "Caméra, scanner",
            "MONITOR" => "Écran",
            "SCSIADAPTER" or "HDCONTROLLER" or "DISKDRIVE" or "STORAGEVOLUME" => "Stockage",
            "FIRMWARE" => "Firmware",
            "SYSTEM" or "COMPUTER" => "Système (carte mère)",
            "BIOMETRIC" => "Biométrie",
            "SOFTWARECOMPONENT" or "EXTENSION" => "Composant logiciel",
            "" => "Pilote",
            _ => driverClass
        };
    }

    // ------------------------------------------------------------------ Matériel (détection automatique)
    private static List<ManagementObject> Wmi(string query)
    {
        try { using var s = new ManagementObjectSearcher(query); return s.Get().Cast<ManagementObject>().ToList(); }
        catch { return new(); }
    }

    private static DateTime? WmiDate(object v)
    {
        try { return v is string s && s.Length > 0 ? ManagementDateTimeConverter.ToDateTime(s) : null; } catch { return null; }
    }

    private static string VendorOf(string gpuName) =>
        Regex.IsMatch(gpuName, "NVIDIA|GeForce|RTX|GTX|Quadro", RegexOptions.IgnoreCase) ? "NVIDIA" :
        Regex.IsMatch(gpuName, @"AMD|Radeon|ATI\b", RegexOptions.IgnoreCase) ? "AMD" :
        Regex.IsMatch(gpuName, "Intel|Arc", RegexOptions.IgnoreCase) ? "Intel" : "Autre";

    public static Task<HardwareInfo> ScanHardwareAsync() => Task.Run(() =>
    {
        var h = new HardwareInfo();
        var installed = InstalledPrograms();
        (string name, string version) Find(string startsWith) =>
            installed.FirstOrDefault(p => p.name.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase));
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

        // ---------- Carte(s) graphique(s)
        var gpus = Wmi("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController")
            .Where(g => !Regex.IsMatch($"{g["Name"]}", "Basic Display|Remote|Virtual|Parsec|Meta|Mirage", RegexOptions.IgnoreCase))
            .ToList();
        // Priorité : NVIDIA / AMD (carte dédiée) puis Intel
        var main = gpus.OrderBy(g => VendorOf($"{g["Name"]}") switch { "NVIDIA" => 0, "AMD" => 1, "Intel" => 2, _ => 3 }).FirstOrDefault();
        if (main != null)
        {
            h.GpuName = $"{main["Name"]}";
            h.GpuDriver = $"{main["DriverVersion"]}";
            h.GpuDriverDate = WmiDate(main["DriverDate"]);
            h.GpuVendor = VendorOf(h.GpuName);
            h.OtherGpus = gpus.Where(g => g != main).Select(g => $"{g["Name"]}").ToList();
        }
        switch (h.GpuVendor)
        {
            case "AMD":
                h.GpuToolName = "AMD Software";
                h.GpuToolPath = FirstExisting(Path.Combine(pf, @"AMD\CNext\CNext\RadeonSoftware.exe"));
                h.GpuToolUrl = "https://www.amd.com/fr/support/download/drivers.html";
                h.GpuToolVersion = Find("AMD Software").version;
                h.GpuDriverFilter = @"Advanced Micro Devices.*Display|AMD.*Radeon|Radeon.*Graphics";
                break;
            case "NVIDIA":
                var app = FirstExisting(Path.Combine(pf, @"NVIDIA Corporation\NVIDIA app\CEF\NVIDIA app.exe"));
                var gfe = FirstExisting(Path.Combine(pf, @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"),
                                        Path.Combine(pf86, @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"));
                h.GpuToolName = app != null || gfe == null ? "NVIDIA App" : "GeForce Experience";
                h.GpuToolPath = app ?? gfe;
                h.GpuToolUrl = "https://www.nvidia.com/fr-fr/software/nvidia-app/";
                h.GpuToolVersion = (Find("NVIDIA app").name != null ? Find("NVIDIA app") : Find("NVIDIA GeForce Experience")).version;
                h.GpuDriverFilter = @"NVIDIA.*(Display|GeForce|RTX|GTX|Quadro)";
                break;
            case "Intel":
                h.GpuToolName = "Intel Driver & Support Assistant";
                h.GpuToolUrl = "https://www.intel.fr/content/www/fr/fr/support/detect.html";
                h.GpuToolVersion = Find("Intel® Driver & Support Assistant").version ?? Find("Intel(R) Driver & Support Assistant").version;
                h.GpuDriverFilter = "";   // Windows Update gère correctement les pilotes Intel
                break;
        }

        // ---------- Processeur et chipset
        var cpu = Wmi("SELECT Name, Manufacturer FROM Win32_Processor").FirstOrDefault();
        if (cpu != null)
        {
            h.CpuName = $"{cpu["Name"]}".Trim();
            h.CpuVendor = $"{cpu["Manufacturer"]}".Contains("AMD") ? "AMD" : $"{cpu["Manufacturer"]}".Contains("Intel") ? "Intel" : "Autre";
        }
        if (h.CpuVendor == "AMD")
        {
            h.ChipsetName = "AMD Chipset Software";
            h.ChipsetVersion = Find("AMD Chipset Software").version;
            h.ChipsetUrl = "https://www.amd.com/fr/support/download/drivers.html";
        }
        else if (h.CpuVendor == "Intel")
        {
            h.ChipsetName = "Intel Chipset Device Software";
            h.ChipsetVersion = Find("Intel(R) Chipset Device Software").version ?? Find("Intel® Chipset Device Software").version;
            h.ChipsetUrl = "https://www.intel.fr/content/www/fr/fr/support/detect.html";
        }

        // ---------- PC, carte mère, BIOS
        var cs = Wmi("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem").FirstOrDefault();
        if (cs != null)
        {
            h.SystemMaker = $"{cs["Manufacturer"]}".Trim();
            h.SystemModel = $"{cs["Model"]}".Trim();
            try { h.IsLaptop = Convert.ToInt32(cs["PCSystemType"]) == 2; } catch { }
        }
        var board = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        if (board != null) { h.BoardMaker = $"{board["Manufacturer"]}".Trim(); h.BoardModel = $"{board["Product"]}".Trim(); }
        var bios = Wmi("SELECT SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS").FirstOrDefault();
        if (bios != null) { h.BiosVersion = $"{bios["SMBIOSBIOSVersion"]}"; h.BiosDate = WmiDate(bios["ReleaseDate"]); h.Serial = $"{bios["SerialNumber"]}".Trim(); }

        // Site de support : constructeur du PC (portables, PC de marque) sinon fabricant de la carte mère
        var maker = $"{h.SystemMaker} {h.BoardMaker}";
        (h.SupportName, h.SupportUrl) = maker switch
        {
            var m when Regex.IsMatch(m, "ASUS", RegexOptions.IgnoreCase) => ("ASUS", "https://www.asus.com/fr/support/download-center/"),
            var m when Regex.IsMatch(m, @"Micro-Star|\bMSI\b", RegexOptions.IgnoreCase) => ("MSI", "https://fr.msi.com/support"),
            var m when Regex.IsMatch(m, "Gigabyte", RegexOptions.IgnoreCase) => ("Gigabyte", "https://www.gigabyte.com/fr/Support"),
            var m when Regex.IsMatch(m, "ASRock", RegexOptions.IgnoreCase) => ("ASRock", "https://www.asrock.com/support/index.fr.asp"),
            var m when Regex.IsMatch(m, "Dell", RegexOptions.IgnoreCase) => ("Dell", "https://www.dell.com/support/home/fr-fr"),
            var m when Regex.IsMatch(m, "HP|Hewlett", RegexOptions.IgnoreCase) => ("HP", "https://support.hp.com/fr-fr/drivers"),
            var m when Regex.IsMatch(m, "Lenovo", RegexOptions.IgnoreCase) => ("Lenovo", "https://pcsupport.lenovo.com/fr/fr"),
            var m when Regex.IsMatch(m, "Acer", RegexOptions.IgnoreCase) => ("Acer", "https://www.acer.com/fr-fr/support/drivers-and-manuals"),
            var m when Regex.IsMatch(m, "Microsoft", RegexOptions.IgnoreCase) => ("Microsoft Surface", "https://support.microsoft.com/fr-fr/surface"),
            _ => (null, null)
        };
        h.SupportUrl = ModelSupportUrl(h) ?? h.SupportUrl;

        // ---------- Logiciels de périphériques et outils constructeur
        var peripherals = new (string label, string pattern)[]
        {
            ("Logitech G HUB", "Logitech G HUB"), ("Logi Options+", "Logi Options"), ("Razer Synapse", "Razer Synapse"),
            ("Corsair iCUE", "iCUE"), ("SteelSeries GG", "SteelSeries GG"), ("HyperX NGENUITY", "NGENUITY"),
            ("Roccat Swarm", "ROCCAT"), ("Wootility", "Wootility"), ("Fanatec", "Fanatec"), ("Thrustmaster", "Thrustmaster"),
            ("Moza Pit House", "Pit House"), ("Elgato", "Elgato"), ("Cardo", "Cardo"), ("Wacom", "Wacom")
        };
        foreach (var (label, pattern) in peripherals)
            if (installed.Any(p => p.name.Contains(pattern, StringComparison.OrdinalIgnoreCase))) h.PeripheralApps.Add(label);

        var makerTools = new (string label, string pattern)[]
        {
            ("ASUS Armoury Crate", "Armoury Crate"), ("MyASUS", "MyASUS"), ("MSI Center", "MSI Center"),
            ("GIGABYTE Control Center", "GIGABYTE Control Center"), ("Dell Command | Update", "Dell Command"),
            ("Dell SupportAssist", "SupportAssist"), ("HP Support Assistant", "HP Support Assistant"),
            ("Lenovo Vantage", "Lenovo Vantage"), ("Lenovo System Update", "Lenovo System Update"), ("Acer Care Center", "Care Center")
        };
        foreach (var (label, pattern) in makerTools)
            if (installed.Any(p => p.name.Contains(pattern, StringComparison.OrdinalIgnoreCase))) h.MakerTools.Add(label);
        return h;
    });

    private static List<(string name, string version)> InstalledPrograms()
    {
        var res = new List<(string, string)>();
        var roots = new (RegistryKey hive, string path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k == null) continue;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var sk = k.OpenSubKey(sub);
                    if (sk?.GetValue("DisplayName") is string n) res.Add((n, sk.GetValue("DisplayVersion") as string ?? ""));
                }
            }
            catch { }
        }
        return res;
    }

    // ------------------------------------------------------------------ Installation (PowerShell élevé)
    public class InstallOptions
    {
        public bool RestorePoint { get; set; } = true;
        public bool Apps { get; set; } = true;
        /// <summary>Applis à mettre à jour (identifiant winget + nom affiché, utilisé pour retrouver le dossier et les processus).</summary>
        public List<PackageRef> Packages { get; set; } = new();
        public bool WindowsUpdate { get; set; } = true;
        /// <summary>Pilotes cochés (installés un par un, avec leur résultat).</summary>
        public bool Drivers { get; set; } = true;
        public List<PackageRef> DriverUpdates { get; set; } = new();
        public bool Store { get; set; } = true;
        public bool Defender { get; set; } = true;
        public string GpuFilter { get; set; } = "";
        public string Log { get; set; }
        /// <summary>Dossier de Centre PC (installateurs prêts, mémoire des mises à jour qui désinstallent d'abord).</summary>
        public string DataDir { get; set; }
        /// <summary>
        /// Bureau rangé : les icônes que les installateurs ajoutent sur le bureau Public (admin requis) sont ramenées
        /// sur le bureau de l'utilisateur, où Centre PC peut ensuite les ranger sans redemander l'autorisation.
        /// </summary>
        public bool TidyPublicShortcuts { get; set; }
        public string UserDesktop { get; set; }
        public string ReplacedDir { get; set; }
        public string StoreDir { get; set; }
        /// <summary>Tests uniquement : winget de remplacement (script qui simule ses réponses).</summary>
        public string WingetPath { get; set; }
    }

    public class PackageRef
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>Mise à jour sautée ou à terminer à la main (ligne « ##MANUEL » du journal).</summary>
    public class ManualAction
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Message { get; set; }
        /// <summary>Installateur déjà téléchargé et vérifié, prêt à être lancé (facultatif).</summary>
        public string Installer { get; set; }

        public static ManualAction Parse(string line)
        {
            var p = line.Split('|');
            if (p.Length < 4) return null;
            return new ManualAction
            {
                Id = p[1], Name = string.IsNullOrWhiteSpace(p[2]) ? p[1] : p[2], Message = p[3],
                Installer = p.Length > 4 && !string.IsNullOrWhiteSpace(p[4]) ? p[4] : null
            };
        }
    }

    /// <summary>
    /// Lance le script d'installation en administrateur. Renvoie le chemin du journal, ou null si refusé.
    /// Sécurité : le script et ses paramètres sont écrits dans un dossier modifiable sans droits. Pour qu'un autre
    /// programme ne puisse pas les remplacer avant le lancement, un petit amorceur (passé en ligne de commande,
    /// donc impossible à modifier) les lit une seule fois en mémoire, vérifie leur empreinte SHA-256 et n'exécute
    /// que ce texte-là.
    /// </summary>
    public static string StartInstall(InstallOptions opt, out Process process)
    {
        process = null;
        var dir = Settings.DataDir;
        var script = Path.Combine(dir, "maj.ps1");
        string scriptText;
        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("maj.ps1"))
        using (var sr = new StreamReader(res))
            scriptText = sr.ReadToEnd();
        File.WriteAllText(script, scriptText, new UTF8Encoding(true));   // BOM requis par PowerShell 5.1

        var logDir = Path.Combine(dir, "Journaux");
        Directory.CreateDirectory(logDir);
        foreach (var old in new DirectoryInfo(logDir).GetFiles("MAJ_*.log").OrderByDescending(f => f.Name).Skip(20))
            try { old.Delete(); } catch { }
        opt.Log = Path.Combine(logDir, $"MAJ_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        opt.DataDir = dir;
        opt.UserDesktop = AppScanner.UserDesktop;
        opt.ReplacedDir = ShortcutGuard.ReplacedDir;
        opt.StoreDir = Settings.StoreDir;
        var paramsFile = Path.Combine(dir, "maj-params.json");
        var paramsText = JsonSerializer.Serialize(opt);
        File.WriteAllText(paramsFile, paramsText, new UTF8Encoding(false));

        var encoded = BuildBootstrap(script, paramsFile, scriptText, paramsText);

        try
        {
            process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
            Log.Info($"Mises à jour lancées ({opt.Packages.Count} application(s), {opt.DriverUpdates.Count} pilote(s))");
            return opt.Log;
        }
        catch (Win32Exception) { return null; }   // UAC refusé
    }

    /// <summary>
    /// Amorceur PowerShell (encodé pour -EncodedCommand) : relit le script et les paramètres une seule fois,
    /// vérifie leurs empreintes SHA-256 (code de sortie 99 si un fichier a été modifié) puis exécute ce texte-là.
    /// </summary>
    internal static string BuildBootstrap(string script, string paramsFile, string scriptText, string paramsText)
    {
        static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var boot =
            "$ErrorActionPreference='Stop';" +
            $"$s=[IO.File]::ReadAllText({Q(script)});$p=[IO.File]::ReadAllText({Q(paramsFile)});" +
            "$h={param($t) -join ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($t)) | ForEach-Object { $_.ToString('x2') })};" +
            $"if ((& $h $s) -ne '{Sha(scriptText.TrimStart('\uFEFF'))}' -or (& $h $p) -ne '{Sha(paramsText)}') {{ exit 99 }};" +
            "& ([scriptblock]::Create($s)) -ParamsJson $p";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(boot));
    }

    // ------------------------------------------------------------------ utilitaires
    /// <summary>Lance un programme et renvoie sa sortie, ses erreurs et son code de fin, séparément.</summary>
    public static async Task<(string stdout, string stderr, int code)> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi);
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await outTask, await errTask, p.ExitCode);
    }

    public static async Task<string> RunCaptureAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi);
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return await outTask + "\n" + await errTask;
    }

    public static void Open(string target, string args = null)
    {
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); } catch { }
    }
}
