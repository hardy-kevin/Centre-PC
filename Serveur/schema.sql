-- Base D1 des retours d'expérience de Centre PC. Aucune adresse IP, aucun nom de personne ni de machine.
CREATE TABLE IF NOT EXISTS retours (
  installation TEXT NOT NULL,   -- identifiant aléatoire, renouvelé chaque mois par l'application
  empreinte    TEXT NOT NULL,   -- empreinte du contenu (dédoublonnage)
  jour         TEXT NOT NULL,   -- jour de l'événement sur le PC (AAAA-MM-JJ)
  recu         TEXT NOT NULL,   -- jour de réception (AAAA-MM-JJ)
  type         TEXT NOT NULL,   -- erreur | classement
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
