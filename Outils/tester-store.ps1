# Vérifie la version Microsoft Store de Centre PC sur ce PC, avant de la soumettre :
#   1. fabrique le paquet MSIX comme GitHub (même manifeste, mêmes images) ;
#   2. lance le kit de certification de Microsoft (WACK) dessus et ouvre son rapport ;
#   3. avec -Installer : installe la version Store sur ce PC pour l'essayer « en vrai ».
# Le paquet est signé avec un certificat de test créé sur ce PC (le kit de certification et l'installation l'exigent ;
# le Store, lui, signe le paquet envoyé avec son propre certificat). Le certificat est ajouté aux « Personnes autorisées »
# de ce PC. Pour le retirer plus tard :
#   Get-ChildItem Cert:\LocalMachine\TrustedPeople, Cert:\CurrentUser\My | Where-Object FriendlyName -eq 'Centre PC - test Store' | Remove-Item
#
# À lancer dans PowerShell ouvert en administrateur (le kit de certification l'exige), depuis le dossier du projet :
#   powershell -ExecutionPolicy Bypass -File Outils\tester-store.ps1 -Name "<Package/Identity/Name>" -Publisher "CN=…" -PublisherName "Kevin Hardy"
#   (les trois valeurs : Partner Center > Gestion des produits > Identité du produit, ou les variables du dépôt GitHub)
# Ajouter -Installer pour installer la version Store. Pour la retirer ensuite :
#   Get-AppxPackage *CentrePC* | Remove-AppxPackage
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$PublisherName,
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "Ouvrez PowerShell en administrateur (le kit de certification l'exige), puis relancez." -ForegroundColor Red; exit 1 }

if ($Name -like '<*' -or $Publisher -notlike 'CN=?*' -or $Publisher -like '*…*') {
    Write-Host "Remplacez -Name et -Publisher par les vraies valeurs (Partner Center > Gestion des produits > Identité du produit)." -ForegroundColor Red
    exit 1
}

# Kit de développement Windows : emplacement indiqué par Windows, sinon l'emplacement habituel
$kits = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue).KitsRoot10
if (-not $kits) { $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\" }
# Outils du SDK (bin\<version>\x64, bin\x64 ou kit de certification) : la version de fichier la plus récente,
# de préférence en x64 (les anciennes versions ne savent pas signer les paquets MSIX)
function Find-SdkTool([string]$exe) {
    @(Get-ChildItem "$kits\bin", "$kits\App Certification Kit" -Recurse -Filter $exe -ErrorAction SilentlyContinue) |
        Sort-Object @{ Expression = { $i = $_.VersionInfo; [version]('{0}.{1}.{2}.{3}' -f $i.FileMajorPart, $i.FileMinorPart, $i.FileBuildPart, $i.FilePrivatePart) }; Descending = $true },
                    @{ Expression = { if ($_.FullName -match '\\x64\\') { 0 } elseif ($_.FullName -match '\\arm64\\') { 1 } else { 2 } } } |
        Select-Object -First 1
}
$makeappx = Find-SdkTool 'makeappx.exe'
$appcert = Get-ChildItem "$kits\App Certification Kit\appcert.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $makeappx -or -not $appcert) {
    Write-Host "Dossier du kit : $kits" -ForegroundColor Yellow
    if (-not $makeappx) { Write-Host "  makeappx.exe introuvable (outils de création de paquets du SDK)." -ForegroundColor Red }
    if (-not $appcert) { Write-Host "  appcert.exe introuvable (composant « Windows App Certification Kit » du SDK)." -ForegroundColor Red }
    Write-Host "Installez le SDK Windows avec « Windows App Certification Kit » coché : https://go.microsoft.com/fwlink/?LinkId=619296" -ForegroundColor Red
    Write-Host "(si le SDK est déjà installé : Paramètres > Applications > Windows Software Development Kit > Modifier, et cochez le kit)" -ForegroundColor Red
    exit 1
}
$appcert = $appcert.FullName
$signtool = Find-SdkTool 'signtool.exe'
if (-not $signtool) { Write-Host "signtool.exe introuvable dans le kit ($kits) : outils de signature du SDK manquants." -ForegroundColor Red; exit 1 }

# Version : celle du projet (le Store exige un 4e chiffre à 0)
$proj = Get-Content CentrePC.csproj -Raw
if ($proj -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw "Version introuvable dans CentrePC.csproj." }
$v = $Matches[1]

