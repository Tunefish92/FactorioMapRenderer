# Game data

Exported from **Factorio 2.1.23** with Space Age (base, elevated-rails, quality, recycler, space-age) with

```
dotnet run --project src/FactorioMapRenderer -c Release -- --export-game-data --game <Factorio folder>
```

| Path | What |
|---|---|
| `gamedata.json` | English names, the map view's colours, the ore stages, where each sprite lies around its entity, the ground textures' layout, the star map (places, distances, routes) |
| `icons/` | the icon of every item, building, fluid, planet, signal and quality, 64 px |
| `sprites/` | every building, belt, pipe shape, ore stage, tree, rock and nest as the game draws it, at the game's full resolution (64 px per tile), one image per direction |
| `tiles/` | the ground textures of every tile at 64 px per tile (JPEG) |
| `space/` | the planets as lit globes (1104 px, the globe 1024 px across), drawn from the 2048 × 1024 textures the game wraps around its 3D planets, with their clouds, Vulcanus' glowing lava and a thin atmosphere; the other places of the star map (512 px) and the sun |
| `logo.png` | the game's icon |

The export runs the game once with `--dump-data` for the prototypes; the pictures come from the game's graphics,
the names from its English locale. The Docker image ships this folder, so the map has all of it without a game
installation, and nothing is loaded from elsewhere.

**These files are not covered by this repository's MIT licence.** They are the property of Wube Software and are
included here only so that this fan-made tool can display save games of their game. This project is not
affiliated with or endorsed by Wube Software. If you are the rights holder and want them removed, please open an
issue.
