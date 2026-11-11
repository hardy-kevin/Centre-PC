# Compile Centre PC puis l'installe dans %LOCALAPPDATA%\Programs\CentrePC avec des raccourcis
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Start-Transcript -Path (Join-Path $PSScriptRoot 'build.log') -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# Kit .NET 8 (SDK) nécessaire pour compiler
$sdk = $null
try { $sdk = (& dotnet --list-sdks 2>$null) | Where-Object { $_ -match '^(8|9|10)\.' } } catch { }
if (-not $sdk) {
    Write-Host "Installation du kit .NET 8 (une seule fois)..." -ForegroundColor Cyan
    winget install --id Microsoft.DotNet.SDK.8 --exact --silent --accept-package-agreements --accept-source-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}
& dotnet --list-sdks
Write-Host "Compilation de Centre PC..." -ForegroundColor Cyan
dotnet publish CentrePC.csproj -c Release -o publish --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "La compilation a échoué." -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\CentrePC'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
# Ferme Centre PC s'il est ouvert, puis attend que le fichier soit libéré
Get-Process CentrePC -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process CentrePC -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
$copie = $false
for ($i = 0; $i -lt 15 -and -not $copie; $i++) {
    # Centre PC peut mettre une seconde à se fermer : on réessaie sans afficher d'erreur
    # (try/catch obligatoire : avec $ErrorActionPreference = 'Stop', PowerShell 5.1 arrête le script
    #  sur « fichier en cours d'utilisation » malgré -ErrorAction SilentlyContinue)
    try { Copy-Item publish\CentrePC.exe $dest -Force -ErrorAction Stop } catch { }
    if ($i -eq 5) { Get-Process CentrePC -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } } }
    if ((Get-Item $dest\CentrePC.exe -ErrorAction SilentlyContinue).Length -eq (Get-Item publish\CentrePC.exe).Length -and
        (Get-Item $dest\CentrePC.exe).LastWriteTime -eq (Get-Item publish\CentrePC.exe).LastWriteTime) { $copie = $true }
    else { Start-Sleep -Milliseconds 700 }
}
if (-not $copie) {
    if (Get-Process CentrePC -ErrorAction SilentlyContinue) {
        Write-Host "Centre PC est encore ouvert (peut-être en administrateur, pendant une mise à jour) : ferme-le puis relance Compiler.bat." -ForegroundColor Red
    } else { Write-Host "Impossible de remplacer CentrePC.exe (antivirus en cours d'analyse ?) : relance Compiler.bat dans quelques secondes." -ForegroundColor Red }
    Stop-Transcript | Out-Null; exit 1
}

$wsh = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $l = $wsh.CreateShortcut((Join-Path $dir 'Centre PC.lnk'))
    $l.TargetPath = "$dest\CentrePC.exe"
    $l.WorkingDirectory = $dest
    $l.Description = 'Applications, bureau et mises à jour'
    $l.Save()
}
Write-Host "Installé : $dest\CentrePC.exe (raccourcis sur le bureau et dans le menu Démarrer)" -ForegroundColor Green
Stop-Transcript | Out-Null
