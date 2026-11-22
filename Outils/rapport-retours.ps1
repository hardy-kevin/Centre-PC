# Rapport des retours d'expérience, pour corriger les bugs et améliorer les propositions.
# Lit la base Cloudflare D1 avec la connexion Wrangler de ce PC et écrit un rapport LOCAL :
#   Retours\rapport.md (le dernier) et Retours\rapport-AAAA-MM-JJ.md (historique)
# Le dossier Retours\ est ignoré par git : les messages d'erreur ne doivent jamais arriver sur le dépôt public
# (ni dans les journaux des workflows GitHub, publics eux aussi).
#
#   .\Outils\rapport-retours.ps1              30 derniers jours
#   .\Outils\rapport-retours.ps1 -Jours 90
param([int]$Jours = 30)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8   # sortie de Wrangler en UTF-8 (accents)
$racine = Split-Path $PSScriptRoot -Parent
$depuis = (Get-Date).AddDays(-$Jours).ToString('yyyy-MM-dd')
$sortie = Join-Path $racine 'Retours'
New-Item -ItemType Directory $sortie -Force | Out-Null

function Q([string]$sql) {
    # Pas de % ni de guillemets doubles dans les requêtes : elles passent par cmd (npx.cmd)
    Push-Location (Join-Path $racine 'Serveur')
    # Avertissements de npm sur la sortie d'erreur : ignorés (Windows PowerShell en ferait des erreurs bloquantes)
    $ErrorActionPreference = 'Continue'
    try { $json = (& npx --yes wrangler d1 execute centre-pc-retours --remote --json --command $sql 2>$null) -join "`n" }
    finally { Pop-Location; $ErrorActionPreference = 'Stop' }
    if ($LASTEXITCODE -ne 0 -or -not $json) { throw "Lecture de la base impossible (wrangler login ?) : $sql" }
    $r = $json | ConvertFrom-Json
    return @($r | ForEach-Object { $_.results } | Where-Object { $_ })
}
function J([string]$champ) { "json_extract(donnees, '$.$champ')" }
function Cell($v) { if ($null -eq $v -or "$v" -eq '') { return '·' }; return ("$v" -replace '\r?\n', ' ' -replace '\|', '¦').Trim() }
$md = New-Object Collections.Generic.List[string]
function Table([string[]]$entetes, $lignes, [scriptblock]$valeurs) {
    $lignes = @($lignes | Where-Object { $null -ne $_ })   # une requête sans résultat arrive comme $null
    if ($lignes.Count -eq 0) { $md.Add('*Rien sur la période.*'); $md.Add(''); return }
    $md.Add('| ' + ($entetes -join ' | ') + ' |')
    $md.Add('|' + (($entetes | ForEach-Object { '---' }) -join '|') + '|')
    foreach ($l in $lignes) { $md.Add('| ' + ((& $valeurs $l | ForEach-Object { Cell $_ }) -join ' | ') + ' |') }
    $md.Add('')
}
$periode = "recu >= '$depuis'"

Write-Host "Lecture des retours depuis le $depuis…"
$md.Add("# Retours d'expérience : rapport du $(Get-Date -Format 'dd/MM/yyyy HH:mm')")
$md.Add('')
$md.Add("Période : depuis le $depuis ($Jours jours). « Inst. » = installations différentes (identifiant renouvelé chaque mois).")
$md.Add('**Document local, jamais publié** : il contient des messages d''erreur.')
$md.Add('')

# ------------------------------------------------------------------ résumé
$md.Add('## Résumé'); $md.Add('')
Table @('Type', 'Retours', 'Inst.', 'Versions') (Q "SELECT type, COUNT(*) AS n, COUNT(DISTINCT installation) AS inst, group_concat(DISTINCT version) AS versions FROM retours WHERE $periode GROUP BY type ORDER BY inst DESC") { param($l) $l.type, $l.n, $l.inst, $l.versions }

