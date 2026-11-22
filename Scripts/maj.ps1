# maj.ps1 — lancé en administrateur par Centre PC. Écrit sa progression dans un journal lu par l'application.
# Lignes spéciales : ##ETAPE|clé|statut|détail, ##MANUEL|id|nom|message|installateur et ##FIN|redemarrage(0/1)
# Lancé via un petit amorceur qui vérifie l'empreinte du script et des paramètres avant de l'exécuter (voir Updates.StartInstall) :
# les paramètres arrivent alors directement en texte (-ParamsJson), sans relire de fichier modifiable.
param([string]$Params, [string]$ParamsJson)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$opt = if ($ParamsJson) { $ParamsJson | ConvertFrom-Json } else { Get-Content -LiteralPath $Params -Raw -Encoding UTF8 | ConvertFrom-Json }
$LogPath = $opt.Log

# Ce script tourne en administrateur mais écrit dans les dossiers de l'utilisateur (journal, réglages, raccourcis mis de
# côté). Si l'un d'eux a été remplacé par un lien (jonction, lien symbolique) vers un autre endroit, un programme sans
# droits pourrait faire écrire ou déplacer des fichiers ailleurs avec les droits administrateur : on s'arrête (code 97).
# Les dossiers synchronisés par OneDrive (fichiers « à la demande ») ne sont pas des liens et restent acceptés.
function Test-Lien([string]$chemin) {
    if ([string]::IsNullOrWhiteSpace($chemin) -or -not (Test-Path -LiteralPath $chemin)) { return $false }
    $i = Get-Item -LiteralPath $chemin -Force -ErrorAction SilentlyContinue
    return $i -and ($i.LinkType -in 'Junction', 'SymbolicLink')
}
$aVerifier = @($opt.DataDir, (Split-Path -LiteralPath $opt.DataDir -Parent), (Split-Path -LiteralPath $LogPath -Parent), $opt.ReplacedDir)
if (@($aVerifier | Where-Object { Test-Lien $_ }).Count -gt 0) { exit 97 }

function Log([string]$m) {
    if ($null -eq $m) { return }
    $m = $m.TrimEnd()
    if ($m -eq '' -or $m -match '^[\s\-\\|/]*$' -or $m -match '[█▒]') { return }   # animations de winget
    [IO.File]::AppendAllText($LogPath, $m + "`n", [Text.UTF8Encoding]::new($false))
}
function Etape($cle, $statut, $detail = '') { Log "##ETAPE|$cle|$statut|$detail" }

Log "Démarrage : $(Get-Date -Format 'dd/MM/yyyy HH:mm')"
$reboot = $false

# ---------------------------------------------------------------- Point de restauration
if ($opt.RestorePoint) {
    Etape 'restore' 'En cours'
    try {
        Checkpoint-Computer -Description 'Avant Centre PC' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        Etape 'restore' 'OK' 'créé'
    } catch { Etape 'restore' 'IGNORÉ' 'protection système désactivée ou déjà créé aujourd''hui' }
}

# ---------------------------------------------------------------- Applications (winget)
# Certaines mises à jour désinstallent l'ancienne version AVANT d'installer la nouvelle (JetBrains, VC++ 2013…).
# Pour celles-là, on vérifie d'abord que l'installation pourra se faire (installateur téléchargé, intact, application
# fermée, place disque) ; sinon la mise à jour est sautée et une action manuelle est demandée (ligne ##MANUEL).
# Après chaque mise à jour, on contrôle que l'application est toujours installée ; sinon on la réinstalle.
# Lignes spéciales : ##MANUEL|id|nom|message|installateur prêt (facultatif)
#                    ##APP|id|statut|détail   (statut : En cours, À jour, À faire, Reporté, Échec)

$codes = @{
    Introuvable        = -1978335212   # aucune application trouvée (winget list)
    NonApplicable      = -1978335189   # aucune mise à jour applicable
    HachageDifferent   = -1978335215
    EmplacementRequis  = -1978335137
    TechnoDifferente   = -1978335090
    AppliOuverte       = -1978334975
    FichierUtilise     = -1978334973
    DisquePlein        = -1978334971
    PasDeReseau        = -1978334969
    RedemarrageAFinir  = -1978334967
    RedemarrageAvant   = -1978334966
}

