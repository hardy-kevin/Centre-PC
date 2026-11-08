# maj.ps1 — lancé en administrateur par Centre PC. Écrit sa progression dans un journal lu par l'appli.
# Lignes spéciales : ##ETAPE|clé|statut|détail   et   ##FIN|redemarrage(0/1)
param([string]$Params)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$opt = Get-Content -LiteralPath $Params -Raw -Encoding UTF8 | ConvertFrom-Json
$LogPath = $opt.Log

function Log([string]$m) {
    if ($null -eq $m) { return }
    $m = $m.TrimEnd()
    if ($m -eq '' -or $m -match '^[\s\-\\|/]*$' -or $m -match '[█▒]') { return }   # animations de winget
    [IO.File]::AppendAllText($LogPath, $m + "`n", [Text.UTF8Encoding]::new($false))
}
function Etape($cle, $statut, $detail = '') { Log "##ETAPE|$cle|$statut|$detail" }

Log "Démarrage : $(Get-Date -Format 'dd/MM/yyyy HH:mm')"
$reboot = $false

# ---------------------------------------------------------------- Point de restauration
if ($opt.RestorePoint) {
    Etape 'restore' 'En cours'
    try {
        Checkpoint-Computer -Description 'Avant Centre PC' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        Etape 'restore' 'OK' 'créé'
    } catch { Etape 'restore' 'IGNORÉ' 'protection système désactivée ou déjà créé aujourd''hui' }
}

# ---------------------------------------------------------------- Applications (winget)
if ($opt.Apps) {
    Etape 'apps' 'En cours'
    $winget = (Get-Command winget.exe -ErrorAction SilentlyContinue).Source
    if (-not $winget) {
        $winget = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $winget) { Etape 'apps' 'ERREUR' 'winget introuvable (installe « App Installer » depuis le Store)' }
    else {
        $communs = @('--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
        $echecs = 0; $total = 0
        if ($opt.AllApps) {
            Log '> winget upgrade --all'
            & $winget upgrade --all --include-unknown @communs 2>&1 | ForEach-Object { Log "$_" }
            if ($LASTEXITCODE -ne 0) { $echecs++ }
            $total = 1
        } else {
            foreach ($id in $opt.AppIds) {
                $total++
                Log "> Mise à jour de $id"
                if ($id.EndsWith('…')) {   # identifiant tronqué par winget : recherche partielle
                    & $winget upgrade --query $id.TrimEnd('…') --include-unknown @communs 2>&1 | ForEach-Object { Log "$_" }
                } else {
                    & $winget upgrade --id $id --exact --include-unknown @communs 2>&1 | ForEach-Object { Log "$_" }
                }
                if ($LASTEXITCODE -ne 0) { $echecs++; Log "  ! échec ($LASTEXITCODE)" }
            }
        }
        if ($total -eq 0) { Etape 'apps' 'IGNORÉ' 'aucune appli cochée' }
        elseif ($echecs -eq 0) { Etape 'apps' 'OK' "$total traitée(s)" }
        else { Etape 'apps' 'PARTIEL' "$echecs échec(s) – souvent une appli ouverte" }
    }
}

# ---------------------------------------------------------------- Windows Update
if ($opt.WindowsUpdate) {
    Etape 'wu' 'En cours'
    try {
        if (-not (Get-Module -ListAvailable -Name PSWindowsUpdate)) {
            Log 'Installation du module PSWindowsUpdate (une seule fois)...'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            if (-not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) {
                Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
            }
            Install-Module -Name PSWindowsUpdate -Force -Scope AllUsers -AllowClobber -ErrorAction Stop
        }
        Import-Module PSWindowsUpdate -ErrorAction Stop
        if (-not (Get-WUServiceManager | Where-Object ServiceID -eq '7971f918-a847-4430-9279-4a52d1efe18d')) {
            Add-WUServiceManager -MicrosoftUpdate -Confirm:$false | Out-Null
        }
        $p = @{ MicrosoftUpdate = $true; AcceptAll = $true; IgnoreReboot = $true }
        # Pilote graphique géré par le logiciel du fabricant (AMD / NVIDIA) : exclu de Windows Update
        if ($opt.GpuFilter) { $p.NotTitle = $opt.GpuFilter }
        $liste = @(Get-WindowsUpdate @p)
        if ($liste.Count -eq 0) { Etape 'wu' 'OK' 'déjà à jour' }
        else {
            $liste | ForEach-Object { Log "  • $($_.Title)" }
            Install-WindowsUpdate @p -Verbose:$false 2>&1 | ForEach-Object { if ($_.Title) { Log "  $($_.Result) : $($_.Title)" } }
            Etape 'wu' 'OK' "$($liste.Count) mise(s) à jour"
        }
        if (Get-WURebootStatus -Silent) { $reboot = $true }
    } catch {
        Log "Erreur : $($_.Exception.Message)"
        Etape 'wu' 'ERREUR' $_.Exception.Message
    }
}

# ---------------------------------------------------------------- Microsoft Store
if ($opt.Store) {
    Etape 'store' 'En cours'
    try {
        Get-CimInstance -Namespace 'root\cimv2\mdm\dmmap' -ClassName 'MDM_EnterpriseModernAppManagement_AppManagement01' -ErrorAction Stop |
            Invoke-CimMethod -MethodName UpdateScanMethod -ErrorAction Stop | Out-Null
        Etape 'store' 'OK' 'mises à jour lancées en arrière-plan'
    } catch { Etape 'store' 'PARTIEL' 'ouvre le Store > Bibliothèque > Obtenir les mises à jour' }
}

# ---------------------------------------------------------------- Defender
if ($opt.Defender) {
    Etape 'defender' 'En cours'
    try {
        Update-MpSignature -ErrorAction Stop
        Etape 'defender' 'OK' (Get-MpComputerStatus).AntivirusSignatureVersion
    } catch { Etape 'defender' 'IGNORÉ' 'Defender désactivé ou autre antivirus' }
}

# ---------------------------------------------------------------- Fin
$cles = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
if ($cles | Where-Object { Test-Path $_ }) { $reboot = $true }
Log "Terminé : $(Get-Date -Format 'HH:mm')"
Log "##FIN|$([int]$reboot)"
