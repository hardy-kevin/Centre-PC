# Publier Centre PC sur le Microsoft Store

Une fois sur le Store, Centre PC est **signé par Microsoft** : plus d'écran « Windows a protégé votre ordinateur »,
installation et mises à jour en un clic depuis le Store. La version GitHub (CentrePC.exe) reste disponible en parallèle.

---

## 1. Créer ton compte développeur (gratuit, une seule fois)

1. Va sur **https://storedeveloper.microsoft.com** (ou directement sur Partner Center : https://partner.microsoft.com/dashboard/registration) et choisis le compte **Individuel** (gratuit).
2. Connecte-toi avec ton compte Microsoft.
3. Microsoft vérifie ton identité (pièce d'identité + photo). Compte quelques heures à quelques jours.

## 2. Réserver le nom « Centre PC »

1. Ouvre **Partner Center** : https://partner.microsoft.com/dashboard/apps-and-games/overview
2. **Nouveau produit** > **Application MSIX ou PWA**.
3. Saisis le nom **Centre PC** et réserve-le (s'il est déjà pris : « Centre PC – Hardy » par exemple).

## 3. Récupérer les identifiants du paquet

Dans la page de l'application : **Gestion des produits** > **Identité du produit**. Note ces trois valeurs :

| Partner Center | Exemple |
|---|---|
| Package/Identity/Name | `12345KevinHardy.CentrePC` |
| Package/Identity/Publisher | `CN=A1B2C3D4-E5F6-…` |
| Package/Properties/PublisherDisplayName | `Kevin Hardy` |

## 4. Les donner à GitHub (une seule fois)

Sur GitHub : **Centre-PC** > **Settings** > **Secrets and variables** > **Actions** > onglet **Variables** > **New repository variable**.
Crée ces trois variables (ce ne sont pas des secrets : elles sont publiques dans le paquet) :

| Nom | Valeur |
|---|---|
| `STORE_IDENTITY_NAME` | Package/Identity/Name |
| `STORE_PUBLISHER` | Package/Identity/Publisher (avec le `CN=`) |
| `STORE_PUBLISHER_NAME` | Package/Properties/PublisherDisplayName |

## 5. Fabriquer le paquet

Lance **Publier.bat**. Environ 5 minutes plus tard, la nouvelle version sur GitHub (Releases) contient,
en plus de CentrePC.exe, un fichier **CentrePC-X.Y.Z.msix** : télécharge-le.

## 6. Créer la soumission dans Partner Center

Dans la page de l'application > **Démarrer la soumission**, puis remplis chaque section :

- **Tarification et disponibilité** : Gratuit, tous les marchés (ou ceux que tu veux).
- **Propriétés** :
  - Catégorie : **Utilitaires et outils**.
  - URL de politique de confidentialité : `https://github.com/hardy-kevin/Centre-PC#confidentialité`
- **Classifications par âge** : réponds au questionnaire (aucun contenu sensible : « 3+ »).
- **Packages** : dépose le fichier **.msix** téléchargé à l'étape 5.
- **Fiche du Store (français)** : description (texte ci-dessous) et au moins **une capture d'écran** de Centre PC
  (touche Windows + Maj + S, format 1366×768 ou plus).
- **Options de soumission** > **Fonctionnalités restreintes** : colle la justification ci-dessous.

Puis **Soumettre au Store**. La certification prend en général quelques jours ; Microsoft envoie un e-mail.

### Description à copier

> Centre PC réunit ton PC Windows en un seul endroit.
>
> • Applications : toutes tes applis triées automatiquement par thème (jeux, bureautique, développement, outils…), avec une recherche instantanée.
> • Bureau : range ton bureau en un clic, donne à tes icônes un style coloré par thème, et remets tout comme avant quand tu veux.
> • Mises à jour : applications, Windows, pilotes, Microsoft Store et antivirus mis à jour en une fois. Ta carte graphique, ta carte mère et ton BIOS sont détectés automatiquement.
>
> Simple, en français, et tout fonctionne même sans connexion internet.

### Justification des fonctionnalités restreintes à copier

> runFullTrust : Centre PC est une application de bureau classique (WPF). Elle range les raccourcis du bureau de l'utilisateur, personnalise leurs icônes et lance Windows Update et winget à la demande de l'utilisateur.
>
> allowElevation : certaines actions demandées explicitement par l'utilisateur nécessitent les droits administrateur, avec la fenêtre de confirmation de Windows : déplacer les raccourcis partagés entre tous les utilisateurs, installer les mises à jour Windows et masquer les flèches des raccourcis. Aucune élévation n'a lieu sans action et accord de l'utilisateur.

## 7. Les versions suivantes

1. Lance **Publier.bat** (le numéro de version augmente tout seul).
2. Télécharge le nouveau **.msix** dans la Release GitHub.
3. Partner Center > **Mettre à jour** la soumission > **Packages** : remplace le fichier > **Soumettre**.

Les utilisateurs du Store reçoivent la mise à jour automatiquement ; ceux de la version GitHub la reçoivent depuis Centre PC.

## Bon à savoir

- Dans la version Store, les raccourcis rangés et les icônes personnalisées sont gardés dans **Documents\Centre PC**
  (les applis du Store ne peuvent pas partager leur dossier AppData avec l'Explorateur, et il serait effacé à la désinstallation).
- La version Store n'utilise pas la mise à jour intégrée : c'est le Store qui s'en charge (règle du Store).
- Pense à utiliser « Tout remettre sur le bureau » et « Revenir aux icônes d'origine » avant de désinstaller la version Store,
  car le Store ne lance pas la désinstallation propre de Centre PC.
