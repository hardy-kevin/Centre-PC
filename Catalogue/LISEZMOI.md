# Catalogue distant signé

`catalogue.json` permet de corriger Centre PC chez tous les utilisateurs **sans publier une nouvelle version** :

- **Classement** : nom de l'application → catégorie. Les choix de chaque utilisateur restent prioritaires.
- **Desactive** : fonctions coupées en attendant un correctif. Identifiants acceptés : `capteurs-hwinfo`, `capteurs-lhm`, `outils-expert`, `retours`.

Ce sont des données, jamais du code. Centre PC lit ce fichier sur GitHub une fois par jour au plus, et ne l'utilise que si sa signature (`catalogue.json.sig`) est valide. Un fichier modifié par quelqu'un d'autre, ou plus ancien que le dernier reçu, est ignoré.

## Mise en place (une fois)

Dans PowerShell, depuis ce dossier :

1. `powershell -ExecutionPolicy Bypass -File cle.ps1` : crée la clé de signature dans Windows et affiche la **clé publique**.
2. `powershell -ExecutionPolicy Bypass -File cle.ps1 -Sauvegarder E:\cle-catalogue.txt` : copie de secours de la clé privée, à ranger **hors du PC et hors du dépôt**. Sans elle, un PC perdu oblige à changer de clé (donc à publier une version).
3. Inscrire la clé publique dans `Core/RemoteCatalog.cs` (`PublicKeys`), puis publier une version.

## Modifier le catalogue

1. Éditer `catalogue.json` (les lignes de `classements_proposes`, dans la base des retours, indiquent quoi ajouter).
2. `powershell -ExecutionPolicy Bypass -File signer.ps1` : augmente la version, met la date, signe.
3. Envoyer `catalogue.json` et `catalogue.json.sig` sur GitHub (branche `main`). Les installations le prennent dans la journée.

Le fichier `.gitattributes` à la racine empêche Git de modifier les fins de ligne de ces deux fichiers : sans cela la signature ne correspondrait plus.

## Si la clé est perdue

Trois niveaux de secours, du plus simple au plus lourd :

1. **Copie de secours de la clé principale** (`cle.ps1 -Sauvegarder`) : `cle.ps1 -Restaurer fichier` la remet sur un PC neuf. Rien d'autre à faire.
2. **Clé de secours** (`cle.ps1 -Secours E:\cle-secours.txt`) : une seconde clé, dont la partie privée n'existe que dans ce fichier (jamais sur le PC), et dont la clé publique est inscrite dans Centre PC dès le départ. Si la clé principale et sa copie sont perdues : `cle.ps1 -Restaurer E:\cle-secours.txt -Nom CentrePC-Catalogue-Secours`, puis `signer.ps1 -Nom CentrePC-Catalogue-Secours`. Toutes les installations acceptent aussitôt ce catalogue. Ranger ce fichier à un autre endroit que la copie de la clé principale.
3. **Tout est perdu** : rien de grave. Le catalogue cesse simplement d'évoluer ; Centre PC continue de fonctionner avec le dernier catalogue reçu. Créer une nouvelle clé, l'inscrire dans `PublicKeys` et publier une version : les mises à jour de Centre PC ne dépendent pas de cette clé.

Si une clé est **volée** (et non perdue) : retirer sa clé publique de `PublicKeys` et publier une version au plus vite. D'ici là, le voleur pourrait au pire fausser le classement des applications ou couper une des quatre fonctions listées plus haut ; le catalogue ne contient jamais de code.

## Changer de clé

`PublicKeys` accepte plusieurs clés : ajouter la nouvelle, publier, attendre que les utilisateurs aient mis à jour, puis signer avec la nouvelle et retirer l'ancienne à la version suivante.
