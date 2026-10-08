# Terrarium for Vintage Story

Generates a replica of the real Earth as a Vintage Story world, inspired by Gegy's Terrarium for Minecraft (https://modrinth.com/mod/terrarium).
Clean-room port: Terrarium's (LGPL) code is not copied, only its feature list is used as reference. Vanilla game code was decompiled only to learn the worldgen contract — never copy it either.

- Target: Vintage Story **1.22.6**, .NET **10** (`net10.0`), C#.
- Mod id `terrarium`, side `Universal`, `requiredOnClient: false` (server does the work; client only gets the real latitude for sun/seasons).

## Build, deploy, test

```bash
dotnet build -c Release                      # builds and zips to %APPDATA%\VintagestoryData\Mods\terrarium_<version>.zip
dotnet run --project tests/SelfCheck         # assert-based self-check, downloads real tiles (needs internet)
```

- Game path defaults to `%USERPROFILE%\Desktop\Vintage Story`; override with `-p:VintageStoryPath=...` or env `VINTAGE_STORY`.
- Game DLLs (`VintagestoryAPI`, `VSEssentials`, `SkiaSharp`) are referenced with `Private=false` — they ship with the game, never bundle them.
- **Keep this source folder out of `VintagestoryData\Mods`**: the game treats every folder in Mods as a mod and compiles loose `.cs` files.
- **Restart the game after every rebuild.** A running game cannot reload `Terrarium.dll` ("Assembly with same name is already loaded"); the mod is then silently disabled for that session (no Earth terrain, raw lang keys in menus).
- Headless server test without touching real saves:
  `VintagestoryServer.exe --dataPath <scratch dir>` with the zip in `<scratch>\Mods` and `serverconfig.json` → `WorldConfig.WorldConfiguration = { "terrariumEnabled": true }`. Success marker in `Logs/server-main.log`: `Terrarium: generating Earth`.

## Architecture (`src/`)

| File | Role |
|---|---|
| `EarthMath.cs` | Pure math, no game types (testable): Terrarium PNG decode, Web Mercator, zoom choice, auto vertical curve, zonal climate. `EarthProjection`: block ↔ lat/lon (world center = origin, north = −Z, equirectangular, longitude wraps). |
| `ElevationSource.cs` | AWS Terrain Tiles (`s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png`), bilinear sampling, disk cache (`<data>/TerrariumCache`) + bounded memory cache. |
| `EarthTerrainGenerator.cs` | Replacement for vanilla `GenTerra` in the Terrain pass. |
| `EarthClimateLayer.cs` | Wraps `GenMaps.climateGen`: temperature/rain by latitude, keeps vanilla geologic activity byte. |
| `TerrariumModSystem.cs` | Settings, handler swap, client latitude hook, `/geotp`, `/geopos`, `/realweather`. |
| `RealWeather.cs` | Optional real-world weather from Open-Meteo: per-region fetch, WMO → vanilla pattern mapping, climate hook (server + client), network channel `terrarium-realweather`. |

Durable decisions — do not change without a reason:
- **Stay on worldType `standard`** and swap only GenTerra's delegate **in place** in `GetRegisteredWorldGenHandlers("standard").OnChunkColumnGen[Terrain]`. A custom worldType would lose every vanilla pass (strata, caves, soil, ores, vegetation, structures). `ExecuteOrder() = 1.0` so all vanilla systems (GenTerra 0, GenMaps 0.1) have registered first.
- **GenTerra output contract** downstream passes rely on: y=0 mantle; solid = `gcfg.defaultRockId` (strata replaces it later); water via `SetFluid` up to `seaLevel − 1`; set `WorldGenTerrainHeightMap`, `RainHeightMap` (water columns = `seaLevel − 1`) and `MapChunk.YMax`.
- **Never generate fake terrain on download failure.** Generated chunks are permanent and a throwing Terrain-pass handler leaves the chunk empty, so `ElevationSource` retries with backoff until data arrives (aborts only on server shutdown).
- **Near-0 m (|elev| < 0.5 m, `MaskedSeaTolerance`) at zoom ≥ 11 is water-masked sea**, not land — the masked area carries ±0.3 m noise, so an exact-0 test leaves 1-block stripes: AWS tiles drop bathymetry there for some seas (Mediterranean, Black Sea, Marmara, US east coast). `ElevationSource` fills those pixels from zoom 10 (`BathymetryZoom`), or −1 m where zoom 10 is blended with land near coasts. Any elevation < 0 gets at least one water block (`EarthTerrainGenerator.TerrainHeight`).
- **Terrarium only runs on worldType `standard`.** The Creative playstyle uses `superflat`, so a Creative world with `terrariumEnabled` silently generates flat land. In-game tests: Standard playstyle, then `/gamemode spectator` to fly.
- **Old saves stay vanilla:** `terrariumEnabled` missing from a save's world config ⇒ disabled. Verified with a scratch server. All terrain settings are `onlyDuringWorldCreate`; the `terrariumRealWeather*` settings are not (also changed at runtime via `/realweather`, stored in the save's world config).
- `worldconfig.json` **must contain `"playStyles": []`** — the singleplayer screen iterates it without a null check and crashes otherwise.
- World-config labels live in `assets/game/lang/worldconfig-<lang>.json` (keys `worldattribute-<code>`, `worldconfig-<code>-<name>`, `worldconfig-category-terrarium`); normal mod lang files are not loaded in the main menu.
- Vertical scale `auto` (default) = log curve (`EarthMath.AutoHeightBlocks`, knee 200 m) that puts the highest peak at `mapSizeY − 12`. Linear m/block options clip high terrain.

