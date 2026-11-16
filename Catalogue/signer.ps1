# Signe catalogue.json avec la clé « CentrePC-Catalogue » de ce PC (créée par cle.ps1).
# À lancer après chaque modification du catalogue, puis publier (Publier.bat ou simple envoi sur GitHub).
#
#   .\signer.ps1          augmente le numéro de version, met la date du jour, signe
#   .\signer.ps1 -Garder  signe sans changer le numéro de version
#   .\signer.ps1 -Nom CentrePC-Catalogue-Secours    signe avec la clé de secours (après cle.ps1 -Restaurer … -Nom …)
param([switch]$Garder, [string]$Nom = 'CentrePC-Catalogue')
$ErrorActionPreference = 'Stop'
$fichier = Join-Path $PSScriptRoot 'catalogue.json'
$Cng = [System.Security.Cryptography.CngKey]
if (-not $Cng::Exists($Nom)) { throw "Clé « $Nom » absente de ce PC : lancez d'abord cle.ps1 (ou cle.ps1 -Restaurer)." }

$texte = [IO.File]::ReadAllText($fichier)
# Le format est vérifié avant de signer : un catalogue illisible serait refusé par toutes les installations
$doc = $texte | ConvertFrom-Json
if ($null -eq $doc.Version -or $null -eq $doc.Classement) { throw "catalogue.json doit contenir « Version » et « Classement »." }
$connues = 'capteurs-hwinfo', 'capteurs-lhm', 'outils-expert', 'retours'
foreach ($d in @($doc.Desactive)) { if ($d -and $connues -notcontains $d) { throw "Fonction inconnue dans « Desactive » : $d (connues : $($connues -join ', '))." } }

if (-not $Garder) {
    $version = [int]$doc.Version + 1
    $texte = [regex]::Replace($texte, '("Version"\s*:\s*)\d+', "`${1}$version", 1)
    $texte = [regex]::Replace($texte, '("Date"\s*:\s*)"[^"]*"', "`${1}`"$(Get-Date -Format 'yyyy-MM-dd')`"", 1)
}
# Fins de ligne « LF » et UTF-8 sans marque : exactement les octets que GitHub servira (voir .gitattributes)
$texte = $texte -replace "`r`n", "`n"
$octets = (New-Object System.Text.UTF8Encoding($false)).GetBytes($texte)
[IO.File]::WriteAllBytes($fichier, $octets)

$ecdsa = New-Object System.Security.Cryptography.ECDsaCng($Cng::Open($Nom))
$ecdsa.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
$signature = $ecdsa.SignData($octets)
if (-not $ecdsa.VerifyData($octets, $signature)) { throw "La signature produite ne se vérifie pas." }
[IO.File]::WriteAllText("$fichier.sig", [Convert]::ToBase64String($signature))

$final = $texte | ConvertFrom-Json
Write-Host "Catalogue version $($final.Version) signé : $(@($final.Classement.PSObject.Properties).Count) application(s), $(@($final.Desactive).Count) fonction(s) coupée(s)."
Write-Host "Publiez catalogue.json et catalogue.json.sig ensemble (Publier.bat, ou git add / commit / push)."
