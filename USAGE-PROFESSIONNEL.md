# Utiliser Centre PC dans une activité professionnelle

*Version du 22 novembre 2026. Ce document informe ; il ne remplace pas un conseil juridique.*

**Centre PC lui-même est gratuit pour tous, y compris les professionnels** (licence GNU GPL version 3) : dépannage, montage de PC, atelier, entreprise. Aucune redevance, aucune inscription.

Voici ce qu'il vous faut avoir en règle.

## 1. Licences des logiciels tiers

Centre PC peut installer temporairement des outils d'autres éditeurs. Leur licence s'applique à vous, même pour quelques minutes.

| Outil | Rôle dans Centre PC | En usage professionnel |
|---|---|---|
| **HWiNFO** (REALiX) | Température du processeur, VRM, SSD, ventilateurs pendant les tests | **Licence HWiNFO Pro requise.** La version gratuite est réservée à l'usage non commercial (14 jours d'évaluation en entreprise, d'après son manuel). |
| **LibreHardwareMonitor** | Alternative à HWiNFO | Logiciel libre (MPL 2.0) : utilisable. Ouvrez-le vous-même, Centre PC lit ses mesures automatiquement. Son pilote PawnIO est refusé par certains anti-triche de jeux. |
| **CrystalDiskInfo, CrystalDiskMark** | Outils d'expert | Éditions standard publiées comme logiciels libres (licence MIT) : utilisables ; vérifiez la licence affichée à l'installation. |
| **CPU-Z** (CPUID), **GPU-Z** (TechPowerUp) | Outils d'expert | Gratuits ; **vérifiez leurs conditions actuelles** sur le site de l'éditeur avant un usage commercial. |

Sans HWiNFO ni LibreHardwareMonitor, les tests fonctionnent quand même : carte graphique complète, bridage du processeur, stabilité. Il manque la température du processeur, des VRM et les ventilateurs, et le test de stabilité est alors limité à 30 minutes.

## 2. Chez un client

- **Accord du client** avant un bilan : les tests chargent la machine au maximum pendant plusieurs minutes à deux heures.
- **Sauvegarde** : vérifiez qu'elle existe avant toute intervention.
- **Rapport** : il contient le nom du PC, tous les logiciels installés et les correctifs de Windows. C'est une donnée du client : transmettez-le seulement à lui, ou avec son accord. Supprimez de votre matériel les rapports dont vous n'avez plus besoin.
- **Données personnelles (RGPD)** : si vous conservez des rapports nominatifs, vous êtes responsable de ce traitement (information du client, durée de conservation, sécurité).
- **Conditions et retours anonymes** : à la première ouverture, Centre PC demande d'accepter ses conditions et de répondre à la question des retours anonymes. Sur le PC d'un client, laissez-le répondre lui-même, ou refusez les retours en son nom faute d'accord explicite.
- **Outils temporaires** : Centre PC désinstalle ce qu'il a installé. Vérifiez-le en fin d'intervention (Paramètres › Applications).

## 3. Votre responsabilité de professionnel

Centre PC est fourni sans garantie (voir `CONDITIONS.md`). Le diagnostic et les conclusions remis au client restent les vôtres : les mesures aident à décider, elles ne remplacent pas votre jugement. Les valeurs GFLOPS / TFLOPS et la « stabilité » du test graphique sont propres à Centre PC ; elles ne sont pas des scores 3DMark ou OCCT.

## 4. Redistribution

Vous pouvez installer Centre PC sur les machines de vos clients et le leur laisser. Si vous distribuez une version **modifiée**, la licence GPL vous oblige à fournir son code source sous la même licence.