# Paquets connus pour désinstaller avant d'installer (complété automatiquement au fil des mises à jour)
$risqueParDefaut = @('JetBrains.*', 'Microsoft.VCRedist.2005.*', 'Microsoft.VCRedist.2008.*',
                     'Microsoft.VCRedist.2010.*', 'Microsoft.VCRedist.2012.*', 'Microsoft.VCRedist.2013.*')
$risqueFichier = Join-Path $opt.DataDir 'maj-desinstallation.json'
$risque = @{}
if (Test-Path -LiteralPath $risqueFichier) { try { (Get-Content -LiteralPath $risqueFichier -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $risque[$_.Name] = [bool]$_.Value } } catch { } }

function Test-Risque([string]$id) {
    if ($risque.ContainsKey($id)) { return $risque[$id] }
    foreach ($m in $risqueParDefaut) { if ($id -like $m) { return $true } }
    return $false
}
function Save-Risque {
    try { [IO.File]::WriteAllText($risqueFichier, ($risque | ConvertTo-Json), [Text.UTF8Encoding]::new($false)) } catch { }
}

function Net([string]$t) { ("$t" -replace '[|\r\n]', ' ').Trim() }

# Avancement d'une application, affiché ligne par ligne dans Centre PC
function AppEtat([string]$id, [string]$statut, [string]$detail = '') { Log "##APP|$(Net $id)|$statut|$(Net $detail)" }

function Manuel([string]$id, [string]$nom, [string]$message, [string]$installateur = '') {
    Log "  ⚠ Action manuelle : $message"
    Log "##MANUEL|$(Net $id)|$(Net $nom)|$(Net $message)|$(Net $installateur)"
    AppEtat $id 'À faire' $message
}

# Lance winget, recopie sa sortie dans le journal et renvoie le code + la sortie
function Invoke-Winget([string[]]$arguments, [switch]$Quiet) {
    $out = @(& $winget @arguments 2>&1 | ForEach-Object { $l = "$_"; if (-not $Quiet) { Log $l }; $l })
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out -join "`n") }
}

function Test-Installe([string]$id) {
    $r = Invoke-Winget @('list', '--id', $id, '--exact', '--accept-source-agreements', '--disable-interactivity') -Quiet
    return ($r.Code -eq 0 -and $r.Out -match [regex]::Escape($id))
}

# Entrée « Programmes et fonctionnalités » correspondant au nom affiché par winget
function Find-Arp([string]$nom) {
    if ([string]::IsNullOrWhiteSpace($nom)) { return $null }
    $n = $nom.TrimEnd('…').Trim()
    $cles = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    Get-ItemProperty $cles -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -and $_.DisplayName.StartsWith($n, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
}

# Dossier d'installation (InstallLocation, sinon déduit de l'icône ou du désinstalleur)
function Get-DossierAppli($arp) {
    if (-not $arp) { return $null }
    foreach ($c in @($arp.InstallLocation, $arp.DisplayIcon, $arp.UninstallString)) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        $p = "$c".Trim()
        if ($p.StartsWith('"')) { $p = $p.Substring(1, [Math]::Max(0, $p.IndexOf('"', 1) - 1)) }
        $p = ($p -replace ',\s*-?\d+$', '').Trim()
        if ($p -match '^(.+?\.exe)\b') { $p = $Matches[1] }
        if ($p.Length -lt 4 -or $p.StartsWith($env:windir, [StringComparison]::OrdinalIgnoreCase)) { continue }   # MsiExec…
        if (Test-Path -LiteralPath $p -PathType Container) { return $p.TrimEnd('\') }
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            $d = Split-Path $p -Parent
            if ((Split-Path $d -Leaf) -eq 'bin') { $d = Split-Path $d -Parent }
            return $d
        }
    }
    return $null
}

function Get-ProcessusDans([string]$dossier) {
    if (-not $dossier) { return @() }
    $prefixe = $dossier.TrimEnd('\') + '\'
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($prefixe, [StringComparison]::OrdinalIgnoreCase) })
}

