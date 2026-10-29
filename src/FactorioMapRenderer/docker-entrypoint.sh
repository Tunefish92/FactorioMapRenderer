#!/bin/sh
# Starts the map server as an unprivileged user that owns the data folder.
#
# A host folder bind-mounted to /data is often owned by root (Docker creates missing folders as
# root) or by the host's own user (Unraid's appdata is 99:100). Started as root, this script hands
# the folder to PUID:PGID (default 1000:1000) and then drops to that user. Started as non-root
# (docker run --user, rootless setups), it runs the app as-is and the folder must already be
# writable by that user.
#
# Layout of the data folder:
#   map/        the rendered map that is served
#   mods/       mods from the mod portal (zip files) that your saves use (optional)
#   cache/      save game source settings, fetched saves, Factorio's runs, a newer headless server
#               (only downloaded for saves newer than the one in the image), game data exported from
#               a mounted game
#   home/       home of the app user; home/.ssh holds the keys for the SSH source
#
# Optional mounts:
#   /game       a Factorio installation for Linux (Steam or standalone), read-only. Reads the saves
#               instead of the headless server in the image, and gives the map its own icons and names.
#   /saves      e.g. the saves folder of a Factorio server, for the "Folder" source.
set -e

DATA_PATH="${FMR_DATA_PATH:-/data}"

if [ -z "$FMR_ADMIN_PASSWORD" ]; then
    echo "Factorio Map Renderer: FMR_ADMIN_PASSWORD must be set (at least 8 characters)." >&2
    echo "It protects the panel that chooses the save game; visitors only see the map." >&2
    exit 1
fi

run_app() {
    cd "$DATA_PATH"
    export HOME="$DATA_PATH/home"
    mkdir -p "$DATA_PATH/mods"

    set -- "$@" --serve 8080 --bind '*' -o "$DATA_PATH/map" --cache "$DATA_PATH/cache" --game-data /app/gamedata \
        --mods "$DATA_PATH/mods" -z "${FMR_MAX_ZOOM:-9}"
    # Your own game if one is mounted, else the headless server that comes with the image.
    if [ -f /game/data/base/info.json ] || [ -f /game/factorio/data/base/info.json ]; then
        set -- "$@" --game /game
    elif [ -f /app/factorio/data/base/info.json ]; then
        set -- "$@" --game /app/factorio
    fi
    case "$FMR_DOWNLOAD_HEADLESS" in
        1|true|TRUE|yes) set -- "$@" --download-headless ;;
    esac
    # shellcheck disable=SC2086
    exec "$@" $FMR_EXTRA_ARGS
}

if [ "$(id -u)" = "0" ]; then
    PUID="${PUID:-1000}"
    PGID="${PGID:-1000}"

    # created here, before the folder is handed over: run_app below still runs as root
    mkdir -p "$DATA_PATH/home" "$DATA_PATH/mods"

    # Only walk the whole tree when the top folder isn't ours yet (first start, or PUID changed);
    # everything the app creates afterwards already has the right owner.
    if [ "$(stat -c '%u:%g' "$DATA_PATH")" != "$PUID:$PGID" ]; then
        echo "Setting owner of $DATA_PATH to $PUID:$PGID"
        chown -R "$PUID:$PGID" "$DATA_PATH"
    fi
    chown "$PUID:$PGID" "$DATA_PATH/home" "$DATA_PATH/mods"

    run_app setpriv --reuid="$PUID" --regid="$PGID" --clear-groups dotnet /app/FactorioMapRenderer.dll
fi

mkdir -p "$DATA_PATH/home"
run_app dotnet /app/FactorioMapRenderer.dll
