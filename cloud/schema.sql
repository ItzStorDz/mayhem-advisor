-- Base de datos del servidor (Cloudflare D1). Aplicar con:  npx wrangler d1 execute mayhem-advisor --remote --file schema.sql
CREATE TABLE IF NOT EXISTS matches (
  id TEXT PRIMARY KEY,          -- HMAC del id de la partida (no se puede buscar en Riot)
  received TEXT NOT NULL,       -- ISO, UTC
  app TEXT NOT NULL,
  lang TEXT NOT NULL,
  patch TEXT NOT NULL,
  champion TEXT NOT NULL,       -- el campeón de quien la subió
  win INTEGER NOT NULL,
  data TEXT NOT NULL            -- JSON: los 10 jugadores (sin nombres), duración y el diario de la app
);
CREATE INDEX IF NOT EXISTS matches_received ON matches (received);
CREATE INDEX IF NOT EXISTS matches_champion ON matches (champion, patch);

CREATE TABLE IF NOT EXISTS reports (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  received TEXT NOT NULL,
  app TEXT NOT NULL,
  lang TEXT NOT NULL,
  os TEXT NOT NULL,
  data TEXT NOT NULL            -- JSON: errores normalizados y contadores de salud
);
CREATE INDEX IF NOT EXISTS reports_received ON reports (received);

CREATE TABLE IF NOT EXISTS seen_errors (m TEXT PRIMARY KEY, first TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS limits (key TEXT PRIMARY KEY, day TEXT NOT NULL, n INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
