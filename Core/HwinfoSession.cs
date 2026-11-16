using System.Diagnostics;
using System.Text;

namespace CentrePC.Core;

/// <summary>
/// HWiNFO géré entièrement par Centre PC, le temps d'un test (une seule autorisation Windows) :
/// <list type="number">
/// <item>s'il n'est pas installé : installation silencieuse par winget (paquet officiel, vérifié) — il sera désinstallé à la fin ;</item>
/// <item>copie des réglages de l'utilisateur, puis réglages du test : mode capteurs seul, fenêtres réduites, sans écran d'accueil,
/// mesures partagées ;</item>
/// <item>lancement, puis à la fin (ou si Centre PC se ferme) : fermeture propre — son pilote, non persistant, est retiré —,
/// réglages d'origine remis, et désinstallation complète si c'est Centre PC qui l'avait installé (dossier et clé de registre compris).</item>
/// </list>
/// Si HWiNFO ne peut pas être installé (fichier retiré par son éditeur, par exemple), le même script passe à LibreHardwareMonitor
/// (logiciel libre) avec les mêmes règles : installé dans Program Files par winget, ouvert réduit, refermé, désinstallé, ainsi que
/// son pilote signé PawnIO s'il n'était pas déjà là.
/// Si HWiNFO est déjà ouvert, Centre PC ne le touche pas. Le travail est fait par un petit script PowerShell lancé en administrateur
/// (Centre PC lui-même ne l'est pas) ; il signale son avancement dans un fichier d'état.
/// </summary>
public sealed class HwinfoSession
{
    private readonly string _dir, _stop, _status, _protected;

    /// <summary>
    /// Dossier où le script administrateur écrit son état, son journal et ses repères : dans Program Files, donc non modifiable
    /// sans droits administrateur. Un programme ordinaire ne peut ni y préparer un fichier piégé, ni y poser un faux repère
    /// « installé par Centre PC ». Centre PC, lui, ne fait qu'y lire.
    /// </summary>
    public static string ProtectedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CentrePC-session");
    public bool Started { get; private set; }

