# Créditos y origen de los datos

Los datos viajan con la app, en la carpeta [`datos`](datos). La atribución está en
[datos/CREDITOS.txt](datos/CREDITOS.txt), que se copia junto al ejecutable en cada build.

## Datos del juego

- **CommunityDragon** (Riot Games): campeones, objetos, aumentos, iconos y precios.
  Es el espejo público de los assets del cliente de LoL. Lo descargan `GameData.cs` y `AugmentCatalog.cs`.

## Estadísticas de Mayhem

- **mayhemmeta.com** — https://mayhemmeta.com: tier, win rate, pick rate, objetos, botas,
  hechizos y aumentos por campeón en ARAM Mayhem (cola 2400).
  Archivos: `datos/mayhem-champions.json`, `datos/mayhem-champion-builds.json`,
  `datos/mayhem-augments.json`.

## Tus partidas

- Partidas de la cola 2400 (Mayhem) leídas del cliente de LoL por el `Collector`
  (`matches.jsonl`). Son tuyas y son la base de las recomendaciones del `Advisor`.

## Notas

- Cada archivo guarda el `patch` al que corresponden los números.
- Los ids de objetos y aumentos son los de Riot: cruzan sin traducción con el catálogo del juego.
- Mayhem Advisor no está avalado por Riot Games ni por el sitio de estadísticas citado.
