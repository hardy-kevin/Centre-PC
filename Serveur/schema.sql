-- Base D1 des retours d'expérience de Centre PC. Aucune adresse IP, aucun nom de personne ni de machine.
CREATE TABLE IF NOT EXISTS retours (
  installation TEXT NOT NULL,   -- identifiant aléatoire, renouvelé chaque mois par l'application
  empreinte    TEXT NOT NULL,   -- empreinte du contenu (dédoublonnage)
  jour         TEXT NOT NULL,   -- jour de l'événement sur le PC (AAAA-MM-JJ)
  recu         TEXT NOT NULL,   -- jour de réception (AAAA-MM-JJ)
  type         TEXT NOT NULL,   -- erreur | classement | non-reconnue | presente | groupe | mot-cle | raccourci | renommage | dossier
  version      TEXT,
  windows      TEXT,
  donnees      TEXT NOT NULL,   -- JSON
  PRIMARY KEY (installation, empreinte, jour)
);
CREATE INDEX IF NOT EXISTS retours_type_recu ON retours (type, recu);

-- Vues de lecture (tableau de bord minimal, à interroger depuis la console D1)
CREATE VIEW IF NOT EXISTS erreurs_frequentes AS
  SELECT json_extract(donnees, '$.type') AS type, json_extract(donnees, '$.etape') AS etape, version,
         COUNT(*) AS occurrences, COUNT(DISTINCT installation) AS installations, MAX(recu) AS derniere
  FROM retours WHERE type = 'erreur' GROUP BY 1, 2, 3 ORDER BY installations DESC, occurrences DESC;

-- Seuil de masse : une correction de classement ne compte que si plusieurs installations distinctes la font.
CREATE VIEW IF NOT EXISTS classements_proposes AS
  SELECT json_extract(donnees, '$.application') AS application, json_extract(donnees, '$.categorie') AS categorie,
         COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'classement' GROUP BY 1, 2 HAVING installations >= 3 ORDER BY installations DESC;

-- Applications installées restées sans catégorie, vues sur plusieurs installations : candidates pour le catalogue.
CREATE VIEW IF NOT EXISTS non_reconnues AS
  SELECT json_extract(donnees, '$.application') AS application, json_extract(donnees, '$.editeur') AS editeur,
         COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'non-reconnue' GROUP BY 1, 2 HAVING installations >= 3 ORDER BY installations DESC;

-- Groupes d'applications faits à la main, sur plusieurs installations : à apprendre au regroupement automatique.
CREATE VIEW IF NOT EXISTS groupes_faits AS
  SELECT json_extract(donnees, '$.action') AS action, json_extract(donnees, '$.applications') AS applications,
         COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'groupe' GROUP BY 1, 2 HAVING installations >= 3 ORDER BY installations DESC;

-- Mots-clés ajoutés aux catégories par plusieurs installations : candidats pour les catégories d'origine.
CREATE VIEW IF NOT EXISTS mots_cles_proposes AS
  SELECT json_extract(donnees, '$.categorie') AS categorie, json_extract(donnees, '$.mot') AS mot,
         COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'mot-cle' GROUP BY 1, 2 HAVING installations >= 3 ORDER BY installations DESC;

-- Noms proposés : utilisés, corrigés ou remplacés, par type de document (qualité de la reconnaissance).
CREATE VIEW IF NOT EXISTS noms_proposes AS
  SELECT json_extract(donnees, '$.type_propose') AS type_propose, json_extract(donnees, '$.propose') AS usage,
         json_extract(donnees, '$.type_final') AS type_final, COUNT(*) AS fois, COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'renommage' GROUP BY 1, 2, 3 ORDER BY fois DESC;

-- Dossiers créés ou ajoutés à la main (noms courants seulement) : dossiers à proposer ou à mieux reconnaître.
CREATE VIEW IF NOT EXISTS dossiers_utilisateurs AS
  SELECT json_extract(donnees, '$.action') AS action, json_extract(donnees, '$.racine') AS racine, json_extract(donnees, '$.chemin') AS chemin,
         json_extract(donnees, '$.vu_comme_appli') AS vu_comme_appli, COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'dossier' GROUP BY 1, 2, 3, 4 HAVING installations >= 3 ORDER BY installations DESC;

-- Raccourcis clavier choisis pour l'accès rapide.
CREATE VIEW IF NOT EXISTS raccourcis_choisis AS
  SELECT json_extract(donnees, '$.touches') AS touches, json_extract(donnees, '$.conflit') AS conflit, COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'raccourci' GROUP BY 1, 2 ORDER BY installations DESC;

-- Applications présentes et la catégorie que Centre PC leur a donnée (vérifier le classement à grande échelle).
CREATE VIEW IF NOT EXISTS applications_presentes AS
  SELECT json_extract(donnees, '$.application') AS application, json_extract(donnees, '$.editeur') AS editeur,
         json_extract(donnees, '$.categorie') AS categorie, COUNT(DISTINCT installation) AS installations
  FROM retours WHERE type = 'presente' GROUP BY 1, 2, 3 HAVING installations >= 3 ORDER BY installations DESC;
