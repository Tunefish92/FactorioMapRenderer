# Factorio Map Renderer

**An interactive map of your Factorio save game, from a file, your server's saves folder or over SSH.**

Every planet and space platform on a zoomable map: zoomed out in the colours of the game's map view, zoomed in in
the game's own graphics and ground textures, plus the solar system with every planet and platform. The factory
(buildings, belts, pipes, rails, power), resources and patches, enemy bases, players, train stops, map tags and
production statistics, with search and click-to-inspect and the game's alt mode. One container renders the map
and serves it.

![Nauvis with its rail network, ore patches, train stops and players](https://raw.githubusercontent.com/Tunefish92/FactorioMapRenderer/main/docs/screenshots/overview.jpg)

![A factory close up, in the game's own graphics](https://raw.githubusercontent.com/Tunefish92/FactorioMapRenderer/main/docs/screenshots/factory.jpg)

![A space platform](https://raw.githubusercontent.com/Tunefish92/FactorioMapRenderer/main/docs/screenshots/platform.jpg)

Source, full documentation and issues: **https://github.com/Tunefish92/FactorioMapRenderer**

## Quick start with Docker Compose

Save this as `docker-compose.yml`, set a password, and run `docker compose up -d`:

```yaml
services:
  map:
    image: tunefish92/factoriomaprenderer:latest
    restart: unless-stopped
    environment:
      # Required, at least 8 characters. A "$" in the password must be written as "$$" here.
      FMR_ADMIN_PASSWORD: change-me-please
    ports:
      - "8080:8080"
    volumes:
      - ./map-data:/data
      # Optional, read-only: your Factorio server's saves, for the "Folder" source.
      # - /path/to/factorio/saves:/saves:ro
```

Open `http://localhost:8080/`, click **Admin login** in the *Save game* tab of the side panel, and choose the
save game:

* **Browse / drag and drop** a save `.zip`.
* **Folder**: the newest save in a folder, e.g. your server's saves mounted at `/saves`.
* **SSH**: `user@host` and the path of the save file or save folder. Put the private key into
  `map-data/home/.ssh/`.
* **Game server (RCON)**, together with a folder or SSH: the running game is saved first, and a *Server* tab
  shows the server's state.

For the folder and SSH sources a **Refresh snapshot** button fetches and renders the current state again.
Visitors who are not logged in only see the map.

## Game data and your own game

The image works out of the box: it brings Factorio's free headless server, which reads the saves, and the game's
icons, names, map colours, sprites, ground textures and star map, exported from Factorio 2.1.23 with Space Age.
For a save newer than that, a newer headless server is downloaded from factorio.com once per version (switch it
off with `FMR_DOWNLOAD_HEADLESS=false`). Mods from the mod portal go into `map-data/mods/` as zip files. The
headless server in the image is x86-64 only; on ARM machines, mount a Factorio for ARM at `/game`.

Reads saves from Factorio 1.0 on; tested up to 2.1.24. Saves of newer versions most likely work and are marked as
untested on the map.

## Settings

| Variable | Default | Meaning |
|---|---|---|
| `FMR_ADMIN_PASSWORD` | – | **Required**, at least 8 characters, any characters. Protects the save game source. In a compose file write `$$` for every `$`, or pass the password with `env_file` and `format: raw`. |
| `FMR_MAX_ZOOM` | `9` | Highest rendered zoom level of the map view (5–10). Each level less renders about four times faster. |
| `FMR_DOWNLOAD_HEADLESS` | `true` | Download a newer headless server for saves newer than the one in the image; `false` never contacts factorio.com. |
| `PUID` / `PGID` | `1000` | Owner of the data folder; the app runs as this user. |

| Path | Meaning |
|---|---|
| `/data` | Rendered map, mods, settings (including the RCON password), fetched saves, a downloaded headless server. |
| `/saves` | Optional, read-only: a folder with saves, e.g. your server's, for the *Folder* source. |
| `/game` | Optional, read-only: a Factorio for Linux, read instead of the headless server in the image. |

The container speaks plain HTTP on port 8080. To reach it from the internet, put it behind a reverse
proxy that adds HTTPS, so the admin password is not sent unencrypted.

Not affiliated with or endorsed by Wube Software; the game's icons and graphics included in the image are their
property.
