# Vérification automatique de Centre PC : compile (et lance les tests) dès que les sources changent.
# Ne réinstalle rien et ne ferme pas Centre PC : le résultat est écrit dans verification.log.
$ErrorActionPreference = 'Continue'
# Accents corrects dans le journal (sortie de dotnet lue en UTF-8)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$log = Join-Path $PSScriptRoot 'verification.log'
$out = Join-Path $env:TEMP 'CentrePC-verification'
$Host.UI.RawUI.WindowTitle = 'Centre PC - vérification automatique'

function Get-Signature {
    # Date de la dernière modification des sources (hors bin, obj, publish)
    Get-ChildItem -Path $PSScriptRoot -Recurse -File -Include *.cs, *.xaml, *.csproj, *.ps1 -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|publish)\\' -and $_.Name -ne 'surveiller.ps1' } |
        Measure-Object -Property LastWriteTimeUtc -Maximum | ForEach-Object { $_.Maximum }
}

function Invoke-Verification {
    $start = Get-Date
    Write-Host ''
    Write-Host ("[{0:HH:mm:ss}] Modification détectée : compilation..." -f $start) -ForegroundColor Cyan
    $build = & dotnet build CentrePC.csproj -c Debug -o (Join-Path $out 'app') --nologo -v q -clp:NoSummary 2>&1 | Out-String
    $buildOk = $LASTEXITCODE -eq 0
    $tests = ''
    $testsOk = $true
    if ($buildOk -and (Test-Path 'Tests\CentrePC.Tests')) {
        Write-Host '  Compilation réussie : tests...' -ForegroundColor Cyan
        $tests = & dotnet test 'Tests\CentrePC.Tests' -c Debug --nologo -v q 2>&1 | Out-String
        $testsOk = $LASTEXITCODE -eq 0
    }
    $status = if (-not $buildOk) { 'ÉCHEC DE LA COMPILATION' } elseif (-not $testsOk) { 'COMPILATION OK, TESTS EN ÉCHEC' } else { 'OK' }
    $text = @(
        "RÉSULTAT : $status"
        "Date : {0:yyyy-MM-dd HH:mm:ss} ({1:N0} s)" -f $start, ((Get-Date) - $start).TotalSeconds
        ''
        '===== Compilation ====='
        $build.Trim()
        ''
        '===== Tests ====='
        $(if ($tests) { $tests.Trim() } else { '(non lancés)' })
    ) -join "`r`n"
    # Écriture en une fois : le fichier n'est jamais lu à moitié écrit
    $tmp = "$log.tmp"
    Set-Content -Path $tmp -Value $text -Encoding UTF8
    Move-Item -Path $tmp -Destination $log -Force
    $color = if ($status -eq 'OK') { 'Green' } else { 'Red' }
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $status) -ForegroundColor $color

    # Vérification réussie : installation de la nouvelle version, Centre PC fermé puis rouvert (s'il était ouvert)
    if ($status -eq 'OK') {
        $wasRunning = [bool](Get-Process CentrePC -ErrorAction SilentlyContinue)
        Write-Host '  Installation de la nouvelle version...' -ForegroundColor Cyan
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'installer.ps1') | Out-Null
        $exe = Join-Path $env:LOCALAPPDATA 'Programs\CentrePC\CentrePC.exe'
        if ($LASTEXITCODE -eq 0) {
            Add-Content -Path $log -Value ("`r`nInstallation : OK ({0:HH:mm:ss})" -f (Get-Date)) -Encoding UTF8
            if ($wasRunning -and (Test-Path $exe)) { Start-Process $exe; Write-Host '  Centre PC rouvert.' -ForegroundColor Green }
            else { Write-Host '  Installé (Centre PC n''était pas ouvert).' -ForegroundColor Green }
        }
        else {
            Add-Content -Path $log -Value "`r`nInstallation : ÉCHEC (voir build.log)" -Encoding UTF8
            Write-Host '  Installation impossible : voir build.log' -ForegroundColor Red
        }
    }
}

Write-Host 'Centre PC - vérification automatique' -ForegroundColor Cyan
Write-Host 'Laissez cette fenêtre ouverte (elle peut être réduite). Ctrl+C pour arrêter.'
$last = $null
$seen = Get-Signature
$changedAt = Get-Date   # première vérification au démarrage
while ($true) {
    Start-Sleep -Seconds 2
    $sig = Get-Signature
    if ($sig -ne $seen) { $seen = $sig; $changedAt = Get-Date; continue }   # encore en cours d'écriture : on attend le calme
    if ($changedAt -and ((Get-Date) - $changedAt).TotalSeconds -ge 4) {
        $changedAt = $null
        Invoke-Verification
    }
}
