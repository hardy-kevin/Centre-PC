# Confidentialité

*Version du 16 novembre 2026 (retours activés).*

**Centre PC ne collecte aucune donnée personnelle.** Vos réglages, l'historique, les journaux et les rapports restent sur votre PC. Des retours anonymes peuvent être partagés **uniquement si vous l'acceptez** (voir plus bas) ; c'est désactivé par défaut.

## Ce qui sort de votre PC, et pourquoi

| Quand | Vers qui | Quoi | Comment le désactiver |
|---|---|---|---|
| Classement des applications non reconnues | Catalogues publics : winget, Flathub, Steam, Google Play, Wikidata, GitHub | **Uniquement le nom de l'application** recherchée | Réglages › Tri automatique › décocher « Identifier automatiquement en ligne » |
| Corrections de classement (catalogue signé) | GitHub | Aucune donnée (simple lecture d'un fichier public, une fois par jour au plus) | Réglages › Tri automatique › décocher « Recevoir les corrections de classement » |
| Recherche d'une nouvelle version de Centre PC | GitHub | Aucune donnée (simple lecture de la page des versions) | Réglages › Version › décocher « Vérifier automatiquement au démarrage » |
| Mises à jour | Windows Update, winget, éditeurs des logiciels | Ce que ces services demandent pour fournir une mise à jour | Ne pas lancer de recherche de mises à jour ; désactiver la vérification quotidienne dans Réglages |
| Vérification de la connexion | Microsoft (adresse de test de Windows) | Aucune donnée | — |
| Test « Vitesse d'Internet » | Cloudflare | Données de test sans contenu | Ne pas lancer ce test |
| Liens vers le support d'un fabricant | Site du fabricant, dans votre navigateur | Le modèle de la carte mère ; pour Dell et Lenovo, le numéro de série du PC | Ne pas cliquer sur le lien |
| Installation d'un outil à votre demande (HWiNFO, LibreHardwareMonitor et son pilote PawnIO, CPU-Z…) | winget, GitHub et l'éditeur de l'outil | Téléchargement du logiciel | Ne pas cocher la mesure de la température du processeur ; ne pas utiliser les outils d'expert |

Comme pour tout accès à Internet, ces services voient votre adresse IP.

## Ce qui reste sur votre PC

- Réglages et journal : `%LOCALAPPDATA%\CentrePC`.
- Rapports de bilan et instantanés : `Documents\Centre PC\Bilans`. Ils contiennent le nom du PC, le matériel, la liste des logiciels et des mises à jour installés : **relisez-les avant de les transmettre**.
- Captures d'écran : `Images\Captures d'écran` et presse-papiers.

La désinstallation de Centre PC ne supprime ni vos documents, ni vos rapports, ni vos captures.

## Retours d'expérience (facultatifs)

Dans Réglages › « Aider à améliorer Centre PC », vous pouvez accepter de partager des retours anonymes. **C'est désactivé par défaut, et refuser ne retire aucune fonction.**

Ce qui est enregistré, seulement après votre accord :

| Retour | Contenu |
|---|---|
| Erreur rencontrée par Centre PC | Type de l'erreur, étape en cours, message, noms des fonctions de Centre PC concernées |
| Catégorie corrigée à la main | Nom de l'application et catégorie choisie |
| Logiciel installé resté sans catégorie | Nom du logiciel et de son éditeur, une seule fois par logiciel. Uniquement les logiciels des dossiers de programmes de Windows ou du Microsoft Store : jamais un script, un document, un raccourci vers un site ou un programme rangé dans vos dossiers personnels |

Chaque retour porte aussi le jour (sans l'heure), la version de Centre PC et celle de Windows.

Ce qui n'est jamais enregistré : nom ou contenu de vos fichiers, dossiers personnels, nom d'utilisateur, nom du PC, adresses e-mail. Les chemins de fichiers sont retirés avant tout enregistrement ; seuls ceux de Windows et des programmes installés sont gardés.

- **Vous voyez tout** : « Voir ce qui serait envoyé » ouvre la file d'attente (`%LOCALAPPDATA%\CentrePC\retours.json`), telle qu'elle partirait.
- **Vous effacez tout** : « Tout effacer », ou décochez la case (la file et l'identifiant sont supprimés).
- **Envoi** : une fois par jour au plus, si vous avez coché la case, vers un service de l'auteur hébergé chez Cloudflare (`centre-pc-retours.centre-pc.workers.dev`). Ce service ne conserve pas l'adresse IP, n'accepte que les champs décrits ci-dessus et supprime les données après 90 jours. Son code est public (dossier `Serveur` du dépôt). Une fois envoyés, les retours sont retirés de la file.
- **Identifiant** : un identifiant aléatoire, sans lien avec vous ni avec votre PC, renouvelé chaque mois, sert seulement à éviter les doublons et à effacer vos retours si vous le demandez.

## Vos droits et contact

Si vous n'avez pas coché « Partager des retours anonymes », aucune donnée vous concernant n'est détenue par l'auteur. Si vous l'avez fait et souhaitez l'effacement de vos retours, indiquez l'identifiant `FeedbackInstallId` de `%LOCALAPPDATA%\\CentrePC\\config.json` : tout ce qui lui est associé sera supprimé. Question : page « Issues » du dépôt GitHub du projet.
