# Service des retours d'expérience

Code du service qui reçoit les retours **anonymes et facultatifs** de Centre PC (désactivés par défaut, voir [CONFIDENTIALITE.md](../CONFIDENTIALITE.md)). Il est public pour que chacun puisse vérifier ce qu'il fait :

- il n'accepte que le format envoyé par Centre PC, avec des champs limités et tronqués ;
- il ne lit ni ne conserve l'adresse IP ;
- il plafonne les envois par installation et au total ;
- il supprime les données de plus de 90 jours ;
- il refuse tout nom d'application, éditeur ou mot-clé qui contient un prénom et un nom de famille, et remplace une telle paire par `%NOM%` dans les messages d'erreur, même si une ancienne version de Centre PC l'a envoyée. Sans liste de noms, il ne garde rien de ces champs.

Hébergement : Cloudflare Workers et base D1 (`retours-worker.js`, `schema.sql`).

La liste de noms (`noms-donnees.js`) est fabriquée par `Outils\listes-noms.ps1` à partir de `Catalogue\noms.gz`. Après chaque mise à jour de la liste : `npx wrangler deploy` depuis ce dossier.
