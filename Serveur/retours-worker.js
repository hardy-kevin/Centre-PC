// Point d'entrée des retours d'expérience de Centre PC (Cloudflare Workers + base D1, offre gratuite).
//
// Principes :
//  - aucune adresse IP n'est lue ni conservée ;
//  - tout ce qui arrive est considéré comme non fiable : taille plafonnée, format strict, champs tronqués ;
//  - plafond par installation et par jour ;
//  - données brutes supprimées après 90 jours (tâche planifiée quotidienne).
//
//  - double vérification des noms de personnes (mêmes règles et mêmes listes que l'application, voir
//    Core/PersonNames.cs) : rien n'est stocké qui citerait un prénom + nom, même envoyé par une ancienne version.
//
// Liaisons attendues (voir LISEZMOI.md) : DB = base D1.

import NOMS from "./noms-donnees.js";   // produit par Outils/listes-noms.ps1 (prénoms et noms de l'Insee, éditeurs connus)

const MAX_BODY = 64 * 1024;        // octets
const MAX_EVENTS = 200;            // par envoi
const MAX_PER_DAY = 400;           // par installation et par jour
const MAX_GLOBAL_PER_DAY = 50000;  // tous envois confondus : protège la base contre un envoi massif forgé
const KEYS = {
  erreur: ["etape", "type", "message", "pile"],
  classement: ["application", "editeur", "categorie", "avant"],
  "non-reconnue": ["application", "editeur"],
  presente: ["application", "editeur", "categorie"],
  groupe: ["action", "nom", "applications"],
  "mot-cle": ["categorie", "mot", "applications"],
  raccourci: ["touches", "conflit", "defaut"],
  renommage: ["element", "extension", "propose", "type_propose", "type_final", "organisme", "date", "range"],
  dossier: ["action", "racine", "chemin", "profondeur", "vu_comme_appli", "types"],
};
const TYPES = new Set(Object.keys(KEYS));
// Champs à valeurs fixes : toute autre valeur est remplacée par une chaîne vide (rien d'inattendu n'est gardé)
const ENUMS = {
  "groupe.action": ["creation", "ajout", "retrait", "sortie-groupe-auto", "dissociation"],
  "raccourci.conflit": ["aucun", "libere", "accepte"],
  "raccourci.defaut": ["oui", "non"],
  "renommage.element": ["fichier", "dossier"],
  "renommage.propose": ["utilise", "modifie", "ignore", "aucun"],
  "renommage.organisme": ["", "garde", "retire", "sans"],
  "renommage.date": ["", "garde", "retire", "sans"],
  "renommage.range": ["oui", "non"],
  "dossier.action": ["creation", "ajout"],
  "dossier.racine": ["documents", "images", "musique", "videos", "telechargements", "bureau", "autre"],
  "dossier.vu_comme_appli": ["", "oui", "non"],
};

// ------------------------------------------------------------------ noms de personnes
const PARTICULES = new Set(["de", "du", "des", "la", "le", "les", "van", "von", "der", "den", "del", "della", "dos", "das", "da", "di", "el", "al",
  "ben", "bin", "mac", "mc", "saint", "sainte", "st", "ste"]);
const ENTREPRISE = new Set(["inc", "ltd", "llc", "llp", "plc", "sa", "sas", "sasu", "sarl", "eurl", "sci", "gmbh", "ag", "kg", "bv", "nv", "srl", "spa",
  "oy", "ab", "as", "kk", "corp", "corporation", "co", "company", "limited", "group", "groupe", "holding", "software", "logiciels", "games", "game",
  "studio", "studios", "technologies", "technology", "tech", "labs", "lab", "interactive", "entertainment", "digital", "media", "systems",
  "solutions", "services", "foundation", "fondation", "association", "project", "team", "community", "electronics", "industries",
  "international", "network", "networks"]);
// Types dont un champ cite une application, un éditeur ou un mot : rien n'est stocké sans les listes
const AVEC_NOMS = new Set(["classement", "non-reconnue", "presente", "groupe", "mot-cle"]);

const fold = (s) => (s || "").replace(/Œ/g, "OE").replace(/œ/g, "oe").replace(/Æ/g, "AE").replace(/æ/g, "ae").replace(/ß/g, "ss")
  .normalize("NFD").replace(/\p{Mn}/gu, "").toLowerCase();

let listes; // { prenoms, noms, editeurs } ou null si le fichier est absent ou illisible
async function getListes() {
  if (listes !== undefined) return listes;
  try {
    if (!NOMS) return (listes = null);
    const bin = Uint8Array.from(atob(NOMS), (c) => c.charCodeAt(0));
    const txt = await new Response(new Blob([bin]).stream().pipeThrough(new DecompressionStream("gzip"))).text();
    if (!txt.startsWith("CentrePC-noms 1\n")) return (listes = null);
    const l = { prenoms: new Set(), noms: new Set(), editeurs: new Set() };
    let cur = null;
    for (const raw of txt.split("\n")) {
      const t = raw.trim();
      if (!t) continue;
      if (t === "[prenoms]") cur = l.prenoms; else if (t === "[noms]") cur = l.noms; else if (t === "[editeurs]") cur = l.editeurs;
      else if (cur && t[0] !== "[") cur.add(t);
    }
    listes = l.prenoms.size > 1000 && l.noms.size > 1000 ? l : null;
  } catch { listes = null; }
  return listes;
}

