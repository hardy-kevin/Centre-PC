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
/// Si HWiNFO est déjà ouvert, Centre PC ne le touche pas. Le travail est fait par un petit script PowerShell lancé en administrateur
/// (Centre PC lui-même ne l'est pas) ; il signale son avancement dans un fichier d'état.
/// </summary>
public sealed class HwinfoSession
{
    private readonly string _dir, _stop, _status;
    public bool Started { get; private set; }

    private HwinfoSession()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CentrePC-hwinfo");
        Directory.CreateDirectory(_dir);
        _stop = Path.Combine(_dir, "arret");
        _status = Path.Combine(_dir, "etat.txt");
    }

    /// <summary>Lance la session (Windows demande l'autorisation). null si refusé ou impossible.</summary>
    public static HwinfoSession Start(out string error)
    {
        error = null;
        var s = new HwinfoSession();
        try
        {
            File.Delete(s._stop);
            File.WriteAllText(s._status, "start|Préparation…");
            var script = Path.Combine(s._dir, "session.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentrePC");
            Directory.CreateDirectory(data);
            var marker = Path.Combine(data, "hwinfo-installe-par-centre-pc");
            var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Stop \"{s._stop}\" -Status \"{s._status}\" "
                     + $"-Parent {Environment.ProcessId} -Marker \"{marker}\"";
            Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
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
            var path = Path.Combine(_dir, "disques.json");
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
param([string]$Stop, [string]$Status, [int]$Parent, [string]$Marker)
$ErrorActionPreference = 'Continue'
try { Start-Transcript -Path (Join-Path (Split-Path $Status) 'session.log') -Force | Out-Null } catch { }
function Say($state, $msg) { [IO.File]::WriteAllText($Status, ""$state|$msg"", [Text.Encoding]::UTF8) }
function Find-Exe {
    foreach ($p in @(""$env:ProgramFiles\HWiNFO64\HWiNFO64.EXE"", ""${env:ProgramFiles(x86)}\HWiNFO64\HWiNFO64.EXE"")) { if (Test-Path $p) { return $p } }
    return $null
}
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

$ours = Test-Path $Marker
$exe = Find-Exe
if (-not $exe) {
    Say 'install' 'Installation de HWiNFO pour le test…'
    & winget install --id REALiX.HWiNFO -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
    $exe = Find-Exe
    if (-not $exe) { Say 'error' 'HWiNFO n''a pas pu être installé : le test se fera sans lui.'; exit 1 }
    New-Item -ItemType File -Force -Path $Marker | Out-Null
    $ours = $true
}
if (Stopped) { $exe = $exe } else {
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
    for ($i = 0; $i -lt 10 -and (Test-Path $dir); $i++) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path $dir) { Start-Sleep -Seconds 1 } }
    Remove-Item 'HKCU:\Software\HWiNFO64' -Recurse -Force -ErrorAction SilentlyContinue
    # Le repère « installé par Centre PC » n'est retiré qu'une fois tout supprimé : sinon le nettoyage se termine à la prochaine session
    if (Test-Path $dir) { Write-Output ""Dossier encore présent : nettoyage à la prochaine session"" } else { Remove-Item $Marker -Force -ErrorAction SilentlyContinue }
}
Remove-Item $Stop -Force -ErrorAction SilentlyContinue
Say 'done' 'Terminé.'
try { Stop-Transcript | Out-Null } catch { }
";
}
