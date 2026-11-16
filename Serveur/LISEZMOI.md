# Point d'entrée des retours d'expérience

Service minimal qui reçoit les retours anonymes de Centre PC (voir `CONFIDENTIALITE.md`). Il tourne sur l'offre gratuite de Cloudflare (Workers + base D1).

**Tant qu'il n'est pas déployé et que son adresse n'est pas inscrite dans `Core/Feedback.cs` (`Endpoint`), Centre PC n'envoie rien.**

## Ce qu'il fait

- Accepte uniquement `POST /v1/retours`, 64 Ko au plus, au format produit par Centre PC.
- Ne lit ni ne conserve l'adresse IP.
- Ne garde que les champs attendus, tronqués ; ignore le reste.
- Plafonne à 400 retours par installation et par jour.
- Supprime les données de plus de 90 jours (tâche quotidienne).

## Mise en place (une fois)

Il faut Node.js et un compte Cloudflare.

1. `npm install -g wrangler` puis `wrangler login`.
2. Dans ce dossier : `wrangler d1 create centre-pc-retours`, puis recopier le `database_id` affiché dans `wrangler.toml`.
3. `wrangler d1 execute centre-pc-retours --remote --file=schema.sql`
4. `wrangler deploy` : l'adresse du service s'affiche (`https://centre-pc-retours.<compte>.workers.dev`).
5. Inscrire `https://…workers.dev/v1/retours` dans `Core/Feedback.cs` (`Endpoint`), mettre à jour `CONFIDENTIALITE.md` (l'envoi devient actif), puis publier une nouvelle version.

## Lire les retours

Console Cloudflare › D1 › `centre-pc-retours` › Console :

- `SELECT * FROM erreurs_frequentes LIMIT 50;`
- `SELECT * FROM classements_proposes LIMIT 100;` (seulement ce que trois installations au moins ont corrigé de la même façon)

## Effacement à la demande

`DELETE FROM retours WHERE installation = '<identifiant>';` — l'identifiant figure dans `%LOCALAPPDATA%\CentrePC\config.json` (`FeedbackInstallId`) de la personne qui le demande.
