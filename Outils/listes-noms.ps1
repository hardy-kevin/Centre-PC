# Fabrique la liste des prénoms et des noms de famille qui protège les retours d'expérience (voir Core\PersonNames.cs).
#
#   .\Outils\listes-noms.ps1            télécharge les fichiers de l'Insee et regénère les listes
#   .\Outils\listes-noms.ps1 -Dossier C:\insee     utilise des fichiers déjà téléchargés (zip ou csv/txt) dans ce dossier
#
# Sources (données ouvertes de l'Insee, licence ouverte Etalab) :
#   - Fichier des prénoms : « Liste des prénoms par sexe depuis 1900 » (prénoms donnés au moins 3 fois), publiée chaque
#     été. La page change chaque année : le script cherche la plus récente, sinon prend $PrenomsUrl.
#   - Fichier des noms (noms de famille donnés au moins 30 fois de 1891 à 2000) : fichier national, définitif.
#   - Outils\editeurs-connus.txt : marques et éditeurs à ne pas confondre avec une personne.
#
# Produit (seulement si le contenu a changé : pas de nouvelle version pour rien) :
#   - Catalogue\noms.gz          embarqué dans Centre PC, et publié pour la mise à jour mensuelle (à signer :
#                                 .\Catalogue\signer.ps1 -Fichier noms.gz)
#   - Serveur\noms-donnees.js    même contenu pour le service des retours (redéployer : wrangler deploy)
#
# Format de noms.gz (texte UTF-8 compressé) : une ligne d'en-tête « CentrePC-noms 1 », « version=AAAAMMJJ », puis les
# sections [prenoms], [noms], [editeurs] : un mot par ligne, en minuscules, sans accents (voir Fold plus bas).
param(
    [string]$Dossier,
    [string]$PrenomsUrl = 'https://www.insee.fr/fr/statistiques/fichier/8595130/prenoms-2025-liste_csv.zip',
    [string]$NomsUrl = 'https://www.insee.fr/fr/statistiques/fichier/3536630/noms2008nat_txt.zip'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$racine = Split-Path $PSScriptRoot -Parent
$travail = Join-Path ([IO.Path]::GetTempPath()) "centrepc-noms-$PID"
New-Item -ItemType Directory -Force $travail | Out-Null

# ------------------------------------------------------------------ règles communes (identiques dans l'appli et le serveur)
# Particules et mots trop courts : jamais dans les listes, et ignorés entre un prénom et un nom (« Jean de La Fontaine »)
$Particules = @('de', 'du', 'des', 'la', 'le', 'les', 'van', 'von', 'der', 'den', 'del', 'della', 'dos', 'das', 'da', 'di', 'el', 'al', 'ben', 'bin', 'mac', 'mc', 'saint', 'sainte', 'st', 'ste')

function Fold([string]$s) {
    # Minuscules sans accents : « Éléonore » → « eleonore » ; « Œ » et « Æ » développés
    $s = $s.Replace('Œ', 'OE').Replace('œ', 'oe').Replace('Æ', 'AE').Replace('æ', 'ae').Replace('ß', 'ss')
    $d = $s.Normalize([Text.NormalizationForm]::FormD)
    $sb = New-Object Text.StringBuilder
    foreach ($c in $d.ToCharArray()) {
        if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($c) -ne [Globalization.UnicodeCategory]::NonSpacingMark) { [void]$sb.Append($c) }
    }
    return $sb.ToString().ToLowerInvariant()
}

function Mots([string]$s) {
    # Mots d'au moins 3 lettres, particules retirées (« JEAN-PIERRE » → jean, pierre ; « DE LA FONTAINE » → fontaine)
    foreach ($m in [regex]::Matches((Fold $s), '[a-z]+')) {
        if ($m.Value.Length -ge 3 -and $Particules -notcontains $m.Value) { $m.Value }
    }
}

# ------------------------------------------------------------------ téléchargement
function Telecharger([string]$url) {
    $dest = Join-Path $travail ([IO.Path]::GetFileName(([Uri]$url).AbsolutePath))
    Write-Host "Téléchargement : $url"
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -UserAgent 'CentrePC-listes-noms'
    return $dest
}

