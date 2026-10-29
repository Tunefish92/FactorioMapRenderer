/* "Save game source" panel. Needs the renderer's server mode (--serve); a page opened from disk only says so. */
(async () => {
  'use strict';

  // The panel is for admins. Visitors of a served map do not see it, only the admin login in the Save game tab.
  const $ = id => document.getElementById(id);
  const current = $('source-current'), status = $('source-status'), controls = $('source-controls');
  const data = window.MAP_DATA || {};
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  async function api(path, options = {}) {
    const response = await fetch(path, { ...options, headers: { 'X-Map-Renderer': '1', ...(options.headers || {}) } });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `The renderer answered with HTTP ${response.status}.`);
    return body;
  }
  const postJson = (path, body) => api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

  // What the "No map yet" placeholder tells this visitor to do.
  const placeholder = (text, canLoad) => {
    $('placeholder-text').textContent = text;
    $('placeholder-browse').hidden = $('placeholder-steps').hidden = !canLoad;
  };

  // Status bar: where the save came from.
  const SOURCE_LABEL = { file: 'Local file', folder: 'Folder', ssh: 'SSH' };
  const showOrigin = s => {
    const pill = $('status-source');
    pill.textContent = SOURCE_LABEL[s.kind] || 'Local file';
    pill.dataset.kind = s.kind || 'file';
    pill.title = s.loadedAt ? `Loaded ${new Date(s.loadedAt).toLocaleString()}` : '';
    pill.hidden = false;
  };

  let source;
  try {
    if (location.protocol === 'file:') throw new Error('opened from disk');
    source = await api('/api/source');
  } catch {
    showOrigin({});
    window.factorioMapUpdates?.();
    $('source').hidden = false;
    current.textContent = data.fileName ? `File: ${data.fileName}` : 'No save loaded.';
    $('source-offline').hidden = false;
    placeholder('Render a save game with the command line tool, or start it with --serve to choose one here.', false);
    return;
  }

  showOrigin(source);
  if (!source.admin) {
    placeholder('No save game has been loaded so far. An admin can choose one after logging in from the side panel.', false);
    $('admin').hidden = false;
    $('admin-reveal').addEventListener('click', () => {
      const show = $('admin-password').type === 'password';
      $('admin-password').type = show ? 'text' : 'password';
      $('admin-reveal').setAttribute('aria-pressed', show);
      $('admin-reveal').title = show ? 'Hide password' : 'Show password';
      $('admin-reveal').setAttribute('aria-label', $('admin-reveal').title);
      $('admin-password').focus();
    });
    $('admin-form').addEventListener('submit', async e => {
      e.preventDefault();
      const button = $('admin-submit');
      button.disabled = true;
      button.textContent = 'Logging in…';
      $('admin-error').hidden = true;
      try {
        await postJson('/api/login', { password: $('admin-password').value });
        location.reload();
      } catch (err) {
        button.disabled = false;
        button.textContent = 'Log in';
        $('admin-error').hidden = false;
        $('admin-error').textContent = err.message;
        $('admin').classList.remove('shake');
        void $('admin').offsetWidth; // restart the animation
        $('admin').classList.add('shake');
        $('admin-password').select();
      }
    });
    return;
  }
  window.factorioMapUpdates?.();
  $('source').hidden = false;
  controls.hidden = false;
  if (source.loginRequired) {
    $('admin-session').hidden = false;
    $('admin-logout').addEventListener('click', async () => {
      $('admin-logout').disabled = true;
      await api('/api/logout', { method: 'POST' }).catch(() => {});
      location.reload();
    });
  }

  const KIND = { file: 'File', folder: 'Folder', ssh: 'SSH' };

  function showSource(s) {
    source = s;
    const loaded = s.loadedAt ? ` · loaded ${new Date(s.loadedAt).toLocaleString()}` : '';
    current.innerHTML = s.kind
      ? `<b>${KIND[s.kind] || s.kind}:</b> ${esc(s.name)}<span class="muted">${esc(loaded)}</span>`
      : (data.fileName ? `<b>File:</b> ${esc(data.fileName)}` : 'No save loaded yet. Choose one below.');
    // Only a save from a folder or over SSH can be fetched again.
    $('source-refresh').hidden = $('status-refresh').hidden = !(s.kind === 'folder' || s.kind === 'ssh');
    if (s.serverInfo) showServerInfo('ok', s.serverInfo, '');
  }

  function showServerInfo(state, info, message) {
    const box = $('server-info');
    box.hidden = false;
    box.dataset.state = state;
    box.innerHTML = `<span class="pill">${state === 'ok' ? 'Connected' : state === 'unsupported' ? 'Not reachable' : 'Checking'}</span>` +
      (info ? `<div>${esc(info)}</div>` : '') + (message ? `<div class="muted">${esc(message)}</div>` : '');
  }

  function setStatus(text, kind) {
    status.hidden = !text;
    status.textContent = text || '';
    status.dataset.kind = kind || '';
  }

  // ---- loading: render.js shows how it goes, to this page and to every other one
  let busy = false;
  /** Runs one load; render.js opens the new map once it has been rendered, or says what went wrong. */
  async function load(what, action) {
    if (busy) return;
    busy = true;
    setStatus('', '');
    window.FMR_RENDER.begin(what);
    try {
      await action();
    } catch (e) {
      window.FMR_RENDER.fail(e.message);
    } finally {
      busy = false;
    }
  }

  // ---- file: browse and drag & drop
  const upload = file => {
    if (!file) return;
    if (!/\.zip$/i.test(file.name)) { setStatus('Please choose a Factorio save file (.zip).', 'error'); return; }
    load(`Loading ${file.name}`, () => api(`/api/upload?name=${encodeURIComponent(file.name)}&modified=${file.lastModified || 0}`, { method: 'POST', body: file }));
  };
  $('source-browse').addEventListener('click', () => $('source-file').click());
  $('placeholder-browse').addEventListener('click', () => $('source-file').click());
  placeholder('Load a save game to see your factory on the map.', true);
  $('source-file').addEventListener('change', e => { upload(e.target.files[0]); e.target.value = ''; });

  // Dropping works anywhere on the page; the drop zone only shows where to aim.
  const hasFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
  let dragDepth = 0;
  window.addEventListener('dragenter', e => { if (hasFiles(e)) { e.preventDefault(); dragDepth++; document.body.classList.add('dragging-file'); } });
  window.addEventListener('dragover', e => { if (hasFiles(e)) e.preventDefault(); });
  window.addEventListener('dragleave', e => { if (hasFiles(e) && --dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging-file'); } });
  window.addEventListener('drop', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0;
    document.body.classList.remove('dragging-file');
    upload(e.dataTransfer.files[0]);
  });

  /** Stores the form's settings in the renderer; they are also stored whenever they are checked or used. */
  async function saveSettings(form, path, settings) {
    if (!form.reportValidity()) return;
    try {
      const s = await postJson(path, settings);
      if (s.hasPassword) { $('rcon-password').value = ''; $('rcon-password').placeholder = 'saved – leave empty to keep it'; }
      setStatus('Settings saved.', 'ok');
    } catch (e) {
      setStatus(e.message, 'error');
    }
  }

  // ---- folder
  $('folder-path').value = source.folderPath || '';
  const folderSettings = () => ({ path: $('folder-path').value.trim() });
  $('folder-save').addEventListener('click', () => saveSettings($('folder-form'), '/api/folder/settings', folderSettings()));
  $('folder-form').addEventListener('submit', e => {
    e.preventDefault();
    load(`Loading the newest save from ${folderSettings().path}`, () => postJson('/api/folder', folderSettings()));
  });

  // ---- ssh
  $('ssh-target').value = source.sshTarget || '';
  $('ssh-port').value = source.sshPort || 22;
  $('ssh-path').value = source.sshPath || '';
  const sshSettings = () => ({ target: $('ssh-target').value.trim(), port: +$('ssh-port').value || 22, path: $('ssh-path').value.trim() });
  $('ssh-save').addEventListener('click', () => saveSettings($('ssh-form'), '/api/ssh/settings', sshSettings()));
  $('ssh-form').addEventListener('submit', e => {
    e.preventDefault();
    const s = sshSettings();
    load(`Copying the save game from ${s.target}`, () => postJson('/api/ssh', s));
  });

  // ---- game server (RCON)
  const rconSettings = () => ({
    host: $('rcon-host').value.trim(),
    port: +$('rcon-port').value || 27015,
    password: $('rcon-password').value,
    saveFirst: $('rcon-savefirst').checked,
  });
  $('rcon-host').value = source.rconHost || '';
  $('rcon-port').value = source.rconPort || 27015;
  $('rcon-savefirst').checked = source.saveFirst !== false;
  if (source.hasPassword) $('rcon-password').placeholder = 'saved – leave empty to keep it';
  $('rcon-save').addEventListener('click', () => saveSettings($('rcon-form'), '/api/rcon/settings', rconSettings()));
  $('rcon-form').addEventListener('submit', async e => {
    e.preventDefault();
    showServerInfo('checking', '', 'Asking the server…');
    try {
      const c = await postJson('/api/rcon/check', rconSettings());
      showServerInfo('ok', `Factorio ${c.version}`, `${c.online} player${c.online === 1 ? '' : 's'} online`);
      $('rcon-password').value = '';
      $('rcon-password').placeholder = 'saved – leave empty to keep it';
    } catch (err) {
      showServerInfo('unsupported', '', err.message);
    }
  });

  const refresh = () =>
    load(source.kind === 'ssh' ? `Copying the save game from ${source.sshTarget}` : `Loading the newest save from ${source.folderPath}`, () => api('/api/refresh', { method: 'POST' }));
  $('source-refresh').addEventListener('click', refresh);
  $('status-refresh').addEventListener('click', refresh); // the same, one click away in the status bar

  // Open the forms that are in use or already filled in.
  if (source.kind === 'folder' || source.folderPath) $('source-folder').open = true;
  if (source.kind === 'ssh' || source.sshTarget) $('source-ssh').open = true;
  if (source.rconHost) $('source-rcon').open = true;
  showSource(source);
})();
