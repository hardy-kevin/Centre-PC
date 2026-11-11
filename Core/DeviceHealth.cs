using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CentrePC.Core;

public enum DeviceStatus { Ok, Info, Warning, Problem }

/// <summary>Action proposée : lien (paramètres Windows, outil), opération immédiate, ou page de Centre PC.</summary>
public sealed class DeviceAction
{
    public string Label { get; set; }
    public string Glyph { get; set; } = "\uE8A7";
    public string Tip { get; set; }
    /// <summary>Lien ouvert par Windows (« ms-settings:sound », « devmgmt.msc »…).</summary>
    public string Uri { get; set; }
    /// <summary>Opération ; renvoie le message à afficher (null : rien).</summary>
    public Func<Task<string>> Run { get; set; }
    /// <summary>Page de Centre PC à ouvrir (« updates »).</summary>
    public string Goto { get; set; }
    public bool Primary { get; set; }
}

/// <summary>Point à signaler, avec ses propres actions (« Réactiver le son »).</summary>
public sealed class DeviceIssue
{
    public DeviceStatus Level { get; set; } = DeviceStatus.Warning;
    public string Text { get; set; }
    public List<DeviceAction> Actions { get; set; } = new();
}

/// <summary>Jauge (place utilisée d'un lecteur, charge de la batterie…) : remplie selon <see cref="Fraction"/>, colorée selon <see cref="Level"/>.</summary>
public sealed class DeviceGauge
{
    public string Label { get; set; }
    public string Detail { get; set; }
    public double Fraction { get; set; }
    public DeviceStatus Level { get; set; }
    /// <summary>Lecteur (« C:\\ ») : clic pour l'ouvrir, clic droit pour ses options.</summary>
    public string Path { get; set; }
    public bool External { get; set; }
    public bool IsSystem { get; set; }
}

/// <summary>Un appareil présenté visuellement (un disque et ses lecteurs) : nom, type, état et jauges.</summary>
public sealed class DeviceGroup
{
    public string Title { get; set; }
    public string Subtitle { get; set; }
    public string Glyph { get; set; }
    public DeviceStatus Level { get; set; }
    public string StatusText { get; set; }
    public bool External { get; set; }
    /// <summary>Disque physique (options : gestion des disques, test de vitesse).</summary>
    public bool IsDisk { get; set; }
    public List<DeviceGauge> Gauges { get; } = new();
}

/// <summary>Une famille de périphériques : état, informations, problèmes, et où aller plus loin.</summary>
public sealed class DeviceCard
{
    public string Key { get; set; }
    public string Title { get; set; }
    public string Glyph { get; set; }
    public string Color { get; set; }
    public string Headline { get; set; }
    public List<string> Lines { get; set; } = new();
    public List<DeviceIssue> Issues { get; set; } = new();
    public List<DeviceAction> Actions { get; set; } = new();
    /// <summary>Appareils présentés en tuiles visuelles (disques), en plus des lignes d'information.</summary>
    public List<DeviceGroup> Groups { get; } = new();
    /// <summary>Paramètres de Windows correspondants.</summary>
    public string SettingsUri { get; set; }
    /// <summary>
    /// Logiciels de fabricants proposés s'ils sont installés (noms cherchés dans la liste des applications).
    /// « Logitech G HUB::logitech,VID_046D » : proposé seulement si un périphérique de ce fabricant est branché
    /// (nom, fabricant ou identifiant USB trouvé dans <see cref="Hardware"/> ou les informations de la carte).
    /// </summary>
    public string[] Software { get; set; } = Array.Empty<string>();
    /// <summary>Noms, fabricants et identifiants des périphériques détectés (pour n'afficher que les logiciels utiles).</summary>
    public string Hardware { get; set; } = "";
    /// <summary>Types de périphériques présents (« gamepad », « mic », « speakers ») : l'interface n'affiche que ce qui sert.</summary>
    public HashSet<string> Present { get; } = new();

    /// <summary>Logiciels de la liste qui correspondent au matériel réellement branché.</summary>
    public IEnumerable<string> RelevantSoftware()
    {
        var hay = Hardware + " " + string.Join(" ", Lines);
        foreach (var entry in Software)
        {
            var parts = entry.Split("::");
            if (parts.Length == 1 || parts[1].Split(',').Any(t => hay.Contains(t.Trim(), StringComparison.OrdinalIgnoreCase))) yield return parts[0];
        }
    }
    public DeviceStatus Status => Issues.Count == 0 ? DeviceStatus.Ok : Issues.Max(i => i.Level);
}

/// <summary>
/// Diagnostic primaire des périphériques qui posent le plus souvent problème : son et micro, réseau et Internet, écrans
/// (fréquence d'affichage), disques (place, santé), batterie, imprimantes, Bluetooth, caméra, clavier / souris / manettes,
/// et périphériques en erreur dans Windows. Lecture seule, sauf les corrections proposées et demandées par l'utilisateur.
/// </summary>
public static partial class DeviceHealth
{
    /// <summary>Ordre d'affichage (les cartes en difficulté passent devant à l'affichage).</summary>
    public static readonly string[] Order = { "problems", "audio", "network", "display", "storage", "memory", "battery", "printers", "bluetooth", "camera", "input" };

    public static Func<Task<DeviceCard>> Collector(string key) => key switch
    {
        "problems" => () => Task.Run(ProblemDevices),
        "audio" => () => Task.Run(Audio),
        "network" => Network,
        "display" => () => Task.Run(Displays),
        "storage" => () => Task.Run(Storage),
        "memory" => () => Task.Run(Memory),
        "battery" => () => Task.Run(Battery),
        "printers" => () => Task.Run(Printers),
        "bluetooth" => () => Task.Run(Bluetooth),
        "camera" => () => Task.Run(Camera),
        "input" => () => Task.Run(Input),
        _ => () => Task.FromResult<DeviceCard>(null)
    };

    // ------------------------------------------------------------------ outils communs
    private static List<ManagementBaseObject> Query(string wql, string scope = null)
    {
        try
        {
            using var s = scope == null ? new ManagementObjectSearcher(wql) : new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            return s.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) { Log.Error("Périphériques : " + wql, ex); return new(); }
    }

    private static string Str(ManagementBaseObject o, string p) { try { return o[p]?.ToString(); } catch { return null; } }
    private static long Num(ManagementBaseObject o, string p) { try { return Convert.ToInt64(o[p] ?? 0); } catch { return 0; } }

