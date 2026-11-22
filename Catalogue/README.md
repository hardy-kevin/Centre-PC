# Catalogue signé

`catalogue.json` permet de corriger Centre PC chez tous les utilisateurs sans publier une nouvelle version :

- **Classement** : catégorie de certaines applications (les choix de chaque utilisateur restent prioritaires) ;
- **Desactive** : fonctions suspendues en attendant un correctif.

Ce sont des **données, jamais du code**. Centre PC ne les utilise que si la signature (`catalogue.json.sig`) correspond à l'une des clés publiques inscrites dans l'application ; un fichier modifié, ou plus ancien que le dernier reçu, est ignoré. La lecture se désactive dans Réglages.

## Signatures des versions (`versions/`)

`versions/X.Y.Z.sig` est la signature, par la même clé, du texte « CentrePC X.Y.Z <empreinte SHA-256 de CentrePC.exe> ». Elle est produite à la fin de `Publier.bat` par `Outils\signer-version.ps1`. L'édition GitHub de Centre PC ne propose et n'installe une nouvelle version que si cette signature est valide.
