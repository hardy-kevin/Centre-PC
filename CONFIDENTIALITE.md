# Confidentialité

*Version du 15 novembre 2026.*

**Centre PC ne collecte aucune donnée personnelle et n'envoie aucune statistique d'utilisation.** Vos réglages, l'historique, les journaux et les rapports restent sur votre PC.

## Ce qui sort de votre PC, et pourquoi

| Quand | Vers qui | Quoi | Comment le désactiver |
|---|---|---|---|
| Classement des applications non reconnues | Catalogues publics : winget, Flathub, Steam, Google Play, Wikidata, GitHub | **Uniquement le nom de l'application** recherchée | Réglages › Tri automatique › décocher « Identifier automatiquement en ligne » |
| Recherche d'une nouvelle version de Centre PC | GitHub | Aucune donnée (simple lecture de la page des versions) | Réglages › Version › décocher « Vérifier automatiquement au démarrage » |
| Mises à jour | Windows Update, winget, éditeurs des logiciels | Ce que ces services demandent pour fournir une mise à jour | Ne pas lancer de recherche de mises à jour ; désactiver la vérification quotidienne dans Réglages |
| Vérification de la connexion | Microsoft (adresse de test de Windows) | Aucune donnée | — |
| Test « Vitesse d'Internet » | Cloudflare | Données de test sans contenu | Ne pas lancer ce test |
| Liens vers le support d'un fabricant | Site du fabricant, dans votre navigateur | Le modèle de la carte mère ; pour Dell et Lenovo, le numéro de série du PC | Ne pas cliquer sur le lien |
| Installation d'un outil à votre demande (HWiNFO…) | winget et l'éditeur de l'outil | Téléchargement du logiciel | Ne pas utiliser ces outils |

Comme pour tout accès à Internet, ces services voient votre adresse IP.

## Ce qui reste sur votre PC

- Réglages et journal : `%LOCALAPPDATA%\CentrePC`.
- Rapports de bilan et instantanés : `Documents\Centre PC\Bilans`. Ils contiennent le nom du PC, le matériel, la liste des logiciels et des mises à jour installés : **relisez-les avant de les transmettre**.
- Captures d'écran : `Images\Captures d'écran` et presse-papiers.

La désinstallation de Centre PC ne supprime ni vos documents, ni vos rapports, ni vos captures.

## Retours d'expérience (à venir)

Une prochaine version pourra proposer d'envoyer des informations **anonymes** (erreurs, classement corrigé) pour améliorer le logiciel. Ce sera **désactivé tant que vous ne l'aurez pas accepté**, le détail de chaque envoi sera consultable, et cette page sera mise à jour avant.

## Vos droits et contact

Aucune donnée vous concernant n'étant détenue par l'auteur, il n'y a rien à consulter ni à effacer de son côté. Question : page « Issues » du dépôt GitHub du projet.