## Status (2026-10-08)

Working, user-tested in singleplayer: Earth terrain, `/geotp` (coordinates or place names via Nominatim), `/geopos`, latitude climate, Turkish + English world-config labels, real-world weather.

Real weather (`RealWeather.cs`) — durable decisions:
- Clouds/fog, wind and thunder/hail go through vanilla `WeatherSimulationRegion.SetWeatherPattern/SetWindPattern/SetWeatherEvent`; vanilla syncs these, so clients **without** the mod see them too. They are re-applied every 10 s because vanilla swaps expired patterns.
- Rain amount and temperature go through `OnGetClimate` (rendering reads `ClimateCondition.Rainfall`), which runs separately on client and server, so they only show on clients **with** the mod. Server sends data only to clients that sent `RealWeatherHello`.
- Only "now" climate queries are changed; `WorldGenValues` and past `ForSuppliedDate*` queries (crop catch-up) stay vanilla.
- Temperature = vanilla day/night + season curve plus today's real anomaly vs a 10-year normal (Open-Meteo archive), clamped ±15 °C. Game clock is not real time, so absolute real temperature is not used.
- On download failure a region keeps vanilla weather (weather is transient; the "never fake terrain" rule does not apply). Open-Meteo free API: non-commercial, <10 000 calls/day, CC BY 4.0 attribution in README.

Open items / known limitations:
- At 256 world height Grand Canyon is only ~31 blocks deep; recommend 384–512 world height. Possible feature: a "mountain compression" setting (trade clipping of >4500 m for steeper mid-altitudes).
- Rainfall is zonal only (Florida/South China come out dry). No land cover, soil, OSM, WorldClim data, no `/geotool` map, no world preview.
- Story structures use vanilla offsets from spawn and may land in the sea; vanilla `LandformMap`/`OceanMap` no longer match the terrain.
- Deep ocean floors reach y≈10; interaction with caves/lava not checked.
- Land below sea level (Dutch polders, Caspian depression) is flooded with sea water.
- Verified by Claude in-game via Computer Use (2026-10-08): Grand Canyon via `/geotp`, Marmara/Golden Horn water after the bathymetry fix.

## In-game testing with Computer Use

Claude cannot see the game window without a desktop Computer Use tool; the in-app browser and Claude in Chrome tools cannot control Vintage Story.
If Computer Use is not in the session's tool list:
1. Enable Computer Use in the Claude desktop app settings.
2. **Start a new session** — tools are attached at session start; `/reload-plugins` does not add it to a running session.
3. Open the session in this folder (`%APPDATA%\VintagestoryData\ModSources\Terrarium`) and ask for an in-game check, e.g. "start Vintage Story, create a Terrarium world, `/geotp Grand Canyon`, inspect the terrain".
Without it, fall back to the headless server test above plus screenshots from the user.

## Publishing

- Public repo: https://github.com/JustEmoBut/terrarium-vintagestory, license **MIT**.
- Keep the README credits: AWS Terrain Tiles / Mapzen (attribution required, https://registry.opendata.aws/terrain-tiles/), OpenStreetMap Nominatim (ODbL, max 1 request/s, identifying User-Agent), and Gegy's Terrarium as inspiration.
- Commit only source; `bin/`, `obj/` are ignored.
