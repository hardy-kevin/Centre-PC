# Catalogue signé

`catalogue.json` permet de corriger Centre PC chez tous les utilisateurs sans publier une nouvelle version :

- **Classement** : catégorie de certaines applications (les choix de chaque utilisateur restent prioritaires) ;
- **Desactive** : fonctions suspendues en attendant un correctif ;
- **Organismes** : expéditeurs reconnus dans les documents pour proposer un nom (`"Nom affiché": ["mot cherché", …]`) ;
- **MotsCles** : mots-clés ajoutés aux catégories d'origine (`"Catégorie": ["mot", …]`) ;
- **Editeurs** : éditeurs de logiciels connus, qui ne sont jamais pris pour un nom de personne dans les retours.

Ce sont des **données, jamais du code**. Centre PC ne les utilise que si la signature (`catalogue.json.sig`) correspond à l'une des clés publiques inscrites dans l'application ; un fichier modifié, ou plus ancien que le dernier reçu, est ignoré. La lecture se désactive dans Réglages.

## Listes de prénoms et de noms (`noms.gz`)

`noms.gz` contient les prénoms et les noms de famille publiés par l'Insee, plus `Outils\editeurs-connus.txt`. Centre PC s'en sert pour ne **jamais** envoyer un prénom suivi ou précédé d'un nom dans les retours d'expérience (le serveur refait la même vérification). La liste est intégrée à l'application ; une version plus récente signée est téléchargée au plus une fois par mois.

- Fabrication : `.\Outils\listes-noms.ps1` (fait aussi chaque mois par GitHub, qui ouvre une demande de fusion) ;
- Signature, à la main : `.\Catalogue\signer.ps1 -Fichier noms.gz`, puis redéployer le serveur (`cd Serveur ; npx wrangler deploy`).

## Propositions

Chaque semaine, GitHub lit les retours agrégés (au moins 3 installations) et ouvre une demande de fusion qui complète `catalogue.json` et décrit le reste dans `propositions.md`. Rien n'est signé automatiquement : relisez, fusionnez, puis `.\Catalogue\signer.ps1`.