function Extraire([string]$fichier) {
    if ($fichier -notmatch '\.zip$') { return $fichier }
    $dir = Join-Path $travail ([IO.Path]::GetFileNameWithoutExtension($fichier))
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    [IO.Compression.ZipFile]::ExtractToDirectory($fichier, $dir)
    $f = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Extension -match '\.(csv|txt)$' } | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $f) { throw "Aucun fichier csv ou txt dans $fichier" }
    return $f.FullName
}

function Lire([string]$fichier) {
    # UTF-8 si le fichier en est (fichiers récents), sinon l'encodage Windows habituel (fichiers anciens de l'Insee)
    $octets = [IO.File]::ReadAllBytes($fichier)
    try { return (New-Object Text.UTF8Encoding($false, $true)).GetString($octets) }
    catch { return [Text.Encoding]::GetEncoding(1252).GetString($octets) }
}

function TrouverPrenomsRecents {
    # La liste change de page chaque année : on suit les liens de la page de la source « Fichier des prénoms »
    try {
        $source = (Invoke-WebRequest -Uri 'https://www.insee.fr/fr/metadonnees/source/serie/s2236' -UseBasicParsing).Content
        $pages = [regex]::Matches($source, '/fr/statistiques/(\d{6,8})') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique -First 12
        $meilleur = $null; $annee = 0
        foreach ($p in $pages) {
            try {
                $html = (Invoke-WebRequest -Uri "https://www.insee.fr/fr/statistiques/$p" -UseBasicParsing).Content
                foreach ($m in [regex]::Matches($html, '/fr/statistiques/fichier/\d+/prenoms-(\d{4})-liste_csv\.zip')) {
                    if ([int]$m.Groups[1].Value -gt $annee) { $annee = [int]$m.Groups[1].Value; $meilleur = "https://www.insee.fr$($m.Value)" }
                }
            } catch { }
        }
        return $meilleur
    } catch { return $null }
}

function Source([string]$motif, [string]$url, [switch]$Prenoms) {
    if ($Dossier) {
        $f = Get-ChildItem $Dossier -File | Where-Object { $_.Name -match $motif } | Select-Object -First 1
        if (-not $f) { throw "Aucun fichier « $motif » dans $Dossier" }
        return Extraire $f.FullName
    }
    if ($Prenoms) {
        $recent = TrouverPrenomsRecents
        if ($recent -and $recent -ne $url) { Write-Host "Liste des prénoms plus récente trouvée : $recent"; $url = $recent }
    }
    return Extraire (Telecharger $url)
}

# ------------------------------------------------------------------ lecture des fichiers de l'Insee
function Colonne([string[]]$lignes, [string]$motif) {
    $sep = if ($lignes[0].Contains(';')) { ';' } elseif ($lignes[0].Contains("`t")) { "`t" } else { ',' }
    $entete = $lignes[0].Split($sep) | ForEach-Object { $_.Trim('"', ' ') }
    $i = [Array]::FindIndex([string[]]$entete, [Predicate[string]] { param($h) $h -match $motif })
    if ($i -lt 0) { $i = 0 }
    return @{ Sep = $sep; Index = $i }
}

$prenoms = New-Object 'Collections.Generic.HashSet[string]'
$fichierPrenoms = Source 'prenoms.*\.(zip|csv)$' $PrenomsUrl -Prenoms
$lignes = (Lire $fichierPrenoms) -split "`r?`n" | Where-Object { $_ }
$c = Colonne $lignes '(?i)^pr[eé]nom'
foreach ($l in $lignes | Select-Object -Skip 1) {
    $v = $l.Split($c.Sep)[$c.Index].Trim('"', ' ')
    if ($v -match '^_' -or $v -match '(?i)rares') { continue }
    foreach ($m in Mots $v) { [void]$prenoms.Add($m) }
}
Write-Host "Prénoms : $($prenoms.Count) mots ($([IO.Path]::GetFileName($fichierPrenoms)))"

