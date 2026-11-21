# Transforme les retours d'expérience agrégés (vues de la base D1, au moins 3 installations) en propositions pour le
# catalogue signé. Lancé chaque semaine par GitHub (.github\workflows\propositions.yml), ou à la main :
#
#   .\Outils\propositions.ps1 -Dossier C:\retours      (fichiers JSON exportés par « wrangler d1 execute … --json »)
#
# Fichiers attendus dans le dossier (un par vue de Serveur\schema.sql) :
#   classements.json      vue classements_proposes  (application, categorie, installations)
#   motscles.json         vue mots_cles_proposes    (categorie, mot, installations)
#   nonreconnues.json     vue non_reconnues         (application, editeur, installations)
#
# Effet :
#   - Catalogue\catalogue.json : ajoute les classements et mots-clés proposés qui n'y sont pas encore (jamais de
#     remplacement d'une valeur déjà choisie). Rien n'est signé : relisez la demande de fusion, puis
#     .\Catalogue\signer.ps1 après la fusion.
#   - Catalogue\propositions.md : rapport lisible (ce qui a été ajouté, désaccords, logiciels à classer à la main).
#     Le dépôt est public : seules des données agrégées et sans rien de personnel y figurent (noms de logiciels,
#     catégories d'origine, mots-clés désignant un logiciel), jamais les erreurs ni les dossiers.
param([Parameter(Mandatory)][string]$Dossier, [int]$Minimum = 3)
$ErrorActionPreference = 'Stop'
$racine = Split-Path $PSScriptRoot -Parent
$catalogue = Join-Path $racine 'Catalogue\catalogue.json'

function Vue([string]$nom) {
    $f = Join-Path $Dossier $nom
    if (-not (Test-Path $f)) { return @() }
    $j = Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json
    # Sortie de wrangler : [ { results: [...] } ]
    $lignes = @()
    foreach ($bloc in @($j)) { if ($null -ne $bloc.results) { $lignes += @($bloc.results) } else { $lignes += $bloc } }
    return $lignes | Where-Object { $_ -and [int]$_.installations -ge $Minimum }
}

$doc = Get-Content $catalogue -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($s in 'Classement', 'MotsCles') { if ($null -eq $doc.$s) { $doc | Add-Member -NotePropertyName $s -NotePropertyValue ([pscustomobject]@{}) } }
$rapport = New-Object Collections.Generic.List[string]
$ajouts = 0
$invalide = { param($c) -not $c -or $c -eq 'personnalisé' -or $c.Length -gt 60 }

# ------------------------------------------------------------------ classements
$rapport.Add('## Classements proposés'); $rapport.Add('')
foreach ($g in (Vue 'classements.json') | Group-Object application) {
    $choix = @($g.Group | Sort-Object { [int]$_.installations } -Descending)
    $haut = $choix[0]
    if ((& $invalide $haut.categorie) -or -not $g.Name -or $g.Name.Length -gt 80) { continue }
    if ($choix.Count -gt 1 -and [int]$choix[1].installations -ge [int]$haut.installations) {
        $rapport.Add("- Désaccord pour **$($g.Name)** : " + (($choix | ForEach-Object { "$($_.categorie) ($($_.installations))" }) -join ', ') + ' : rien ajouté.')
        continue
    }
    $actuel = $doc.Classement.PSObject.Properties[$g.Name]
    if ($actuel) {
        if ($actuel.Value -ne $haut.categorie) { $rapport.Add("- **$($g.Name)** : le catalogue dit « $($actuel.Value) », $($haut.installations) installations disent « $($haut.categorie) » (à trancher à la main).") }
        continue
    }
    $doc.Classement | Add-Member -NotePropertyName $g.Name -NotePropertyValue $haut.categorie
    $rapport.Add("- Ajouté : **$($g.Name)** → $($haut.categorie) ($($haut.installations) installations)")
    $ajouts++
}

# ------------------------------------------------------------------ mots-clés
$rapport.Add(''); $rapport.Add('## Mots-clés proposés'); $rapport.Add('')
foreach ($m in Vue 'motscles.json') {
    if ((& $invalide $m.categorie) -or -not $m.mot -or $m.mot.Length -gt 60) { continue }
    $liste = $doc.MotsCles.PSObject.Properties[$m.categorie]
    if (-not $liste) { $doc.MotsCles | Add-Member -NotePropertyName $m.categorie -NotePropertyValue @(); $liste = $doc.MotsCles.PSObject.Properties[$m.categorie] }
    if (@($liste.Value) -contains $m.mot) { continue }
    $liste.Value = @($liste.Value) + $m.mot
    $rapport.Add("- Ajouté : « $($m.mot) » → $($m.categorie) ($($m.installations) installations)")
    $ajouts++
}

# ------------------------------------------------------------------ logiciels sans catégorie (à classer à la main)
$rapport.Add(''); $rapport.Add('## Logiciels restés sans catégorie'); $rapport.Add('')
foreach ($n in (Vue 'nonreconnues.json') | Sort-Object { [int]$_.installations } -Descending | Select-Object -First 100) {
    if ($doc.Classement.PSObject.Properties[$n.application]) { continue }
    $rapport.Add("- $($n.application)" + $(if ($n.editeur) { " ($($n.editeur))" } else { '' }) + " : $($n.installations) installations")
}

$entete = "# Propositions du $(Get-Date -Format 'dd/MM/yyyy')`n`nDonnées agrégées des retours d'expérience (au moins $Minimum installations). " +
          "$ajouts ajout(s) dans catalogue.json, non signé : relisez, fusionnez, puis lancez ``.\Catalogue\signer.ps1``.`n"
# Le rapport n'est réécrit que si son contenu change (la date seule ne doit pas ouvrir une demande de fusion chaque semaine).
$fichier = Join-Path $racine 'Catalogue\propositions.md'
$corps = ($rapport -join "`n") + "`n"
$ancien = if (Test-Path $fichier) { (Get-Content $fichier -Raw -Encoding UTF8) -replace "`r`n", "`n" } else { '' }
if ($ajouts -gt 0 -or -not $ancien.EndsWith("`n" + $corps)) {
    [IO.File]::WriteAllText($fichier, $entete + "`n" + $corps, (New-Object Text.UTF8Encoding($false)))
}
if ($ajouts -gt 0) {
    $json = $doc | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($catalogue, ($json -replace "`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
}
Write-Host "$ajouts ajout(s) proposé(s). Rapport : Catalogue\propositions.md"
