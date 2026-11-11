using System.Diagnostics;
using System.Text;

namespace CentrePC.Core;

/// <summary>
/// Outils d'expert (CPU-Z, GPU-Z, CrystalDiskInfo…) ouverts « le temps de l'utilisation » : s'il n'est pas installé, l'outil est installé
/// en silence par winget (paquet officiel vérifié), ouvert, puis désinstallé complètement dès qu'on le ferme. S'il était déjà installé,
/// il est simplement ouvert et laissé en place. Une seule autorisation Windows ; le travail est fait par un petit script en administrateur.
/// </summary>
public static class ToolSession
{
    public sealed record Tool(string Name, string WingetId, string ExeFilter, string Purpose);

    public static readonly Tool[] Tools =
    {
        new("CPU-Z", "CPUID.CPU-Z", "cpuz*.exe", "processeur, carte mère, mémoire (timings, SPD)"),
        new("GPU-Z", "TechPowerUp.GPU-Z", "GPU-Z*.exe", "carte graphique (BIOS, bus PCIe, capteurs)"),
        new("CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo", "DiskInfo64*.exe", "santé S.M.A.R.T. détaillée des disques"),
        new("CrystalDiskMark", "CrystalDewWorld.CrystalDiskMark", "DiskMark64*.exe", "mesure de référence des disques"),
        new("HWiNFO", "REALiX.HWiNFO", "HWiNFO64*.exe", "tous les capteurs et l'inventaire complet"),
    };

    /// <summary>Ouvre l'outil (Windows demande l'autorisation). Renvoie un message d'erreur, ou null.</summary>
    public static string Open(Tool t)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "CentrePC-outils");
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "outil.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentrePC");
            Directory.CreateDirectory(data);
            var marker = Path.Combine(data, $"outil-{t.WingetId}-installe-par-centre-pc");
            var log = Path.Combine(dir, $"{t.Name}.log");
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Id \"{t.WingetId}\" -Filter \"{t.ExeFilter}\" -Marker \"{marker}\" -Log \"{log}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            return null;
        }
        catch (System.ComponentModel.Win32Exception) { return "Autorisation refusée."; }
        catch (Exception ex) { Log.Error("Outil " + t.Name, ex); return ex.Message; }
    }

    private const string Script = @"
param([string]$Id, [string]$Filter, [string]$Marker, [string]$Log)
try { Start-Transcript -Path $Log -Force | Out-Null } catch { }
function Find-Exe {
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages')) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($r in $roots) {
        $f = Get-ChildItem -Path $r -Filter $Filter -Recurse -Depth 3 -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { return $f.FullName }
    }
    return $null
}
$ours = Test-Path $Marker
$exe = Find-Exe
if (-not $exe) {
    & winget install --id $Id -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
    Write-Output ""winget install : $LASTEXITCODE""
    $exe = Find-Exe
    if (-not $exe) { Write-Output 'Introuvable après installation'; exit 1 }
    New-Item -ItemType File -Force -Path $Marker | Out-Null
    $ours = $true
}
Write-Output ""Ouverture : $exe""
$name = [IO.Path]::GetFileNameWithoutExtension($exe)
Start-Process -FilePath $exe | Out-Null
# Attendre que l'outil soit ouvert, puis qu'il soit fermé par l'utilisateur
$wait = (Get-Date).AddSeconds(30)
while (-not (Get-Process -Name $name -ErrorAction SilentlyContinue) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 500 }
while (Get-Process -Name $name -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
Write-Output 'Outil fermé'
if ($ours) {
    $folder = Split-Path $exe
    & winget uninstall --id $Id -e --silent --accept-source-agreements --disable-interactivity | Out-Null
    Write-Output ""winget uninstall : $LASTEXITCODE""
    $w = (Get-Date).AddSeconds(60)
    while ((Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'unins*' -or $_.Name -like '_iu*' }) -and (Get-Date) -lt $w) { Start-Sleep -Milliseconds 500 }
    # Dossier restant (réglages, journaux) : supprimé seulement s'il appartient à l'outil (jamais Program Files lui-même)
    if ((Test-Path $folder) -and $folder -ne $env:ProgramFiles -and $folder -ne ${env:ProgramFiles(x86)}) { Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path $folder)) { Remove-Item $Marker -Force -ErrorAction SilentlyContinue }
}
try { Stop-Transcript | Out-Null } catch { }
";
}