# ------------------------------------------------------------------ erreurs
$md.Add('## Erreurs'); $md.Add('')
$md.Add('Classées par nombre d''installations touchées. « Pile » : fonctions de Centre PC traversées, la plus profonde d''abord.'); $md.Add('')
$err = Q "SELECT $(J 'etape') AS etape, $(J 'type') AS genre, $(J 'message') AS message, $(J 'pile') AS pile, group_concat(DISTINCT version) AS versions, group_concat(DISTINCT windows) AS windows, COUNT(*) AS n, COUNT(DISTINCT installation) AS inst, MIN(jour) AS premier, MAX(jour) AS dernier FROM retours WHERE type = 'erreur' AND $periode GROUP BY 1, 2, 3, 4 ORDER BY inst DESC, n DESC LIMIT 60"
$i = 0
foreach ($e in $err) {
    $i++
    $md.Add("### $i. $(Cell $e.etape) · $(Cell $e.genre)")
    $md.Add('')
    $md.Add("- **Touche** : $($e.inst) installation(s), $($e.n) fois, du $($e.premier) au $($e.dernier)")
    $md.Add("- **Versions** : $(Cell $e.versions) · **Windows** : $(Cell $e.windows)")
    $md.Add("- **Message** : $(Cell $e.message)")
    $md.Add("- **Pile** : ``$(Cell $e.pile)``")
    $md.Add('')
}
if ($i -eq 0) { $md.Add('*Aucune erreur sur la période.*'); $md.Add('') }
$md.Add('### Erreurs par version'); $md.Add('')
Table @('Version', 'Erreurs', 'Inst. touchées', 'Inst. qui envoient des retours') (Q "SELECT version, SUM(type = 'erreur') AS n, COUNT(DISTINCT CASE WHEN type = 'erreur' THEN installation END) AS inst, COUNT(DISTINCT installation) AS total FROM retours WHERE $periode GROUP BY version ORDER BY version DESC") { param($l) $l.version, $l.n, $l.inst, $l.total }

# ------------------------------------------------------------------ noms de documents proposés
$md.Add('## Noms de documents proposés'); $md.Add('')
$md.Add('Un type souvent « modifié » ou dont le type final diffère : la reconnaissance se trompe. Organisme ou date souvent retirés : détection trop large.'); $md.Add('')
Table @('Type proposé', 'Retours', 'Utilisé', 'Modifié', 'Ignoré', 'Type final différent', 'Organisme retiré', 'Date retirée', 'Rangé ensuite') (Q "SELECT $(J 'type_propose') AS t, COUNT(*) AS n, SUM($(J 'propose') = 'utilise') AS u, SUM($(J 'propose') = 'modifie') AS m, SUM($(J 'propose') = 'ignore') AS ig, SUM($(J 'type_final') <> $(J 'type_propose')) AS autre, SUM($(J 'organisme') = 'retire') AS org, SUM($(J 'date') = 'retire') AS dat, SUM($(J 'range') = 'oui') AS rg FROM retours WHERE type = 'renommage' AND $periode GROUP BY 1 ORDER BY n DESC") { param($l) $(if ($l.t) { $l.t } else { '(aucun nom proposé)' }), $l.n, $l.u, $l.m, $l.ig, $l.autre, $l.org, $l.dat, $l.rg }
$md.Add('### Confusions de type (proposé → choisi par l''utilisateur)'); $md.Add('')
Table @('Proposé', 'Choisi', 'Fois', 'Inst.') (Q "SELECT $(J 'type_propose') AS p, $(J 'type_final') AS f, COUNT(*) AS n, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'renommage' AND $periode AND $(J 'type_propose') <> '' AND $(J 'type_final') <> $(J 'type_propose') GROUP BY 1, 2 ORDER BY n DESC LIMIT 30") { param($l) $l.p, $l.f, $l.n, $l.inst }

