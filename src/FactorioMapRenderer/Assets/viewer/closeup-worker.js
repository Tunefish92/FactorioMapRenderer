/*
 * Draws tiles of the game's graphics close up away from the page, so that zooming and panning stay smooth: the page
 * asks for a tile, this worker loads what it needs (regions, sprites, textures), draws it with closeup-draw.js on an
 * offscreen canvas and hands the finished picture back. The page keeps several of them busy at once.
 *
 * Messages: { type: 'icons', icons } the game icons (versioned urls); { type: 'surface', id, closeUp } a surface's
 * palette, sprites and regions; { type: 'draw', id, surface, width, height, … } a tile (see closeup-draw.js), or of
 * the alt mode (kind 'alt', with its entries);
 * { type: 'cancel', id } a tile that is no longer needed. Answers { id, bitmap } or { id, error }.
 */
'use strict';

// the files of a render are asked for with its build, as the page does ("?v=…" of this script's own address)
const version = self.location.search;
const versioned = url => version ? url + version : url;
importScripts(versioned('closeup-draw.js'));

let icons = [];
const surfaces = new Map(); // id → { closeUp, regionSet, regions: key → region or null }
const images = new Map();   // url → promise of an ImageBitmap or null

const load = url => {
  if (!images.has(url)) {
    images.set(url, fetch(url)
      .then(r => (r.ok ? r.blob() : null))
      .then(blob => (blob ? createImageBitmap(blob) : null))
      .catch(() => null));
  }
  return images.get(url);
};

// A region file calls FMR_REGION(surface, key, region) when it runs; importScripts runs it here.
let arrived = null;
self.FMR_REGION = (surface, key, region) => { arrived = region; };
function loadRegion(surfaceId, s, key) {
  if (!s.regionSet.has(key)) return Promise.resolve(null);
  if (!s.regions.has(key)) {
    arrived = null;
    try {
      importScripts(versioned(`surfaces/${surfaceId}/${key}.js`));
    } catch {
      // not there (any more): nothing to draw
    }
    s.regions.set(key, arrived);
  }
  return Promise.resolve(s.regions.get(key));
}

const queue = [];
let busy = false;
async function work() {
  if (busy) return;
  busy = true;
  while (queue.length) {
    const job = queue.shift();
    try {
      const canvas = new OffscreenCanvas(job.width, job.height);
      const icon = i => (icons[i] ? load(icons[i]) : null);
      if (job.kind === 'alt') await self.FMR_CLOSEUP.drawAlt(canvas, job, icon);
      else {
        const s = surfaces.get(job.surface);
        await self.FMR_CLOSEUP.draw(canvas, { ...job, closeUp: s.closeUp }, key => loadRegion(job.surface, s, key), url => load(versioned(url)), icon);
      }
      const bitmap = canvas.transferToImageBitmap();
      self.postMessage({ id: job.id, bitmap }, [bitmap]);
    } catch (e) {
      self.postMessage({ id: job.id, error: String(e) });
    }
  }
  busy = false;
}

self.onmessage = e => {
  const m = e.data;
  if (m.type === 'icons') icons = m.icons;
  else if (m.type === 'surface') {
    surfaces.set(m.id, { closeUp: m.closeUp, regionSet: new Set(m.closeUp.regions ? m.closeUp.regions.split(' ') : []), regions: new Map() });
  } else if (m.type === 'cancel') {
    const i = queue.findIndex(j => j.id === m.id);
    if (i >= 0) queue.splice(i, 1);
  } else if (m.type === 'draw') {
    queue.push(m);
    work();
  }
};
