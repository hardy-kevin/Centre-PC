# Signe un fichier du catalogue avec la clé « CentrePC-Catalogue » de ce PC (créée par cle.ps1).
# À lancer après chaque modification, puis publier (Publier.bat ou simple envoi sur GitHub).
#
#   .\signer.ps1                      catalogue.json : augmente le numéro de version, met la date du jour, signe
#   .\signer.ps1 -Garder              catalogue.json : signe sans changer le numéro de version
#   .\signer.ps1 -Fichier noms.gz     liste des prénoms et noms (produite par Outils\listes-noms.ps1) : signe tel quel
#   .\signer.ps1 -Nom CentrePC-Catalogue-Secours    signe avec la clé de secours (après cle.ps1 -Restaurer … -Nom …)
param([switch]$Garder, [string]$Nom = 'CentrePC-Catalogue', [string]$Fichier = 'catalogue.json')
$ErrorActionPreference = 'Stop'
$chemin = Join-Path $PSScriptRoot $Fichier
if (-not (Test-Path $chemin)) { throw "Fichier introuvable : $chemin" }
$Cng = [System.Security.Cryptography.CngKey]
if (-not $Cng::Exists($Nom)) { throw "Clé « $Nom » absente de ce PC : lancez d'abord cle.ps1 (ou cle.ps1 -Restaurer)." }

function Signer([byte[]]$octets) {
    $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($Cng::Open($Nom))
    $ecdsa.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
    $signature = $ecdsa.SignData($octets)
    if (-not $ecdsa.VerifyData($octets, $signature)) { throw "La signature produite ne se vérifie pas." }
    [IO.File]::WriteAllText("$chemin.sig", [Convert]::ToBase64String($signature))
}

if ($Fichier -like '*.gz') {
    # Liste des noms : vérifiée (en-tête, sections) avant de signer, jamais modifiée
    Add-Type -AssemblyName System.IO.Compression
    $octets = [IO.File]::ReadAllBytes($chemin)
    $z = New-Object IO.Compression.GZipStream((New-Object IO.MemoryStream(, $octets)), [IO.Compression.CompressionMode]::Decompress)
    $texte = (New-Object IO.StreamReader($z, [Text.Encoding]::UTF8)).ReadToEnd()
    if (-not $texte.StartsWith("CentrePC-noms 1`n") -or $texte -notmatch "`nversion=\d{8}`n" -or $texte -notmatch "`n\[prenoms\]`n" -or $texte -notmatch "`n\[noms\]`n") {
        throw "$Fichier n'a pas le format attendu : regénérez-le avec Outils\listes-noms.ps1."
    }
    Signer $octets
    Write-Host "$Fichier signé ($([regex]::Match($texte, 'version=(\d{8})').Groups[1].Value)). Publiez $Fichier et $Fichier.sig ensemble."
    exit 0
}

$texte = [IO.File]::ReadAllText($chemin)
# Le format est vérifié avant de signer : un catalogue illisible serait refusé par toutes les installations
$doc = $texte | ConvertFrom-Json
if ($null -eq $doc.Version -or $null -eq $doc.Classement) { throw "catalogue.json doit contenir « Version » et « Classement »." }
$connues = 'capteurs-hwinfo', 'capteurs-lhm', 'outils-expert', 'retours'
foreach ($d in @($doc.Desactive)) { if ($d -and $connues -notcontains $d) { throw "Fonction inconnue dans « Desactive » : $d (connues : $($connues -join ', '))." } }
# Sections facultatives : listes de textes simples (jamais d'expression régulière ni de code)
foreach ($section in 'Organismes', 'MotsCles') {
    if ($null -ne $doc.$section) {
        foreach ($p in $doc.$section.PSObject.Properties) {
            foreach ($v in @($p.Value)) { if ($v -isnot [string] -or $v.Length -gt 60) { throw "« $section » › « $($p.Name) » : textes de 60 caractères au plus." } }
        }
    }
}
foreach ($e in @($doc.Editeurs)) { if ($e -and ($e -isnot [string] -or $e.Length -gt 60)) { throw "« Editeurs » : textes de 60 caractères au plus." } }

if (-not $Garder) {
    $version = [int]$doc.Version + 1
    $texte = [regex]::Replace($texte, '("Version"\s*:\s*)\d+', "`${1}$version", 1)
    $texte = [regex]::Replace($texte, '("Date"\s*:\s*)"[^"]*"', "`${1}`"$(Get-Date -Format 'yyyy-MM-dd')`"", 1)
}
# Fins de ligne « LF » et UTF-8 sans marque : exactement les octets que GitHub servira (voir .gitattributes)
$texte = $texte -replace "`r`n", "`n"
$octets = (New-Object System.Text.UTF8Encoding($false)).GetBytes($texte)
[IO.File]::WriteAllBytes($chemin, $octets)
Signer $octets

$final = $texte | ConvertFrom-Json
Write-Host "Catalogue version $($final.Version) signé : $(@($final.Classement.PSObject.Properties).Count) application(s), $(@($final.Desactive).Count) fonction(s) coupée(s)."
Write-Host "Publiez catalogue.json et catalogue.json.sig ensemble (Publier.bat, ou git add / commit / push)."
