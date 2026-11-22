# Signe l'empreinte d'une version publiée de Centre PC avec la clé de l'auteur (la même que le catalogue).
# Lancé automatiquement à la fin de Publier.bat ; à relancer à la main si la fenêtre a été fermée trop tôt :
#
#   .\Outils\signer-version.ps1 -Version 1.2.11
#
# 1. attend que GitHub ait publié CentrePC.exe.sha256 pour cette version (environ 5 minutes après Publier.bat) ;
# 2. signe « CentrePC <version> <empreinte> » avec la clé CentrePC-Catalogue du magasin de clés de Windows ;
# 3. dépose la signature dans Catalogue\versions\<version>.sig et l'envoie sur GitHub.
#
# Centre PC (édition GitHub) n'installe une mise à jour que si cette signature est valide : même avec le compte GitHub
# piraté, une version fabriquée par quelqu'un d'autre ne serait jamais installée. La clé privée ne quitte jamais ce PC.
param([Parameter(Mandatory)][string]$Version, [string]$Nom = 'CentrePC-Catalogue', [int]$Minutes = 25)
$ErrorActionPreference = 'Stop'
$racine = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Numéro de version attendu : 1.2.11" }
$Cng = [System.Security.Cryptography.CngKey]
if (-not $Cng::Exists($Nom)) { throw "Clé « $Nom » absente de ce PC : lancez d'abord Catalogue\cle.ps1 -Restaurer <copie de secours>." }

# 1. Empreinte publiée par GitHub (fichier public, lu sans compte)
$url = "https://github.com/hardy-kevin/Centre-PC/releases/download/v$Version/CentrePC.exe.sha256"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$fin = (Get-Date).AddMinutes($Minutes)
$sha = $null
Write-Host "Attente de la version $Version sur GitHub (fabrication en cours, environ 5 minutes)..." -ForegroundColor Cyan
while (-not $sha) {
    try {
        $texte = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20).Content
        if ($texte -is [byte[]]) { $texte = [Text.Encoding]::ASCII.GetString($texte) }
        $m = [regex]::Match("$texte", '\b[0-9a-fA-F]{64}\b')
        if ($m.Success) { $sha = $m.Value.ToLowerInvariant() }
    } catch { }
    if (-not $sha) {
        if ((Get-Date) -gt $fin) { throw "La version $Version n'est pas encore publiée. Relancez plus tard : .\Outils\signer-version.ps1 -Version $Version" }
        Start-Sleep -Seconds 30
    }
}
Write-Host "Empreinte de CentrePC.exe $Version : $sha"

# 2. Signature (même format que le catalogue : ECDSA P-256, SHA-256, signature brute en base64)
$message = [Text.Encoding]::UTF8.GetBytes("CentrePC $Version $sha")
$ecdsa = New-Object System.Security.Cryptography.ECDsaCng($Cng::Open($Nom))
$ecdsa.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
$signature = $ecdsa.SignData($message)
if (-not $ecdsa.VerifyData($message, $signature)) { throw "La signature produite ne se vérifie pas." }

# 3. Dépôt dans le dépôt Git
$dossier = Join-Path $racine 'Catalogue\versions'
New-Item -ItemType Directory -Path $dossier -Force | Out-Null
$fichier = Join-Path $dossier "$Version.sig"
[IO.File]::WriteAllText($fichier, [Convert]::ToBase64String($signature), (New-Object Text.UTF8Encoding($false)))
Push-Location $racine
try {
    git add -- "Catalogue/versions/$Version.sig"
    git commit -m "Signature de la version $Version" -- "Catalogue/versions/$Version.sig" | Out-Null
    git push origin HEAD
    if ($LASTEXITCODE -ne 0) { throw "L'envoi de la signature a échoué : relancez git push." }
}
finally { Pop-Location }
Write-Host "Version $Version signée : les installations la proposeront dans quelques minutes." -ForegroundColor Green
