/* Factorio map viewer. Data comes from map-data.js (window.MAP_DATA) and surfaces/N.js (window.MAP_SURFACES[N]). */
(async () => {
  'use strict';

  const D = window.MAP_DATA;

  // The files of a render are asked for with its build, so the browser keeps them instead of asking the server
  // again for each one; a new render has a new build.
  const versioned = url => D.build ? `${url}?v=${D.build}` : url;
  if (D.icons) D.icons = D.icons.map(versioned);

  // Before the first save is loaded there is nothing to show; source.js fills in what to do next.
  if (D.empty) {
    document.body.classList.add('empty');
    document.getElementById('placeholder').hidden = false;
  }

  const store = {
    get(key, fallback) {
      try { const v = localStorage.getItem('fmr:' + key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
    },
    set(key, value) {
      try { localStorage.setItem('fmr:' + key, JSON.stringify(value)); } catch { /* storage unavailable */ }
    },
  };

  function esc(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
  const fmt = n => Math.round(n).toLocaleString('en-US');
  const coord = v => Math.round(v).toLocaleString('en-US');

  /** Names as the game shows them: "[item=iron-plate]" becomes the icon, colour and font tags are dropped. */
  function rich(text) {
    return esc(text)
      .replace(/\[(item|fluid|entity|virtual-signal|planet|space-location|recipe|technology|quality)=([^\]]+)\]/g, (tag, kind, id) => {
        const index = (D.rich || {})[`${kind}=${id}`];
        return index !== undefined && D.icons[index] ? `<img class="rich-icon" src="${D.icons[index]}" alt="${esc(id)}">` : '';
      })
      .replace(/\[\/?(color|font)(=[^\]]*)?\]/g, '')
      .trim();
  }
  /** The same, as plain text: for search and the page title. */
  const plain = text => String(text).replace(/\[[a-z-]+=([^\],]+)[^\]]*\]/g, '').replace(/\[\/?(color|font)(=[^\]]*)?\]/g, '').replace(/\s+/g, ' ').trim();
  window.factorioRich = rich;

  // Status bar: which save this is and when it was written; source.js adds where it came from.
  if (!D.empty) {
    document.getElementById('status-session').textContent = D.title;
    const saveName = (D.fileName || '').replace(/\.zip$/i, '');
    if (saveName && saveName !== D.title) document.getElementById('status-save').textContent = saveName;
    if (D.savedAt) {
      const saved = new Date(D.savedAt);
      const time = document.getElementById('status-time');
      time.textContent = 'Saved ' + saved.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
      time.title = saved.toISOString().replace('T', ' ').replace(/:\d\d\.000Z$/, ' UTC');
    }
    document.getElementById('statusbar').hidden = false;
  }

  // With game data the game's own icon is the logo; the bundled one is the fallback.
  if (D.logo) {
    document.querySelector('.placeholder-logo').src = D.logo;
    for (const logo of document.querySelectorAll('.load-logo')) logo.src = D.logo;
    document.querySelector('#sidebar header .logo').src = D.logo;
    const icon = document.querySelector('link[rel=icon]');
    icon.href = D.logo;
    icon.type = 'image/png';
  }

  // Tabs of the side panel
  const tabs = [...document.querySelectorAll('#tabs [data-tab]')];
  function showTab(name) {
    if (!tabs.some(t => t.dataset.tab === name && !t.hidden)) name = 'layers';
    for (const tab of tabs) tab.setAttribute('aria-selected', tab.dataset.tab === name);
    for (const panel of document.querySelectorAll('.tab-panel')) panel.hidden = panel.dataset.panel !== name;
    document.getElementById('sidebar-content').scrollTop = 0;
    store.set('tab', name);
  }
  for (const tab of tabs) tab.addEventListener('click', () => showTab(tab.dataset.tab));
  window.factorioShowTab = showTab;
  // Without a map there is only one thing to do: choose a save game.
  showTab(D.empty ? 'save' : store.get('tab', 'layers'));

  // Options that do not need a map
  const themeSelect = document.getElementById('opt-theme');
  themeSelect.value = store.get('theme', 'system');
  themeSelect.addEventListener('change', () => {
    store.set('theme', themeSelect.value);
    if (themeSelect.value === 'system') delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = themeSelect.value;
  });

  // Sidebar toggle
  let map = null;
  const toggleSidebar = collapsed => {
    document.body.classList.toggle('collapsed', collapsed);
    setTimeout(() => map?.invalidateSize(), 220);
  };
  document.getElementById('toggle-sidebar').addEventListener('click', () => toggleSidebar(true));
  document.getElementById('show-sidebar').addEventListener('click', () => toggleSidebar(false));
  // On a phone the panel covers the map: it starts closed, and closes again once something on the map was chosen.
  const narrow = () => window.matchMedia('(max-width: 700px), (max-height: 500px)').matches; // a phone, upright or on its side
  if (narrow()) toggleSidebar(true);
  const backToMap = () => { if (narrow() && !document.body.classList.contains('collapsed')) toggleSidebar(true); };
  // popups as wide as there is room for
  const popupWidth = () => Math.min(320, window.innerWidth - 56);

  document.getElementById('summary').innerHTML =
    (D.summary || []).map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v)}</td></tr>`).join('');

  if (D.empty || !D.surfaces.length) return;

  document.title = `${D.title} – Factorio Map`;
  document.getElementById('title').textContent = D.title;

  // ---------------------------------------------------------------- the map

  // Game coordinates map directly to Leaflet: lat = game y (south), lng = game x (east). At zoom z a game tile
  // is 2^z / 32 pixels wide, on every surface.
  const f = D.pixelsPerTileAtZoom0;
  // zoom 11 shows the game's graphics at their own resolution, 64 px per tile; 12 and 13 enlarge them
  const MAX_ZOOM = Math.max(D.maxZoom + 2, 13);
  const crs = L.extend({}, L.CRS.Simple, { transformation: new L.Transformation(f, 0, f, 0) });
  map = L.map('map', {
    crs,
    preferCanvas: true,
    minZoom: -4,
    maxZoom: MAX_ZOOM,
    zoomSnap: 0.5,
    zoomDelta: 0.5,
    wheelPxPerZoomLevel: 90,
  });
  window.factorioMap = map; // for automated viewer tests and console debugging

  // Surfaces zoom in half steps, so that their tiles are drawn at or close to their own size. The star map has no
  // tiles: there the zoom glides without steps. Each turn of the wheel or move on the touchpad moves where the zoom
  // is heading, and the map follows it frame by frame, around the point under the pointer.
  const ZOOM_FEEL = {
    surface: { zoomSnap: 0.5, zoomDelta: 0.5 },
    space: { zoomSnap: 0, zoomDelta: 0.25 },
  };
  const WHEEL_PX_PER_ZOOM = 300, GLIDE_MS = 110;
  let glide = null; // { target, point, last, frame }
  function setZoomFeel(kind) {
    Object.assign(map.options, ZOOM_FEEL[kind]);
    if (kind === 'space') map.scrollWheelZoom.disable(); else map.scrollWheelZoom.enable();
    if (glide) { cancelAnimationFrame(glide.frame); glide = null; }
  }
  function glideStep(now) {
    const z = map.getZoom(), k = 1 - Math.exp(-Math.max(0, now - glide.last) / GLIDE_MS); // a frame's time can be older than the wheel's
    glide.last = now;
    const next = Math.abs(glide.target - z) < 0.002 ? glide.target : z + (glide.target - z) * k;
    map.setZoomAround(glide.point, next, { animate: false });
    if (next === glide.target) glide = null; else glide.frame = requestAnimationFrame(glideStep);
  }
  map.getContainer().addEventListener('wheel', e => {
    if (current?.surface.kind !== 'space') return;
    e.preventDefault();
    const point = map.mouseEventToContainerPoint(e);
    const target = Math.min(map.getMaxZoom(), Math.max(map.getMinZoom(), (glide ? glide.target : map.getZoom()) + L.DomEvent.getWheelDelta(e) * (e.ctrlKey ? 4 : 1) / WHEEL_PX_PER_ZOOM)); // a pinch: small steps
    if (glide) Object.assign(glide, { target, point });
    else {
      glide = { target, point, last: performance.now() };
      glide.frame = requestAnimationFrame(glideStep);
    }
  }, { passive: false });
  map.attributionControl.setPrefix('<a href="https://leafletjs.com">Leaflet</a>');
  map.attributionControl.addAttribution(`Rendered from <b>${esc(D.fileName)}</b>`);

  const RADIUS = { players: 9, vehicles: 6, landmarks: 8, stations: 6, tags: 6, patches: 6, nests: 6 };
  // Markers with a game icon are drawn larger so the icon stays recognisable.
  const ICON_RADIUS = { players: 12, vehicles: 11, landmarks: 13, stations: 10, tags: 11, patches: 11, nests: 11 };
  const LABELLED = new Set(['players', 'stations', 'tags']);

  // Game icons are decoded up front so the canvas can draw them immediately.
  const iconImages = (D.icons || []).map(src => {
    const img = new Image();
    img.src = src;
    return img;
  });
  await Promise.all(iconImages.map(img => img.decode().catch(() => { /* missing icon: marker falls back to its disc */ })));

  // Markers grow with the zoom level: small in the overview, easy to recognise when zoomed in.
  const markerScale = zoom => Math.min(2.2, Math.max(1, 1 + (zoom - 6) * 0.3));
  const ScaledMarker = L.CircleMarker.extend({
    _project() {
      this._radius = this.options.radius * markerScale(this._map.getZoom());
      L.CircleMarker.prototype._project.call(this);
    },
  });

  /** Canvas marker: dark disc, optional coloured ring (player colour), game icon. */
  const IconMarker = ScaledMarker.extend({
    _updatePath() {
      const renderer = this._renderer;
      if (!renderer._drawing || this._empty()) return;
      const ctx = renderer._ctx, p = this._point.round(), o = this.options, r = this._radius;
      ctx.save();
      ctx.globalAlpha = o.dimmed ? 0.5 : 1;
      ctx.beginPath();
      ctx.arc(p.x, p.y, r, 0, Math.PI * 2);
      ctx.fillStyle = 'rgba(16, 20, 26, 0.82)';
      ctx.fill();
      ctx.lineWidth = o.ring ? 2.5 : 1.2;
      ctx.strokeStyle = o.ring || 'rgba(255, 255, 255, 0.55)';
      ctx.stroke();
      const img = iconImages[o.iconIndex];
      if (img && img.complete && img.naturalWidth) {
        const s = r * 1.5;
        ctx.imageSmoothingQuality = 'high';
        ctx.drawImage(img, p.x - s / 2, p.y - s / 2, s, s);
      }
      ctx.restore();
    },
  });

  function popupHtml(name, subtitle, x, y, details, iconIndex = -1) {
    const rows = (details || []).map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v)}</td></tr>`).join('');
    const icon = iconIndex >= 0 && D.icons[iconIndex] ? `<img class="popup-icon" src="${D.icons[iconIndex]}" alt="">` : '';
    return `${icon}<h3>${rich(name)}</h3><div class="sub">${esc(subtitle)}<br>x ${coord(x)} · y ${coord(y)}</div>${rows ? `<table>${rows}</table>` : ''}`;
  }

  const SparseTiles = L.GridLayer.extend({
    initialize(path, set, options) {
      L.GridLayer.prototype.initialize.call(this, options);
      this.path = path;
      this.available = new Set(set.list ? set.list.split(' ') : []);
      this.ext = set.ext || 'png';
    },
    createTile(coords, done) {
      const key = `${coords.z}/${coords.x}/${coords.y}`;
      if (!this.available.has(key)) {
        const empty = document.createElement('div');
        setTimeout(() => done(null, empty), 0);
        return empty;
      }
      const img = document.createElement('img');
      img.alt = '';
      img.decoding = 'async';
      img.onload = () => done(null, img);
      img.onerror = () => { img.style.visibility = 'hidden'; done(null, img); }; // no broken-image icon
      img.src = versioned(`${this.path}/${key}.${this.ext}`);
      return img;
    },
  });

  // ---------------------------------------------------------------- the game's own graphics, close up

  // Drawn here in the browser from the game's own files (sprites at 64 px per tile, ground textures), so they are as
  // sharp as in the game at every zoom and on every screen. The renderer only says what lies where, per region of
  // 256 × 256 tiles (surfaces/N/X_Y.js), loaded as the regions come into view.
  const pending = new Map();      // "surface/region" → resolve of its promise
  const regionCache = new Map();  // "surface/region" → promise of the region
  window.FMR_REGION = (surface, key, region) => {
    const resolve = pending.get(`${surface}/${key}`);
    pending.delete(`${surface}/${key}`);
    if (resolve) resolve(region);
  };
  function loadRegion(surface, closeUp, key) {
    const id = `${surface}/${key}`;
    if (!closeUp.regionSet.has(key)) return Promise.resolve(null);
    if (!regionCache.has(id)) {
      regionCache.set(id, new Promise(resolve => {
        pending.set(id, region => {
          if (region) {
            const raw = atob(region.g);
            region.ground = new Uint8Array(raw.length);
            for (let i = 0; i < raw.length; i++) region.ground[i] = raw.charCodeAt(i);
          }
          resolve(region);
        });
        const script = document.createElement('script');
        script.src = versioned(`surfaces/${surface}/${key}.js`);
        script.onerror = () => { pending.delete(id); resolve(null); };
        document.head.appendChild(script);
      }));
    }
    return regionCache.get(id);
  }

  const imageCache = new Map();
  const loadImage = url => {
    if (!imageCache.has(url)) {
      imageCache.set(url, new Promise(resolve => {
        const img = new Image();
        img.onload = () => resolve(img);
        img.onerror = () => resolve(null);
        img.src = versioned(url);
      }));
    }
    return imageCache.get(url);
  };

  // The tiles are drawn away from the page, by a few workers at once (closeup-worker.js), so that zooming and
  // panning stay smooth; a browser whose workers cannot draw has the page draw them (closeup-draw.js either way).
  const drawPool = (() => {
    if (typeof Worker !== 'function' || typeof OffscreenCanvas !== 'function' || !('transferToImageBitmap' in OffscreenCanvas.prototype)
      || location.protocol === 'file:') return null;
    const workers = [], jobs = new Map(), surfaces = new Set();
    let next = 1;
    try {
      for (let i = 0; i < Math.max(1, Math.min(3, (navigator.hardwareConcurrency || 2) - 1)); i++) {
        const worker = new Worker(versioned('closeup-worker.js'));
        worker.load = 0;
        worker.onmessage = ({ data }) => {
          const job = jobs.get(data.id);
          if (!job) { data.bitmap?.close(); return; } // no longer needed
          jobs.delete(data.id);
          worker.load--;
          if (data.bitmap) job.resolve(data.bitmap); else job.reject(new Error(data.error));
        };
        worker.postMessage({ type: 'icons', icons: D.icons || [] });
        workers.push(worker);
      }
    } catch {
      return null;
    }
    return {
      /** Resolves to the tile as a bitmap; the least busy worker draws it. */
      draw(job, surface = null, closeUp = null) {
        if (surface && !surfaces.has(surface.id)) {
          surfaces.add(surface.id);
          const { regionSet, ...plain } = closeUp; // what a worker needs, without what the page added
          for (const worker of workers) worker.postMessage({ type: 'surface', id: surface.id, closeUp: plain });
        }
        const worker = workers.reduce((a, b) => (b.load < a.load ? b : a));
        const id = next++;
        worker.load++;
        const promise = new Promise((resolve, reject) => jobs.set(id, { resolve, reject }));
        worker.postMessage({ type: 'draw', id, surface: surface?.id, ...job });
        return { id, worker, promise };
      },
      /** A tile that has left the view before it was drawn. */
      cancel(handle) {
        if (!jobs.delete(handle.id)) return;
        handle.worker.load--;
        handle.worker.postMessage({ type: 'cancel', id: handle.id });
      },
    };
  })();

  const CloseUp = L.GridLayer.extend({
    initialize(surface, closeUp, kind, options) {
      L.GridLayer.prototype.initialize.call(this, options);
      this.surface = surface;
      this.closeUp = closeUp;
      this.kind = kind;
      closeUp.regionSet ??= new Set(closeUp.regions ? closeUp.regions.split(' ') : []);
      if (drawPool) this.on('tileunload', e => e.tile.drawing && drawPool.cancel(e.tile.drawing));
    },
    createTile(coords, done) {
      const size = this.getTileSize(), ratio = Math.min(window.devicePixelRatio || 1, 2);
      const tile = document.createElement('canvas');
      tile.width = size.x * ratio;
      tile.height = size.y * ratio;
      const perTile = 2 ** coords.z * f, span = size.x / perTile;
      const job = { kind: this.kind, x0: coords.x * span, y0: coords.y * span, span, perTile, ratio };
      const ready = () => done(null, tile);
      if (drawPool) {
        tile.drawing = drawPool.draw({ ...job, width: tile.width, height: tile.height }, this.surface, this.closeUp);
        tile.drawing.promise.then(bitmap => {
          tile.drawing = null;
          tile.getContext('bitmaprenderer').transferFromImageBitmap(bitmap);
          ready();
        }, ready);
      } else {
        window.FMR_CLOSEUP.draw(tile, { ...job, closeUp: this.closeUp }, key => loadRegion(this.surface.id, this.closeUp, key),
          loadImage, i => iconImages[i]).then(ready, ready);
      }
      return tile;
    },
  });

  /** Lines between chunks, drawn in the browser once a chunk is large enough on screen. */
  const ChunkGrid = L.GridLayer.extend({
    createTile(coords) {
      // a line along the top and left of every chunk, as the tile's background: cheaper than drawing them
      const tile = document.createElement('div'), size = this.getTileSize(), chunkPx = 2 ** coords.z * f * 32;
      if (chunkPx < 96) return tile;
      const at = v => ((-(v % chunkPx)) + chunkPx) % chunkPx;
      const line = 'rgba(255, 255, 255, 0.16)';
      tile.style.backgroundImage = `linear-gradient(to right, ${line} 1px, transparent 1px), linear-gradient(to bottom, ${line} 1px, transparent 1px)`;
      tile.style.backgroundSize = `${chunkPx}px ${chunkPx}px`;
      tile.style.backgroundPosition = `${at(coords.x * size.x)}px ${at(coords.y * size.y)}px`;
      return tile;
    },
  });
  const grid = new ChunkGrid({ zIndex: 50, maxZoom: MAX_ZOOM });

  /** The game's alt mode: over each building what it makes, mines or lets through; drawn as the close-up tiles are. */
  const AltMode = L.GridLayer.extend({
    initialize(entries, options) {
      L.GridLayer.prototype.initialize.call(this, options);
      this.cells = new Map(); // 32 × 32 tiles → entries [x, y, size, [icon…], [module icon…]]
      for (const entry of entries) {
        const key = Math.floor(entry[0] / 32) + ',' + Math.floor(entry[1] / 32);
        if (!this.cells.has(key)) this.cells.set(key, []);
        this.cells.get(key).push(entry);
      }
      if (drawPool) this.on('tileunload', e => e.tile.drawing && drawPool.cancel(e.tile.drawing));
    },
    createTile(coords, done) {
      const size = this.getTileSize(), ratio = Math.min(window.devicePixelRatio || 1, 2);
      const tile = document.createElement('canvas');
      tile.width = size.x * ratio;
      tile.height = size.y * ratio;
      const perTile = 2 ** coords.z * f, span = size.x / perTile, x0 = coords.x * span, y0 = coords.y * span, margin = 4;
      const entries = [];
      for (let cy = Math.floor((y0 - margin) / 32); cy <= Math.floor((y0 + span + margin) / 32); cy++)
        for (let cx = Math.floor((x0 - margin) / 32); cx <= Math.floor((x0 + span + margin) / 32); cx++)
          entries.push(...(this.cells.get(cx + ',' + cy) || []));
      const ready = () => done(null, tile);
      if (!entries.length) { setTimeout(ready, 0); return tile; }
      const job = { kind: 'alt', entries, x0, y0, span, perTile, ratio };
      if (drawPool) {
        tile.drawing = drawPool.draw({ ...job, width: tile.width, height: tile.height });
        tile.drawing.promise.then(bitmap => {
          tile.drawing = null;
          tile.getContext('bitmaprenderer').transferFromImageBitmap(bitmap);
          ready();
        }, ready);
      } else {
        window.FMR_CLOSEUP.drawAlt(tile, job, i => iconImages[i]).then(ready, ready);
      }
      return tile;
    },
  });

  // ---------------------------------------------------------------- surfaces

  /** Loads surfaces/N.js once; it sets window.MAP_SURFACES[N]. */
  function loadSurface(id) {
    window.MAP_SURFACES = window.MAP_SURFACES || {};
    if (window.MAP_SURFACES[id]) return Promise.resolve(window.MAP_SURFACES[id]);
    return new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = versioned(`surfaces/${id}.js`);
      script.onload = () => resolve(window.MAP_SURFACES[id]);
      script.onerror = () => reject(new Error(`surfaces/${id}.js could not be loaded`));
      document.head.appendChild(script);
    });
  }

  const visibility = store.get('layers', {});
  const layerDefs = D.layers;
  let current = null;       // { surface, data, handles, searchIndex, inspectGrid }

  /** A surface's layers, markers and what can be clicked or searched: built once, when it is first shown. */
  const builtSurfaces = new Map();
  function buildSurface(surface, data) {
    const handles = {}, searchIndex = [];
    layerDefs.forEach((layer, index) => {
      if (layer.kind === 'raster' && (layer.id === 'ground' || layer.id === 'graphics')) {
        if (!data.closeUp) return;
        handles[layer.id] = {
          leaflet: new CloseUp(surface, data.closeUp, layer.id, {
            tileSize: 256, minZoom: data.closeUp.minZoom, maxZoom: MAX_ZOOM, zIndex: index + 1, noWrap: true, className: `tiles-${layer.id}`,
            updateWhenZooming: false, // drawing is dear: only for the zoom a zoom ends at
          }),
          count: -1,
        };
        return;
      }
      if (layer.kind === 'raster') {
        const set = data.tiles[layer.id];
        if (!set) return;
        handles[layer.id] = {
          leaflet: new SparseTiles(`tiles/${surface.id}/${layer.id}`, set, {
            tileSize: 256,
            minNativeZoom: set.minNativeZoom,
            maxNativeZoom: set.maxNativeZoom,
            maxZoom: MAX_ZOOM,
            zIndex: index + 1,
            noWrap: true,
            className: `tiles-${layer.id}`,
          }),
          count: -1,
        };
        return;
      }
      const markers = data.markers[layer.id];
      if (!markers || !markers.items.length) return;
      const group = L.layerGroup();
      const subgroups = markers.legend.map(() => L.layerGroup().addTo(group));
      for (const [x, y, name, color, legendIndex, dimmed, details, iconIndex = -1, ring = ''] of markers.items) {
        const hasIcon = iconIndex >= 0 && iconImages[iconIndex]?.naturalWidth > 0;
        const radius = hasIcon ? ICON_RADIUS[layer.id] || 10 : RADIUS[layer.id] || 6;
        const marker = hasIcon
          ? new IconMarker([y, x], { radius, iconIndex, ring, dimmed: dimmed === 1, bubblingMouseEvents: false })
          : new ScaledMarker([y, x], {
            radius,
            color: ring || 'rgba(0,0,0,.65)',
            weight: ring ? 2.5 : 1,
            fillColor: layer.id === 'players' ? ring || color : color,
            fillOpacity: dimmed ? 0.45 : 0.95,
            opacity: dimmed ? 0.6 : 1,
            bubblingMouseEvents: false,
          });
        const subtitle = markers.legend[legendIndex] ? `${layer.title} · ${markers.legend[legendIndex][0]}` : layer.title;
        marker.bindPopup(() => popupHtml(name, subtitle, x, y, details, iconIndex), { maxWidth: popupWidth() });
        const label = rich(name) || esc(markers.legend[legendIndex]?.[0] || layer.title);
        if (LABELLED.has(layer.id)) {
          marker.bindTooltip(label, { permanent: true, direction: 'right', offset: [radius + 2, 0], className: `map-label permanent${layer.id === 'players' ? '' : ' minor'}` });
        }
        else marker.bindTooltip(label, { direction: 'top', offset: [0, -radius], className: 'map-label' });
        (subgroups[legendIndex] || subgroups[0] || group).addLayer(marker);
        searchIndex.push({ name: plain(name), layer, lat: y, lng: x, open: () => { ensureVisible(layer.id); marker.openPopup(); } });
      }
      handles[layer.id] = { leaflet: group, subgroups, legend: markers.legend, count: markers.items.length };
    });

    // Clickable buildings (not drawn as markers – they are part of the raster tiles).
    const cell = 32, inspectGrid = new Map();
    data.inspect.forEach((item, i) => {
      const [x, y, r] = item;
      for (let cx = Math.floor((x - r) / cell); cx <= Math.floor((x + r) / cell); cx++) {
        for (let cy = Math.floor((y - r) / cell); cy <= Math.floor((y + r) / cell); cy++) {
          const key = cx + ',' + cy;
          if (!inspectGrid.has(key)) inspectGrid.set(key, []);
          inspectGrid.get(key).push(i);
        }
      }
      const layer = layerDefs.find(l => l.title === item[4]) || layerDefs.find(l => l.id === 'buildings');
      searchIndex.push({ name: plain(item[3]), layer, lat: y, lng: x, open: () => { ensureVisible(layer.id); showInspect(item); } });
    });
    const alt = new AltMode(data.alt || [], { zIndex: 60, maxZoom: MAX_ZOOM, noWrap: true, className: 'alt-mode' });
    return { handles, searchIndex, inspectGrid, alt };
  }

  async function showSurface(surface, view) {
    if (surface.kind === 'space') return showSpace(view);
    const data = await loadSurface(surface.id);
    if (current) for (const h of Object.values(current.handles)) map.removeLayer(h.leaflet);
    map.closePopup();
    document.body.classList.remove('space-view');
    setZoomFeel('surface');
    let built = builtSurfaces.get(surface.id);
    if (!built) builtSurfaces.set(surface.id, built = buildSurface(surface, data));
    // legends hidden on the last visit are shown again, as their tick boxes are
    else for (const h of Object.values(built.handles)) for (const g of h.subgroups || []) h.leaflet.addLayer(g);
    const { handles, searchIndex, inspectGrid } = built;
    if (current?.alt) map.removeLayer(current.alt);

    current = { surface, data, handles, searchIndex, inspectGrid, alt: built.alt };
    applyAlt();
    store.set('surface', surface.name);
    buildLayerList();
    renderStatistics();
    markSurface();
    search.dispatchEvent(new Event('input'));

    if (view) map.setView([view.y, view.x], view.zoom);
    else {
      const b = surface.focus || surface.bounds;
      map.fitBounds(L.latLngBounds([b[1], b[0]], [b[3], b[2]]).pad(0.05));
    }
    const [x1, y1, x2, y2] = surface.bounds, margin = Math.max(x2 - x1, y2 - y1) * 0.3 + 64;
    map.setMaxBounds(L.latLngBounds([y1 - margin, x1 - margin], [y2 + margin, x2 + margin]));
    map.setMinZoom(surface.minZoom - 2);
    applyCloseUp();
  }

  function showInspect([x, y, , name, group, details, iconIndex = -1]) {
    L.popup({ maxWidth: popupWidth() }).setLatLng([y, x]).setContent(popupHtml(name, group, x, y, details, iconIndex)).openOn(map);
  }

  map.on('click', e => {
    if (!current) return;
    const x = e.latlng.lng, y = e.latlng.lat;
    const tolerance = 4 / (2 ** map.getZoom() * f);
    let best = null, bestScore = Infinity;
    for (const i of current.inspectGrid.get(Math.floor(x / 32) + ',' + Math.floor(y / 32)) || []) {
      const item = current.data.inspect[i];
      const layer = layerDefs.find(l => l.title === item[4]);
      if (layer && !(current.handles[layer.id] && shown(layer.id))) continue;
      const d = Math.max(Math.abs(item[0] - x), Math.abs(item[1] - y));
      if (d > item[2] + tolerance) continue;
      const score = d / (item[2] + tolerance);
      if (score < bestScore) { bestScore = score; best = item; }
    }
    if (best) showInspect(best);
  });

  // ---------------------------------------------------------------- the solar system

  // The star map: the sun in the middle, every planet and space location where the game puts it (distance from
  // the sun, direction clockwise from north), the routes between them, and every platform: in orbit around its
  // planet, or on its way along a route. A click on a planet or platform opens its map.
  const SPACE = D.space && { id: 'space', name: 'space', title: 'Solar system', kind: 'space', minZoom: -2 };
  const SPACE_SCALE = 64; // map units per unit of distance in the star map
  const spacePosition = location => {
    const angle = location.orientation * 2 * Math.PI;
    return [-location.distance * Math.cos(angle) * SPACE_SCALE, location.distance * Math.sin(angle) * SPACE_SCALE]; // [y, x]
  };

  function showSpace(view) {
    if (current) for (const h of Object.values(current.handles)) map.removeLayer(h.leaflet);
    if (current?.alt) map.removeLayer(current.alt);
    map.closePopup();
    document.body.classList.add('space-view');
    setZoomFeel('space');
    const space = D.space, group = L.layerGroup(), searchIndex = [];
    const places = new Map(space.locations.map(l => [l.name, l]));
    const spaceLayer = { title: 'Solar system', color: '#f0a03c' };
    let extent = 10;

    // routes from edge to edge: they end a little outside the planets instead of running across them
    const globeRadius = place => place.magnitude * 1.6 * SPACE_SCALE;
    for (const c of space.connections) {
      const from = places.get(c.from), to = places.get(c.to);
      if (!from || !to) continue;
      const [y1, x1] = spacePosition(from), [y2, x2] = spacePosition(to);
      const length = Math.hypot(x2 - x1, y2 - y1), gap = 0.25 * SPACE_SCALE;
      const start = globeRadius(from) + gap, end = length - globeRadius(to) - gap;
      if (end <= start) continue;
      const dx = (x2 - x1) / length, dy = (y2 - y1) / length;
      L.polyline([[y1 + dy * start, x1 + dx * start], [y1 + dy * end, x1 + dx * end]],
        { color: '#cfd8e3', weight: 1.5, opacity: 0.35, dashArray: '6 8', interactive: false }).addTo(group);
    }
    if (space.starmap_star) {
      const r = 4 * SPACE_SCALE;
      L.imageOverlay(space.starmap_star, [[-r, -r], [r, r]], { interactive: false, className: 'space-star' }).addTo(group);
    }
    for (const place of space.locations) {
      const [y, x] = spacePosition(place);
      extent = Math.max(extent, place.distance + place.magnitude * 2);
      const r = globeRadius(place);
      const surface = D.surfaces.find(s => s.id === place.surface);
      const ri = r * (place.scale || 1); // a globe's picture has room for its atmosphere
      const image = L.imageOverlay(place.image, [[y - ri, x - ri], [y + ri, x + ri]], { interactive: !!surface, className: surface ? 'space-planet visited' : 'space-planet' }).addTo(group);
      // a globe is lit from its upper left: turned so that its lit side faces the sun
      if (place.scale > 1 && (x || y)) {
        const turn = Math.atan2(-y, -x) - Math.atan2(-0.42, -0.55);
        loadImage(place.image).then(img => {
          if (!img) return;
          const canvas = document.createElement('canvas');
          canvas.width = img.naturalWidth;
          canvas.height = img.naturalHeight;
          const ctx = canvas.getContext('2d');
          ctx.translate(canvas.width / 2, canvas.height / 2);
          ctx.rotate(turn);
          ctx.drawImage(img, -canvas.width / 2, -canvas.height / 2);
          canvas.toBlob(blob => blob && image.setUrl(URL.createObjectURL(blob)));
        });
      }
      const label = L.marker([y + r, x], {
        icon: L.divIcon({ className: 'space-label', html: `<span>${esc(place.title)}</span>`, iconSize: null }), interactive: !!surface, zIndexOffset: 1000,
      }).addTo(group);
      if (surface) {
        const open = () => showSurface(surface).then(updateHash);
        image.on('click', open);
        label.on('click', open);
        searchIndex.push({ name: place.title, layer: spaceLayer, lat: y, lng: x, open });
      }
    }

    // platforms: in orbit around a place, or between the two ends of their route
    const orbiting = new Map();
    for (const p of space.platforms) {
      const surface = D.surfaces.find(s => s.id === p.surface);
      if (!surface) continue;
      let position;
      if (p.connection) {
        const c = space.connections.find(c => c.name === p.connection);
        const from = c && places.get(c.from), to = c && places.get(c.to);
        if (from && to) {
          const [y1, x1] = spacePosition(from), [y2, x2] = spacePosition(to);
          position = [y1 + (y2 - y1) * p.distance, x1 + (x2 - x1) * p.distance];
        }
      }
      if (!position && places.get(p.location)) {
        const place = places.get(p.location), n = orbiting.get(p.location) || 0;
        orbiting.set(p.location, n + 1);
        const [y, x] = spacePosition(place), r = place.magnitude * 2.4 * SPACE_SCALE, angle = n * 0.5 - 2.4;
        position = [y + Math.sin(angle) * r, x + Math.cos(angle) * r];
      }
      if (!position) continue;
      const moving = !!p.connection;
      // the platform's own icon; the star map's platform symbol is a shape the game colours itself
      const icon = surface.icon >= 0 ? D.icons[surface.icon] : null;
      const marker = L.marker(position, {
        icon: L.divIcon({ className: `space-platform${moving ? ' moving' : ''}`, html: icon ? `<img src="${icon}" alt="">` : '<span class="surface-dot"></span>', iconSize: [30, 30], iconAnchor: [15, 15] }),
      }).addTo(group);
      const where = moving ? `On its way (${Math.round(p.distance * 100)} %)` : `In orbit around ${places.get(p.location)?.title || p.location}`;
      marker.bindTooltip(`${rich(p.title)}<br><span class="muted">${esc(where)}</span>`, { direction: 'right', offset: [14, 0], className: 'map-label' });
      const open = () => showSurface(surface).then(updateHash);
      marker.on('click', open);
      searchIndex.push({ name: plain(p.title), layer: spaceLayer, lat: position[0], lng: position[1], open });
    }

    group.addTo(map);
    current = { surface: SPACE, data: { counts: [], production: [], inspect: [] }, handles: { space: { leaflet: group } }, searchIndex, inspectGrid: new Map() };
    store.set('surface', 'space');
    buildLayerList();
    renderStatistics();
    markSurface();
    search.dispatchEvent(new Event('input'));
    const r = extent * SPACE_SCALE;
    map.setMaxBounds(L.latLngBounds([-r * 1.4, -r * 1.4], [r * 1.4, r * 1.4]));
    map.setMinZoom(SPACE.minZoom);
    if (view) map.setView([view.y, view.x], view.zoom);
    else if (narrow()) {
      // a phone has little room: the planets as close as they fit, the sun at their side
      const around = space.locations.filter(l => l.planet).flatMap(l => {
        const [y, x] = spacePosition(l), m = l.magnitude * 2.4 * SPACE_SCALE;
        return [[y - m, x - m], [y + m, x + m]];
      });
      const sun = 4 * SPACE_SCALE;
      map.fitBounds(L.latLngBounds([[-sun, -sun], [sun, sun], ...around]).pad(0.08));
    } else {
      // the inner system, where the planets are; the edge and beyond are a scroll away
      const inner = Math.max(...space.locations.filter(l => l.planet).map(l => l.distance + l.magnitude * 2), 10) * SPACE_SCALE;
      map.fitBounds(L.latLngBounds([-inner, -inner], [inner, inner]));
    }
  }

  // ---------------------------------------------------------------- surface picker

  const planets = D.surfaces.filter(s => s.kind !== 'platform');
  const platforms = D.surfaces.filter(s => s.kind === 'platform');
  const surfaceIcon = s => s.icon >= 0 && D.icons[s.icon] ? `<img src="${D.icons[s.icon]}" alt="">` : '<span class="surface-dot"></span>';
  document.getElementById('surface-picker').hidden = D.surfaces.length < 2 && !SPACE;
  document.getElementById('planets').innerHTML = (SPACE
    ? `<button type="button" role="radio" data-surface="space" title="The star map with every planet and platform">`
      + `${D.space.starmap_star ? `<img src="${D.space.starmap_star}" alt="">` : ''}<span>Solar system</span></button>` : '') + planets.map(s =>
    `<button type="button" role="radio" data-surface="${s.id}" title="${esc(plain(s.title))}: ${fmt(s.chunks)} chunks charted">${surfaceIcon(s)}<span>${rich(s.title)}</span></button>`).join('');
  // On a phone the same choice lies on the map, in a row along its bottom edge; platforms open the panel's list.
  const bar = document.getElementById('surface-bar');
  bar.hidden = D.surfaces.length < 2 && !SPACE;
  bar.innerHTML = document.getElementById('planets').innerHTML
    + (platforms.length ? `<button type="button" id="surface-bar-platforms" title="Space platforms">${surfaceIcon(platforms[0])}<span>Platforms</span><span class="muted">${platforms.length}</span></button>` : '');
  document.getElementById('surface-bar-platforms')?.addEventListener('click', () => {
    document.querySelector('[data-tab="layers"]').click();
    document.getElementById('platforms').open = true;
    toggleSidebar(false);
    document.getElementById('surface-picker').scrollIntoView({ block: 'start' });
  });
  if (platforms.length) {
    const list = document.getElementById('platforms');
    list.hidden = false;
    document.getElementById('platform-count').textContent = platforms.length;
    document.getElementById('platform-items').innerHTML = platforms.map(s =>
      `<button type="button" role="radio" data-surface="${s.id}">${surfaceIcon(s)}<span class="name">${rich(s.title)}</span>`
      + `<span class="muted">${s.location ? esc(s.location) : ''}</span></button>`).join('');
    list.open = store.get('platformsOpen', false);
    list.addEventListener('toggle', () => store.set('platformsOpen', list.open));
  }
  for (const button of document.querySelectorAll('[data-surface]')) {
    button.addEventListener('click', () => {
      const surface = button.dataset.surface === 'space' ? SPACE : D.surfaces.find(s => s.id === +button.dataset.surface);
      backToMap();
      if (surface && surface !== current?.surface) showSurface(surface).then(updateHash);
    });
  }
  function markSurface() {
    for (const button of document.querySelectorAll('[data-surface]')) {
      const on = button.dataset.surface === String(current.surface.id);
      button.setAttribute('aria-checked', on);
      if (on && button.closest('#surface-bar')) button.scrollIntoView({ block: 'nearest', inline: 'center' });
      if (on && button.closest('#platforms')) document.getElementById('platforms').open = true;
    }
  }

  // ---------------------------------------------------------------- layer list

  function setLayer(id, on) {
    visibility[id] = on;
    store.set('layers', visibility);
    const h = current?.handles[id];
    // the flat layers that give way close up are added by applyCloseUp, below
    if (h) { if (!on) map.removeLayer(h.leaflet); else if (!DRAWN_CLOSE_UP.includes(id)) h.leaflet.addTo(map); }
    const box = document.querySelector(`input[data-layer="${id}"]`);
    if (box) box.checked = on;
    refreshGroups();
    applyCloseUp();
  }

  // Close up, the game's graphics draw everything of these layers. The flat map-view squares under them would only
  // show through the gaps between the buildings, as coloured backgrounds, enlarged and blurred: they give way there,
  // and are not even loaded. Whether a layer is shown is still the tick in its box.
  const DRAWN_CLOSE_UP = ['resources', 'nature', 'enemies', 'buildings', 'belts', 'pipes', 'rails', 'power'];
  const shown = id => visibility[id] ?? layerDefs.find(l => l.id === id)?.visible;
  /** @param zoom the zoom the map is at, or heading for when a zoom begins */
  function applyCloseUp(zoom = map.getZoom()) {
    if (!current) return;
    const graphics = current.handles.graphics;
    const closeUp = !!current.data.closeUp && !!graphics && map.hasLayer(graphics.leaflet) && zoom >= current.data.closeUp.minZoom;
    for (const id of DRAWN_CLOSE_UP) {
      const h = current.handles[id];
      if (!h) continue;
      if (shown(id) && !closeUp) { if (!map.hasLayer(h.leaflet)) h.leaflet.addTo(map); }
      // a layer added before the map has its first view is only set up then: it can only be taken off after that
      else if (map.hasLayer(h.leaflet)) { if (h.leaflet._map) map.removeLayer(h.leaflet); else h.leaflet.once('add', () => applyCloseUp()); }
    }
  }
  map.on('zoomanim', e => applyCloseUp(e.zoom)); // before the layers load tiles for a zoom they give way at
  map.on('zoomend', () => applyCloseUp());

  function ensureVisible(id) {
    if (current.handles[id] && !shown(id)) setLayer(id, true);
  }

  /** Group headers: how many of the group's layers are shown, and a tick box for all of them at once. */
  function refreshGroups() {
    for (const group of document.querySelectorAll('#layers .group')) {
      const boxes = [...group.querySelectorAll('input[data-layer]:not(:disabled)')];
      const shown = boxes.filter(b => b.checked).length;
      group.querySelector('.group-count').textContent = boxes.length ? `${shown}/${boxes.length}` : '';
      const all = group.querySelector('.group-all');
      all.checked = shown > 0;
      all.indeterminate = shown > 0 && shown < boxes.length;
    }
  }

  const layersElement = document.getElementById('layers');
  const closedGroups = new Set(store.get('closedGroups', []));

  function buildLayerList() {
    layersElement.innerHTML = '';
    let container = null, currentGroup = null;
    for (const layer of layerDefs) {
      const h = current.handles[layer.id];
      if (layer.group !== currentGroup) {
        currentGroup = layer.group;
        const group = document.createElement('details');
        group.className = 'group';
        group.open = !closedGroups.has(currentGroup);
        group.innerHTML = `<summary><input type="checkbox" class="group-all" title="Show or hide all ${esc(currentGroup.toLowerCase())} layers" aria-label="Show or hide all ${esc(currentGroup)} layers">`
          + `<span class="group-name">${esc(currentGroup)}</span><span class="group-count"></span></summary><div class="group-body"></div>`;
        layersElement.appendChild(group);
        container = group.querySelector('.group-body');
        const name = currentGroup;
        group.addEventListener('toggle', () => {
          if (group.open) closedGroups.delete(name); else closedGroups.add(name);
          store.set('closedGroups', [...closedGroups]);
        });
        group.querySelector('.group-all').addEventListener('click', e => e.stopPropagation()); // do not fold the group
        group.querySelector('.group-all').addEventListener('change', e => {
          const on = e.target.checked; // read once: every setLayer below re-evaluates this very box
          for (const box of group.querySelectorAll('input[data-layer]:not(:disabled)')) setLayer(box.dataset.layer, on);
        });
      }

      const legend = h?.legend || [];
      const row = document.createElement('div');
      row.className = 'layer-row' + (h ? '' : ' empty');
      const hasLegend = legend.length > 0;
      row.innerHTML = `
        <label title="${esc(layer.description)}">
          <input type="checkbox" data-layer="${layer.id}" ${h ? '' : 'disabled'}>
          <span class="swatch ${layer.kind === 'marker' ? 'dot' : ''}" style="background:${layer.color}"></span>
          <span class="name">${esc(layer.title)}</span>
        </label>
        <span class="count">${h && h.count >= 0 ? fmt(h.count) : ''}</span>
        <button class="expand" title="Legend" aria-label="Show legend" ${hasLegend ? '' : 'style="visibility:hidden"'}>▸</button>`;
      container.appendChild(row);

      const legendElement = document.createElement('div');
      legendElement.className = 'legend';
      legendElement.hidden = true;
      legend.forEach(([label, color, count, iconIndex = -1], i) => {
        const swatch = iconIndex >= 0 && D.icons[iconIndex]
          ? `<img class="legend-icon" src="${D.icons[iconIndex]}" alt="">`
          : `<span class="swatch dot" style="background:${color}"></span>`;
        const item = document.createElement('div');
        item.className = 'layer-row';
        item.innerHTML = `<label><input type="checkbox" checked data-sub="${i}">${swatch}<span class="name">${esc(label)}</span></label><span class="count">${fmt(count)}</span>`;
        legendElement.appendChild(item);
      });
      container.appendChild(legendElement);

      row.querySelector('.expand').addEventListener('click', e => {
        legendElement.hidden = !legendElement.hidden;
        e.currentTarget.textContent = legendElement.hidden ? '▸' : '▾';
      });
      const box = row.querySelector('input');
      box.addEventListener('change', () => setLayer(layer.id, box.checked));
      legendElement.querySelectorAll('input[data-sub]').forEach(sub => sub.addEventListener('change', () => {
        const g = h.subgroups[+sub.dataset.sub];
        if (sub.checked) h.leaflet.addLayer(g); else h.leaflet.removeLayer(g);
      }));

      if (h) setLayer(layer.id, visibility[layer.id] ?? layer.visible);
    }
    refreshGroups();
  }

  // ---------------------------------------------------------------- statistics

  const iconCell = i => i >= 0 && D.icons[i] ? `<img class="table-icon" src="${D.icons[i]}" alt="">` : '<span class="table-icon"></span>';
  const tableRows = (list, filter) => {
    const f = filter.toLowerCase();
    return list.filter(([k]) => k.toLowerCase().includes(f)).slice(0, 500)
      .map(([k, v, icon = -1]) => `<tr><td><span class="with-icon">${iconCell(icon)}<span>${esc(k)}</span></span></td><td>${fmt(v)}</td></tr>`).join('')
      || '<tr><td class="muted">Nothing</td><td></td></tr>';
  };
  const countFilter = document.getElementById('count-filter'), productionFilter = document.getElementById('production-filter');
  function renderStatistics() {
    document.getElementById('stats-surface').innerHTML = current.surface.kind === 'space' ? '' : `on ${rich(current.surface.title)}`;
    document.getElementById('counts').innerHTML = tableRows(current.data.counts, countFilter.value);
    document.getElementById('production').innerHTML = tableRows(current.data.production, productionFilter.value);
  }
  countFilter.addEventListener('input', renderStatistics);
  productionFilter.addEventListener('input', renderStatistics);

  // ---------------------------------------------------------------- search

  const search = document.getElementById('search');
  const results = document.getElementById('search-results');
  search.addEventListener('input', () => {
    const q = search.value.trim().toLowerCase();
    results.innerHTML = '';
    if (q.length < 2 || !current) return;
    const hits = current.searchIndex
      .filter(s => s.name.toLowerCase().includes(q))
      .sort((a, b) => (b.name.toLowerCase().startsWith(q) - a.name.toLowerCase().startsWith(q)) || a.name.localeCompare(b.name))
      .slice(0, 40);
    for (const hit of hits) {
      const li = document.createElement('li');
      li.innerHTML = `<span class="swatch dot" style="background:${hit.layer.color}"></span><span>${esc(hit.name)}</span><span class="layer">${esc(hit.layer.title)}</span>`;
      li.addEventListener('click', () => {
        backToMap();
        map.setView([hit.lat, hit.lng], Math.max(map.getZoom(), D.maxZoom - 1));
        hit.open();
      });
      results.appendChild(li);
    }
    if (hits.length === 0) results.innerHTML = '<li class="layer">No matches</li>';
  });

  // ---------------------------------------------------------------- options

  const labels = document.getElementById('opt-labels');
  labels.checked = store.get('labels', true);
  const applyLabels = () => {
    document.body.classList.toggle('no-labels', !labels.checked);
    store.set('labels', labels.checked);
  };
  labels.addEventListener('change', applyLabels);
  applyLabels();

  const gridBox = document.getElementById('opt-grid');
  gridBox.checked = store.get('grid', true);
  const applyGrid = () => { if (gridBox.checked) grid.addTo(map); else map.removeLayer(grid); store.set('grid', gridBox.checked); };
  gridBox.addEventListener('change', applyGrid);
  applyGrid();

  function applyAlt() {
    if (!current?.alt) return;
    if (altBox.checked) current.alt.addTo(map); else map.removeLayer(current.alt);
  }
  const altBox = document.getElementById('opt-alt');
  altBox.checked = store.get('alt', true);
  altBox.addEventListener('change', () => { store.set('alt', altBox.checked); applyAlt(); });

  const applyFar = () => document.body.classList.toggle('labels-far', map.getZoom() < 4);
  map.on('zoomend', applyFar);
  applyFar();

  // Cursor position
  const cursor = document.getElementById('cursor');
  map.on('mousemove', e => {
    if (current?.surface.kind === 'space') { cursor.textContent = `zoom ${map.getZoom().toFixed(1)}`; return; }
    const x = e.latlng.lng, y = e.latlng.lat;
    cursor.textContent = `x ${coord(Math.floor(x))}   y ${coord(Math.floor(y))}   chunk ${Math.floor(x / 32)}, ${Math.floor(y / 32)}   zoom ${map.getZoom()}`;
  });

  // ---------------------------------------------------------------- view in the URL

  // #surface/zoom/x/y, so that views can be bookmarked and shared.
  function fromHash() {
    const m = /^#([^/]+)\/(-?[\d.]+)\/(-?[\d.]+)\/(-?[\d.]+)$/.exec(location.hash);
    if (!m) return null;
    const name = decodeURIComponent(m[1]);
    const surface = name === 'space' ? SPACE : D.surfaces.find(s => s.name === name);
    return surface ? { surface, zoom: +m[2], x: +m[3], y: +m[4] } : null;
  }
  function updateHash() {
    if (!current) return;
    const c = map.getCenter();
    history.replaceState(null, '', `#${encodeURIComponent(current.surface.name)}/${+map.getZoom().toFixed(2)}/${Math.round(c.lng)}/${Math.round(c.lat)}`);
  }
  let hashTimer = 0;
  map.on('moveend', () => { clearTimeout(hashTimer); hashTimer = setTimeout(updateHash, 250); });
  window.addEventListener('hashchange', () => {
    const view = fromHash();
    if (!view) return;
    if (view.surface === current?.surface) map.setView([view.y, view.x], view.zoom);
    else showSurface(view.surface, view);
  });

  const start = fromHash();
  const remembered = (store.get('surface', '') === 'space' && SPACE) || D.surfaces.find(s => s.name === store.get('surface', '')) || D.surfaces[0];
  await showSurface(start?.surface || remembered, start);
  updateHash();
})();