# Télécharge l'installateur (winget vérifie son empreinte) dans le dossier des installateurs prêts
function Get-Installateur([string]$id) {
    $dir = Join-Path $cache ($id -replace '[^\w\.\-]', '_')
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    $r = Invoke-Winget @('download', '--id', $id, '--exact', '--download-directory', $dir, '--skip-dependencies',
                         '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity') -Quiet
    $fichier = Get-ChildItem -LiteralPath $dir -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -notin '.yaml', '.yml' } | Sort-Object Length -Descending | Select-Object -First 1
    $manifeste = (Get-ChildItem -LiteralPath $dir -File -Recurse -Include '*.yaml', '*.yml' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    [pscustomobject]@{ Code = $r.Code; Dossier = $dir; Fichier = $fichier; Manifeste = $manifeste }
}

if ($opt.Apps) {
    Etape 'apps' 'En cours'
    $winget = if ($opt.WingetPath) { $opt.WingetPath } else { (Get-Command winget.exe -ErrorAction SilentlyContinue).Source }
    if (-not $winget) {
        $winget = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $winget) { Etape 'apps' 'ERREUR' 'winget introuvable (installez « Programme d''installation d''application » depuis le Microsoft Store)' }
    else {
        # Installateurs préparés : dans un dossier de Program Files, non modifiable sans droits. Ce script tourne en
        # administrateur et y supprime des dossiers : dans un dossier de l'utilisateur, un programme sans droits pourrait
        # remplacer un sous-dossier par un lien vers un dossier de Windows et le faire vider avec les droits administrateur.
        # (Sans droits administrateur, par exemple pour les tests automatiques, le cache de l'utilisateur suffit.)
        $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $cache = if ($admin) { Join-Path $env:ProgramFiles 'CentrePC-session\Installateurs' }
                 else { Join-Path $(if ($opt.CacheDir) { $opt.CacheDir } else { $opt.DataDir }) 'Installateurs' }
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        Get-ChildItem -LiteralPath $cache -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-14) } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

        $bureauPublic = [Environment]::GetFolderPath('CommonDesktopDirectory')
        $raccourcisPublics = { if (-not $bureauPublic) { return @() }; @(Get-ChildItem -LiteralPath $bureauPublic -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.lnk', '.url' } | Select-Object -ExpandProperty FullName) }
        $publicAvant = & $raccourcisPublics

        $communs = @('--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
        $ok = 0; $echecs = 0; $manuels = 0; $reportes = 0; $total = 0

        foreach ($p in @($opt.Packages)) {
            $id = "$($p.Id)"; $nom = if ($p.Name) { "$($p.Name)" } else { $id }
            if (-not $id) { continue }
            $total++
            Log "> Mise à jour de $id"
            AppEtat $id 'En cours' 'téléchargement et installation…'

            if ($id.EndsWith('…')) {   # identifiant tronqué par winget : recherche partielle, sans contrôle possible
                $r = Invoke-Winget (@('upgrade', '--query', $id.TrimEnd('…'), '--include-unknown') + $communs)
                if ($r.Code -eq 0) { $ok++; AppEtat $id 'À jour' } else { $echecs++; Log "  ! échec ($($r.Code))"; AppEtat $id 'Échec' "code $($r.Code)" }
                continue
            }

            $arp = Find-Arp $nom
            $dossier = Get-DossierAppli $arp
            # Dossier d'installation imposé à winget (en administrateur) : seulement s'il vient d'une entrée de la machine
            # (HKLM), jamais d'une entrée de l'utilisateur (HKCU), modifiable par n'importe quel programme sans droits
            $dossierImpose = if ($arp -and "$($arp.PSPath)" -match 'HKEY_LOCAL_MACHINE') { $dossier } else { $null }
            $etaitInstalle = Test-Installe $id
            $pret = $null

            # ---------- 1) Vérifications préalables pour les mises à jour qui désinstallent d'abord
            if (Test-Risque $id) {
                Log "  Cette mise à jour peut désinstaller l'ancienne version : vérification avant de commencer…"
                AppEtat $id 'En cours' 'vérification avant de désinstaller l''ancienne version…'
                $ouverts = Get-ProcessusDans $dossier
                if ($ouverts.Count -gt 0) {
                    $noms = ($ouverts | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
                    Manuel $id $nom "$nom est ouvert ($noms) : fermez-le puis relancez la mise à jour. Aucune modification n'a été effectuée."
                    $manuels++; continue
                }
                $pret = Get-Installateur $id
                if ($pret.Code -ne 0 -or -not $pret.Fichier -or $pret.Fichier.Length -lt 1kb) {
                    Manuel $id $nom "Impossible de préparer la nouvelle version de $nom (téléchargement ou vérification en échec, code $($pret.Code)). La mise à jour a été ignorée afin de ne pas désinstaller l'application : mettez-la à jour depuis l'application elle-même ou depuis le site de l'éditeur."
                    $manuels++; continue
                }
                $sig = Get-AuthenticodeSignature -LiteralPath $pret.Fichier.FullName -ErrorAction SilentlyContinue
                if ($sig -and "$($sig.Status)" -in 'HashMismatch', 'NotTrusted', 'Incompatible') {
                    Manuel $id $nom "L'installateur de $nom a une signature invalide ($($sig.Status)). Mise à jour ignorée par sécurité."
                    Remove-Item -LiteralPath $pret.Dossier -Recurse -Force -ErrorAction SilentlyContinue
                    $manuels++; continue
                }
                $lettre = $env:SystemDrive.TrimEnd(':')
                $libre = (Get-PSDrive -Name $lettre -ErrorAction SilentlyContinue).Free
                if ($libre -and $libre -lt (3 * $pret.Fichier.Length + 500MB)) {
                    Manuel $id $nom "Pas assez d'espace disque pour installer la nouvelle version de $nom. Libérez de l'espace puis relancez la mise à jour."
                    $manuels++; continue
                }
                # Le manifeste confirme (ou non) la désinstallation préalable : mémorisé pour les prochaines fois
                if ($pret.Manifeste -match '(?im)^\s*UpgradeBehavior:\s*(\w+)') {
                    $risque[$id] = ($Matches[1] -eq 'uninstallPrevious'); Save-Risque
                }
                Log "  Nouvelle version prête et vérifiée : $($pret.Fichier.Name)"
            }

            # ---------- 2) Mise à jour
            $r = Invoke-Winget (@('upgrade', '--id', $id, '--exact', '--include-unknown') + $communs)
            if ($r.Out -match "(?i)désinstallation du paquet|uninstalling package|starting package uninstall") {
                if (-not $risque[$id]) { $risque[$id] = $true; Save-Risque }
            }

            # L'application exige son dossier d'installation : on lui redonne celui de la version actuelle
            if ($r.Code -eq $codes.EmplacementRequis -and $dossierImpose) {
                Log "  Nouvel essai en conservant le dossier actuel : $dossierImpose"
                $r = Invoke-Winget (@('upgrade', '--id', $id, '--exact', '--include-unknown', '--location', $dossierImpose) + $communs)
            }
            if ($r.Code -in $codes.RedemarrageAFinir, $codes.RedemarrageAvant) { $reboot = $true }

            # ---------- 3) Contrôle : l'application est-elle toujours là ?
            if ($etaitInstalle -and -not (Test-Installe $id)) {
                Log "  ! $nom n'est plus installé après la mise à jour : réinstallation…"
                $ri = Invoke-Winget (@('install', '--id', $id, '--exact') + $communs)
                if ($ri.Code -in $codes.RedemarrageAFinir, $codes.RedemarrageAvant) { $reboot = $true }
                if (Test-Installe $id) { Log "  Réinstallé correctement"; $ok++; AppEtat $id 'À jour' 'réinstallée automatiquement' }
                else {
                    if (-not $pret) { $pret = Get-Installateur $id }
                    $chemin = if ($pret -and $pret.Fichier) { $pret.Fichier.FullName } else { '' }
                    Manuel $id $nom "$nom a été désinstallé par sa mise à jour mais la nouvelle version n'a pas pu s'installer. Lancez l'installateur préparé pour le réinstaller." $chemin
                    $manuels++; $pret = $null
                }
                if ($pret) { Remove-Item -LiteralPath $pret.Dossier -Recurse -Force -ErrorAction SilentlyContinue }
                continue
            }
            if ($pret) { Remove-Item -LiteralPath $pret.Dossier -Recurse -Force -ErrorAction SilentlyContinue }

            # ---------- 4) Résultat
            switch ($r.Code) {
                0                           { $ok++; AppEtat $id 'À jour' }
                $codes.RedemarrageAFinir    { $ok++; AppEtat $id 'À jour' 'redémarrage nécessaire' }
                $codes.RedemarrageAvant     { $ok++; Log "  Redémarrez le PC pour terminer."; AppEtat $id 'À jour' 'redémarrage nécessaire' }
                $codes.NonApplicable        { $ok++; Log "  Déjà à jour."; AppEtat $id 'À jour' 'déjà à jour' }
                $codes.HachageDifferent     { $reportes++; Log "  Reporté : l'éditeur a changé son fichier et le catalogue winget n'est pas encore à jour. Nouvel essai dans quelques jours."
                                              AppEtat $id 'Reporté' "l'éditeur vient de changer son fichier ; nouvel essai à la prochaine mise à jour" }
                $codes.EmplacementRequis    { $manuels++; Manuel $id $nom "$nom ne peut pas être mis à jour automatiquement : lancez $nom, qui se met à jour lui-même." }
                $codes.TechnoDifferente     { $manuels++; Manuel $id $nom "La nouvelle version de $nom s'installe différemment de l'actuelle : elle doit être désinstallée puis réinstallée manuellement. Si $nom est installé par un autre logiciel (par exemple un lanceur de jeux), excluez-le des mises à jour dans Réglages." }
                { $_ -in $codes.AppliOuverte, $codes.FichierUtilise } { $manuels++; Manuel $id $nom "$nom est ouvert : fermez-le puis relancez la mise à jour." }
                $codes.DisquePlein          { $manuels++; Manuel $id $nom "Disque plein : libérez de l'espace puis relancez la mise à jour de $nom." }
                $codes.PasDeReseau          { $echecs++; Log "  ! pas de connexion Internet"; AppEtat $id 'Échec' 'pas de connexion internet' }
                default                     { $echecs++; Log "  ! échec ($($r.Code))"; AppEtat $id 'Échec' "erreur $($r.Code) (détails dans le journal)" }
            }
        }

        # Icônes ajoutées sur le bureau Public par les installateurs : ramenées sur le bureau de l'utilisateur
        # (Centre PC les range ou retire les doublons ensuite, sans nouvelle demande d'autorisation)
        if ($opt.TidyPublicShortcuts -and $opt.UserDesktop -and (Test-Path -LiteralPath $opt.UserDesktop) -and -not (Test-Lien $opt.UserDesktop)) {
            # nouvelles icônes + icônes déjà rangées dans Centre PC (doublons laissés par d'anciennes mises à jour)
            $rangees = @(Get-ChildItem -LiteralPath $opt.StoreDir -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
            foreach ($f in (& $raccourcisPublics | Where-Object { $_ -notin $publicAvant -or (Split-Path $_ -Leaf) -in $rangees })) {
                try {
                    $dest = Join-Path $opt.UserDesktop (Split-Path $f -Leaf)
                    if (Test-Path -LiteralPath $dest) {   # déjà présent sur le bureau de l'utilisateur : doublon mis de côté
                        New-Item -ItemType Directory -Path $opt.ReplacedDir -Force | Out-Null
                        $dest = Join-Path $opt.ReplacedDir ("{0}_{1}" -f (Get-Date -Format 'yyyyMMddHHmmss'), (Split-Path $f -Leaf))
                    }
                    Move-Item -LiteralPath $f -Destination $dest -ErrorAction Stop
                    if ($dest.StartsWith($opt.ReplacedDir)) { (Get-Item -LiteralPath $dest).LastWriteTime = Get-Date }
                } catch { }
            }
        }

        $resume = @("$ok à jour")
        if ($manuels) { $resume += "$manuels action(s) requise(s)" }
        if ($reportes) { $resume += "$reportes reportée(s)" }
        if ($echecs) { $resume += "$echecs échec(s)" }
        if ($total -eq 0) { Etape 'apps' 'IGNORÉ' 'aucune application sélectionnée' }
        elseif ($echecs -gt 0) { Etape 'apps' 'PARTIEL' ($resume -join ', ') }
        elseif ($manuels -gt 0 -or $reportes -gt 0) { Etape 'apps' 'À VÉRIFIER' ($resume -join ', ') }
        else { Etape 'apps' 'OK' "$total traitée(s)" }
    }
}

# ---------------------------------------------------------------- Windows Update
if ($opt.WindowsUpdate) {
    # API de Windows Update fournie par Windows (comme pour les pilotes plus bas) : aucun module téléchargé ni code tiers
    # exécuté avec les droits administrateur. Les mises à jour des autres produits Microsoft (Office…) sont incluses
    # quand le service Microsoft Update est actif ; un serveur d'entreprise (WSUS) reste respecté sinon.
    Etape 'wu' 'En cours'
    try {
        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'Centre PC'
        $mu = '7971f918-a847-4430-9279-4a52d1efe18d'
        $services = New-Object -ComObject Microsoft.Update.ServiceManager
        $muActif = { @($services.Services | Where-Object { $_.ServiceID -eq $mu }).Count -gt 0 }
        if (-not (& $muActif)) {
            try { [void]$services.AddService2($mu, 7, '') } catch { Log "  Microsoft Update n'a pas pu être activé : $($_.Exception.Message)" }
        }
        $searcher = $session.CreateUpdateSearcher()
        if (& $muActif) { $searcher.ServerSelection = 3; $searcher.ServiceID = $mu }
        Log '> Recherche des mises à jour de Windows…'
        # Système seulement : les pilotes ont leur propre étape (choisis un par un dans Centre PC)
        $res = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
        $lot = New-Object -ComObject Microsoft.Update.UpdateColl
        $ignorees = 0
        foreach ($u in $res.Updates) {
            if ($u.InstallationBehavior.CanRequestUserInput) { $ignorees++; Log "  (à installer depuis Windows Update) $($u.Title)"; continue }
            if (-not $u.EulaAccepted) { $u.AcceptEula() }
            Log "  • $($u.Title)"
            [void]$lot.Add($u)
        }
        if ($lot.Count -eq 0) { Etape 'wu' 'OK' $(if ($ignorees) { "$ignorees mise(s) à jour à installer depuis Windows Update" } else { 'déjà à jour' }) }
        else {
            $dl = $session.CreateUpdateDownloader(); $dl.Updates = $lot
            [void]$dl.Download()
            $prets = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($u in $lot) { if ($u.IsDownloaded) { [void]$prets.Add($u) } }
            $okW = 0; $echecsW = $lot.Count - $prets.Count
            if ($echecsW) { Log "  ! $echecsW mise(s) à jour non téléchargée(s)" }
            if ($prets.Count -gt 0) {
                $inst = $session.CreateUpdateInstaller(); $inst.Updates = $prets
                $r = $inst.Install()
                for ($i = 0; $i -lt $prets.Count; $i++) {
                    $ur = $r.GetUpdateResult($i)
                    if ($ur.ResultCode -in 2, 3) { $okW++; Log "  Installée : $($prets.Item($i).Title)" }
                    else { $echecsW++; Log ("  ! Échec (0x{0:X8}) : {1}" -f $ur.HResult, $prets.Item($i).Title) }
                }
                if ($r.RebootRequired) { $reboot = $true }
            }
            $resume = @("$okW installée(s)")
            if ($echecsW) { $resume += "$echecsW échec(s)" }
            if ($ignorees) { $resume += "$ignorees à installer depuis Windows Update" }
            Etape 'wu' $(if ($echecsW) { 'PARTIEL' } else { 'OK' }) ($resume -join ', ')
        }
        try { if ((New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired) { $reboot = $true } } catch { }
    } catch {
        Log "Erreur : $($_.Exception.Message)"
        Etape 'wu' 'ERREUR' $_.Exception.Message
    }
}

# ---------------------------------------------------------------- Pilotes (cochés dans Centre PC)
# Installés un par un avec l'API de Windows Update, pour donner un résultat par périphérique.
# Lignes spéciales : ##PILOTE|identifiant|statut|détail   (statut : En cours, À jour, Échec, Reporté)
function PiloteEtat([string]$id, [string]$statut, [string]$detail = '') { Log "##PILOTE|$(Net $id)|$statut|$(Net $detail)" }

if ($opt.Drivers -and @($opt.DriverUpdates).Count -gt 0) {
    Etape 'drivers' 'En cours'
    $okP = 0; $echecsP = 0; $reportesP = 0
    try {
        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'Centre PC'
        $voulus = @{}
        foreach ($d in @($opt.DriverUpdates)) { $voulus["$($d.Id)"] = "$($d.Name)" }
        Log '> Recherche des pilotes sélectionnés…'
        $trouves = @{}
        $res = $session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
        foreach ($u in $res.Updates) { if ($voulus.ContainsKey($u.Identity.UpdateID)) { $trouves[$u.Identity.UpdateID] = $u } }

        foreach ($id in $voulus.Keys) {
            $nom = $voulus[$id]
            if (-not $trouves.ContainsKey($id)) {
                Log "  $nom : plus proposé par Windows Update (déjà installé ou remplacé)"
                PiloteEtat $id 'À jour' 'déjà installé ou remplacé par une version plus récente'
                $okP++; continue
            }
            $u = $trouves[$id]
            Log "> Pilote : $nom ($($u.Title))"
            PiloteEtat $id 'En cours' 'téléchargement…'
            try {
                if (-not $u.EulaAccepted) { $u.AcceptEula() }
                $lot = New-Object -ComObject Microsoft.Update.UpdateColl
                [void]$lot.Add($u)
                $dl = $session.CreateUpdateDownloader(); $dl.Updates = $lot
                $r = $dl.Download()
                if ($r.ResultCode -notin 2, 3) { throw "téléchargement impossible (code $($r.ResultCode))" }
                PiloteEtat $id 'En cours' 'installation…'
                $inst = $session.CreateUpdateInstaller(); $inst.Updates = $lot
                $r = $inst.Install()
                $hr = $r.GetUpdateResult(0)
                if ($hr.RebootRequired) { $reboot = $true }
                switch ($r.ResultCode) {
                    2 { $okP++; PiloteEtat $id 'À jour' $(if ($hr.RebootRequired) { 'redémarrage nécessaire' } else { 'installé' }); Log '  Installé correctement' }
                    3 { $okP++; PiloteEtat $id 'À jour' 'installé avec avertissements'; Log '  Installé avec avertissements' }
                    5 { $reportesP++; PiloteEtat $id 'Reporté' 'installation interrompue'; Log '  Interrompu' }
                    default { $echecsP++; PiloteEtat $id 'Échec' ('erreur 0x{0:X8}' -f $hr.HResult); Log ("  ! échec (0x{0:X8})" -f $hr.HResult) }
                }
            } catch {
                $echecsP++; PiloteEtat $id 'Échec' $_.Exception.Message; Log "  ! échec : $($_.Exception.Message)"
            }
        }
        $resume = @("$okP installé(s)")
        if ($reportesP) { $resume += "$reportesP reporté(s)" }
        if ($echecsP) { $resume += "$echecsP échec(s)" }
        Etape 'drivers' $(if ($echecsP) { 'PARTIEL' } else { 'OK' }) ($resume -join ', ')
    } catch {
        Log "Erreur : $($_.Exception.Message)"
        Etape 'drivers' 'ERREUR' $_.Exception.Message
    }
}

# ---------------------------------------------------------------- Microsoft Store
if ($opt.Store) {
    Etape 'store' 'En cours'
    try {
        Get-CimInstance -Namespace 'root\cimv2\mdm\dmmap' -ClassName 'MDM_EnterpriseModernAppManagement_AppManagement01' -ErrorAction Stop |
            Invoke-CimMethod -MethodName UpdateScanMethod -ErrorAction Stop | Out-Null
        Etape 'store' 'OK' 'mises à jour lancées en arrière-plan'
    } catch { Etape 'store' 'PARTIEL' 'ouvrez Microsoft Store > Bibliothèque > Obtenir les mises à jour' }
}

# ---------------------------------------------------------------- Defender
if ($opt.Defender) {
    Etape 'defender' 'En cours'
    try {
        Update-MpSignature -ErrorAction Stop
        Etape 'defender' 'OK' (Get-MpComputerStatus).AntivirusSignatureVersion
    } catch { Etape 'defender' 'IGNORÉ' 'Defender désactivé ou autre antivirus' }
}

# ---------------------------------------------------------------- Fin
$cles = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
if ($cles | Where-Object { Test-Path $_ }) { $reboot = $true }
Log "Terminé : $(Get-Date -Format 'HH:mm')"
Log "##FIN|$([int]$reboot)"