$noms = New-Object 'Collections.Generic.HashSet[string]'
$fichierNoms = Source 'noms.*\.(zip|txt|csv)$' $NomsUrl
$lignes = (Lire $fichierNoms) -split "`r?`n" | Where-Object { $_ }
$c = Colonne $lignes '(?i)^nom'
foreach ($l in $lignes | Select-Object -Skip 1) {
    $v = $l.Split($c.Sep)[$c.Index].Trim('"', ' ')
    if ($v -match '(?i)autres noms') { continue }
    foreach ($m in Mots $v) { [void]$noms.Add($m) }
}
Write-Host "Noms de famille : $($noms.Count) mots ($([IO.Path]::GetFileName($fichierNoms)))"
if ($prenoms.Count -lt 5000 -or $noms.Count -lt 50000) { throw "Listes anormalement courtes : format des fichiers de l'Insee changé ? Rien n'est écrit." }

$editeurs = New-Object 'Collections.Generic.HashSet[string]'
foreach ($l in Get-Content (Join-Path $PSScriptRoot 'editeurs-connus.txt') -Encoding UTF8) {
    $t = $l.Trim()
    if ($t -and -not $t.StartsWith('#')) { [void]$editeurs.Add((Fold $t)) }
}

# ------------------------------------------------------------------ écriture (seulement si le contenu change)
function Trie($set) { $a = [string[]]@($set); [Array]::Sort($a, [StringComparer]::Ordinal); return $a }
$corps = "[prenoms]`n" + ((Trie $prenoms) -join "`n") + "`n[noms]`n" + ((Trie $noms) -join "`n") + "`n[editeurs]`n" + ((Trie $editeurs) -join "`n") + "`n"

$gz = Join-Path $racine 'Catalogue\noms.gz'
function Decompresser([string]$f) {
    $in = [IO.File]::OpenRead($f)
    try { $z = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress); return (New-Object IO.StreamReader($z, [Text.Encoding]::UTF8)).ReadToEnd() }
    finally { $in.Dispose() }
}
if (Test-Path $gz) {
    $ancien = Decompresser $gz
    $i = $ancien.IndexOf('[prenoms]')
    if ($i -ge 0 -and $ancien.Substring($i) -ceq $corps) { Write-Host 'Listes inchangées : rien à écrire.'; Remove-Item $travail -Recurse -Force; exit 0 }
}

$texte = "CentrePC-noms 1`nversion=$(Get-Date -Format 'yyyyMMdd')`nsources=$([IO.Path]::GetFileName($fichierPrenoms)) $([IO.Path]::GetFileName($fichierNoms))`n" + $corps
$octets = (New-Object Text.UTF8Encoding($false)).GetBytes($texte)
$ms = New-Object IO.MemoryStream
$z = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionLevel]::Optimal)
$z.Write($octets, 0, $octets.Length); $z.Dispose()
$compresse = $ms.ToArray()
[IO.File]::WriteAllBytes($gz, $compresse)
# La signature précédente ne correspond plus : supprimée pour ne pas publier un couple incohérent
if (Test-Path "$gz.sig") { Remove-Item "$gz.sig" }

$js = "// Fichier produit par Outils\listes-noms.ps1 : ne pas modifier à la main.`n// Même contenu que Catalogue\noms.gz (prénoms et noms de l'Insee, éditeurs connus), compressé puis encodé en base64.`nexport default `"" + [Convert]::ToBase64String($compresse) + "`";`n"
[IO.File]::WriteAllText((Join-Path $racine 'Serveur\noms-donnees.js'), $js, (New-Object Text.UTF8Encoding($false)))

Remove-Item $travail -Recurse -Force
Write-Host "Écrit : Catalogue\noms.gz ($([math]::Round($compresse.Length / 1KB)) Ko) et Serveur\noms-donnees.js."
Write-Host 'Ensuite : .\Catalogue\signer.ps1 -Fichier noms.gz, puis redéployer le service des retours (cd Serveur ; wrangler deploy).'
