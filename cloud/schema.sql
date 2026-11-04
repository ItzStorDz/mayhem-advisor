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

-- Opiniones, ideas y fallos que la gente escribe en la app (ventana de después de la partida o pestaña «Sugerencias»).
CREATE TABLE IF NOT EXISTS feedback (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  received TEXT NOT NULL,       -- ISO, UTC
  app TEXT NOT NULL,
  lang TEXT NOT NULL,
  os TEXT NOT NULL,
  kind TEXT NOT NULL,           -- idea / bug / other
  rating INTEGER,               -- 1-5 estrellas (opcional)
  source TEXT NOT NULL,         -- postgame (ventana tras la partida) / app (pestaña «Sugerencias»)
  text TEXT NOT NULL,
  context TEXT                  -- JSON, si la adjuntó: su última partida (campeón, aumentos, objetos, minutos)
);
CREATE INDEX IF NOT EXISTS feedback_received ON feedback (received);

CREATE TABLE IF NOT EXISTS seen_errors (m TEXT PRIMARY KEY, first TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS limits (key TEXT PRIMARY KEY, day TEXT NOT NULL, n INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
