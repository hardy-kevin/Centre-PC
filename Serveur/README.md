# Service des retours d'expérience

Code du service qui reçoit les retours **anonymes et facultatifs** de Centre PC (désactivés par défaut, voir [CONFIDENTIALITE.md](../CONFIDENTIALITE.md)). Il est public pour que chacun puisse vérifier ce qu'il fait :

- il n'accepte que le format envoyé par Centre PC, avec des champs limités et tronqués ;
- il ne lit ni ne conserve l'adresse IP ;
- il plafonne les envois par installation et au total ;
- il supprime les données de plus de 90 jours.

Hébergement : Cloudflare Workers et base D1 (`retours-worker.js`, `schema.sql`).
