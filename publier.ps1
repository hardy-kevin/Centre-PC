# Publie Centre PC sur GitHub : envoie le code, puis crée la version (GitHub fabrique CentrePC.exe tout seul)
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$repoUrl = 'https://github.com/hardy-kevin/Centre-PC.git'
$page = 'https://github.com/hardy-kevin/Centre-PC'
function Stop-WithError($msg) { Write-Host "`n  $msg" -ForegroundColor Red; Read-Host "`n  Entrée pour fermer"; exit 1 }

# Git
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "Installation de Git (une seule fois)..." -ForegroundColor Cyan
    winget install --id Git.Git --exact --silent --accept-package-agreements --accept-source-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-WithError "Git n'a pas pu être installé." }
}

# Construction automatique sur GitHub (fichier placé ici car il ne peut pas être copié directement dans .github)
New-Item -ItemType Directory -Path '.github\workflows' -Force | Out-Null
Copy-Item 'Outils\release.yml' '.github\workflows\release.yml' -Force
Copy-Item 'Outils\tests.yml' '.github\workflows\tests.yml' -Force   # tests automatiques à chaque envoi
Remove-Item 'Outils\README-depot-public.md' -ErrorAction SilentlyContinue

if (-not (Test-Path .git)) { git init -b main | Out-Null }
if (-not (git config user.name))  { git config user.name 'Kevin Hardy' }
if (-not (git config user.email)) { git config user.email 'hardy-kevin@users.noreply.github.com' }
if (git remote) { git remote set-url origin $repoUrl } else { git remote add origin $repoUrl }

# Version : celle du projet, augmentée automatiquement si elle est déjà publiée
$proj = Get-Content CentrePC.csproj -Raw -Encoding UTF8   # sans -Encoding, PowerShell 5.1 lit en ANSI et abîme les accents
if ($proj -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { Stop-WithError "Version introuvable dans CentrePC.csproj." }
$maj = [int]$Matches[1]; $min = [int]$Matches[2]; $pat = [int]$Matches[3]
Write-Host "Connexion à GitHub (une fenêtre de connexion peut s'ouvrir la première fois)..." -ForegroundColor Cyan
$tags = (git ls-remote --tags origin) -join "`n"
if ($LASTEXITCODE -ne 0) { Stop-WithError "Impossible de joindre GitHub. Vérifie la connexion internet et ton accès au dépôt." }
while ($tags -match "refs/tags/v$maj\.$min\.$pat(\^\{\})?(\n|$)") { $pat++ }
$ver = "$maj.$min.$pat"
$proj = $proj -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$ver</Version>"
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'CentrePC.csproj'), $proj, [Text.UTF8Encoding]::new($false))

Write-Host "`n  Publication de Centre PC $ver" -ForegroundColor Green
$notes = Read-Host "  Qu'est-ce qui change dans cette version ? (Entrée pour passer)"
if ([string]::IsNullOrWhiteSpace($notes)) { $notes = 'Améliorations et corrections.' }
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'NOTES_VERSION.md'), "## Centre PC $ver`n`n- $notes`n", [Text.UTF8Encoding]::new($false))

git add -A
git commit -m "Centre PC $ver" | Out-Null

# Si le dépôt GitHub contient déjà des fichiers (README créé sur le site…), on les fusionne en gardant les nôtres
if (git ls-remote --heads origin main) {
    git pull origin main --no-rebase --allow-unrelated-histories -X ours --no-edit
}
git push -u origin main
if ($LASTEXITCODE -ne 0) { Stop-WithError "L'envoi du code a échoué (droits sur le dépôt ?)." }

git tag -a "v$ver" -m "Centre PC $ver"
git push origin "v$ver"
if ($LASTEXITCODE -ne 0) { Stop-WithError "La création de la version a échoué." }

Write-Host "`n  C'est envoyé ! GitHub fabrique CentrePC.exe (environ 5 minutes)." -ForegroundColor Green
Write-Host "  La version apparaîtra ici : $page/releases" -ForegroundColor Green
Start-Process "$page/actions"
Read-Host "`n  Entrée pour fermer"