function mots(texte) {
  const res = [];
  for (const m of (texte || "").matchAll(/\p{L}+/gu)) {
    const f = fold(m[0]).replace(/[^a-z]/g, "");
    if (f.length >= 3 && !PARTICULES.has(f)) res.push({ index: m.index, length: m[0].length, mot: f });
  }
  return res;
}
const paire = (L, a, b) => (L.prenoms.has(a) && L.noms.has(b)) || (L.noms.has(a) && L.prenoms.has(b));
function contientNom(L, texte) {
  const w = mots(texte);
  for (let i = 0; i + 1 < w.length; i++) if (paire(L, w[i].mot, w[i + 1].mot)) return true;
  return false;
}
function masquerNoms(L, texte) {
  const w = mots(texte), zones = [];
  for (let i = 0; i + 1 < w.length; i++)
    if (paire(L, w[i].mot, w[i + 1].mot)) { zones.push([w[i].index, w[i + 1].index + w[i + 1].length]); i++; }
  let t = texte;
  for (const [a, b] of zones.reverse()) t = t.slice(0, a) + "%NOM%" + t.slice(b);
  return t;
}
function editeurPersonne(L, editeur) {
  if (!editeur) return false;
  if (contientNom(L, editeur)) return true;
  const tokens = fold(editeur).match(/[a-z0-9]+/g) || [];
  const plat = tokens.join(" ");
  if (tokens.some((t) => ENTREPRISE.has(t)) || L.editeurs.has(plat) || L.editeurs.has(tokens.join(""))) return false;
  const w = tokens.filter((t) => t.length >= 3 && !PARTICULES.has(t) && !/^\d+$/.test(t));
  return w.length === 1 && (L.noms.has(w[0]) || L.prenoms.has(w[0]));
}

/** Applique les règles de noms à un événement ; renvoie null s'il ne doit pas être stocké. */
function protegerNoms(L, type, d) {
  if (AVEC_NOMS.has(type) && !L) return null;
  if (!L) {
    // Erreur reçue alors que les listes manquent : seuls le type et la pile (noms de fonctions) sont gardés
    if (type === "erreur") { d.etape = ""; d.message = ""; }
    return d;
  }
  if (d.application !== undefined && contientNom(L, d.application)) return null;
  if (type === "groupe" && d.applications.split(" | ").some((a) => contientNom(L, a))) return null;
  if (type === "mot-cle" && contientNom(L, d.mot)) return null;
  if (d.editeur !== undefined && editeurPersonne(L, d.editeur)) d.editeur = "";
  if (type === "erreur") { d.etape = masquerNoms(L, d.etape); d.message = masquerNoms(L, d.message); }
  return d;
}

const text = (v, max) => (typeof v === "string" ? v.slice(0, max) : "");
const reply = (status, body) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json; charset=utf-8" } });

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/v1/retours") return reply(404, { erreur: "introuvable" });
    if (request.method !== "POST") return reply(405, { erreur: "méthode non acceptée" });
    if (Number(request.headers.get("content-length") || 0) > MAX_BODY) return reply(413, { erreur: "trop volumineux" });

    let raw;
    try { raw = await request.text(); } catch { return reply(400, { erreur: "illisible" }); }
    if (raw.length > MAX_BODY) return reply(413, { erreur: "trop volumineux" });
    let body;
    try { body = JSON.parse(raw); } catch { return reply(400, { erreur: "format" }); }

    const installation = text(body?.installation, 32);
    if (!/^[0-9a-f]{32}$/.test(installation) || !Array.isArray(body?.evenements)) return reply(400, { erreur: "format" });

    const jourServeur = new Date().toISOString().slice(0, 10);
    const deja = await env.DB.prepare("SELECT COUNT(*) AS n FROM retours WHERE installation = ? AND recu = ?").bind(installation, jourServeur).first();
    let reste = MAX_PER_DAY - (deja?.n ?? 0);
    if (reste <= 0) return reply(429, { erreur: "plafond du jour atteint" });
    const total = await env.DB.prepare("SELECT COUNT(*) AS n FROM retours WHERE recu = ?").bind(jourServeur).first();
    if ((total?.n ?? 0) >= MAX_GLOBAL_PER_DAY) return reply(429, { erreur: "service saturé pour aujourd'hui" });

    const insert = env.DB.prepare(
      "INSERT OR IGNORE INTO retours (installation, empreinte, jour, recu, type, version, windows, donnees) VALUES (?, ?, ?, ?, ?, ?, ?, ?)");
    const L = await getListes();
    const batch = [];
    for (const e of body.evenements.slice(0, MAX_EVENTS)) {
      if (reste <= 0) break;
      const type = text(e?.Type, 20);
      const jour = text(e?.Jour, 10);
      const empreinte = text(e?.Empreinte, 16);
      if (!TYPES.has(type) || !/^\d{4}-\d{2}-\d{2}$/.test(jour) || !/^[0-9A-F]{16}$/.test(empreinte)) continue;
      const donnees = {};
      for (const k of KEYS[type]) {
        const v = text(e?.Donnees?.[k], 700);
        const allowed = ENUMS[type + "." + k];
        donnees[k] = allowed && !allowed.includes(v) ? "" : v;
      }
      if (!protegerNoms(L, type, donnees)) continue;   // cite une personne : rien n'est stocké
      batch.push(insert.bind(installation, empreinte, jour, jourServeur, type, text(e?.Version, 20), text(e?.Windows, 30), JSON.stringify(donnees)));
      reste--;
    }
    if (batch.length > 0) await env.DB.batch(batch);
    return reply(200, { recus: batch.length });
  },

  // Tâche planifiée (une fois par jour) : les données brutes ne sont pas gardées plus de 90 jours.
  async scheduled(event, env) {
    const limite = new Date(Date.now() - 90 * 24 * 3600 * 1000).toISOString().slice(0, 10);
    await env.DB.prepare("DELETE FROM retours WHERE recu < ?").bind(limite).run();
  },
};