# ------------------------------------------------------------------ classement des applications
$md.Add('## Classement des applications'); $md.Add('')
$md.Add('### Catégories corrigées à la main'); $md.Add('')
$md.Add('Même correction sur plusieurs installations : la règle automatique est fausse pour tout le monde.'); $md.Add('')
Table @('Application', 'Éditeur', 'Avant', 'Après', 'Inst.') (Q "SELECT $(J 'application') AS a, $(J 'editeur') AS e, $(J 'avant') AS av, $(J 'categorie') AS c, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'classement' AND $periode GROUP BY 1, 3, 4 ORDER BY inst DESC LIMIT 50") { param($l) $l.a, $l.e, $l.av, $l.c, $l.inst }
$md.Add('### Catégories automatiques les plus corrigées'); $md.Add('')
Table @('Catégorie donnée', 'Corrigée vers', 'Applications', 'Inst.') (Q "SELECT $(J 'avant') AS av, $(J 'categorie') AS c, COUNT(DISTINCT $(J 'application')) AS apps, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'classement' AND $periode AND $(J 'avant') <> '' GROUP BY 1, 2 ORDER BY inst DESC LIMIT 20") { param($l) $l.av, $l.c, $l.apps, $l.inst }
$md.Add('### Applications restées sans catégorie'); $md.Add('')
Table @('Application', 'Éditeur', 'Inst.') (Q "SELECT $(J 'application') AS a, $(J 'editeur') AS e, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'non-reconnue' AND $periode GROUP BY 1 ORDER BY inst DESC LIMIT 40") { param($l) $l.a, $l.e, $l.inst }

# ------------------------------------------------------------------ dossiers, groupes, mots-clés, raccourci
$md.Add('## Dossiers créés ou ajoutés'); $md.Add('')
$md.Add('Dossiers souvent créés : à proposer dans le plan de rangement. « Vu comme appli » à oui sans raison : détection à corriger.'); $md.Add('')
Table @('Action', 'Grand dossier', 'Chemin', 'Vu comme appli', 'Inst.') (Q "SELECT $(J 'action') AS a, $(J 'racine') AS r, $(J 'chemin') AS c, $(J 'vu_comme_appli') AS v, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'dossier' AND $periode GROUP BY 1, 2, 3, 4 ORDER BY inst DESC LIMIT 40") { param($l) $l.a, $l.r, $l.c, $l.v, $l.inst }
$md.Add('## Groupes d''applications faits à la main'); $md.Add('')
Table @('Action', 'Nom', 'Applications', 'Inst.') (Q "SELECT $(J 'action') AS a, $(J 'nom') AS n, $(J 'applications') AS ap, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'groupe' AND $periode GROUP BY 1, 2, 3 ORDER BY inst DESC LIMIT 30") { param($l) $l.a, $l.n, $l.ap, $l.inst }
$md.Add('## Mots-clés ajoutés'); $md.Add('')
Table @('Catégorie', 'Mot', 'Inst.') (Q "SELECT $(J 'categorie') AS c, $(J 'mot') AS m, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'mot-cle' AND $periode GROUP BY 1, 2 ORDER BY inst DESC LIMIT 30") { param($l) $l.c, $l.m, $l.inst }
$md.Add('## Raccourci du lanceur'); $md.Add('')
Table @('Touches', 'Conflit', 'Inst.') (Q "SELECT $(J 'touches') AS t, $(J 'conflit') AS c, COUNT(DISTINCT installation) AS inst FROM retours WHERE type = 'raccourci' AND $periode GROUP BY 1, 2 ORDER BY inst DESC") { param($l) $l.t, $l.c, $l.inst }

$texte = ($md -join "`n") + "`n"
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $sortie 'rapport.md'), $texte, $utf8)
[IO.File]::WriteAllText((Join-Path $sortie "rapport-$(Get-Date -Format 'yyyy-MM-dd').md"), $texte, $utf8)
Write-Host "Rapport écrit : Retours\rapport.md ($($err.Count) erreur(s) distincte(s))"