    private HwinfoSession()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CentrePC-hwinfo");
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString("N")[..12];
        _protected = ProtectedDir;
        // Signal d'arrêt : seul fichier que Centre PC (sans droits) écrit ; le script ne fait que constater sa présence
        _stop = Path.Combine(_dir, "arret-" + id);
        _status = Path.Combine(_protected, $"etat-{id}.txt");
    }

    /// <summary>Lance la session (Windows demande l'autorisation). null si refusé ou impossible.</summary>
    public static HwinfoSession Start(out string error)
    {
        error = null;
        var s = new HwinfoSession();
        try
        {
            if (RemoteCatalog.IsDisabled("capteurs-hwinfo") && RemoteCatalog.IsDisabled("capteurs-lhm"))
            {
                error = "La mesure de la température du processeur est suspendue pour l'instant (un correctif est en préparation).";
                return null;
            }
            foreach (var old in Directory.EnumerateFiles(s._dir, "arret*")) try { File.Delete(old); } catch { }
            var script = Path.Combine(s._dir, "session.ps1");
            var marker = Path.Combine(s._protected, "hwinfo-installe-par-centre-pc");
            // Script vérifié par empreinte avant exécution en administrateur (un autre programme ne peut pas le remplacer)
            Updates.StartVerifiedElevated(script, Script, ("Stop", s._stop), ("Status", s._status), ("Parent", Environment.ProcessId.ToString()), ("Marker", marker),
                ("NoHwinfo", RemoteCatalog.IsDisabled("capteurs-hwinfo") ? "1" : "0"), ("NoLhm", RemoteCatalog.IsDisabled("capteurs-lhm") ? "1" : "0"));
            s.Started = true;
            return s;
        }
        catch (System.ComponentModel.Win32Exception) { error = "Autorisation refusée : le test se fera sans HWiNFO."; return null; }
        catch (Exception ex) { error = "Impossible de lancer HWiNFO : " + ex.Message; Log.Error("HWiNFO géré", ex); return null; }
    }

    /// <summary>Étape actuelle (« install », « run », « error »…) et message à afficher.</summary>
    public (string state, string message) Status()
    {
        try
        {
            var t = File.ReadAllText(_status).Trim();
            var i = t.IndexOf('|');
            return i < 0 ? (t, "") : (t[..i], t[(i + 1)..]);
        }
        catch { return ("start", "Préparation…"); }
    }

    /// <summary>Santé SMART des disques relevée par le script (administrateur) : température, usure, heures, erreurs.</summary>
    public sealed record DiskHealthInfo(string Name, string Health, double? Temp, double? TempMax, double? Wear, double? Hours, double? ReadErrors, double? WriteErrors);

    public List<DiskHealthInfo> DiskHealth()
    {
        var res = new List<DiskHealthInfo>();
        try
        {
            var path = Path.Combine(_protected, "disques.json");
            if (!File.Exists(path) || File.GetLastWriteTime(path) < DateTime.Now.AddHours(-1)) return res;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
            static double? N(System.Text.Json.JsonElement e, string p) =>
                e.TryGetProperty(p, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : null;
            foreach (var e in items)
                res.Add(new DiskHealthInfo(e.TryGetProperty("Name", out var n) ? n.GetString() : "Disque", e.TryGetProperty("Health", out var h) ? h.GetString() : null,
                                           N(e, "Temp"), N(e, "TempMax"), N(e, "Wear"), N(e, "Hours"), N(e, "ReadErrors"), N(e, "WriteErrors")));
        }
        catch (Exception ex) { Log.Error("Santé des disques", ex); }
        return res;
    }

    /// <summary>Fin du test : le script ferme HWiNFO, remet les réglages et désinstalle si besoin (en arrière-plan).</summary>
    public void Stop()
    {
        if (!Started) return;
        Started = false;
        try { File.WriteAllText(_stop, "1"); } catch { }
    }

    private const string Script = @"
param([string]$Stop, [string]$Status, [string]$Parent, [string]$Marker, [string]$NoHwinfo, [string]$NoLhm)
$Parent = [int]$Parent
$skip = $false
$ErrorActionPreference = 'Continue'
# État, journal et repères : dossier de Program Files (créé ici, en administrateur), illisible en écriture pour un programme ordinaire
$etatDir = Split-Path $Status
if (-not $etatDir.StartsWith($env:ProgramFiles, [StringComparison]::OrdinalIgnoreCase)) { exit 98 }
New-Item -ItemType Directory -Force -Path $etatDir | Out-Null
Get-ChildItem -Path $etatDir -Filter 'etat-*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
try { Start-Transcript -Path (Join-Path $etatDir 'session.log') -Force | Out-Null } catch { }
function Say($state, $msg) { [IO.File]::WriteAllText($Status, ""$state|$msg"", [Text.Encoding]::UTF8) }
function Find-Exe {
    foreach ($p in @(""$env:ProgramFiles\HWiNFO64\HWiNFO64.EXE"", ""${env:ProgramFiles(x86)}\HWiNFO64\HWiNFO64.EXE"")) { if (Test-Path $p) { return $p } }
    return $null
}
# Seul un programme signé par son éditeur (signature valide) est lancé avec les droits administrateur
function Trusted($path) { try { (Get-AuthenticodeSignature -FilePath $path).Status -eq 'Valid' } catch { $false } }
function Stopped { (Test-Path $Stop) -or -not (Get-Process -Id $Parent -ErrorAction SilentlyContinue) }
# HWiNFO suivi par son nom (et non par le processus lancé) : il peut se relancer lui-même ou avoir été ouvert par l'installation
function Running { @(Get-Process -Name 'HWiNFO64' -ErrorAction SilentlyContinue) }

# Santé des disques (comme CrystalDiskInfo) : compteurs SMART lisibles seulement en administrateur
try {
    $disks = Get-PhysicalDisk | ForEach-Object {
        $r = $_ | Get-StorageReliabilityCounter
        [pscustomobject]@{ Name = $_.FriendlyName; Health = [string]$_.HealthStatus; Temp = $r.Temperature; TempMax = $r.TemperatureMax; Wear = $r.Wear;
                           Hours = $r.PowerOnHours; ReadErrors = $r.ReadErrorsUncorrected; WriteErrors = $r.WriteErrorsUncorrected; StartStop = $r.StartStopCycleCount }
    }
    @($disks) | ConvertTo-Json | Set-Content -Encoding UTF8 -Path (Join-Path (Split-Path $Status) 'disques.json')
} catch { }

# ---------------------------------------------------------------- secours : LibreHardwareMonitor (logiciel libre)
# Mêmes principes que pour HWiNFO : installé le temps du test par le catalogue winget (empreinte vérifiée), dans Program Files
# uniquement, ouvert réduit, refermé, puis désinstallé s'il n'était pas là avant. Son pilote signé PawnIO suit la même règle.
function Run-Lhm($reason) {
    $lhmDir = Join-Path $env:ProgramFiles 'CentrePC-capteurs'
    $lhmMarker = $Marker + '-lhm'
    $pawnMarker = $Marker + '-pawnio'
    function Find-Lhm {
        foreach ($r in @($lhmDir, ""$env:ProgramFiles\LibreHardwareMonitor"", ""$env:ProgramFiles\WinGet\Packages"")) {
            if (Test-Path $r) {
                $f = Get-ChildItem -Path $r -Filter 'LibreHardwareMonitor.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($f) { return $f.FullName }
            }
        }
        return $null
    }
    # Dossier ou fichier du pilote : un service seulement « marqué pour suppression » (jusqu'au redémarrage) ne compte pas
    function Pawn-Present { (Test-Path ""$env:ProgramFiles\PawnIO"") -or (Test-Path ""$env:SystemRoot\System32\drivers\PawnIO.sys"") }
    function Lhm-Running { @(Get-Process -Name 'LibreHardwareMonitor' -ErrorAction SilentlyContinue) }
    function Winget-Log($label, $lines, $code) { Write-Output ""$label : code $code""; $lines | Select-Object -Last 8 | ForEach-Object { Write-Output ""  $_"" } }
    function Clean-Up($quiet) {
        try { Remove-NetFirewallRule -DisplayName 'Centre PC - capteurs (temporaire)' -ErrorAction SilentlyContinue } catch { }
        if (Test-Path $lhmMarker) {
            if (-not $quiet) { Say 'uninstall' 'Désinstallation de LibreHardwareMonitor…' }
            $o = & winget uninstall --id LibreHardwareMonitor.LibreHardwareMonitor -e --all-versions --silent --accept-source-agreements --disable-interactivity 2>&1
            Winget-Log 'winget uninstall LibreHardwareMonitor' $o $LASTEXITCODE
            for ($i = 0; $i -lt 10 -and (Test-Path $lhmDir); $i++) { Remove-Item $lhmDir -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path $lhmDir) { Start-Sleep -Seconds 1 } }
            try { Get-WmiObject -Namespace root -Class __Namespace -Filter ""Name='LibreHardwareMonitor'"" -ErrorAction SilentlyContinue | Remove-WmiObject -ErrorAction SilentlyContinue } catch { }
            # winget range parfois le programme dans son propre dossier de Program Files : il est retiré aussi
            Get-ChildItem -Path ""$env:ProgramFiles\WinGet\Packages"" -Filter 'LibreHardwareMonitor.LibreHardwareMonitor_*' -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
            Remove-Item ""$env:ProgramFiles\WinGet\Links\LibreHardwareMonitor.exe"" -Force -ErrorAction SilentlyContinue
            # Entrée fantôme dans « Applications installées » (installation dont les fichiers n'existent plus) : retirée
            foreach ($root in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall') {
                Get-ChildItem -Path $root -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'LibreHardwareMonitor.LibreHardwareMonitor_*' } | ForEach-Object {
                    $loc = (Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue).InstallLocation
                    if (-not $loc -or -not (Test-Path $loc)) { Remove-Item -Path $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue; Write-Output (""Entrée fantôme retirée : "" + $_.PSChildName) }
                }
            }
            if (Find-Lhm) { Write-Output 'LibreHardwareMonitor encore présent : retrait à la prochaine session' } else { Remove-Item $lhmMarker -Force -ErrorAction SilentlyContinue }
        }
        if (Test-Path $pawnMarker) {
            if (-not $quiet) { Say 'uninstall' 'Retrait du pilote PawnIO…' }
            $o = & winget uninstall --id namazso.PawnIO -e --silent --accept-source-agreements --disable-interactivity 2>&1
            Winget-Log 'winget uninstall PawnIO' $o $LASTEXITCODE
            Start-Sleep -Seconds 2
            $svc = Get-Service -Name 'PawnIO' -ErrorAction SilentlyContinue
            Write-Output (""PawnIO après retrait : service = "" + $(if ($svc) { $svc.Status } else { 'absent' }) + "", fichiers = "" + (Pawn-Present))
            if (Pawn-Present) { Write-Output 'PawnIO encore présent : retrait à la prochaine session' }
            else { if ($svc) { & sc.exe delete PawnIO | Out-Null }; Remove-Item $pawnMarker -Force -ErrorAction SilentlyContinue }
        }
    }

    Write-Output ""HWiNFO indisponible ($reason) : passage à LibreHardwareMonitor""
    if ($NoLhm -eq '1') { Say 'error' ""HWiNFO indisponible ($reason), et LibreHardwareMonitor est suspendu pour l'instant.""; return }
    if (-not (Pawn-Present)) {
        Say 'install' 'HWiNFO indisponible : installation du pilote de capteurs PawnIO…'
        $o = & winget install --id namazso.PawnIO -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1
        Winget-Log 'winget install PawnIO' $o $LASTEXITCODE
        if (Pawn-Present) { New-Item -ItemType File -Force -Path $pawnMarker | Out-Null }
        else { Write-Output 'PawnIO absent après installation : certaines mesures peuvent manquer' }
    }
    $lhm = Find-Lhm
    $code = 0
    if (-not $lhm) {
        Say 'install' 'Installation de LibreHardwareMonitor pour le test…'
        $o = & winget install --id LibreHardwareMonitor.LibreHardwareMonitor -e --source winget --scope machine --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1
        $code = $LASTEXITCODE
        Winget-Log 'winget install LibreHardwareMonitor' $o $code
        $lhm = Find-Lhm
        # Le repère est posé dès qu'une installation a été tentée : le nettoyage retire aussi une installation incomplète
        New-Item -ItemType File -Force -Path $lhmMarker | Out-Null
    }
    if (-not $lhm) {
        Clean-Up $true
        Say 'error' (""Ni HWiNFO ($reason) ni LibreHardwareMonitor (code "" + ('0x{0:X8}' -f $code) + "") n'ont pu être installés."")
        return
    }
    Write-Output ""LibreHardwareMonitor : $lhm""

    if (-not (Stopped)) {
        # Réglages du test : démarrage réduit près de l'horloge (l'original est remis à la fin)
        $cfg = Join-Path (Split-Path $lhm) 'LibreHardwareMonitor.config'
        $backup = if (Test-Path $cfg) { [IO.File]::ReadAllText($cfg) } else { $null }
        # Ses mesures sont lues sur son serveur local, limité à cette machine (127.0.0.1) ; par précaution, le pare-feu lui interdit
        # en plus toute connexion entrante pendant le test (règle retirée à la fin).
        $wanted = [ordered]@{ startMinMenuItem = 'true'; minTrayMenuItem = 'true'; minCloseMenuItem = 'false'; runWebServerMenuItem = 'true';
                              listenerIp = '127.0.0.1'; listenerPort = '48085'; authenticationEnabled = 'false' }
        $rule = 'Centre PC - capteurs (temporaire)'
        try { Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue; New-NetFirewallRule -DisplayName $rule -Direction Inbound -Program $lhm -Action Block -Profile Any | Out-Null }
        catch { Write-Output (""Pare-feu : "" + $_) }
        try {
            $xml = New-Object System.Xml.XmlDocument
            if ($backup) { $xml.LoadXml($backup) } else { $xml.LoadXml('<?xml version=""1.0"" encoding=""utf-8""?><configuration><appSettings /></configuration>') }
            $app = $xml.SelectSingleNode('/configuration/appSettings')
            foreach ($k in $wanted.Keys) {
                $n = $app.SelectSingleNode(""add[@key='$k']"")
                if (-not $n) { $n = $xml.CreateElement('add'); $n.SetAttribute('key', $k); $null = $app.AppendChild($n) }
                $n.SetAttribute('value', $wanted[$k])
            }
            $xml.Save($cfg)
        } catch { Write-Output (""Réglages de LibreHardwareMonitor : "" + $_) }

        Say 'open' 'Ouverture de LibreHardwareMonitor…'
        Start-Process -FilePath $lhm -WorkingDirectory (Split-Path $lhm) -WindowStyle Minimized | Out-Null
        $wait = (Get-Date).AddSeconds(25)
        while (@(Lhm-Running).Count -eq 0 -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 300 }
        if (@(Lhm-Running).Count -eq 0) { Say 'error' 'LibreHardwareMonitor ne s''est pas ouvert : le test se fera sans lui.'; Start-Sleep -Seconds 3 }
        else {
            Say 'run' 'LibreHardwareMonitor fournit ses mesures.'
            Start-Sleep -Seconds 4
            foreach ($port in 48085, 8085) {
                try { $r = Invoke-WebRequest -Uri ""http://127.0.0.1:$port/data.json"" -UseBasicParsing -TimeoutSec 4; Write-Output (""Serveur local, port $port : "" + $r.StatusCode + "", "" + $r.Content.Length + "" caractères""); Write-Output (""  "" + $r.Content.Substring(0, [Math]::Min(700, $r.Content.Length))) }
                catch { Write-Output (""Serveur local, port $port : "" + $_.Exception.Message) }
            }
            $deadline = (Get-Date).AddHours(3)
            $gone = 0
            while (-not (Stopped) -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
                if (@(Lhm-Running).Count -eq 0) { $gone++; if ($gone -ge 6) { break } } else { $gone = 0 }
            }
        }
        Say 'close' 'Fermeture de LibreHardwareMonitor…'
        foreach ($o in Lhm-Running) {
            $null = $o.CloseMainWindow()
            if (-not $o.WaitForExit(4000)) { Stop-Process -Id $o.Id -Force -ErrorAction SilentlyContinue }
        }
        $wait = (Get-Date).AddSeconds(15)
        while (@(Lhm-Running).Count -gt 0 -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 300 }
        Start-Sleep -Milliseconds 500
        try { Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue } catch { }
        if ($backup -ne $null) { [IO.File]::WriteAllText($cfg, $backup) } elseif (Test-Path $cfg) { Remove-Item $cfg -Force -ErrorAction SilentlyContinue }
    }
    Clean-Up $false
    Say 'done' 'Terminé.'
}

if ($NoHwinfo -eq '1') { Run-Lhm 'suspendu pour l''instant'; try { Stop-Transcript | Out-Null } catch { }; exit 0 }
$ours = Test-Path $Marker
$exe = Find-Exe
if (-not $exe) {
    Say 'install' 'Installation de HWiNFO pour le test…'
    # Source « winget » seule : une panne de la source du Microsoft Store ne doit pas bloquer l'installation
    $out = & winget install --id REALiX.HWiNFO -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1
    $code = $LASTEXITCODE
    Write-Output (""winget install : code $code""); $out | Select-Object -Last 12 | ForEach-Object { Write-Output ""  $_"" }
    $exe = Find-Exe
    if (-not $exe -and $code -ne -2145844844) {
        # Catalogue local périmé ou abîmé : mise à jour, puis un second essai
        Say 'install' 'Installation de HWiNFO : nouvel essai…'
        & winget source update --name winget --disable-interactivity 2>&1 | Out-Null
        $out = & winget install --id REALiX.HWiNFO -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1
        $code = $LASTEXITCODE
        Write-Output (""winget install (2) : code $code""); $out | Select-Object -Last 12 | ForEach-Object { Write-Output ""  $_"" }
        $exe = Find-Exe
    }
    if (-not $exe) {
        # Le catalogue winget pointe parfois vers un fichier que l'éditeur a déjà remplacé par une version plus récente (erreur 404).
        # Dans ce cas : téléchargement sur le miroir officiel de l'éditeur, en HTTPS, et l'installateur n'est lancé que
        # si sa signature est valide ET au nom de REALiX. Le fichier est gardé dans un dossier réservé aux administrateurs.
        $m = [regex]::Match(($out | Out-String), 'hwi64_(\d+)\.exe')
        if ($m.Success) {
            Say 'install' 'Installation de HWiNFO depuis le miroir de son éditeur…'
            $base = [int]$m.Groups[1].Value
            $tmp = Join-Path $env:ProgramFiles 'CentrePC-installation'
            $setup = Join-Path $tmp 'hwinfo-setup.exe'
            try {
                New-Item -ItemType Directory -Force -Path $tmp | Out-Null
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $ProgressPreference = 'SilentlyContinue'
                # Miroir officiel de l'éditeur (SourceForge), qui garde toutes les versions : celle du catalogue d'abord, puis les suivantes.
                # Le site hwinfo.com lui-même refuse les téléchargements automatiques : on ne le sollicite pas.
                $candidates = @($base, ($base + 2), ($base + 4), ($base + 6))
                foreach ($v in $candidates) {
                    $url = ""https://downloads.sourceforge.net/project/hwinfo/Windows_Installer/hwi64_$v.exe""
                    & (Join-Path $env:SystemRoot 'System32\curl.exe') -L -f -s --connect-timeout 10 --max-time 240 -o $setup $url 2>&1 | Out-Null
                    if ($LASTEXITCODE -eq 0 -and (Test-Path $setup) -and (Get-Item $setup).Length -gt 5MB) { Write-Output ""Téléchargé : $url""; break }
                    Remove-Item $setup -Force -ErrorAction SilentlyContinue
                }
                if (Test-Path $setup) {
                    $sig = Get-AuthenticodeSignature -FilePath $setup
                    if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -match 'REALiX') {
                        $p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-' -PassThru -Wait
                        Write-Output (""Installateur officiel : code "" + $p.ExitCode)
                    } else { Write-Output (""Installateur refusé (signature) : "" + $sig.Status + "" / "" + $sig.SignerCertificate.Subject) }
                } else { Write-Output 'Aucune version trouvée sur le miroir de l''éditeur.' }
            } catch { Write-Output (""Installation directe : "" + $_) }
            finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
            $exe = Find-Exe
        }
    }
    if (-not $exe) {
        $why = switch ($code) {
            { $_ -eq -1978335215 } { 'le fichier téléchargé ne correspond pas à celui attendu (l''éditeur vient de publier une version)' }
            { $_ -eq -1978335212 } { 'paquet introuvable dans le catalogue' }
            { $_ -eq -1978335224 } { 'téléchargement impossible (connexion ?)' }
            { $_ -eq -2145844844 } { 'fichier retiré par son éditeur' }
            default { 'code ' + ('0x{0:X8}' -f $code) }
        }
        Run-Lhm $why
        try { Stop-Transcript | Out-Null } catch { }
        exit 0
    }
    New-Item -ItemType File -Force -Path $Marker | Out-Null
    $ours = $true
}
if (-not (Trusted $exe)) { Say 'error' 'HWiNFO trouvé sur ce PC n''est pas signé par son éditeur : il n''est pas lancé.'; $skip = $true }
if ((Stopped) -or $skip) { $exe = $exe } else {
    # Réglages du test (l'original est remis à la fin)
    $ini = Join-Path (Split-Path $exe) 'HWiNFO64.INI'
    $backup = if (Test-Path $ini) { [IO.File]::ReadAllText($ini) } else { $null }
    $hadReg = Test-Path 'HKCU:\Software\HWiNFO64'
    $text = if ($backup) { $backup } else { ""[Settings]`r`n"" }
    $wanted = [ordered]@{ SensorsOnly = '1'; OpenSystemSummary = '0'; OpenSensors = '1'; MinimalizeMainWnd = '1'; MinimalizeSensors = '1';
                          MinimalizeSensorsClose = '0'; ShowWelcomeAndProgress = '0'; ShowRegDialog = '0'; SensorsSM = '1'; AutoUpdate = '0' }
    foreach ($k in $wanted.Keys) {
        if ($text -match ""(?m)^$k=.*$"") { $text = $text -replace ""(?m)^$k=.*$"", ""$k=$($wanted[$k])"" }
        else { $text = $text.TrimEnd() + ""`r`n$k=$($wanted[$k])`r`n"" }
    }
    [IO.File]::WriteAllText($ini, $text)

    # Une instance ouverte par l'installation est d'abord fermée, pour repartir avec les réglages du test
    foreach ($o in Running) { $null = $o.CloseMainWindow(); if (-not $o.WaitForExit(5000)) { Stop-Process -Id $o.Id -Force -ErrorAction SilentlyContinue } }
    Say 'open' 'Ouverture de HWiNFO…'
    Start-Process -FilePath $exe | Out-Null
    $wait = (Get-Date).AddSeconds(20)
    while (@(Running).Count -eq 0 -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 300 }
    if (@(Running).Count -eq 0) { Say 'error' 'HWiNFO ne s''est pas ouvert : le test se fera sans lui.' }
    else {
        Say 'run' 'HWiNFO fournit ses mesures.'
        $deadline = (Get-Date).AddHours(3)
        $gone = 0
        while (-not (Stopped) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            if (@(Running).Count -eq 0) { $gone++; if ($gone -ge 6) { break } } else { $gone = 0 }   # fermé par l'utilisateur
        }
    }

    # Fermeture propre (son pilote non persistant est retiré), en dernier recours forcée
    Say 'close' 'Fermeture de HWiNFO…'
    foreach ($o in Running) {
        $null = $o.CloseMainWindow()
        if (-not $o.WaitForExit(6000)) { & taskkill.exe /pid $o.Id | Out-Null; if (-not $o.WaitForExit(8000)) { Stop-Process -Id $o.Id -Force -ErrorAction SilentlyContinue } }
    }
    # Attendre la fin réelle de HWiNFO (sinon son programme reste verrouillé et ne peut pas être supprimé)
    $wait = (Get-Date).AddSeconds(20)
    while (@(Running).Count -gt 0 -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 300 }
    foreach ($o in Running) { Stop-Process -Id $o.Id -Force -ErrorAction SilentlyContinue }
    Write-Output (""HWiNFO ouvert après fermeture : "" + @(Running).Count)
    Start-Sleep -Milliseconds 800
    if ($backup -ne $null) { [IO.File]::WriteAllText($ini, $backup) } elseif (Test-Path $ini) { Remove-Item $ini -Force }
    if (-not $hadReg) { Remove-Item 'HKCU:\Software\HWiNFO64' -Recurse -Force -ErrorAction SilentlyContinue }
}

# Installé par Centre PC : désinstallation complète
if ($ours) {
    Say 'uninstall' 'Désinstallation de HWiNFO…'
    & winget uninstall --id REALiX.HWiNFO -e --silent --accept-source-agreements --disable-interactivity | Out-Null
    Write-Output (""winget uninstall : code "" + $LASTEXITCODE)
    # Le désinstalleur continue parfois après le retour de winget : on attend qu'il ait fini
    $wait = (Get-Date).AddSeconds(60)
    while ((Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'unins*' -or $_.Name -like '_iu*' }) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 500 }
    $dir = Join-Path $env:ProgramFiles 'HWiNFO64'
    $un = Join-Path $dir 'unins000.exe'
    # Dossier de Program Files (réservé aux administrateurs) : le désinstalleur de HWiNFO retire aussi son entrée dans « Applications installées »
    if (Test-Path $un) {
        Start-Process -FilePath $un -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        $wait = (Get-Date).AddSeconds(60)
        while ((Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'unins*' -or $_.Name -like '_iu*' }) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 500 }
        Write-Output 'Désinstalleur de HWiNFO exécuté.'
    }
    for ($i = 0; $i -lt 10 -and (Test-Path $dir); $i++) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path $dir) { Start-Sleep -Seconds 1 } }
    Remove-Item 'HKCU:\Software\HWiNFO64' -Recurse -Force -ErrorAction SilentlyContinue
    # Le repère « installé par Centre PC » n'est retiré qu'une fois tout supprimé : sinon le nettoyage se termine à la prochaine session
    if (Test-Path $dir) { Write-Output ""Dossier encore présent : nettoyage à la prochaine session"" } else { Remove-Item $Marker -Force -ErrorAction SilentlyContinue }
}
Say 'done' 'Terminé.'
try { Stop-Transcript | Out-Null } catch { }
";
}
