# Clé de signature du catalogue de Centre PC.
#
#   .\cle.ps1                        crée la clé (si elle n'existe pas) et affiche la clé publique
#   .\cle.ps1 -Sauvegarder fichier   copie de secours de la clé privée (à ranger hors du PC, jamais dans le dépôt)
#   .\cle.ps1 -Restaurer fichier     remet la clé sur un PC neuf
#   .\cle.ps1 -Secours fichier       crée une SECONDE clé, de secours : sa partie privée va uniquement dans le fichier
#                                    indiqué (clé USB), elle n'est pas gardée sur ce PC. Sa clé publique est inscrite
#                                    dans Centre PC à côté de la première : si la clé principale est perdue, la clé de
#                                    secours permet de continuer à signer sans attendre une nouvelle version.
#   .\cle.ps1 -Restaurer fichier -Nom CentrePC-Catalogue-Secours    remet la clé de secours en service
#
# La clé privée reste dans le magasin de clés de Windows de votre compte ; seule la clé publique est inscrite dans Centre PC.
param([string]$Sauvegarder, [string]$Restaurer, [string]$Secours, [string]$Nom = 'CentrePC-Catalogue')
$ErrorActionPreference = 'Stop'
$nom = $Nom
$Cng = [System.Security.Cryptography.CngKey]

if ($Secours) {
    # Clé de secours : créée, copiée dans le fichier, puis retirée de ce PC (elle ne doit exister que hors ligne)
    if (Test-Path $Secours) { throw "Le fichier $Secours existe déjà : il n'est pas remplacé." }
    $nomSecours = 'CentrePC-Catalogue-Secours'
    if ($Cng::Exists($nomSecours)) { throw "Une clé de secours est déjà en service sur ce PC." }
    $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $k = $Cng::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $nomSecours, $p)
    $prive = $k.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    $pub = $k.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    [IO.File]::WriteAllText($Secours, [Convert]::ToBase64String($prive))
    # Relecture du fichier avant de retirer la clé du PC
    if ([IO.File]::ReadAllText($Secours).Trim() -ne [Convert]::ToBase64String($prive)) { throw "La copie n'a pas pu être relue : la clé de secours reste sur ce PC, recommencez." }
    $k.Delete()
    $texteSecours = [Convert]::ToBase64String($pub[8..71])
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'cle-publique-secours.txt'), $texteSecours)
    Write-Host "Clé de secours écrite dans $Secours et retirée de ce PC."
    Write-Host "Rangez ce fichier hors du PC, à un autre endroit que la copie de la clé principale. Ne le publiez jamais."
    Write-Host ""
    Write-Host "Clé publique de secours (à inscrire aussi dans Core\RemoteCatalog.cs, PublicKeys) :"
    Write-Host $texteSecours
    return
}
if ($Restaurer) {
    if ($Cng::Exists($nom)) { throw "Une clé « $nom » existe déjà sur ce PC : elle n'est pas remplacée." }
    $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $blob = [Convert]::FromBase64String(([IO.File]::ReadAllText($Restaurer)).Trim())
    $p.Parameters.Add((New-Object System.Security.Cryptography.CngProperty('ECCPRIVATEBLOB', $blob, [System.Security.Cryptography.CngPropertyOptions]::None)))
    $null = $Cng::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $nom, $p)
    Write-Host "Clé restaurée."
}
elseif (-not $Cng::Exists($nom)) {
    $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $null = $Cng::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $nom, $p)
    Write-Host "Clé créée dans le magasin de clés de Windows (« $nom »)."
}

$cle = $Cng::Open($nom)
if ($Sauvegarder) {
    $prive = $cle.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    [IO.File]::WriteAllText($Sauvegarder, [Convert]::ToBase64String($prive))
    Write-Host "Copie de secours écrite dans $Sauvegarder : rangez-la hors du PC (clé USB, coffre de mots de passe) et ne la publiez jamais."
}
# Clé publique : 8 octets d'en-tête, puis X et Y (32 octets chacun)
$public = $cle.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
$texte = [Convert]::ToBase64String($public[8..71])
if ($nom -eq 'CentrePC-Catalogue') { [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'cle-publique.txt'), $texte) }
Write-Host ""
Write-Host "Clé publique (à inscrire dans Core\RemoteCatalog.cs, PublicKeys) :"
Write-Host $texte
Write-Host ""
if ($nom -eq 'CentrePC-Catalogue') { Write-Host "Elle est aussi enregistrée dans cle-publique.txt (publique : peut être dans le dépôt)." }
