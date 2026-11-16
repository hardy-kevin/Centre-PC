// Point d'entrée des retours d'expérience de Centre PC (Cloudflare Workers + base D1, offre gratuite).
//
// Principes :
//  - aucune adresse IP n'est lue ni conservée ;
//  - tout ce qui arrive est considéré comme non fiable : taille plafonnée, format strict, champs tronqués ;
//  - plafond par installation et par jour ;
//  - données brutes supprimées après 90 jours (tâche planifiée quotidienne).
//
// Liaisons attendues (voir LISEZMOI.md) : DB = base D1.

const MAX_BODY = 64 * 1024;        // octets
const MAX_EVENTS = 200;            // par envoi
const MAX_PER_DAY = 400;           // par installation et par jour
const MAX_GLOBAL_PER_DAY = 50000;  // tous envois confondus : protège la base contre un envoi massif forgé
const TYPES = new Set(["erreur", "classement", "non-reconnue"]);
const KEYS = { erreur: ["etape", "type", "message", "pile"], classement: ["application", "categorie"], "non-reconnue": ["application", "editeur"] };

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
    const batch = [];
    for (const e of body.evenements.slice(0, MAX_EVENTS)) {
      if (reste <= 0) break;
      const type = text(e?.Type, 20);
      const jour = text(e?.Jour, 10);
      const empreinte = text(e?.Empreinte, 16);
      if (!TYPES.has(type) || !/^\d{4}-\d{2}-\d{2}$/.test(jour) || !/^[0-9A-F]{16}$/.test(empreinte)) continue;
      const donnees = {};
      for (const k of KEYS[type]) donnees[k] = text(e?.Donnees?.[k], 700);
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
