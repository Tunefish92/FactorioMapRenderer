/* The Server tab: what the Factorio server says about itself over RCON, and for admins a console.
   Only there when the game server's RCON is set up in the Save game tab. */
(async () => {
  'use strict';

  if (location.protocol === 'file:') return;
  const $ = id => document.getElementById(id);
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const tab = document.querySelector('#tabs [data-tab="server"]'), panel = document.querySelector('.tab-panel[data-panel="server"]');

  async function api(path, options = {}) {
    const response = await fetch(path, { ...options, headers: { 'X-Map-Renderer': '1', ...(options.headers || {}) } });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `The renderer answered with HTTP ${response.status}.`);
    return body;
  }
  const command = text => api('/api/rcon/command', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ command: text }) });

  let source;
  try { source = await api('/api/source'); } catch { return; }
  if (!source.hasServer) {
    // a tab remembered from when the server was set up
    if (!panel.hidden) window.factorioShowTab?.('layers');
    return;
  }
  tab.hidden = false;
  $('server-admin').hidden = !source.admin;

  const rows = pairs => pairs.filter(([, value]) => value !== undefined && value !== null && value !== '')
    .map(([label, value]) => `<tr><td>${esc(label)}</td><td>${esc(value)}</td></tr>`).join('');

  async function refresh() {
    let stats;
    try { stats = await api('/api/serverstats'); } catch (e) { stats = { available: true, error: e.message }; }
    if (!stats.available) return;
    const health = $('server-health');
    $('server-error').hidden = !stats.error;
    $('server-error').textContent = stats.error ? `The server does not answer: ${stats.error}` : '';
    health.hidden = false;
    health.textContent = stats.error ? 'Not reachable' : 'Running';
    health.dataset.state = stats.error ? 'down' : 'ok';
    if (!stats.error) {
      const players = stats.players || [];
      $('server-state').innerHTML = rows([
        ['Version', stats.version],
        ['Players online', players.length ? `${players.length}: ${players.join(', ')}` : 'Nobody'],
        ['Map age', stats.time],
        // "[planet=nauvis] Nauvis - Evolution factor: 0.4512. (Time 12%) …", one line per planet
        ...(stats.evolution || '').split('\n').filter(Boolean).map(line => {
          const m = /^(?:\[[^\]]*\]\s*)?(.*?)\s*-\s*Evolution factor:\s*([\d.]+)/.exec(line);
          return m ? [`Evolution on ${m[1]}`, `${(parseFloat(m[2]) * 100).toFixed(1)} %`] : ['Evolution', line];
        }),
      ]);
    } else {
      $('server-state').innerHTML = '';
    }
    $('server-updated').textContent = `Updated ${new Date().toLocaleTimeString()}`;
  }

  // Asked only while someone looks at the tab.
  let timer = 0;
  const watch = () => {
    clearInterval(timer);
    if (panel.hidden || document.hidden) return;
    refresh();
    timer = setInterval(refresh, 10000);
  };
  new MutationObserver(watch).observe(panel, { attributes: true, attributeFilter: ['hidden'] });
  document.addEventListener('visibilitychange', watch);
  watch();

  if (!source.admin) return;

  // ---------------------------------------------------------------- admin: console

  const output = $('server-output');
  async function run(text) {
    output.hidden = false;
    output.textContent = `> ${text}\n…`;
    try {
      const answer = await command(text);
      output.textContent = `> ${text}\n${answer.output || '(no output)'}`;
    } catch (error) {
      output.textContent = `> ${text}\n${error.message}`;
    }
    setTimeout(refresh, 1000);
  }
  $('server-command-form').addEventListener('submit', e => {
    e.preventDefault();
    const text = $('server-command').value.trim();
    if (!text) return;
    run(text).then(() => $('server-command').select());
  });

  // The commands that matter for running a server; what each one does is written here, the game does not tell.
  const COMMANDS = [
    ['/players', 'Everyone who has played on this server, online or not.'],
    ['/players online', 'Who is playing right now.'],
    ['/admins', 'The server\'s admins.'],
    ['/version', 'The game version the server runs.'],
    ['/time', 'How long the map has been running.'],
    ['/evolution', 'How far the enemies have evolved, per planet.'],
    ['/seed', 'The map seed.'],
    ['/server-save', 'Saves the game now, under the name of the running save.'],
    ['/whisper', 'A private message: /whisper <player> <text>.'],
    ['/kick', 'Disconnects a player: /kick <player> <reason>.'],
    ['/ban', 'Bans a player: /ban <player> <reason>.'],
    ['/unban', 'Lifts a ban: /unban <player>.'],
    ['/promote', 'Makes a player admin: /promote <player>.'],
    ['/demote', 'Takes admin rights away: /demote <player>.'],
    ['/purge', 'Deletes a player\'s chat messages: /purge <player>.'],
    ['/quit', 'Saves and stops the server.'],
  ];
  $('server-list-items').innerHTML = COMMANDS.map(([name, about]) =>
    `<li><button type="button" data-name="${esc(name)}"><span><b>${esc(name)}</b><small>${esc(about)}</small></span><em>command</em></button></li>`).join('');
  $('server-list-items').addEventListener('click', e => {
    const button = e.target.closest('button[data-name]');
    if (!button) return;
    $('server-command').value = button.dataset.name + ' ';
    $('server-command').focus();
  });

  $('server-save-now').addEventListener('click', () => run('/server-save'));
  $('server-stop').addEventListener('click', () => {
    if (!confirm('Stop the game server?\n\nIt saves the game, and everyone playing is disconnected. It only comes back if its host restarts it (Docker restart policy, service).')) return;
    run('/quit');
  });
})();
