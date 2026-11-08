using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Reflection;
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

    public List<string> PeripheralApps = new();
    public List<string> MakerTools = new();
}

public static class Updates
{

    // ------------------------------------------------------------------ winget
    public static async Task<List<WingetUpdate>> ScanWingetAsync()
    {
        var output = await RunCaptureAsync("winget",
            "upgrade --include-unknown --accept-source-agreements --disable-interactivity");
        return ParseWinget(output)
            .Where(u => !Settings.Current.WingetExclusions.Contains(u.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
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
    public static Task<List<WuUpdate>> ScanWindowsUpdateAsync(HardwareInfo hw) => Native.RunSta(() =>
    {
        var list = new List<WuUpdate>();
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
            list.Add(new WuUpdate
            {
                Title = title,
                IsDriver = type == 2,
                Ignored = gpuFilter != null && gpuFilter.IsMatch(title),
                IgnoredBy = hw?.GpuToolName,
                Size = size > 0 ? $"{size / 1024m / 1024m:0} Mo" : ""
            });
        }
        return list;
    });

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
        var bios = Wmi("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        if (bios != null) { h.BiosVersion = $"{bios["SMBIOSBIOSVersion"]}"; h.BiosDate = WmiDate(bios["ReleaseDate"]); }

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
        public List<string> AppIds { get; set; } = new();
        public bool AllApps { get; set; }
        public bool WindowsUpdate { get; set; } = true;
        public bool Store { get; set; } = true;
        public bool Defender { get; set; } = true;
        public string GpuFilter { get; set; } = "";
        public string Log { get; set; }
    }

    /// <summary>Lance le script d'installation en administrateur. Renvoie le chemin du journal, ou null si refusé.</summary>
    public static string StartInstall(InstallOptions opt, out Process process)
    {
        process = null;
        var dir = Settings.DataDir;
        var script = Path.Combine(dir, "maj.ps1");
        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("maj.ps1"))
        using (var sr = new StreamReader(res))
            File.WriteAllText(script, sr.ReadToEnd(), new UTF8Encoding(true));   // BOM requis par PowerShell 5.1

        var logDir = Path.Combine(dir, "Journaux");
        Directory.CreateDirectory(logDir);
        foreach (var old in new DirectoryInfo(logDir).GetFiles("MAJ_*.log").OrderByDescending(f => f.Name).Skip(20))
            try { old.Delete(); } catch { }
        opt.Log = Path.Combine(logDir, $"MAJ_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        var paramsFile = Path.Combine(dir, "maj-params.json");
        File.WriteAllText(paramsFile, JsonSerializer.Serialize(opt), new UTF8Encoding(false));

        try
        {
            process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Params \"{paramsFile}\"")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
            return opt.Log;
        }
        catch (Win32Exception) { return null; }   // UAC refusé
    }

    // ------------------------------------------------------------------ utilitaires
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
