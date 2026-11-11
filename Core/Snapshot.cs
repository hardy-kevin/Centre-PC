using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace CentrePC.Core;

/// <summary>
/// État figé de l'installation à la date d'un bilan : Windows (version, mises à jour), BIOS, matériel, pilotes, applications
/// et leurs versions, mesures de performance et de stabilité. Deux instantanés comparés montrent ce qui a changé entre deux bilans.
/// Enregistrés en JSON dans Documents\Centre PC\Bilans (lisibles, transportables).
/// </summary>
public sealed class MachineSnapshot
{
    public DateTime Date { get; set; }
    public string Level { get; set; }
    public string Machine { get; set; }
    public string Windows { get; set; }
    public string Build { get; set; }
    public string Bios { get; set; }
    public string Board { get; set; }
    public string Cpu { get; set; }
    public string Ram { get; set; }
    public List<string> Gpus { get; set; } = new();
    public List<string> Disks { get; set; } = new();
    /// <summary>Applications installées : nom → version.</summary>
    public Dictionary<string, string> Apps { get; set; } = new();
    /// <summary>Pilotes : périphérique → « version (date) ».</summary>
    public Dictionary<string, string> Drivers { get; set; } = new();
    /// <summary>Mises à jour de Windows installées : numéro KB → date.</summary>
    public Dictionary<string, string> Updates { get; set; } = new();
    /// <summary>Mesures : nom → valeur (GFLOPS, températures, débits…).</summary>
    public Dictionary<string, double> Metrics { get; set; } = new();
    /// <summary>Constats du bilan (niveau et texte), pour suivre leur évolution.</summary>
    public List<string> Findings { get; set; } = new();
    public string Verdict { get; set; }

