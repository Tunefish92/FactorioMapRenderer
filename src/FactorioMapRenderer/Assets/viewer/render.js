/*
 * The map being rendered: at start (after an update of the server, or of the save it was started with) or because
 * an admin loads a save. While the map's files are replaced, every page shows how far the render has come over the
 * blurred map: its steps with their times, the bar, and the lines the server writes to its console. Once it is done
 * the page loads the new map. source.js opens it at once for the admin's own load (window.FMR_RENDER).
 */
(() => {
  'use strict';

  if (location.protocol === 'file:') return; // opened from disk: no server that renders

  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const REASONS = {
    update: ['Server updated', 'Bringing the map up to date', 'The server was updated and renders the map again, so that it shows everything the new version can.'],
    start: ['Server started', 'Rendering the map', 'The server renders the save game it was started with.'],
    load: ['New save game', 'Loading a new save game', 'A new save game is being loaded.'],
  };

  // The steps of a render, told apart by the renderer's stages; getting the save is only part of loading one.
  const PHASES = [
    { id: 'fetch', title: 'Getting the save game' },
    { id: 'factorio', title: 'Factorio reads the save', match: /^(Reading the save game|Downloading Factorio|Starting Factorio|Loading the save in Factorio|Loading the map|Reading the map from the game|Finishing)/ },
    { id: 'analyse', title: 'Finding buildings and resources', match: /^Finding buildings/ },
    { id: 'draw', title: 'Drawing the map', match: /^Drawing the map/ },
    { id: 'write', title: 'Writing the page', match: /^Writing the page/ },
  ];
  const phaseOf = (name, load) => {
    const i = PHASES.findIndex(p => p.match?.test(name));
    return i >= 0 ? i : load ? 0 : 1;
  };

  const duration = s => {
    s = Math.round(s);
    return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${String(s % 60).padStart(2, '0')} s`;
  };

  // own: the admin's own load, which this page began (before: the render the server had shown until then)
  let modal = null, from = 0, highest = 0, timer = 0, reloading = 0, own = null, lastId = 0;

  function build() {
    modal = document.createElement('div');
    modal.id = 'render-modal';
    modal.setAttribute('role', 'dialog');
    modal.setAttribute('aria-modal', 'true');
    modal.setAttribute('aria-labelledby', 'render-title');
    modal.innerHTML = `
      <div class="render-card">
        <header>
          <img class="load-logo" src="${esc((window.MAP_DATA || {}).logo || 'logo.svg')}" width="52" height="52" alt="">
          <div>
            <p class="news-kicker" id="render-kicker"></p>
            <h2 id="render-title"></h2>
            <p class="render-save" id="render-save"></p>
          </div>
        </header>
        <div class="render-body">
          <p class="render-intro" id="render-intro"></p>
          <div class="render-now">
            <span class="render-percent" id="render-percent">0 %</span>
            <span class="render-stage" id="render-stage">Starting…</span>
          </div>
          <div class="progress-track" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-labelledby="render-stage">
            <div id="render-bar" class="progress-bar"></div>
          </div>
          <div class="render-times"><span id="render-elapsed"></span><span id="render-left"></span></div>
          <ol class="render-steps" id="render-steps"></ol>
          <details class="render-console" id="render-console">
            <summary>Console <span id="render-lines"></span></summary>
            <pre id="render-log" aria-live="off"></pre>
          </details>
        </div>
        <footer>
          <p id="render-done" class="render-done" role="status"></p>
          <button type="button" id="render-ok" class="primary" disabled>OK</button>
        </footer>
      </div>`;
    // everything behind it stays out of reach of mouse, keyboard and screen readers
    for (const element of document.body.children) element.inert = true;
    document.body.appendChild(modal);
    document.body.classList.add('rendering');
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && modal) e.preventDefault(); }, true);
    try { modal.querySelector('#render-console').open = localStorage.getItem('fmr:renderConsole') === '1'; } catch { /* no storage */ }
    modal.querySelector('#render-console').addEventListener('toggle', e => {
      try { localStorage.setItem('fmr:renderConsole', e.target.open ? '1' : '0'); } catch { /* no storage */ }
    });
    modal.querySelector('#render-ok').addEventListener('click', () => {
      if (modal.classList.contains('failed') && !modal.dataset.reload) close(); else location.reload();
    });
  }

  function close() {
    clearInterval(reloading);
    modal.remove();
    modal = null;
    own = null;
    document.body.classList.remove('rendering');
    for (const element of document.body.children) element.inert = false;
  }

  function header(reason, title) {
    const [kicker, heading, intro] = REASONS[reason] || REASONS.load;
    modal.querySelector('#render-kicker').textContent = kicker;
    modal.querySelector('#render-title').textContent = heading;
    modal.querySelector('#render-intro').textContent = `${intro} The map can be used again once it is done.`;
    modal.querySelector('#render-save').textContent = title || '';
  }

  function steps(state) {
    const load = state.reason === 'load';
    const phases = PHASES.map((p, i) => ({ ...p, i, seconds: 0, stages: [] }));
    for (const s of state.stages || []) {
      const p = phases[phaseOf(s.name, load)];
      p.seconds += s.seconds;
      p.stages.push(s);
    }
    const current = state.running && state.stages?.length ? phaseOf(state.stages[state.stages.length - 1].name, load) : -1;
    const list = modal.querySelector('#render-steps');
    list.innerHTML = phases.filter(p => load || p.i > 0).map(p => {
      const i = p.i;
      const failed = state.failed && i === (state.stages?.length ? phaseOf(state.stages[state.stages.length - 1].name, load) : 0);
      const status = failed ? 'failed' : i === current ? 'active' : p.stages.length && (i < current || !state.running) ? 'done' : 'pending';
      let detail = '';
      if (p.id === 'draw' && p.stages.length) {
        const names = p.stages.map(s => s.name.replace(/^Drawing the map: /, ''));
        detail = status === 'active' ? `${esc(names[names.length - 1])} · ${names.length - 1} surface${names.length === 2 ? '' : 's'} done` : `${names.length} surface${names.length === 1 ? '' : 's'}`;
      } else if (status === 'active' && p.stages.length && p.stages[p.stages.length - 1].name !== p.title) {
        detail = esc(p.stages[p.stages.length - 1].name);
      }
      return `<li data-state="${status}"><span class="render-step-title">${esc(p.title)}</span>`
        + `<span class="render-step-detail">${detail}</span>`
        + `<span class="render-step-time">${p.stages.length ? duration(p.seconds) : ''}</span></li>`;
    }).join('');
  }

  function show(state) {
    if (!modal) build();
    header(state.reason || own?.reason, state.title || own?.title);
    const log = modal.querySelector('#render-log');
    const atEnd = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
    if (state.lines?.length) log.textContent += (log.textContent ? '\n' : '') + state.lines.join('\n');
    if (atEnd) log.scrollTop = log.scrollHeight;
    from = (state.from || 0) + (state.lines?.length || 0);
    modal.querySelector('#render-lines').textContent = from ? `· ${from} lines` : '';

    const done = !state.running && state.finished;
    highest = Math.max(highest, state.percent || 0); // never runs backwards
    const percent = done && !state.failed ? 100 : highest;
    modal.querySelector('#render-bar').style.width = `${percent}%`;
    modal.querySelector('.progress-track').setAttribute('aria-valuenow', percent);
    modal.querySelector('#render-percent').textContent = `${percent} %`;
    modal.querySelector('#render-stage').textContent = done
      ? (state.failed ? 'Stopped' : 'The map is ready')
      : `${state.stage && state.stage !== 'Starting' ? state.stage : 'Starting'}…`;
    const elapsed = state.elapsed ?? 0;
    modal.querySelector('#render-elapsed').textContent = `${duration(elapsed)} so far`;
    // what is left, from how fast it went so far; only once there is enough to go by
    modal.querySelector('#render-left').textContent = !done && percent >= 8 && elapsed > 5
      ? `about ${duration(Math.max(5, Math.ceil(elapsed * (100 - percent) / percent / 5) * 5))} left` : '';
    steps(state);

    if (done) {
      modal.classList.toggle('failed', !!state.failed);
      const ok = modal.querySelector('#render-ok');
      ok.disabled = false;
      if (state.failed) {
        // a failed load leaves the map as it was; after a failed render at start it may be incomplete
        modal.dataset.reload = state.reason === 'load' ? '' : '1';
        modal.querySelector('#render-done').textContent = (state.error || 'The map could not be rendered.')
          + (state.reason === 'load' ? '' : ' An admin can load the save game again in the side panel.');
        ok.textContent = state.reason === 'load' ? 'Close' : 'OK';
      } else if (!reloading) {
        let seconds = 5;
        const tell = () => { modal.querySelector('#render-done').textContent = `Done. The new map opens in ${seconds} s.`; };
        tell();
        ok.textContent = 'Open the map';
        reloading = setInterval(() => { if (--seconds <= 0) location.reload(); else tell(); }, 1000);
      }
      ok.focus();
    }
    return done;
  }

  async function poll() {
    let next = 4000; // nothing running: ask now and then whether a render has begun
    try {
      const response = await fetch(`/api/render?from=${from}`, { cache: 'no-store' });
      if (response.ok) {
        const state = await response.json();
        if (own?.error && !state.running) {
          // refused before it began: nothing of the render before belongs to it
          if (state.id === own.before) Object.assign(state, { reason: 'load', title: own.title, stages: [], lines: [], elapsed: 0 });
          Object.assign(state, { finished: true, failed: true, error: own.error });
        }
        if (own && state.id === own.before && !own.error) {
          next = 700; // the admin's own load, before the server has begun it
        } else if (state.running || (modal && state.finished)) {
          // A page opened after a render was over has nothing to show; one that saw it, its end.
          if (show(state)) return;
          next = 700;
        }
        lastId = state.id || lastId;
      }
    } catch {
      if (modal) next = 1500; // the server is busy or restarting: keep asking
    }
    timer = setTimeout(poll, next);
  }

  // The admin's own load: the window opens at once and says what went wrong if it does.
  window.FMR_RENDER = {
    begin(title) {
      if (modal) close();
      own = { reason: 'load', title, before: lastId };
      from = 0;
      highest = 0;
      show({ reason: 'load', title, running: true, stage: 'Starting', percent: 0, elapsed: 0, stages: [] });
      clearTimeout(timer);
      poll();
    },
    /** What the server answered to the load: shown with the steps it got through. */
    fail(message) {
      if (!own) return;
      own.error = message;
      clearTimeout(timer);
      poll();
    },
  };

  poll();
  window.addEventListener('pagehide', () => clearTimeout(timer));
})();