    private static string Size(double bytes) => bytes >= 1e12 ? $"{bytes / 1e12:0.#} To" : bytes >= 1e9 ? $"{bytes / 1e9:0} Go" : $"{bytes / 1e6:0} Mo";

    /// <summary>Lance une commande en administrateur (Windows demande l'autorisation) ; message de résultat.</summary>
    private static async Task<string> Elevated(string file, string args, string done)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p != null) await p.WaitForExitAsync();
            return p == null || p.ExitCode == 0 ? done : $"L'opération n'a pas abouti (code {p.ExitCode}).";
        }
        catch (System.ComponentModel.Win32Exception) { return "Action annulée."; }
        catch (Exception ex) { return "Impossible : " + ex.Message; }
    }

    /// <summary>
    /// Périphérique virtuel, créé par un logiciel (identifiant « ROOT\… » ou « SWD\… », adaptateurs VPN et réseaux virtuels) :
    /// son logiciel le désactive quand il ne s'en sert pas, et le désactive de nouveau si on l'active à sa place.
    /// </summary>
    private static bool IsVirtual(string id, string name) =>
        (id ?? "").StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || (id ?? "").StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"virtual|virtuel", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Active un périphérique désactivé, puis vérifie qu'il l'est resté (sinon, explique pourquoi).</summary>
    private static async Task<string> EnableAsync(string id, string name)
    {
        int exit;
        try
        {
            var p = Process.Start(new ProcessStartInfo("pnputil.exe", $"/enable-device \"{id}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return "Impossible de lancer l'activation.";
            await p.WaitForExitAsync();
            exit = p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return "Action annulée : Windows demande l'autorisation administrateur pour activer un périphérique."; }
        catch (Exception ex) { return "Impossible : " + ex.Message; }
        if (exit == 3010) return $"{name} sera activé au prochain redémarrage du PC.";
        await Task.Delay(1500);
        InvalidateHardware();
        var code = Pnp($"DeviceID = '{id.Replace("\\", "\\\\").Replace("'", "\\'")}'").Select(o => Num(o, "ConfigManagerErrorCode")).DefaultIfEmpty(-1).First();
        if (code == 0) return $"{name} est activé.";
        if (code == 22)
            return exit == 0
                ? $"{name} a été activé, mais quelque chose l'a aussitôt désactivé de nouveau : en général son propre logiciel, ou une règle de l'entreprise. Ouvrez ce logiciel pour l'activer."
                : $"Windows n'a pas pu activer {name} (code {exit}) : essayez depuis le Gestionnaire de périphériques.";
        return code > 0 ? $"{name} est activé, mais signale encore un problème (code {code})." : $"{name} n'est plus détecté.";
    }

    /// <summary>L'accès à un capteur est-il refusé dans Confidentialité de Windows (« microphone », « webcam ») ?</summary>
    private static bool PrivacyDenied(string capability)
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            try
            {
                using var k = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}");
                if (string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        return false;
    }

    private static readonly Dictionary<long, string> ErrorCodes = new()
    {
        [1] = "n'est pas configuré correctement", [3] = "a un pilote endommagé ou la mémoire manque", [10] = "ne peut pas démarrer",
        [12] = "manque de ressources (conflit avec un autre périphérique)", [14] = "attend un redémarrage du PC", [18] = "a besoin que ses pilotes soient réinstallés",
        [19] = "a une configuration endommagée", [21] = "est en cours de suppression", [22] = "est désactivé", [28] = "n'a pas de pilote installé",
        [29] = "est désactivé par le micrologiciel (BIOS)", [31] = "ne fonctionne pas : Windows ne peut pas charger son pilote", [32] = "a un pilote désactivé",
        [37] = "a un pilote qui a échoué au démarrage", [39] = "a un pilote absent ou endommagé", [41] = "a un pilote chargé mais le matériel est introuvable",
        [43] = "a été arrêté parce qu'il a signalé un problème", [48] = "a un pilote bloqué (incompatible)", [52] = "a un pilote non signé ou non vérifiable"
    };

    /// <summary>
    /// Périphériques Windows (WMI, la lecture la plus lente) gardés en mémoire jusqu'au prochain branchement / retrait
    /// ou à la prochaine action : les réanalyses en direct d'une carte restent instantanées.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<ManagementBaseObject>> PnpCache = new();

    /// <summary>À appeler quand le matériel a pu changer (périphérique branché, activé, analyse complète).</summary>
    public static void InvalidateHardware() => PnpCache.Clear();

    /// <summary>Requête WMI sur le matériel, gardée en mémoire comme <see cref="Pnp"/>.</summary>
    private static List<ManagementBaseObject> CachedQuery(string wql, string scope = null) =>
        PnpCache.GetOrAdd((scope ?? "") + "§" + wql, _ => Query(wql, scope));

    private static List<ManagementBaseObject> Pnp(string where) =>
        PnpCache.GetOrAdd(where, w => Query($"SELECT Name, PNPClass, DeviceID, ConfigManagerErrorCode, Status, Manufacturer FROM Win32_PnPEntity WHERE {w}"));

    // ------------------------------------------------------------------ périphériques en erreur
    private static DeviceCard ProblemDevices()
    {
        var c = new DeviceCard
        {
            Key = "problems", Title = "Périphériques en erreur", Glyph = "\uE7BA", Color = "#EF4444",
            Actions =
            {
                new DeviceAction { Label = "Rechercher les pilotes", Glyph = "\uE895", Goto = "updates", Tip = "Pilotes proposés par Windows Update (page Mises à jour)" },
                new DeviceAction { Label = "Détecter le matériel", Glyph = "\uE72C", Tip = "Windows recherche à nouveau les périphériques branchés (autorisation administrateur)",
                                   Run = () => Elevated("pnputil.exe", "/scan-devices", "Détection terminée : relancez l'analyse pour voir le résultat.") },
                new DeviceAction { Label = "Gestionnaire de périphériques", Glyph = "\uE7F8", Uri = "devmgmt.msc" }
            }
        };
        var bad = Pnp("ConfigManagerErrorCode <> 0").Where(o => Num(o, "ConfigManagerErrorCode") is not (24 or 45)).ToList();
        var virtualOff = new List<string>();
        foreach (var o in bad.OrderBy(o => Num(o, "ConfigManagerErrorCode") == 22 ? 1 : 0))
        {
            var code = Num(o, "ConfigManagerErrorCode");
            var name = Str(o, "Name") ?? "Périphérique inconnu";
            var id = Str(o, "DeviceID");
            var issue = new DeviceIssue
            {
                Level = code == 22 ? DeviceStatus.Info : DeviceStatus.Problem,
                Text = $"{name} {(ErrorCodes.TryGetValue(code, out var why) ? why : "rencontre un problème")} (code {code})"
            };
            if (code == 22 && IsVirtual(id, name))
            {
                // Périphérique créé par un logiciel (VPN, réseau virtuel…) : c'est ce logiciel qui l'active quand il s'en sert
                virtualOff.Add(name);
                continue;
            }
            if (code == 22)
                issue.Actions.Add(new DeviceAction { Label = "Activer", Glyph = "\uE73E", Run = () => EnableAsync(id, name) });
            else if (code is 28 or 18 or 39 or 31 or 37)
                issue.Actions.Add(new DeviceAction { Label = "Chercher un pilote", Glyph = "\uE895", Goto = "updates" });
            else
                issue.Actions.Add(new DeviceAction { Label = "Redémarrer le périphérique", Glyph = "\uE72C",
                                                     Run = () => Elevated("pnputil.exe", $"/restart-device \"{id}\"", $"{name} a été redémarré.") });
            c.Issues.Add(issue);
        }
        if (virtualOff.Count > 0)
            c.Lines.Add((virtualOff.Count == 1 ? "Désactivé par son logiciel quand il ne s'en sert pas (normal) : " : "Désactivés par leur logiciel quand ils ne s'en servent pas (normal) : ")
                        + string.Join(", ", virtualOff.Distinct()));
        int real = c.Issues.Count(i => i.Level == DeviceStatus.Problem);
        c.Headline = real == 0 ? (c.Issues.Count == 0 ? "Tous les périphériques reconnus par Windows fonctionnent." : "Aucun périphérique en panne · certains sont désactivés.")
                   : real == 1 ? "1 périphérique ne fonctionne pas correctement." : $"{real} périphériques ne fonctionnent pas correctement.";
        return c;
    }

    // ------------------------------------------------------------------ son
    private static DeviceCard Audio()
    {
        var c = new DeviceCard
        {
            Key = "audio", Title = "Son et micro", Glyph = "\uE767", Color = "#EC4899", SettingsUri = "ms-settings:sound",
            Software = new[] { "Realtek Audio Console::realtek", "Dolby Access", "Nahimic", "Sonic Studio::asus,xonar,VID_0B05,VEN_1043", "SteelSeries GG::steelseries,VID_1038",
                               "Logitech G HUB::logitech,VID_046D", "Razer Synapse::razer,VID_1532", "Corsair iCUE::corsair,VID_1B1C", "HyperX NGENUITY::hyperx,VID_0951,VID_03F0",
                               "Voicemeeter", "Elgato Wave Link::elgato,VID_0FD9",
                               "Sound Blaster Command::creative,VEN_1102", "Creative App::creative,VEN_1102", "Sound Blaster Connect::creative,VEN_1102",
                               "FiiO::fiio,VID_2972", "Focusrite Control::focusrite,VID_1235", "Universal Audio Console::universal audio,VID_2B5A" }
        };
        var output = CoreAudio.Default(render: true);
        var input = CoreAudio.Default(render: false);
        c.Hardware = string.Join(" ", Pnp("PNPClass = 'MEDIA' OR PNPClass = 'AudioEndpoint'").Select(o => $"{Str(o, "Name")} {Str(o, "Manufacturer")} {Str(o, "DeviceID")}"));
        if (output == null)
            c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Problem, Text = "Aucune sortie audio : branchez un casque ou des haut-parleurs, ou vérifiez le pilote audio.",
                                           Actions = { new DeviceAction { Label = "Paramètres du son", Uri = "ms-settings:sound" } } });
        else
        {
            c.Lines.Add($"Sortie : {output.Name} · volume {output.Volume:0} %{(output.Muted ? " · coupé" : "")}");
            if (output.Muted || output.Volume < 2)
                c.Issues.Add(new DeviceIssue
                {
                    Text = output.Muted ? $"Le son est coupé sur {output.Name}." : $"Le volume de {output.Name} est à zéro.",
                    Actions = { new DeviceAction { Label = "Réactiver le son", Glyph = "\uE767", Primary = true,
                                                   Run = () => Task.Run(() => CoreAudio.Unmute(render: true) ? "Son réactivé." : "Impossible de modifier le volume.") } }
                });
            c.Actions.Add(new DeviceAction { Label = "Tester le son", Glyph = "\uE768", Tip = "Joue le son « information » de Windows",
                                             Run = () => Task.FromResult(MessageBeep(0x40) ? $"Un son a été joué sur {output.Name}. Rien entendu ? Vérifiez le volume du casque ou des enceintes." : null) });
        }
        if (input == null) c.Lines.Add("Micro : aucun");
        else
        {
            c.Lines.Add($"Micro : {input.Name} · niveau {input.Volume:0} %{(input.Muted ? " · coupé" : "")}");
            if (input.Muted || input.Volume < 2)
                c.Issues.Add(new DeviceIssue
                {
                    Text = input.Muted ? $"Le micro {input.Name} est coupé : on ne vous entend pas en appel." : $"Le niveau du micro {input.Name} est à zéro.",
                    Actions = { new DeviceAction { Label = "Réactiver le micro", Glyph = "\uE720", Primary = true,
                                                   Run = () => Task.Run(() => CoreAudio.Unmute(render: false) ? "Micro réactivé." : "Impossible de modifier le micro.") } }
                });
            if (PrivacyDenied("microphone"))
                c.Issues.Add(new DeviceIssue
                {
                    Text = "Les applications n'ont pas le droit d'utiliser le micro (Confidentialité de Windows).",
                    Actions = { new DeviceAction { Label = "Autoriser le micro", Glyph = "\uE72E", Uri = "ms-settings:privacy-microphone" } }
                });
        }
        SoundCards(c);
        if (output != null) c.Present.Add("speakers");
        if (input != null) c.Present.Add("mic");
        try { c.Issues.AddRange(InputChecks.Audio()); } catch (Exception ex) { Log.Error("Réglages du son", ex); }
        if (output != null) c.Actions.Add(new DeviceAction { Label = "Trouver la bonne sortie", Glyph = "\uE767", Goto = "test:speakers", Tip = "Teste chaque casque et enceinte branchés" });
        if (input != null) c.Actions.Add(new DeviceAction { Label = "Trouver le bon micro", Glyph = "\uE720", Goto = "test:mic", Tip = "Montre quel micro reçoit votre voix" });
        c.Headline = !c.Issues.Any(i => i.Level >= DeviceStatus.Warning) ? "Le son et le micro sont actifs." : "Le son ou le micro demande votre attention.";
        return c;
    }

    // ------------------------------------------------------------------ réseau
    private static readonly System.Text.RegularExpressions.Regex VirtualAdapter = new(
        @"hyper-v|virtualbox|vmware|vethernet|wsl|tap-|tunnel|loopback|bluetooth|wan miniport|npcap|zerotier|tailscale|wireguard|openvpn|nordlynx|pseudo",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static async Task<DeviceCard> Network()
    {
        var c = new DeviceCard { Key = "network", Title = "Réseau et Internet", Glyph = "\uE701", Color = "#3B82F6", SettingsUri = "ms-settings:network-status" };
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                        && !VirtualAdapter.IsMatch(n.Description + " " + n.Name))
            .ToList();
        foreach (var n in up)
        {
            var kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Câble (Ethernet)" : "Réseau";
            var speed = n.Speed > 0 ? $" · {(n.Speed >= 1_000_000_000 ? $"{n.Speed / 1e9:0.#} Gbit/s" : $"{n.Speed / 1e6:0} Mbit/s")}" : "";
            c.Lines.Add($"{kind} : {n.Description}{speed}");
        }
        if (up.Count == 0)
        {
            c.Issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Problem, Text = "Aucune connexion active : Wi-Fi désactivé ou déconnecté, ou câble débranché.",
                Actions = { new DeviceAction { Label = "Paramètres Wi-Fi", Glyph = "\uE701", Uri = "ms-settings:network-wifi" } }
            });
            c.Headline = "Le PC n'est connecté à aucun réseau.";
            return c;
        }

        // Internet joignable ? Même test que Windows, puis distinction box / DNS
        bool web = false, ip = false, dns = false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            web = (await http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt")).Contains("Microsoft Connect Test");
        }
        catch { }
        if (!web)
        {
            try { using var ping = new Ping(); ip = (await ping.SendPingAsync("1.1.1.1", 2000)).Status == IPStatus.Success; } catch { }
            try { dns = (await System.Net.Dns.GetHostAddressesAsync("www.microsoft.com")).Length > 0; } catch { }
        }
        if (web) c.Headline = "Connecté à Internet.";
        else if (ip && !dns)
        {
            c.Headline = "Internet répond, mais les noms de sites ne sont pas trouvés (DNS).";
            c.Issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Problem, Text = "La résolution des noms (DNS) échoue : les sites ne s'ouvrent pas alors que la connexion fonctionne.",
                Actions = { new DeviceAction { Label = "Vider le cache DNS", Glyph = "\uE72C", Primary = true,
                                               Run = () => Task.Run(() => DnsFlushResolverCache() ? "Cache DNS vidé : réessayez d'ouvrir un site." : "Impossible de vider le cache DNS.") } }
            });
        }
        else if (ip)
        {
            c.Headline = "Connecté, mais l'accès au web est filtré (portail Wi-Fi, proxy ou pare-feu ?).";
            c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Warning, Text = "Un réseau public peut demander une connexion dans le navigateur avant d'accéder à Internet." });
        }
        else
        {
            c.Headline = "Connecté au réseau, mais pas à Internet.";
            c.Issues.Add(new DeviceIssue
            {
                Level = DeviceStatus.Problem, Text = "La box ou le fournisseur d'accès ne répond pas : redémarrez la box, vérifiez ses voyants et les câbles.",
                Actions = { new DeviceAction { Label = "Utilitaire de résolution", Glyph = "\uE90F", Uri = "ms-settings:troubleshoot" } }
            });
        }
        return c;
    }

    // ------------------------------------------------------------------ écrans et carte graphique
    private static DeviceCard Displays()
    {
        var c = new DeviceCard
        {
            Key = "display", Title = "Écrans et carte graphique", Glyph = "\uE7F4", Color = "#8B5CF6", SettingsUri = "ms-settings:display",
            Software = new[] { "NVIDIA App::nvidia", "NVIDIA Control Panel::nvidia", "Panneau de configuration NVIDIA::nvidia", "GeForce Experience::nvidia", "AMD Software::amd,radeon",
                               "Intel Graphics Command Center::intel", "Intel® Graphics Software::intel", "Intel Graphics Software::intel" }
        };
        int n = 0;
        foreach (var d in DisplayModes.Active())
        {
            n++;
            var name = d.Monitor;
            c.Lines.Add($"{name} : {d.Width} × {d.Height} · {d.Hz} Hz");
            if (d.MaxHz > d.Hz + 1)
            {
                var target = d;
                c.Issues.Add(new DeviceIssue
                {
                    Level = DeviceStatus.Warning,
                    Text = $"{name} fonctionne à {d.Hz} Hz alors qu'il peut afficher {d.MaxHz} Hz : l'image serait plus fluide.",
                    Actions = { new DeviceAction { Label = $"Passer à {d.MaxHz} Hz", Glyph = "\uE7F4", Primary = true,
                                                   Run = () => Task.Run(() => DisplayModes.SetRefresh(target) ? $"{target.Monitor} affiche maintenant {target.MaxHz} Hz." : "Windows a refusé ce réglage.") },
                                new DeviceAction { Label = "Paramètres avancés", Uri = "ms-settings:display-advancedgraphics" } }
                });
            }
        }
        foreach (var g in Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
        {
            var gname = Str(g, "Name");
            if (string.IsNullOrEmpty(gname)) continue;
            // Écran virtuel (casque VR, bureau à distance, diffusion) : pas une carte graphique, ignoré
            if (IsVirtual(Str(g, "PNPDeviceID"), gname) || System.Text.RegularExpressions.Regex.IsMatch(gname, @"parsec|remote display|idd|indirect display|spacedesk|virtual display", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;
            DateTime? date = null;
            try { var raw = Str(g, "DriverDate"); if (raw != null) date = ManagementDateTimeConverter.ToDateTime(raw); } catch { }
            c.Lines.Add($"Carte graphique : {gname}" + (date != null ? $" · pilote du {date:d MMMM yyyy}" : ""));
            if (gname.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
                c.Issues.Add(new DeviceIssue
                {
                    Level = DeviceStatus.Problem, Text = "La carte graphique utilise le pilote de base de Windows : jeux et vidéos seront lents.",
                    Actions = { new DeviceAction { Label = "Installer le pilote", Glyph = "\uE895", Goto = "updates", Primary = true } }
                });
            else if (date != null && DateTime.Now - date.Value > TimeSpan.FromDays(365))
                c.Issues.Add(new DeviceIssue
                {
                    Level = DeviceStatus.Info, Text = $"Le pilote de {gname} a plus d'un an : une version récente améliore souvent les jeux.",
                    Actions = { new DeviceAction { Label = "Voir les mises à jour", Glyph = "\uE895", Goto = "updates" } }
                });
        }
        c.Headline = n == 0 ? "Aucun écran détecté." : n == 1 ? "1 écran." : $"{n} écrans.";
        return c;
    }

    // ------------------------------------------------------------------ stockage
    private static DeviceCard Storage()
    {
        var c = new DeviceCard
        {
            Key = "storage", Title = "Disques", Glyph = "\uEDA2", Color = "#10B981", SettingsUri = "ms-settings:storagesense",
            Software = new[] { "Samsung Magician::samsung", "Crucial Storage Executive::crucial,micron", "WD Dashboard::WDC,WD ,western digital,sandisk", "Western Digital Dashboard::WDC,WD ,western digital,sandisk",
                               "Kingston SSD Manager::kingston", "CrystalDiskInfo", "SeaTools::seagate" }
        };
        // Disques physiques (nom, type, santé) et lettres de lecteur de chacun
        const string ns = @"\\.\root\Microsoft\Windows\Storage";
        var letters = new Dictionary<char, string>();
        foreach (var part in CachedQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", ns))
        {
            var raw = Str(part, "DriveLetter");
            char l = raw?.Length == 1 ? raw[0] : (char)Num(part, "DriveLetter");
            if (char.IsLetter(l)) letters[char.ToUpperInvariant(l)] = Num(part, "DiskNumber").ToString();
        }
        var disks = new Dictionary<string, DeviceGroup>();
        foreach (var p in CachedQuery("SELECT DeviceId, FriendlyName, HealthStatus, MediaType, BusType, Size FROM MSFT_PhysicalDisk", ns))
        {
            var name = Str(p, "FriendlyName") ?? "Disque";
            long bus = Num(p, "BusType"), media = Num(p, "MediaType"), health = Num(p, "HealthStatus");
            var kind = bus == 17 ? "SSD NVMe" : media == 4 ? "SSD" : media == 3 ? "Disque dur" : bus == 7 ? "Disque USB" : "Disque";
            var g = new DeviceGroup
            {
                Title = name, Subtitle = $"{kind} · {Size(Num(p, "Size"))}" + (bus == 7 ? " · externe" : ""),
                Glyph = media == 3 ? "\uEDA2" : bus == 7 ? "\uE88E" : "\uE8B7",
                Level = health switch { 0 => DeviceStatus.Ok, 1 => DeviceStatus.Warning, 2 => DeviceStatus.Problem, _ => DeviceStatus.Info },
                StatusText = health switch { 0 => "En bonne santé", 1 => "Avertissement", 2 => "Défaillant", _ => "État inconnu" },
                External = bus == 7, IsDisk = true
            };
            disks[Str(p, "DeviceId") ?? name] = g;
            c.Hardware += $" {name}";
            if (health is 1 or 2)
                c.Issues.Add(new DeviceIssue
                {
                    Level = health == 2 ? DeviceStatus.Problem : DeviceStatus.Warning,
                    Text = $"{name} signale un problème de santé : sauvegardez vos fichiers importants sans attendre."
                });
        }
        DeviceGroup others = null;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderBy(d => d.Name))
        {
            double free = d.AvailableFreeSpace, total = d.TotalSize;
            double pct = total > 0 ? free * 100 / total : 100;
            var letter = d.Name.TrimEnd('\\');
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? letter : $"{letter} ({d.VolumeLabel})";
            bool system = string.Equals(d.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);
            var level = pct < 5 || free < 5e9 ? DeviceStatus.Problem : pct < 10 || free < 10e9 ? DeviceStatus.Warning : DeviceStatus.Ok;
            var gauge = new DeviceGauge
            {
                Label = label + (system ? " · Windows" : ""), Detail = $"{Size(free)} libres sur {Size(total)}",
                Fraction = total > 0 ? 1 - free / total : 0, Level = level, Path = d.Name, IsSystem = system
            };
            if (letters.TryGetValue(char.ToUpperInvariant(letter[0]), out var num) && disks.TryGetValue(num, out var g)) { gauge.External = g.External; g.Gauges.Add(gauge); }
            else (others ??= new DeviceGroup { Title = "Autres lecteurs", Glyph = "\uEDA2", Level = DeviceStatus.Ok }).Gauges.Add(gauge);
            if (level != DeviceStatus.Ok)
                c.Issues.Add(new DeviceIssue
                {
                    Level = level,
                    Text = $"{label} est presque plein ({Size(free)} libres) : " + (system ? "Windows ralentit et les mises à jour peuvent échouer." : "vous ne pourrez bientôt plus y enregistrer de fichiers."),
                    Actions = { new DeviceAction { Label = "Libérer de l'espace", Glyph = "\uE74D", Uri = "ms-settings:storagesense", Primary = system } }
                });
        }
        // Disque système en premier, puis par lettre
        c.Groups.AddRange(disks.Values.OrderBy(g => g.Gauges.Any(x => x.Label.EndsWith("· Windows")) ? 0 : 1)
                               .ThenBy(g => g.Gauges.Count == 0 ? "~" : g.Gauges[0].Label));
        if (others != null) c.Groups.Add(others);
        c.Headline = c.Issues.Count == 0 ? "Place disponible et disques en bonne santé." : "Un disque demande votre attention.";
        return c;
    }

    // ------------------------------------------------------------------ batterie
    private static DeviceCard Battery()
    {
        var bat = Query("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
        if (bat.Count == 0) return null;   // PC fixe : pas de carte
        var c = new DeviceCard { Key = "battery", Title = "Batterie", Glyph = "\uE83F", Color = "#F59E0B", SettingsUri = "ms-settings:batterysaver" };
        var b = bat[0];
        var charge = Num(b, "EstimatedChargeRemaining");
        bool plugged = Num(b, "BatteryStatus") is 2 or 6 or 7 or 8 or 9;
        var group = new DeviceGroup { Title = "Batterie", Subtitle = plugged ? "Branché sur secteur" : "Sur batterie", Glyph = "\uE83F", Level = DeviceStatus.Ok };
        group.Gauges.Add(new DeviceGauge { Label = "Charge", Detail = $"{charge} %", Fraction = charge / 100.0,
                                           Level = charge < 10 && !plugged ? DeviceStatus.Problem : charge < 20 && !plugged ? DeviceStatus.Warning : DeviceStatus.Ok });
        c.Groups.Add(group);
        try
        {
            long design = Query("SELECT DesignedCapacity FROM BatteryStaticData", @"\\.\root\wmi").Select(o => Num(o, "DesignedCapacity")).FirstOrDefault();
            long full = Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"\\.\root\wmi").Select(o => Num(o, "FullChargedCapacity")).FirstOrDefault();
            if (design > 0 && full > 0)
            {
                var health = Math.Min(100, full * 100.0 / design);
                group.Gauges.Add(new DeviceGauge { Label = "Capacité restante", Detail = $"{health:0} % de la capacité d'origine", Fraction = health / 100,
                                                   Level = health < 60 ? DeviceStatus.Warning : DeviceStatus.Ok });
                if (health < 60)
                    c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Warning, Text = $"La batterie ne retient plus que {health:0} % de sa capacité : son autonomie est fortement réduite, un remplacement peut être envisagé." });
            }
        }
        catch { }
        c.Actions.Add(new DeviceAction
        {
            Label = "Rapport détaillé", Glyph = "\uE9F9", Tip = "Rapport de Windows sur l'usure et l'autonomie (ouvert dans le navigateur)",
            Run = async () =>
            {
                var file = Path.Combine(Path.GetTempPath(), "rapport-batterie.html");
                var p = Process.Start(new ProcessStartInfo("powercfg", $"/batteryreport /output \"{file}\"") { CreateNoWindow = true, UseShellExecute = false });
                if (p != null) await p.WaitForExitAsync();
                if (!File.Exists(file)) return "Rapport indisponible sur ce PC.";
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                return null;
            }
        });
        if (charge < 15 && !plugged) c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Warning, Text = "Batterie faible : branchez le chargeur." });
        c.Headline = c.Issues.Count == 0 ? "Batterie en bon état." : "La batterie demande votre attention.";
        return c;
    }

    // ------------------------------------------------------------------ imprimantes
    private static readonly System.Text.RegularExpressions.Regex VirtualPrinter = new(
        @"pdf|xps|onenote|fax|send to|envoyer vers|anydesk|snagit|cutepdf|adobe", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static DeviceCard Printers()
    {
        var c = new DeviceCard
        {
            Key = "printers", Title = "Imprimantes", Glyph = "\uE749", Color = "#06B6D4", SettingsUri = "ms-settings:printers",
            Software = new[] { "HP Smart::hp", "Epson::epson", "Canon::canon", "Brother::brother", "Samsung Printer::samsung", "Kyocera::kyocera", "Lexmark::lexmark", "Xerox::xerox" }
        };
        var printers = Query("SELECT Name, Default, WorkOffline, DetectedErrorState, PrinterStatus FROM Win32_Printer")
            .Where(p => !VirtualPrinter.IsMatch(Str(p, "Name") ?? "")).ToList();
        if (printers.Count == 0) return null;   // aucune imprimante réelle : pas de carte
        var jobs = Query("SELECT Name FROM Win32_PrintJob").Select(j => Str(j, "Name")?.Split(',')[0].Trim()).Where(n => n != null).ToList();
        foreach (var p in printers)
        {
            var name = Str(p, "Name");
            bool def = Str(p, "Default") == "True";
            int waiting = jobs.Count(j => j.Equals(name, StringComparison.OrdinalIgnoreCase));
            c.Lines.Add($"{name}{(def ? " · par défaut" : "")}{(waiting > 0 ? $" · {waiting} document(s) en attente" : "")}");
            var queue = new DeviceAction { Label = "File d'attente", Glyph = "\uE9F9", Uri = $"rundll32.exe printui.dll,PrintUIEntry /o /n \"{name}\"" };
            if (Str(p, "WorkOffline") == "True" || Num(p, "PrinterStatus") == 7)
                c.Issues.Add(new DeviceIssue { Text = $"{name} est hors ligne : allumez-la et vérifiez sa connexion (câble ou Wi-Fi).", Actions = { queue } });
            var err = Num(p, "DetectedErrorState");
            var errText = err switch { 3 => "papier presque épuisé", 4 => "plus de papier", 5 => "encre ou toner presque épuisé", 6 => "plus d'encre ou de toner",
                                       7 => "capot ouvert", 8 => "bourrage papier", 10 => "intervention nécessaire", 11 => "bac de sortie plein", _ => null };
            if (errText != null) c.Issues.Add(new DeviceIssue { Level = err is 3 or 5 ? DeviceStatus.Info : DeviceStatus.Warning, Text = $"{name} : {errText}.", Actions = { queue } });
            if (waiting > 0 && errText == null)
                c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Info, Text = $"{waiting} document(s) attendent sur {name} : s'ils sont bloqués, annulez-les depuis la file d'attente.", Actions = { queue } });
        }
        c.Headline = c.Issues.Count == 0 ? (printers.Count == 1 ? "Imprimante prête." : $"{printers.Count} imprimantes prêtes.") : "Une imprimante demande votre attention.";
        return c;
    }

    // ------------------------------------------------------------------ Bluetooth
    private static readonly System.Text.RegularExpressions.Regex BtNoise = new(
        @"service|enumerator|énumérateur|protocol|protocole|rfcomm|generic|générique|transport|attribute|avrcp|hands-free|mains libres|audio gateway|a2dp snk",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static DeviceCard Bluetooth()
    {
        var all = Pnp("PNPClass = 'Bluetooth'");
        var adapters = all.Where(o => (Str(o, "DeviceID") ?? "").StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                                      || (Str(o, "DeviceID") ?? "").StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (adapters.Count == 0) return null;   // pas de Bluetooth sur ce PC
        var c = new DeviceCard
        {
            Key = "bluetooth", Title = "Bluetooth", Glyph = "\uE702", Color = "#2563EB", SettingsUri = "ms-settings:bluetooth",
            Software = new[] { "Sena Device Manager::sena", "Sony | Headphones Connect::WH-,WF-,sony", "Galaxy Buds::buds", "Bose Music::bose", "JBL Headphones::jbl" }
        };
        foreach (var a in adapters)
        {
            c.Lines.Add($"Adaptateur : {Str(a, "Name")}");
            if (Num(a, "ConfigManagerErrorCode") != 0)
                c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Problem, Text = $"L'adaptateur Bluetooth ne fonctionne pas (code {Num(a, "ConfigManagerErrorCode")}).",
                                               Actions = { new DeviceAction { Label = "Gestionnaire de périphériques", Uri = "devmgmt.msc" } } });
        }
        var paired = all.Where(o => !adapters.Contains(o))
            .Select(o => Str(o, "Name")).Where(n => !string.IsNullOrWhiteSpace(n) && !BtNoise.IsMatch(n))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(n => n).ToList();
        if (paired.Count > 0) c.Lines.Add($"Appareils associés : {string.Join(", ", paired.Take(8))}{(paired.Count > 8 ? "…" : "")}");
        c.Headline = c.Issues.Count == 0 ? $"Bluetooth disponible · {paired.Count} appareil(s) associé(s)." : "Le Bluetooth ne fonctionne pas.";
        c.Lines.Add("Un appareil ne se connecte plus ? Vérifiez que le Bluetooth est activé, puis supprimez et associez-le à nouveau.");
        return c;
    }

    // ------------------------------------------------------------------ caméra
    private static DeviceCard Camera()
    {
        var cams = Pnp("PNPClass = 'Camera' OR PNPClass = 'Image'")
            .Where(o => Str(o, "PNPClass") == "Camera" || System.Text.RegularExpressions.Regex.IsMatch(Str(o, "Name") ?? "", "cam|caméra|camera|webcam", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();
        if (cams.Count == 0) return null;
        var c = new DeviceCard
        {
            Key = "camera", Title = "Caméra", Glyph = "\uE714", Color = "#14B8A6", SettingsUri = "ms-settings:privacy-webcam",
            Software = new[] { "Logi Tune::logitech,VID_046D", "Logitech Capture::logitech,VID_046D", "Logitech G HUB::logitech,VID_046D", "Razer Synapse::razer,VID_1532", "Elgato Camera Hub::elgato,VID_0FD9" },
            Actions = { new DeviceAction { Label = "Tester la caméra", Glyph = "\uE714", Uri = "microsoft.windows.camera:", Tip = "Ouvre l'application Caméra de Windows" } }
        };
        foreach (var cam in cams)
        {
            c.Lines.Add(Str(cam, "Name"));
            c.Hardware += $" {Str(cam, "Manufacturer")} {Str(cam, "DeviceID")}";
            var code = Num(cam, "ConfigManagerErrorCode");
            if (code == 22)
                c.Issues.Add(new DeviceIssue { Text = $"{Str(cam, "Name")} est désactivée.",
                                               Actions = { new DeviceAction { Label = "Activer", Glyph = "\uE73E", Primary = true,
                                                                              Run = () => EnableAsync(Str(cam, "DeviceID"), Str(cam, "Name")) } } });
            else if (code != 0)
                c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Problem, Text = $"{Str(cam, "Name")} ne fonctionne pas (code {code})." });
        }
        if (PrivacyDenied("webcam"))
            c.Issues.Add(new DeviceIssue { Text = "Les applications n'ont pas le droit d'utiliser la caméra (Confidentialité de Windows).",
                                           Actions = { new DeviceAction { Label = "Autoriser la caméra", Glyph = "\uE72E", Uri = "ms-settings:privacy-webcam", Primary = true } } });
        c.Headline = c.Issues.Count == 0 ? "Caméra prête." : "La caméra demande votre attention.";
        return c;
    }

    // ------------------------------------------------------------------ clavier, souris, manettes
    private static DeviceCard Input()
    {
        var c = new DeviceCard
        {
            Key = "input", Title = "Clavier, souris et manettes", Glyph = "\uE765", Color = "#64748B", SettingsUri = "ms-settings:mousetouchpad",
            Software = new[] { "Logitech G HUB::logitech,VID_046D", "Logi Options+::logitech,VID_046D", "Logitech Options::logitech,VID_046D", "Razer Synapse::razer,VID_1532",
                               "Corsair iCUE::corsair,VID_1B1C", "SteelSeries GG::steelseries,VID_1038", "HyperX NGENUITY::hyperx,VID_0951,VID_03F0", "ROCCAT Swarm::roccat,VID_1E7D",
                               "Glorious Core::glorious,VID_258A", "Wootility::wooting,VID_31E3", "DS4Windows::VID_054C,dualshock,dualsense",
                               "Xbox Accessories::xbox,VID_045E", "Accessoires Xbox::xbox,VID_045E", "ASUS Armoury Crate::asus,rog,VID_0B05", "Armoury Crate::asus,rog,VID_0B05" }
        };
        var kb = Pnp("PNPClass = 'Keyboard'");
        var mice = Pnp("PNPClass = 'Mouse'");
        var pads = Pnp("PNPClass = 'HIDClass' OR PNPClass = 'XnaComposite' OR PNPClass = 'XboxComposite'")
            .Where(o => System.Text.RegularExpressions.Regex.IsMatch(Str(o, "Name") ?? "", @"game ?controller|manette|xbox|dualsense|dualshock|wireless controller|gamepad|joystick|contrôleur de jeu",
                                                                      System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();
        static string Names(List<ManagementBaseObject> list) =>
            string.Join(", ", list.Select(o => Str(o, "Name")).Where(n => n != null && !System.Text.RegularExpressions.Regex.IsMatch(n, "^(périphérique|clavier|souris|hid|standard)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                   .Distinct().Take(4));
        string Line(string label, List<ManagementBaseObject> l) { var n = Names(l); return $"{label} : {l.Count}" + (n.Length > 0 ? $" ({n})" : ""); }
        c.Hardware = string.Join(" ", kb.Concat(mice).Concat(pads).Select(o => $"{Str(o, "Name")} {Str(o, "Manufacturer")} {Str(o, "DeviceID")}"));
        if (pads.Count > 0)
        {
            c.Present.Add("gamepad");
            c.Actions.Add(new DeviceAction { Label = "Tester les manettes", Glyph = "\uE7FC", Goto = "test:gamepad", Tip = "Boutons, sticks, gâchettes et vibrations" });
            c.Actions.Add(new DeviceAction { Label = "Calibrer (Windows)", Glyph = "\uE7FC", Uri = "joy.cpl", Tip = "Contrôleurs de jeu de Windows : calibrage" });
        }
        bool laptop = InputChecks.IsLaptop();
        if (kb.Count > 0) c.Issues.AddRange(InputChecks.Keyboard(laptop));
        if (mice.Count > 0) c.Issues.AddRange(InputChecks.Mouse(laptop));
        c.Lines.Add(Line("Claviers", kb));
        c.Lines.Add(Line("Souris et pavés tactiles", mice));
        c.Lines.Add(pads.Count == 0 ? "Manettes : aucune branchée" : Line("Manettes", pads));
        foreach (var o in kb.Concat(mice).Concat(pads).Where(o => Num(o, "ConfigManagerErrorCode") is not (0 or 24 or 45)))
            c.Issues.Add(new DeviceIssue { Level = DeviceStatus.Problem, Text = $"{Str(o, "Name")} ne fonctionne pas (code {Num(o, "ConfigManagerErrorCode")}).",
                                           Actions = { new DeviceAction { Label = "Redémarrer le périphérique", Glyph = "\uE72C",
                                                                          Run = () => Elevated("pnputil.exe", $"/restart-device \"{Str(o, "DeviceID")}\"", "Périphérique redémarré.") } } });
        c.Headline = c.Issues.Any(i => i.Level == DeviceStatus.Problem) ? "Un périphérique de saisie ne fonctionne pas."
                   : c.Issues.Any(i => i.Level == DeviceStatus.Warning) ? "Un réglage gêne l'utilisation du clavier ou de la souris."
                   : c.Issues.Count > 0 ? "Clavier et souris reconnus · quelques réglages à connaître."
                   : pads.Count > 0 ? "Clavier, souris et manettes reconnus." : "Clavier et souris reconnus.";
        return c;
    }

    /// <summary>Nom de la sortie audio et du micro utilisés par défaut (outils de test).</summary>
    public static string CurrentOutput() => CoreAudio.Default(render: true)?.Name;
    public static string CurrentInput() => CoreAudio.Default(render: false)?.Name;

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
    [DllImport("dnsapi.dll")] private static extern bool DnsFlushResolverCache();
}

/// <summary>Volume et coupure du son par l'API audio de Windows (Core Audio).</summary>
internal static partial class CoreAudio
{
    public sealed record Endpoint(string Name, float Volume, bool Muted);

    public static Endpoint Default(bool render)
    {
        try
        {
            var vol = Volume(render, out var name);
            if (vol == null) return null;
            Marshal.ThrowExceptionForHR(vol.GetMasterVolumeLevelScalar(out var level));
            Marshal.ThrowExceptionForHR(vol.GetMute(out var muted));
            Marshal.ReleaseComObject(vol);
            return new Endpoint(name, level * 100, muted);
        }
        catch (Exception ex) { Log.Error("Audio", ex); return null; }
    }

    /// <summary>Réactive le son (ou le micro) ; remonte le volume s'il était à zéro.</summary>
    public static bool Unmute(bool render)
    {
        try
        {
            var vol = Volume(render, out _);
            if (vol == null) return false;
            var ctx = Guid.Empty;
            vol.SetMute(false, ref ctx);
            vol.GetMasterVolumeLevelScalar(out var level);
            if (level < 0.05f) vol.SetMasterVolumeLevelScalar(render ? 0.4f : 0.7f, ref ctx);
            Marshal.ReleaseComObject(vol);
            return true;
        }
        catch (Exception ex) { Log.Error("Audio", ex); return false; }
    }

    private static IAudioEndpointVolume Volume(bool render, out string name)
    {
        name = null;
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            // eRender 0 / eCapture 1 ; eMultimedia 1 (eCommunications pour le micro : celui des appels)
            if (en.GetDefaultAudioEndpoint(render ? 0 : 1, render ? 1 : 2, out var dev) != 0 || dev == null) return null;
            name = FriendlyName(dev) ?? (render ? "Sortie audio" : "Micro");
            var iid = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iid, 0x17, IntPtr.Zero, out var obj);   // CLSCTX_ALL
            Marshal.ReleaseComObject(dev);
            return obj as IAudioEndpointVolume;
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    private static string FriendlyName(IMMDevice dev)
    {
        try
        {
            dev.OpenPropertyStore(0, out var store);   // STGM_READ
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            store.GetValue(ref key, out var pv);
            var s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;   // VT_LPWSTR
            PropVariantClear(ref pv);
            Marshal.ReleaseComObject(store);
            return s;
        }
        catch { return null; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);
}

/// <summary>Écrans actifs, fréquence actuelle et maximale à la même définition ; changement de fréquence.</summary>
internal static class DisplayModes
{
    public sealed record Display(string Device, string Monitor, int Width, int Height, int Hz, int MaxHz);

    public static List<Display> Active()
    {
        var res = new List<Display>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((dd.StateFlags & 0x1) == 0) continue;   // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            var cur = NewMode();
            if (!EnumDisplaySettings(dd.DeviceName, -1, ref cur)) continue;   // ENUM_CURRENT_SETTINGS
            int max = cur.dmDisplayFrequency;
            var m = NewMode();
            for (int k = 0; EnumDisplaySettings(dd.DeviceName, k, ref m); k++)
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel && m.dmDisplayFrequency > max)
                    max = m.dmDisplayFrequency;
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string name = EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : null;
            if (string.IsNullOrWhiteSpace(name) || name.Contains("Generic", StringComparison.OrdinalIgnoreCase) || name.Contains("générique", StringComparison.OrdinalIgnoreCase))
                name = $"Écran {res.Count + 1}";
            res.Add(new Display(dd.DeviceName, name, cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, max));
        }
        return res;
    }

    /// <summary>Passe l'écran à sa fréquence maximale, à la même définition (enregistré par Windows).</summary>
    public static bool SetRefresh(Display d)
    {
        var m = NewMode();
        if (!EnumDisplaySettings(d.Device, -1, ref m)) return false;
        m.dmDisplayFrequency = d.MaxHz;
        m.dmFields = 0x400000;   // DM_DISPLAYFREQUENCY
        return ChangeDisplaySettingsEx(d.Device, ref m, IntPtr.Zero, 0x1, IntPtr.Zero) == 0;   // CDS_UPDATEREGISTRY ; DISP_CHANGE_SUCCESSFUL
    }

    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
}