    public static string Folder
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Centre PC", "Bilans");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Save()
    {
        var path = Path.Combine(Folder, $"Instantané {Machine} {Date:yyyy-MM-dd HH-mm}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        return path;
    }

    /// <summary>Dernier instantané de cette machine (avant celui en cours).</summary>
    public static MachineSnapshot Latest(string machine)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder, "Instantané *.json").OrderByDescending(File.GetLastWriteTime))
            {
                var s = JsonSerializer.Deserialize<MachineSnapshot>(File.ReadAllText(f));
                if (s != null && string.Equals(s.Machine, machine, StringComparison.OrdinalIgnoreCase)) return s;
            }
        }
        catch (Exception ex) { Log.Error("Instantanés", ex); }
        return null;
    }

    // ------------------------------------------------------------------ relevé de l'installation
    /// <summary>Applications (registre « Programmes et fonctionnalités »), pilotes (WMI) et mises à jour de Windows.</summary>
    public void CaptureInstallation()
    {
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        })
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        var display = (k?.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(display) || k.GetValue("SystemComponent") is int sc && sc == 1 || k.GetValue("ParentKeyName") != null) continue;
                        Apps[display] = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "";
                    }
                    catch { }
            }
            catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver");
            foreach (ManagementBaseObject o in s.Get())
            {
                var dev = o["DeviceName"]?.ToString()?.Trim();
                var cls = o["DeviceClass"]?.ToString();
                if (string.IsNullOrEmpty(dev) || cls is "SYSTEM" or "VOLUME" or "VOLUMESNAPSHOT" or "LEGACYDRIVER" or "SOFTWARECOMPONENT") continue;
                string date = null;
                try { date = ManagementDateTimeConverter.ToDateTime(o["DriverDate"]?.ToString()).ToString("yyyy-MM-dd"); } catch { }
                Drivers[dev] = $"{o["DriverVersion"]}" + (date != null ? $" ({date})" : "");
            }
        }
        catch (Exception ex) { Log.Error("Instantané : pilotes", ex); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementBaseObject o in s.Get())
            {
                var id = o["HotFixID"]?.ToString();
                if (!string.IsNullOrEmpty(id)) Updates[id] = o["InstalledOn"]?.ToString() ?? "";
            }
        }
        catch (Exception ex) { Log.Error("Instantané : mises à jour", ex); }
    }

    // ------------------------------------------------------------------ comparaison
    /// <summary>Ce qui a changé depuis l'instantané précédent : constats dans le rapport et tableau détaillé.</summary>
    public void CompareTo(MachineSnapshot old, CheckupReport report)
    {
        if (old == null)
        {
            report.Add(DeviceStatus.Info, "Suivi", "Premier bilan de cette machine : il servira de référence pour les prochains.",
                       "Aucun instantané précédent dans Documents\\Centre PC\\Bilans.");
            return;
        }
        var rows = new List<string[]>();
        void Row(string what, string before, string after) => rows.Add(new[] { what, before ?? "—", after ?? "—" });
        int days = Math.Max(0, (int)(Date - old.Date).TotalDays);

        if (old.Windows != Windows || old.Build != Build) Row("Windows", $"{old.Windows} ({old.Build})", $"{Windows} ({Build})");
        if (old.Bios != Bios) Row("BIOS / UEFI", old.Bios, Bios);
        if (old.Cpu != Cpu) Row("Processeur", old.Cpu, Cpu);
        if (old.Ram != Ram) Row("Mémoire", old.Ram, Ram);
        if (!old.Gpus.SequenceEqual(Gpus)) Row("Cartes graphiques", string.Join(", ", old.Gpus), string.Join(", ", Gpus));
        if (!old.Disks.SequenceEqual(Disks)) Row("Disques", string.Join(", ", old.Disks), string.Join(", ", Disks));

        var newUpdates = Updates.Keys.Except(old.Updates.Keys).OrderBy(k => k).ToList();
        foreach (var u in newUpdates) Row("Mise à jour Windows installée", null, $"{u} ({Updates[u]})");
        var addedApps = Apps.Keys.Except(old.Apps.Keys).OrderBy(k => k).ToList();
        var removedApps = old.Apps.Keys.Except(Apps.Keys).OrderBy(k => k).ToList();
        var updatedApps = Apps.Keys.Intersect(old.Apps.Keys).Where(k => Apps[k] != old.Apps[k]).OrderBy(k => k).ToList();
        foreach (var a in addedApps) Row("Application installée", null, $"{a} {Apps[a]}");
        foreach (var a in removedApps) Row("Application supprimée", $"{a} {old.Apps[a]}", null);
        foreach (var a in updatedApps) Row("Application mise à jour", $"{a} {old.Apps[a]}", Apps[a]);
        var changedDrivers = Drivers.Keys.Where(k => old.Drivers.TryGetValue(k, out var v) && v != Drivers[k]).OrderBy(k => k).ToList();
        var newDrivers = Drivers.Keys.Except(old.Drivers.Keys).OrderBy(k => k).ToList();
        var goneDrivers = old.Drivers.Keys.Except(Drivers.Keys).OrderBy(k => k).ToList();
        foreach (var d in changedDrivers) Row($"Pilote mis à jour : {d}", old.Drivers[d], Drivers[d]);
        foreach (var d in newDrivers) Row($"Nouveau périphérique / pilote : {d}", null, Drivers[d]);
        foreach (var d in goneDrivers) Row($"Périphérique retiré : {d}", old.Drivers[d], null);

        // Mesures : évolution (une baisse de performance ou une hausse de température est signalée)
        var perfDrops = new List<string>();
        foreach (var (name, value) in Metrics)
        {
            if (!old.Metrics.TryGetValue(name, out var before) || before == 0) continue;
            double change = (value / before - 1) * 100;
            if (Math.Abs(change) < 3 && !name.Contains("°C")) continue;
            Row($"Mesure : {name}", before.ToString("0.##"), $"{value:0.##} ({change:+0;-0} %)");
            bool higherIsWorse = name.Contains("°C") || name.Contains("ms");
            if ((!higherIsWorse && change <= -10) || (higherIsWorse && name.Contains("°C") && value - before >= 8))
                perfDrops.Add(name.Contains("°C") ? $"{name} : {before:0} → {value:0} °C" : $"{name} : {change:0} %");
        }
        report.Tables.Add(($"Changements depuis le bilan du {old.Date:d MMMM yyyy} ({days} jour(s))", new[] { "Élément", "Avant", "Maintenant" }, rows));

        var summary = new List<string>();
        if (old.Build != Build) summary.Add("Windows mis à jour");
        if (newUpdates.Count > 0) summary.Add($"{newUpdates.Count} mise(s) à jour Windows");
        if (old.Bios != Bios) summary.Add("BIOS changé");
        if (changedDrivers.Count + newDrivers.Count > 0) summary.Add($"{changedDrivers.Count + newDrivers.Count} pilote(s) modifié(s) ou ajouté(s)");
        if (addedApps.Count > 0) summary.Add($"{addedApps.Count} application(s) installée(s)");
        if (updatedApps.Count > 0) summary.Add($"{updatedApps.Count} mise(s) à jour d'application");
        if (removedApps.Count > 0) summary.Add($"{removedApps.Count} application(s) supprimée(s)");
        report.Add(DeviceStatus.Info, "Depuis le dernier bilan",
            summary.Count == 0 ? $"Aucun changement d'installation depuis le bilan du {old.Date:d MMMM yyyy}." : $"Depuis le bilan du {old.Date:d MMMM yyyy} : {string.Join(", ", summary)}.",
            $"{rows.Count} différence(s) détaillée(s) dans l'annexe. Instantané précédent : niveau {old.Level}, verdict « {old.Verdict} ».");
        if (perfDrops.Count > 0)
            report.Add(DeviceStatus.Warning, "Évolution", "Les performances ou les températures se sont dégradées depuis le dernier bilan.",
                string.Join(" · ", perfDrops),
                summary.Count > 0 ? "Un des changements ci-dessus (pilote, mise à jour, BIOS) peut en être la cause ; sinon : poussière, pâte thermique, mode d'alimentation." : "Nettoyez la poussière et vérifiez le mode d'alimentation.");
        // Constats apparus depuis
        var appeared = Findings.Except(old.Findings).Where(f => f.StartsWith("Problem") || f.StartsWith("Warning")).ToList();
        if (appeared.Count > 0 && old.Findings.Count > 0)
            report.Tables.Add(("Points nouveaux depuis le dernier bilan", new[] { "Constat" }, appeared.Select(a => new[] { a[(a.IndexOf('|') + 1)..] }).ToList()));
    }
}
