# Centre PC

**Ton PC Windows, en un seul endroit.** Gratuit, libre, en français.

<a href="https://apps.microsoft.com/detail/9PP71HDT2X5S?cid=github&mode=direct"><img src="https://get.microsoft.com/images/fr%20dark.svg" width="200" alt="Télécharger Centre PC depuis le Microsoft Store"/></a>

- **Applications** : toutes tes applis, triées automatiquement par thème, avec une recherche instantanée et un lanceur au clavier.
- **Bureau** : un clic pour ranger ton bureau, et tout se remet comme avant en un clic.
- **Documents** : tes fichiers rangés dans Documents, Images, Musique et Vidéos ; rien n'est supprimé, tout s'annule.
- **Mises à jour** : applications, Windows, pilotes, Microsoft Store et antivirus en une fois ; carte mère, BIOS et microcode vérifiés.
- **Périphériques** : clavier, souris, son, écrans, disques et mémoire en direct, avec des corrections réversibles et des outils de test.
- **Bilan du PC** : santé, performances, stabilité et températures, avec un rapport pour un client ou un technicien.

## Installation

Le plus simple : installe Centre PC depuis le [Microsoft Store](https://apps.microsoft.com/detail/9PP71HDT2X5S?cid=github) (voir plus bas). Sinon, version GitHub :

1. Télécharge **CentrePC.exe** depuis la [dernière version](https://github.com/hardy-kevin/Centre-PC/releases/latest).
2. Lance-le et accepte l'installation (raccourcis + entrée dans « Applications installées »).
3. Si Windows affiche un écran bleu « Windows a protégé votre ordinateur » : clique sur **Informations complémentaires** puis **Exécuter quand même** (l'application n'est pas encore signée numériquement).

Rien d'autre à installer : tout est dans un seul fichier (Windows 10 et 11, 64 bits).

## Sur le Microsoft Store

Centre PC est disponible sur le [Microsoft Store](https://apps.microsoft.com/detail/9PP71HDT2X5S?cid=github) : version signée par Microsoft, sans écran d'avertissement à l'installation, mises à jour automatiques par le Store. En ligne de commande : `winget install 9PP71HDT2X5S`.

Pour respecter les règles du Store, cette édition n'installe aucun logiciel tiers : la température du processeur y est mesurée si tu ouvres toi-même HWiNFO ou LibreHardwareMonitor, et les « outils d'expert » n'y figurent pas.

Centre PC te plaît ? Une note sur le Store et un mot à ton entourage l'aident à se faire connaître.

## Mises à jour de Centre PC

Centre PC vérifie au démarrage si une nouvelle version est publiée ici et propose de l'installer.
Si tu choisis « Plus tard », tu peux la faire à tout moment dans **Réglages > Centre PC**.

## Sans connexion internet

Tout fonctionne hors connexion (applications, bureau, icônes, réglages). Seuls la recherche en ligne du type des applis, les mises à jour et la vérification de nouvelles versions attendent le retour d'internet.

## Confidentialité

Centre PC ne collecte aucune donnée personnelle. Pour identifier une appli inconnue, seul son **nom** est envoyé à des catalogues publics ; cette option se désactive dans Réglages. Des retours anonymes (erreurs, catégories corrigées) peuvent être partagés **uniquement si tu l'acceptes** : c'est désactivé par défaut. Détail complet : [CONFIDENTIALITE.md](CONFIDENTIALITE.md).

Voir aussi : [conditions d'utilisation](CONDITIONS.md) · [usage professionnel](USAGE-PROFESSIONNEL.md).

## Déployer sur plusieurs postes

Dans **Réglages**, utilise « Exporter la configuration », renomme le fichier `CentrePC.config.json` et place-le à côté de `CentrePC.exe` : il sera appliqué au premier lancement sur chaque poste.

## Désinstaller

Paramètres Windows > Applications installées > Centre PC > Désinstaller. Les icônes rangées reviennent sur le bureau et les icônes d'origine sont restaurées.

## Licence

© 2026 Kevin Hardy — logiciel libre et gratuit sous licence [GNU GPL version 3](LICENSE) : chacun peut l'utiliser (y compris dans une activité professionnelle), l'étudier, le modifier et le redistribuer ; toute version modifiée distribuée doit rester libre, sous la même licence, avec son code source.

## Signature et vérification

La version GitHub (`CentrePC.exe`) n'est pas signée numériquement : un certificat de signature est payant, et Centre PC est un projet gratuit. C'est ce qui provoque l'écran « Windows a protégé votre ordinateur » à la première installation.

- **Sans avertissement** : la version du Microsoft Store, signée par Microsoft (en cours de certification).
- **Vérifier la version GitHub** : chaque version publiée est fabriquée par GitHub Actions à partir de ce dépôt, après les tests automatiques, et accompagnée de son empreinte SHA-256. Cette empreinte est ensuite signée par l'auteur avec une clé qui ne quitte jamais son PC (`Catalogue/versions/`). Centre PC n'installe une mise à jour que si l'empreinte et sa signature sont valides : un compte GitHub piraté ne suffirait pas à diffuser une fausse version.
- Le code est public : tout ce que fait l'application peut être relu ici.

## Pour les développeurs

C# / WPF / .NET 8. Compiler : `dotnet publish -c Release -o publish`. Tests : `dotnet test Tests\CentrePC.Tests`.

Les versions sont construites et publiées par GitHub Actions à partir d'une étiquette `vX.Y.Z`. Les dossiers `Serveur` (service des retours) et `Catalogue` (corrections signées) ont chacun leur README.
