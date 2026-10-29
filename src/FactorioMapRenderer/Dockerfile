# syntax=docker/dockerfile:1

# The publish output is architecture-independent (it carries SkiaSharp's native libraries for every
# Linux architecture under runtimes/), so the build stage runs natively on the build machine. Only
# the final stage is per-architecture, which keeps multi-arch builds fast.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/FactorioMapRenderer/FactorioMapRenderer.csproj src/FactorioMapRenderer/
RUN dotnet restore src/FactorioMapRenderer/FactorioMapRenderer.csproj

COPY src/FactorioMapRenderer/ src/FactorioMapRenderer/
# The changelog is built into the app: it shows what changed after an update.
COPY CHANGELOG.md ./
# The native libraries for Windows, macOS, Android and musl are never used in this image and
# make up most of the publish output.
RUN dotnet publish src/FactorioMapRenderer/FactorioMapRenderer.csproj -c Release -o /app/publish --no-restore \
 && find /app/publish/runtimes -mindepth 1 -maxdepth 1 ! -name linux-x64 ! -name linux-arm64 ! -name linux ! -name unix -exec rm -rf {} +

# Factorio's headless server, so that a running container reads saves without fetching anything. It is free to
# download and only exists for x86-64; on other machines a game for that machine is mounted at /game instead.
FROM --platform=$BUILDPLATFORM debian:trixie-slim AS factorio
ARG TARGETARCH
ARG FACTORIO_VERSION=2.1.23
RUN mkdir -p /opt/factorio \
 && if [ "$TARGETARCH" = "amd64" ]; then \
      apt-get update && apt-get install -y --no-install-recommends curl ca-certificates xz-utils \
      && curl -fsSL "https://factorio.com/get-download/${FACTORIO_VERSION}/headless/linux64" -o /tmp/factorio.tar.xz \
      && tar -xJf /tmp/factorio.tar.xz -C /opt \
      && rm /tmp/factorio.tar.xz \
      && test -f /opt/factorio/data/base/info.json; \
    fi

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# The net.unraid.docker.* labels give the container its icon and "WebUI" link on Unraid's Docker
# page even when it isn't installed from the Community Applications template (e.g. via Compose).
LABEL org.opencontainers.image.title="Factorio Map Renderer" \
      org.opencontainers.image.description="Interactive map of your Factorio save game, from a file, a server's saves folder or over SSH" \
      org.opencontainers.image.source="https://github.com/Tunefish92/FactorioMapRenderer" \
      org.opencontainers.image.licenses="MIT" \
      net.unraid.docker.icon="https://raw.githubusercontent.com/Tunefish92/FactorioMapRenderer/main/unraid/factorio-map-renderer.png" \
      net.unraid.docker.webui="http://[IP]:[PORT:8080]/"

# - curl: container health check
# - openssh-client: the "SSH" save game source
# - xz-utils: unpacking a newer headless server, downloaded only for saves newer than the bundled one
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl openssh-client xz-utils ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data

COPY --from=build /app/publish .
COPY --from=factorio /opt/factorio/ /app/factorio/
# The game's icons, names, map colours, sprites, ground textures and star map, so the map has them
# without a game installation. They are Wube Software's assets, see gamedata/README.md.
COPY gamedata/ /app/gamedata/
COPY src/FactorioMapRenderer/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
# Strip CR in case the script was checked out with Windows line endings. The `map-renderer` command
# runs the renderer's own commands, e.g. `docker exec factorio-map map-renderer --help`.
RUN sed -i 's/\r$//' /usr/local/bin/docker-entrypoint.sh \
 && chmod 755 /usr/local/bin/docker-entrypoint.sh \
 && printf '#!/bin/sh\nexec dotnet /app/FactorioMapRenderer.dll "$@"\n' > /usr/local/bin/map-renderer \
 && chmod 755 /usr/local/bin/map-renderer

ENV FMR_DATA_PATH=/data \
    FMR_MAX_ZOOM=9 \
    FMR_DOWNLOAD_HEADLESS=true \
    PUID=1000 \
    PGID=1000

# Starts as root only long enough for docker-entrypoint.sh to give /data to PUID:PGID, then
# runs the app as that user.
VOLUME /data
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/api/health || exit 1

ENTRYPOINT ["docker-entrypoint.sh"]