Write-Host "Compilation de Centre PC $v..." -ForegroundColor Cyan
# Dossier de travail sans accent ni nom d'utilisateur : le kit de certification et certains outils du SDK perdent les
# accents des chemins (« Kévin » devient « Kevin ») et tous les tests échouent alors sans explication
$out = Join-Path $env:ProgramData 'CentrePC-test-store'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
dotnet publish CentrePC.csproj -c Release -o "$out\publish" -p:Version=$v --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }

Write-Host "Fabrication du paquet MSIX..." -ForegroundColor Cyan
$dir = "$out\msix"
New-Item -ItemType Directory -Path "$dir\Assets" -Force | Out-Null
Copy-Item "$out\publish\CentrePC.exe" $dir
Copy-Item Outils\store\Assets\* "$dir\Assets\"
$m = (Get-Content Outils\store\AppxManifest.xml -Raw -Encoding UTF8).Replace('{IDENTITY}', $Name).Replace('{PUBLISHER}', $Publisher).Replace('{PUBLISHER_NAME}', $PublisherName).Replace('{VERSION}', "$v.0")
Set-Content "$dir\AppxManifest.xml" $m -Encoding utf8
$msix = "$out\CentrePC-$v.msix"
& $makeappx.FullName pack /d $dir /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx a échoué." }

# Signature de test : certificat au nom de l'éditeur du Store (réutilisé d'une fois sur l'autre), approuvé sur ce PC
Write-Host "Signature de test..." -ForegroundColor Cyan
$friendly = 'Centre PC - test Store'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.FriendlyName -eq $friendly -and $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName $friendly `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
$cer = "$out\test-store.cer"
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint)) {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}
Write-Host "  Outils : $($makeappx.FullName) ($($makeappx.VersionInfo.FileVersion)) / $($signtool.FullName) ($($signtool.VersionInfo.FileVersion))" -ForegroundColor DarkGray
& $signtool.FullName sign /v /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE -ne 0) {
    Write-Host "Signature impossible : l'outil de signature est sans doute trop ancien pour les paquets MSIX." -ForegroundColor Red
    Write-Host "Installez le SDK Windows récent, puis relancez : winget install Microsoft.WindowsSDK.10.0.26100" -ForegroundColor Red
    exit 1
}
Write-Host "  $msix" -ForegroundColor Green

Write-Host "Kit de certification (plusieurs minutes ; Centre PC va s'ouvrir et se fermer tout seul)..." -ForegroundColor Cyan
$report = "$out\rapport-certification.xml"
Get-AppxPackage -Name $Name | Remove-AppxPackage -ErrorAction SilentlyContinue   # version de test précédente : le kit installe la sienne
& $appcert reset | Out-Null
& $appcert test -appxpackagepath $msix -reportoutputpath $report
if (Test-Path $report) {
    [xml]$x = Get-Content $report
    $result = $x.REPORT.OVERALL_RESULT
    $color = if ($result -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host "Résultat global : $result" -ForegroundColor $color
    # Tests en échec ou avec avertissement, en clair
    foreach ($t in $x.SelectNodes('//TEST')) {
        $r = $t.RESULT.InnerText
        if (-not $r) { $r = $t.RESULT }
        if ($r -eq 'PASS') { continue }
        Write-Host ("  [{0}] {1}" -f $r, $t.NAME) -ForegroundColor Yellow
        foreach ($msg in $t.SelectNodes('.//MESSAGE')) { Write-Host "      $($msg.TEXT)$($msg.InnerText)" }
    }
    Write-Host "Rapport complet : $report"
    Start-Process $report
} else { Write-Host "Le kit n'a pas produit de rapport." -ForegroundColor Red }

if ($Installer) {
    Write-Host "Installation de la version Store sur ce PC..." -ForegroundColor Cyan
    Get-AppxPackage -Name $Name | Remove-AppxPackage -ErrorAction SilentlyContinue   # version de test précédente
    Add-AppxPackage -Path $msix
    Write-Host "Installée : menu Démarrer > Centre PC (version Store, réglages dans Documents\Centre PC)." -ForegroundColor Green
    Write-Host "Pour la retirer : Get-AppxPackage *CentrePC* | Remove-AppxPackage"
}
