# Catalogue signé

`catalogue.json` permet de corriger Centre PC chez tous les utilisateurs sans publier une nouvelle version :

- **Classement** : catégorie de certaines applications (les choix de chaque utilisateur restent prioritaires) ;
- **Desactive** : fonctions suspendues en attendant un correctif.

Ce sont des **données, jamais du code**. Centre PC ne les utilise que si la signature (`catalogue.json.sig`) correspond à l'une des clés publiques inscrites dans l'application ; un fichier modifié, ou plus ancien que le dernier reçu, est ignoré. La lecture se désactive dans Réglages.
