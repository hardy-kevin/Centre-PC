# Centre PC

**Ton PC Windows, en un seul endroit.** Centre PC regroupe trois choses dans une seule application simple :

- **Applications** : toutes tes applis, triées automatiquement par thème (Jeux, Bureautique, Développement, Administration système…), avec une recherche instantanée.
- **Bureau** : un clic pour ranger ton bureau, des icônes personnalisées par thème, et tout se remet comme avant en un clic.
- **Mises à jour** : applications, Windows, pilotes, Microsoft Store et antivirus mis à jour en une fois. Le matériel (carte graphique AMD / NVIDIA / Intel, carte mère, BIOS) est détecté automatiquement.

## Installation

1. Télécharge **CentrePC.exe** depuis la [dernière version](https://github.com/hardy-kevin/Centre-PC/releases/latest).
2. Lance-le et accepte l'installation (raccourcis + entrée dans « Applications installées »).
3. Si Windows affiche un écran bleu « Windows a protégé votre ordinateur » : clique sur **Informations complémentaires** puis **Exécuter quand même** (l'application n'est pas encore signée numériquement).

Rien d'autre à installer : tout est dans un seul fichier (Windows 10 et 11, 64 bits).

## Bientôt sur le Microsoft Store

Une version signée par Microsoft sera disponible sur le Microsoft Store (sans écran d'avertissement à l'installation).

## Mises à jour de Centre PC

Centre PC vérifie au démarrage si une nouvelle version est publiée ici et propose de l'installer.
Si tu choisis « Plus tard », tu peux la faire à tout moment dans **Réglages > Centre PC**.

## Sans connexion internet

Tout fonctionne hors connexion (applications, bureau, icônes, réglages). Seuls la recherche en ligne du type des applis, les mises à jour et la vérification de nouvelles versions attendent le retour d'internet.

## Confidentialité

Centre PC ne collecte rien. Pour identifier une appli inconnue, seul son **nom** est envoyé à winget (outil de Microsoft) et à Wikidata. Cette option se désactive dans Réglages.

## Déployer sur plusieurs postes

Dans **Réglages > Thèmes**, exporte ta configuration, renomme le fichier `CentrePC.config.json` et place-le à côté de `CentrePC.exe` : il sera appliqué au premier lancement sur chaque poste.

## Désinstaller

Paramètres Windows > Applications installées > Centre PC > Désinstaller. Les icônes rangées reviennent sur le bureau et les icônes d'origine sont restaurées.

## Licence

© 2026 Kevin Hardy — [PolyForm Noncommercial 1.0.0](LICENSE.md) : usage non commercial libre, usage commercial sur accord de l'auteur.

## Pour les développeurs

C# / WPF / .NET 8. Compiler : `dotnet publish -c Release -o publish` (ou `Compiler.bat`).
Publier une version : `Publier.bat` (crée l'étiquette `vX.Y.Z`, GitHub Actions construit et publie `CentrePC.exe`).
