/*
 * Draws one tile of the game's own graphics close up: the ground in the game's textures, or the entities as the game
 * draws them. Shared by the page and the workers of closeup-worker.js, which draw the tiles away from the page so
 * that zooming and panning stay smooth; the page draws them itself where a browser has no workers that can draw.
 *
 * Also the alt mode's icons over the buildings (drawAlt).
 *
 * job: { kind: 'ground' | 'graphics', x0, y0 (top left in game tiles), span (game tiles across), perTile (pixels per
 * game tile), ratio (device pixels per pixel), closeUp (the surface's palette, sprites, regionSize) }
 * loadRegion(key) and loadImage(url) resolve to a region of surfaces/N/X_Y.js and an image, or null.
 */
(() => {
  'use strict';

  // which of a tile's variants lies at a place: always the same one for the same place
  const variation = (x, y, count) => {
    let h = Math.imul(x, 73856093) ^ Math.imul(y, 19349663);
    h = Math.imul(h ^ (h >>> 15), 0x2c1b3c6d);
    return ((h ^ (h >>> 12)) >>> 0) % count;
  };
  const TEXTURE = 64; // pixels per tile in the ground textures
  const usable = img => img && (img.naturalWidth ?? img.width) > 0;

  /** The region's ground: one palette index per tile, row by row (255: not charted). */
  function decodeGround(region) {
    if (!region || region.ground) return region;
    const raw = atob(region.g);
    region.ground = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) region.ground[i] = raw.charCodeAt(i);
    return region;
  }

  async function draw(canvas, job, loadRegion, loadImage, iconImage) {
    const { kind, x0, y0, span, perTile, ratio, closeUp } = job;
    const R = closeUp.regionSize, margin = kind === 'graphics' ? 34 : 0; // tall buildings and wires reach out of their region
    const keys = [];
    for (let ry = Math.floor((y0 - margin) / R); ry <= Math.floor((y0 + span + margin) / R); ry++)
      for (let rx = Math.floor((x0 - margin) / R); rx <= Math.floor((x0 + span + margin) / R); rx++) keys.push([rx, ry]);
    const regions = (await Promise.all(keys.map(([rx, ry]) => loadRegion(`${rx}_${ry}`)))).map(decodeGround);
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingQuality = 'high';
    if (kind === 'ground') await drawGround(ctx, closeUp, keys, regions, x0, y0, span, ratio * perTile, loadImage);
    else {
      ctx.setTransform(ratio * perTile, 0, 0, ratio * perTile, -x0 * ratio * perTile, -y0 * ratio * perTile);
      await drawGraphics(ctx, closeUp, regions, x0, y0, span, perTile, loadImage, iconImage);
    }
  }

  /** Each tile on whole device pixels, edge to edge: no seams between them. */
  async function drawGround(ctx, closeUp, keys, regions, x0, y0, span, scale, loadImage) {
    const palette = closeUp.palette, R = closeUp.regionSize;
    const textures = await Promise.all(palette.map(p => p[1] ? loadImage(p[1]) : null));
    const px = x => Math.round((x - x0) * scale), py = y => Math.round((y - y0) * scale);
    keys.forEach(([rx, ry], i) => {
      const region = regions[i];
      if (!region) return;
      const tx1 = Math.max(Math.floor(x0), rx * R), tx2 = Math.min(Math.ceil(x0 + span), rx * R + R);
      const ty1 = Math.max(Math.floor(y0), ry * R), ty2 = Math.min(Math.ceil(y0 + span), ry * R + R);
      for (let ty = ty1; ty < ty2; ty++) {
        for (let tx = tx1; tx < tx2; tx++) {
          const index = region.ground[(ty - ry * R) * R + (tx - rx * R)];
          if (index === 255) continue;
          const [color, , material, count, w, h] = palette[index], texture = textures[index];
          const dx = px(tx), dy = py(ty), dw = px(tx + 1) - dx, dh = py(ty + 1) - dy;
          if (!usable(texture)) {
            ctx.fillStyle = color;
            ctx.fillRect(dx, dy, dw, dh);
            continue;
          }
          const sx = material ? (((tx % w) + w) % w) * TEXTURE : variation(tx, ty, count) * TEXTURE;
          const sy = material ? (((ty % h) + h) % h) * TEXTURE : 0;
          ctx.drawImage(texture, sx, sy, TEXTURE, TEXTURE, dx, dy, dw, dh);
        }
      }
    });
  }

  async function drawGraphics(ctx, closeUp, regions, x0, y0, span, perTile, loadImage, iconImage) {
    const sprites = closeUp.sprites, items = [];
    for (const region of regions) {
      if (!region) continue;
      const s = region.s;
      for (let i = 0; i < s.length; i += 4) {
        const [, w, h] = sprites[s[i + 1]];
        if (s[i + 2] > x0 + span || s[i + 3] > y0 + span || s[i + 2] + w < x0 || s[i + 3] + h < y0) continue;
        items.push([s[i], s[i + 1], s[i + 2], s[i + 3]]);
      }
      const fp = region.f;
      for (let i = 0; i < fp.length; i += 6) {
        if (fp[i + 2] > x0 + span || fp[i + 3] > y0 + span || fp[i + 4] < x0 || fp[i + 5] < y0) continue;
        items.push([fp[i], -1, fp[i + 2], fp[i + 3], fp[i + 4], fp[i + 5], fp[i + 1]]);
      }
      const wires = region.w || [];
      for (let i = 0; i < wires.length; i += 5) {
        const [, x1, y1, x2, y2] = wires.slice(i, i + 5);
        if (Math.max(x1, x2) < x0 || Math.min(x1, x2) > x0 + span || Math.max(y1, y2) < y0 || Math.min(y1, y2) > y0 + span) continue;
        items.push([wires[i], -2, x1, y1, x2, y2]);
      }
    }
    items.sort((a, b) => a[0] - b[0]); // the renderer's order: ore, flat things, then back to front
    const images = new Map(), icons = new Map();
    await Promise.all([
      ...[...new Set(items.filter(i => i[1] >= 0).map(i => sprites[i[1]][0]))].map(async url => images.set(url, await loadImage(url))),
      ...[...new Set(items.filter(i => i[1] === -1 && i[6] >= 0).map(i => i[6]))].map(async icon => icons.set(icon, await iconImage(icon))),
    ]);
    for (const [, sprite, x, y, x2, y2, icon] of items) {
      if (sprite === -2) {
        // a copper wire, sagging a little between its poles
        ctx.strokeStyle = 'rgba(206, 124, 70, 0.9)';
        ctx.lineWidth = Math.max(0.05, 1.2 / perTile);
        ctx.beginPath();
        ctx.moveTo(x, y);
        ctx.quadraticCurveTo((x + x2) / 2, (y + y2) / 2 + Math.hypot(x2 - x, y2 - y) * 0.04, x2, y2);
        ctx.stroke();
        continue;
      }
      if (sprite >= 0) {
        const [url, w, h] = sprites[sprite], img = images.get(url);
        if (usable(img)) ctx.drawImage(img, x, y, w, h);
        continue;
      }
      // no sprite in the game's data: the footprint, with the building's icon
      ctx.fillStyle = 'rgba(58, 54, 46, 0.9)';
      ctx.fillRect(x, y, x2 - x, y2 - y);
      const img = icons.get(icon), s = Math.min(x2 - x, y2 - y) * 0.75;
      if (usable(img) && s * perTile >= 12) ctx.drawImage(img, (x + x2 - s) / 2, (y + y2 - s) / 2, s, s);
    }
  }

  /** Icons of size s (in tiles) side by side, centred on x, y; on dark discs, as the game's alt mode. */
  function row(ctx, icons, images, x, y, s, perTile, discs) {
    if (s * perTile < 9) return;
    icons.forEach((icon, i) => {
      const img = images.get(icon);
      if (!usable(img)) return;
      const ix = x + (i - (icons.length - 1) / 2) * s * 1.05 - s / 2, iy = y - s / 2;
      ctx.fillStyle = 'rgba(0, 0, 0, 0.45)';
      ctx.beginPath();
      if (discs) ctx.arc(ix + s / 2, iy + s / 2, s * 0.58, 0, 2 * Math.PI);
      else ctx.roundRect(ix - s * 0.04, iy - s * 0.04, s * 1.08, s * 1.08, s * 0.15);
      ctx.fill();
      ctx.drawImage(img, ix, iy, s, s);
    });
  }

  /** The alt mode over the buildings of a tile: job.entries [x, y, size, [icon…], [module icon…]]. */
  async function drawAlt(canvas, job, iconImage) {
    const { entries, x0, y0, perTile, ratio } = job;
    const images = new Map();
    await Promise.all([...new Set(entries.flatMap(e => [...e[3], ...e[4]]))].map(async i => images.set(i, await iconImage(i))));
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio * perTile, 0, 0, ratio * perTile, -x0 * ratio * perTile, -y0 * ratio * perTile);
    ctx.imageSmoothingQuality = 'high';
    for (const [x, y, extent, icons, modules] of entries) {
      // as in the game: one icon large, several side by side and smaller; small buildings small icons;
      // the modules in a row below, or in the middle of a beacon
      const n = icons.length, s = Math.min(extent < 1.5 ? 0.55 : extent * 0.5, 2.4) / (n > 1 ? Math.min(n, 3) * 0.6 : 1);
      if (n) row(ctx, icons, images, x, y, s, perTile, true);
      if (modules.length) {
        const m = Math.min(extent * (n ? 0.2 : 0.3), 0.75, extent * 0.9 / modules.length);
        row(ctx, modules, images, x, n ? y + extent * 0.33 : y, m, perTile, !n);
      }
    }
  }

  self.FMR_CLOSEUP = { draw, drawAlt };
})();
