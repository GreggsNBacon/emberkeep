'use strict';
// Experimental features (the "experimental" branch), kept apart from app.js so they're easy to review:
// Overview with activity, Backups tab, Schedule tab, players and CPU/memory history in the header,
// port clash warnings, Duplicate server, server notes, notifications (Windows + Discord), export/import.
// Uses app.js helpers: h, $, $$, api, toast, state, can, current, game, select, fmtBytes, openModal, confirmDialog, browse…

const relTime = iso => {
  const t = new Date(iso), s = Math.round((Date.now() - t) / 1000);
  if (s < 45) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400 && t.getDate() === new Date().getDate()) return t.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  return t.toLocaleString([], { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
};
const canAnywhere = perm => !!(state.me?.full || state.me?.permissions?.includes(perm));
const toggleEl = (checked, onChange) => {
  const cb = h('input', { type: 'checkbox', ...(checked ? { checked: true } : {}) });
  cb.addEventListener('change', () => onChange(cb.checked, cb));
  return h('span', { class: 'toggle' }, cb, h('span'));
};

// ---------------- Overview ----------------

$('#overviewBtn').onclick = () => {
  state.view = 'overview';
  localStorage.setItem('hb.view', 'overview');
  $('#app').classList.remove('menu');
  renderAll();
};

function renderOverviewButton() {
  const b = $('#overviewBtn');
  b.hidden = state.servers.length < 1;
  b.classList.toggle('active', state.view === 'overview');
  const running = state.servers.filter(s => s.state === 'Running').length;
  $('#overviewCount').textContent = running ? `${running} running` : '';
}

function showOverview() {
  $('#emptyState').hidden = true;
  $('#serverView').hidden = true;
  $('#overviewView').hidden = false;
  document.title = 'Overview | Emberkeep';
  renderOverview();
}

function renderOverview() {
  renderOverviewButton();
  const list = state.servers;
  const running = list.filter(s => s.state === 'Running');
  const players = running.reduce((n, s) => n + (s.players?.count || 0), 0);
  const counted = running.some(s => s.players);
  $('#ovSummary').textContent = `${running.length} of ${list.length} server${list.length === 1 ? '' : 's'} running`
    + (counted ? ` · ${players} player${players === 1 ? '' : 's'} online` : '');
  const canCtl = canAnywhere('control');
  $('#ovCards').replaceChildren(...list.map(s => {
    const g = game(s.gameId);
    const up = s.state === 'Running', stopped = s.state === 'Stopped';
    const card = h('div', { class: `ov-card${up ? ' up' : ''}`, role: 'button', tabindex: '0',
      onclick: e => { if (!e.target.closest('button')) select(s.id); },
      onkeydown: e => { if (e.key === 'Enter') select(s.id); } },
      h('div', { class: 'ov-card-top' }, cachedBadge(`ov-${s.id}`, s.gameId, s, false, s.artAppId),
        h('div', { class: 'ov-name' }, h('strong', {}, s.name), h('span', { class: 'dim' }, g.name))),
      h('div', { class: 'ov-state' }, h('span', { class: `dot ${isCrashed(s) ? 'state-crashed' : `state-${s.state}`}` }), stateLabel(s)),
      h('div', { class: 'ov-nums' },
        up ? h('span', {}, `${fmtUptime(s.uptimeSeconds)} up`) : null,
        up ? h('span', {}, `CPU ${Math.round(s.cpu)}%`) : null,
        up ? h('span', {}, fmtBytes(s.memory)) : null,
        s.players ? h('span', { title: s.players.names.join(', ') || 'Nobody yet' }, `${s.players.count} player${s.players.count === 1 ? '' : 's'}`) : null),
      canCtl ? h('div', { class: 'ov-actions' },
        stopped ? h('button', { class: 'btn btn-small btn-go', type: 'button', onclick: () => ovControl(s, 'start') }, 'Start') : null,
        up ? h('button', { class: 'btn btn-small', type: 'button', onclick: () => ovControl(s, 'restart') }, 'Restart') : null,
        up || s.state === 'Starting' ? h('button', { class: 'btn btn-small btn-stop', type: 'button', onclick: () => ovControl(s, 'stop') }, 'Stop') : null) : null);
    card.style.setProperty('--game', g.color);
    return card;
  }));
  const f = $('#ovFilter');
  const want = f.value;
  f.replaceChildren(h('option', { value: '' }, 'All servers'), ...list.map(s => h('option', { value: s.id }, s.name)));
  f.value = list.some(s => s.id === want) ? want : '';
  loadActivitySoon();
}

async function ovControl(s, action) {
  try { await api('POST', `/api/servers/${s.id}/${action}`); }
  catch (e) { toast(e.message, 'bad'); }
}

// Server updates arrive every few seconds; the activity list is fetched at most every 5 s because of them.
let activityTimer = null, activityAt = 0;
function loadActivitySoon() {
  if (activityTimer) return;
  activityTimer = setTimeout(() => { activityTimer = null; loadActivity(); }, Math.max(300, 5000 - (Date.now() - activityAt)));
}
$('#ovFilter').addEventListener('change', loadActivity);
async function loadActivity() {
  if ($('#overviewView').hidden) return;
  const id = $('#ovFilter').value;
  activityAt = Date.now();
  let list;
  try { list = await api('GET', `/api/activity?count=80${id ? `&server=${encodeURIComponent(id)}` : ''}`); }
  catch (e) { $('#ovActivity').replaceChildren(h('li', { class: 'dim' }, e.message)); return; }
  $('#ovActivity').replaceChildren(...(list.length ? list.map(activityRow)
    : [h('li', { class: 'activity-empty dim' }, 'Nothing yet. Starts, stops, crashes, backups and scheduled tasks show up here.')]));
}
function activityRow(e) {
  return h('li', { class: `activity lvl-${e.level}` },
    h('span', { class: 'dot' }),
    h('div', { class: 'activity-main' },
      h('div', {}, e.serverName ? h('strong', {}, e.serverName) : null, e.serverName ? ' ' : null, e.text),
      h('div', { class: 'dim' }, `${relTime(e.time)} · ${e.who}`)));
}
setInterval(() => { if (!$('#overviewView').hidden) loadActivity(); }, 30000);

// ---------------- header: players, CPU/memory history, port clashes ----------------

const statHist = { id: null, at: 0, points: [] };
function extrasHeader(s, notice) {
  // Players
  const box = $('#statPlayersBox');
  box.hidden = !s.players;
  if (s.players) {
    const b = $('#statPlayers');
    b.textContent = String(s.players.count);
    b.title = s.players.count ? s.players.names.join(', ') : 'Nobody is on the server';
    b.onclick = () => openMenu(b, s.players.count ? s.players.names.map(n => ({ label: n, onClick: () => copyText(n, `Copied ${n}`) }))
      : [{ label: 'Nobody is on the server', disabled: true }], { plain: true });
  }
  // Sparklines: refresh the history every 20 s while running.
  if (s.state === 'Running' && (statHist.id !== s.id || Date.now() - statHist.at > 20000)) {
    statHist.id = s.id; statHist.at = Date.now();
    api('GET', `/api/servers/${s.id}/history`).then(p => { if (statHist.id === s.id) { statHist.points = p; drawSparks(); } }).catch(() => { });
  }
  if (statHist.id !== s.id || s.state !== 'Running') { statHist.points = []; }
  drawSparks();
  // Port clash: only when nothing more important is showing.
  const clash = state.detail?.portConflicts || [];
  if (notice.hidden && clash.length && s.state !== 'Running') {
    notice.className = 'notice warn'; notice.hidden = false;
    const names = clash.map(c => c.name).join(', ');
    notice.append(h('span', {}, `${names} ${clash.length === 1 ? 'uses' : 'use'} the same port (${clash[0].port}). Only one of them can run at a time, and friends could join the wrong one.`),
      can('settings') || can('launch') ? h('button', { class: 'btn btn-small', type: 'button', onclick: () => setTab('settings') }, 'Change the port') : null);
  }
}
function drawSparks() {
  const pts = statHist.points;
  for (const [id, key, max] of [['#sparkCpu', 'cpu', 100], ['#sparkMem', 'mem', null]]) {
    const svg = $(id);
    if (!svg) continue;
    if (pts.length < 2) { svg.replaceChildren(); svg.parentElement.title = ''; continue; }
    const vals = pts.map(p => p[key]);
    const top = max ?? (Math.max(...vals) * 1.15 || 1);
    const d = vals.map((v, i) => `${(i / (vals.length - 1) * 80).toFixed(1)},${(17 - v / top * 15).toFixed(1)}`).join(' ');
    svg.innerHTML = `<polyline points="${d}" fill="none" stroke="currentColor" stroke-width="1.4" vector-effect="non-scaling-stroke" stroke-linejoin="round"/>`;
    const mins = Math.round((new Date(pts[pts.length - 1].t) - new Date(pts[0].t)) / 60000);
    svg.parentElement.title = key === 'cpu' ? `CPU over the last ${mins} min (peak ${Math.max(...vals).toFixed(0)}%)` : `Memory over the last ${mins} min (peak ${fmtBytes(Math.max(...vals))})`;
  }
}

// ---------------- Backups tab ----------------

async function backupNow(id) {
  toast('Backing up…');
  try {
    const b = await api('POST', `/api/servers/${id}/backups`);
    toast(`Backed up (${fmtBytes(b.size)}).`, 'good');
    if (state.tab === 'backups' && id === state.selectedId) loadBackups();
  } catch (e) { toast(e.message, 'bad'); }
}

async function loadBackups() {
  const panel = $('#panel-backups');
  const s = current();
  if (!s || panel.hidden) return;
  let d;
  try { d = await api('GET', `/api/servers/${s.id}/backups`); }
  catch (e) { panel.replaceChildren(h('div', { class: 'notice bad' }, e.message)); return; }
  if (s.id !== state.selectedId) return;
  const canMake = can('control'), canChange = can('settings');
  const saveSettings = async change => {
    try { await api('PUT', `/api/servers/${s.id}/backups/settings`, { folders: d.folders, keep: d.keep, onStop: d.onStop, ...change }); toast('Saved.', 'good'); loadBackups(); }
    catch (e) { toast(e.message, 'bad'); }
  };
  const keep = h('input', { class: 'input mono', type: 'number', min: 1, max: 500, value: d.keep, style: { width: '80px' }, disabled: !canChange || null });
  keep.addEventListener('change', () => saveSettings({ keep: Number(keep.value) || 10 }));
  const exists = d.items.filter(i => i.exists);

  panel.replaceChildren(h('div', { class: 'tab-scroll' },
    h('div', { class: 'toolbar' },
      canMake ? h('button', { class: 'btn btn-primary', type: 'button', disabled: d.busy || !exists.length || null, onclick: () => backupNow(s.id) }, d.busy ? 'Backing up…' : 'Back up now') : null,
      canBrowseHost() ? h('button', { class: 'btn', type: 'button', onclick: () => api('POST', `/api/servers/${s.id}/backups/browse`).catch(e => toast(e.message, 'bad')) }, 'Open backup folder') : null,
      h('span', { class: 'spacer' }),
      h('label', { class: 'inline dim' }, 'Keep the newest', keep),
      h('label', { class: 'inline dim' }, toggleEl(d.onStop, on => saveSettings({ onStop: on })), 'Back up when the server stops')),
    !exists.length ? h('div', { class: 'notice warn' }, 'None of the folders below exist yet, so there\'s nothing to back up. Start the server once to create its world, or choose its save folders.') : null,
    d.canSaveFirst ? null : h('p', { class: 'dim small' }, `${game(s.gameId).name} can't be asked to save from Emberkeep, so a backup of a running server holds what's on disk. For a guaranteed copy, back up while it's stopped (or turn on Back up when the server stops).`),
    h('section', { class: 'set-group' },
      h('h2', {}, 'Backups'),
      d.backups.length ? h('div', { class: 'backup-list' }, ...d.backups.map(b => h('div', { class: 'backup-row' },
        h('div', { class: 'backup-main' }, h('strong', {}, new Date(b.created).toLocaleString([], { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })),
          h('span', { class: 'dim' }, `${reasonLabel(b.reason)} · ${fmtBytes(b.size)}`)),
        canChange ? h('button', { class: 'btn btn-small', type: 'button', disabled: s.state !== 'Stopped' || null, title: s.state !== 'Stopped' ? 'Stop the server first' : '', onclick: () => restoreBackup(s, b) }, 'Restore') : null,
        canChange ? h('button', { class: 'btn btn-small btn-ghost', type: 'button', onclick: async () => {
          if (!(await confirmDialog('Delete this backup?', `The backup from ${new Date(b.created).toLocaleString()} will be deleted for good.`, { okText: 'Delete', danger: true }))) return;
          try { await api('DELETE', `/api/servers/${s.id}/backups/${encodeURIComponent(b.name)}`); loadBackups(); } catch (e) { toast(e.message, 'bad'); }
        } }, 'Delete') : null)))
        : h('p', { class: 'dim' }, 'No backups yet.'),
      h('p', { class: 'dim small' }, `Saved in ${d.folder}`)),
    h('section', { class: 'set-group' },
      h('h2', {}, 'What gets backed up'),
      h('p', {}, d.custom ? 'Your list of folders, plus the server\'s config files.' : `${game(s.gameId).name}'s usual save folders, plus the server's config files.`),
      h('div', { class: 'backup-items' }, ...d.items.map(i => h('div', { class: `backup-item${i.exists ? '' : ' missing'}` },
        h('span', { class: 'mono-path' }, i.resolved), h('span', { class: 'dim' }, i.exists ? (i.isDir ? 'folder' : 'file') : 'not found')))),
      canChange ? h('div', { class: 'row gap', style: { marginTop: '10px' } },
        h('button', { class: 'btn btn-small', type: 'button', onclick: () => foldersDialog(s, d) }, 'Choose folders'),
        d.custom ? h('button', { class: 'btn btn-small btn-ghost', type: 'button', onclick: () => saveSettings({ folders: null }) }, 'Use the game\'s folders') : null) : null)));
}
const reasonLabel = r => ({ manual: 'Made by hand', scheduled: 'Scheduled', 'server stopped': 'When the server stopped', 'server crashed': 'After a crash', 'before restore': 'Safety copy before a restore' })[r] || r;

function foldersDialog(s, d) {
  const t = h('textarea', { class: 'input mono', rows: 6, spellcheck: 'false', placeholder: 'world\nC:\\Users\\you\\AppData\\LocalLow\\Game\\Saves' });
  t.value = (d.folders || d.suggested).join('\n');
  const m = openModal({
    title: 'Folders to back up',
    body: h('div', { class: 'field-stack' },
      h('p', {}, 'One folder or file per line: relative to the server folder, or a full path. %APPDATA%, %LOCALAPPDATA% and %USERPROFILE% work too. Config files Emberkeep knows about are always included.'),
      t,
      h('div', { class: 'row gap' }, h('button', { class: 'btn btn-small', type: 'button', onclick: async () => {
        const p = await browse({ title: 'Choose a folder to back up', start: s.installDir });
        if (p) t.value = (t.value.trim() ? t.value.trim() + '\n' : '') + p;
      } }, 'Add a folder'), h('span', { class: 'dim small' }, d.suggested.length ? `The game's usual folders: ${d.suggested.join(', ')}` : ''))),
    foot: [h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => m.close() }, 'Cancel'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: async () => {
        const folders = t.value.split('\n').map(x => x.trim()).filter(Boolean);
        try { await api('PUT', `/api/servers/${s.id}/backups/settings`, { folders, keep: d.keep, onStop: d.onStop }); m.close(); toast('Saved.', 'good'); loadBackups(); }
        catch (e) { toast(e.message, 'bad'); }
      } }, 'Save')],
  });
}

async function restoreBackup(s, b) {
  const when = new Date(b.created).toLocaleString();
  if (!(await confirmDialog('Restore this backup?', `${s.name}'s world and config files go back to how they were on ${when}. What's there now is backed up first, so you can undo this.`, { okText: 'Restore' }))) return;
  toast('Restoring…');
  try { await api('POST', `/api/servers/${s.id}/backups/${encodeURIComponent(b.name)}/restore`); toast('Restored. Start the server to play on it.', 'good'); loadBackups(); }
  catch (e) { toast(e.message, 'bad'); loadBackups(); }
}

// ---------------- Schedule tab ----------------

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const ACTIONS = {
  restart: 'Restart the server', backup: 'Back up', command: 'Send a console command',
  stop: 'Stop the server', start: 'Start the server', update: 'Update the server files',
};
function describeWhen(t) {
  if (t.kind === 'interval') {
    const m = t.everyMinutes;
    return m % 1440 === 0 ? `every ${m / 1440 === 1 ? 'day' : `${m / 1440} days`}` : m % 60 === 0 ? `every ${m / 60 === 1 ? 'hour' : `${m / 60} hours`}` : `every ${m} minutes`;
  }
  const days = !t.days.length || t.days.length === 7 ? 'every day'
    : [1, 2, 3, 4, 5].every(d => t.days.includes(d)) && t.days.length === 5 ? 'on weekdays'
      : [0, 6].every(d => t.days.includes(d)) && t.days.length === 2 ? 'at weekends'
        : 'on ' + [...t.days].sort((a, b) => ((a + 6) % 7) - ((b + 6) % 7)).map(d => DAYS[d]).join(', ');
  return `${days} at ${t.time}`;
}

let schedule = null;
async function loadSchedule() {
  const panel = $('#panel-schedule');
  const s = current();
  if (!s || panel.hidden) return;
  try { schedule = await api('GET', `/api/servers/${s.id}/schedules`); }
  catch (e) { panel.replaceChildren(h('div', { class: 'notice bad' }, e.message)); return; }
  if (s.id !== state.selectedId) return;
  schedule.serverId = s.id;
  const canChange = can('settings'), canRun = can('control');
  const tasks = schedule.tasks;
  const save = async list => {
    try { await api('PUT', `/api/servers/${s.id}/schedules`, list); loadSchedule(); }
    catch (e) { toast(e.message, 'bad'); loadSchedule(); }
  };
  const plain = () => tasks.map(x => x.task);
  panel.replaceChildren(h('div', { class: 'tab-scroll' },
    h('div', { class: 'toolbar' },
      canChange ? h('button', { class: 'btn btn-primary', type: 'button', onclick: () => taskDialog(s, null, plain(), save) }, 'Add a task') : null,
      h('span', { class: 'spacer' }),
      h('span', { class: 'dim small' }, 'Tasks run while Emberkeep is open (in the window or the tray).')),
    tasks.length ? h('div', { class: 'task-list' }, ...tasks.map(({ task: t, next }) => h('div', { class: `task-row${t.enabled ? '' : ' off'}` },
      canChange ? toggleEl(t.enabled, on => save(plain().map(x => x.id === t.id ? { ...x, enabled: on } : x))) : null,
      h('div', { class: 'task-main' },
        h('strong', {}, `${ACTIONS[t.action] || t.action}${t.action === 'command' ? `: ${t.command}` : ''}`),
        h('span', { class: 'dim' }, [describeWhen(t),
          t.warn && schedule.canWarn && ['restart', 'stop', 'update'].includes(t.action) ? 'warns players first' : null,
          t.enabled && next ? `next ${relFuture(next)}` : t.enabled ? null : 'off'].filter(Boolean).join(' · ')),
        t.lastRun ? h('span', { class: `dim small${(t.lastResult || '').startsWith('Failed') ? ' bad-text' : ''}` }, `Last run ${relTime(t.lastRun)}: ${t.lastResult}`) : null),
      canRun ? h('button', { class: 'btn btn-small', type: 'button', onclick: async e => {
        e.target.disabled = true;
        try { const r = await api('POST', `/api/servers/${s.id}/schedules/${t.id}/run`); toast(r.result, r.result.startsWith('Failed') ? 'bad' : 'good'); } catch (err) { toast(err.message, 'bad'); }
        loadSchedule();
      } }, 'Run now') : null,
      canChange ? h('button', { class: 'btn btn-small btn-ghost', type: 'button', onclick: () => taskDialog(s, t, plain(), save) }, 'Edit') : null,
      canChange ? h('button', { class: 'btn btn-small btn-ghost', type: 'button', onclick: () => save(plain().filter(x => x.id !== t.id)) }, 'Delete') : null)))
      : h('div', { class: 'schedule-empty' },
        h('p', {}, 'Nothing scheduled. Some ideas:'),
        canChange ? h('div', { class: 'row gap wrap' },
          h('button', { class: 'btn btn-small', type: 'button', onclick: () => save([...plain(), { action: 'restart', kind: 'daily', time: '05:00', days: [], warn: true, enabled: true }]) }, 'Restart every day at 05:00'),
          h('button', { class: 'btn btn-small', type: 'button', onclick: () => save([...plain(), { action: 'backup', kind: 'interval', everyMinutes: 360, days: [], enabled: true }]) }, 'Back up every 6 hours'),
          schedule.canUpdate ? h('button', { class: 'btn btn-small', type: 'button', onclick: () => save([...plain(), { action: 'update', kind: 'daily', time: '04:30', days: [], warn: true, enabled: true }]) }, 'Update every night') : null) : null)));
}
function relFuture(iso) {
  const t = new Date(iso), m = Math.round((t - Date.now()) / 60000);
  if (m < 1) return 'any moment';
  if (m < 60) return `in ${m} min`;
  const sameDay = t.toDateString() === new Date().toDateString();
  return sameDay ? `at ${t.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : t.toLocaleString([], { weekday: 'long', hour: '2-digit', minute: '2-digit' });
}

function taskDialog(s, existing, list, save) {
  const t = existing ? structuredClone(existing) : { action: 'restart', kind: 'daily', time: '05:00', days: [], everyMinutes: 360, command: '', warn: true, enabled: true };
  const actionSel = h('select', { class: 'input' }, Object.entries(ACTIONS)
    .filter(([k]) => (k !== 'command' || schedule.canCommand) && (k !== 'update' || schedule.canUpdate))
    .map(([k, l]) => h('option', { value: k, ...(k === t.action ? { selected: true } : {}) }, l)));
  const kindSel = h('select', { class: 'input' }, h('option', { value: 'daily' }, 'At a time of day'), h('option', { value: 'interval' }, 'Every so often'));
  kindSel.value = t.kind;
  const time = h('input', { class: 'input mono', type: 'time', value: t.time, style: { maxWidth: '130px' } });
  const dayBtns = DAYS.map((d, i) => {
    const b = h('button', { class: `day-chip${t.days.includes(i) ? ' on' : ''}`, type: 'button', 'aria-pressed': String(t.days.includes(i)), onclick: () => {
      t.days = t.days.includes(i) ? t.days.filter(x => x !== i) : [...t.days, i];
      b.classList.toggle('on'); b.setAttribute('aria-pressed', String(t.days.includes(i)));
    } }, d);
    return b;
  });
  const days = h('div', { class: 'day-chips' }, ...[1, 2, 3, 4, 5, 6, 0].map(i => dayBtns[i]));
  const every = h('input', { class: 'input mono', type: 'number', min: 5, style: { maxWidth: '90px' } });
  const unit = h('select', { class: 'input', style: { maxWidth: '130px' } }, h('option', { value: '1' }, 'minutes'), h('option', { value: '60' }, 'hours'), h('option', { value: '1440' }, 'days'));
  const u = t.everyMinutes % 1440 === 0 ? 1440 : t.everyMinutes % 60 === 0 ? 60 : 1;
  unit.value = String(u); every.value = t.everyMinutes / u;
  const cmd = h('input', { class: 'input mono', type: 'text', value: t.command, spellcheck: 'false', placeholder: game(s.gameId).quickCommands?.[0] || 'say Hello' });
  const warn = h('input', { type: 'checkbox', ...(t.warn ? { checked: true } : {}) });
  const field = (label, ...kids) => h('label', { class: 'fs-field' }, h('span', {}, label), ...kids);
  const body = h('div', { class: 'field-stack' });
  const draw = () => {
    t.action = actionSel.value; t.kind = kindSel.value;
    body.replaceChildren(...[
      field('Task', actionSel),
      t.action === 'command' ? field('Command', cmd) : null,
      field('When', kindSel),
      t.kind === 'daily' ? h('div', { class: 'fs-field' }, h('span', {}, 'Time and days'), h('div', { class: 'row gap wrap' }, time, days),
        h('span', { class: 'dim small' }, 'No days picked means every day.')) : h('div', { class: 'fs-field' }, h('span', {}, 'Every'), h('div', { class: 'row gap' }, every, unit)),
      ['restart', 'stop', 'update'].includes(t.action) ? (schedule.canWarn
        ? h('label', { class: 'check' }, warn, ' Warn players in the game 10, 5 and 1 minutes before')
        : h('p', { class: 'dim small' }, `${game(s.gameId).name} has no way to message players from Emberkeep, so they won't be warned.`)) : null,
      t.action === 'restart' || t.action === 'stop' ? h('p', { class: 'dim small' }, 'Skipped if the server isn\'t running at the time.') : null,
    ].filter(Boolean));
  };
  actionSel.addEventListener('change', draw); kindSel.addEventListener('change', draw);
  draw();
  const m = openModal({
    title: existing ? 'Edit task' : 'Add a task', body, small: true,
    foot: [h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => m.close() }, 'Cancel'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: async () => {
        t.time = time.value || '05:00';
        t.everyMinutes = Math.max(5, Math.round(Number(every.value) * Number(unit.value)) || 60);
        t.command = cmd.value.trim();
        t.warn = warn.checked;
        if (t.action === 'command' && !t.command) { toast('Type the command to send.', 'bad'); return; }
        m.close();
        await save(existing ? list.map(x => x.id === t.id ? t : x) : [...list, t]);
      } }, existing ? 'Save' : 'Add')],
  });
}

// ---------------- Duplicate server ----------------

function duplicateDialog() {
  const s = current();
  if (!s) return;
  const name = h('input', { class: 'input', type: 'text', value: `${s.name} (copy)`, autofocus: true });
  const copy = h('input', { type: 'radio', name: 'dupmode', value: 'copy', checked: true });
  const same = h('input', { type: 'radio', name: 'dupmode', value: 'same' });
  const parent = s.installDir.replace(/[\\/][^\\/]+[\\/]?$/, '');
  const dir = h('input', { class: 'input mono', type: 'text', spellcheck: 'false', value: `${parent}\\${s.installDir.split(/[\\/]/).filter(Boolean).pop()} 2` });
  const dirRow = h('div', { class: 'with-btn' }, dir, h('button', { class: 'btn', type: 'button', onclick: async () => {
    const p = await browse({ title: 'Choose an empty folder for the copy', start: parent });
    if (p) dir.value = p;
  } }, 'Browse'));
  const sync = () => { dirRow.hidden = !copy.checked; };
  copy.addEventListener('change', sync); same.addEventListener('change', sync);
  const m = openModal({
    title: `Duplicate ${s.name}`, small: true,
    body: h('div', { class: 'field-stack' },
      h('label', { class: 'fs-field' }, h('span', {}, 'Name'), name),
      h('label', { class: 'check' }, copy, ' Copy the server\'s files to a new folder (a separate world)'),
      dirRow,
      h('label', { class: 'check' }, same, ' Use the same folder (just another set of launch settings)'),
      s.state !== 'Stopped' ? h('p', { class: 'dim small' }, 'Stop the server first to copy its files.') : null,
      h('p', { class: 'dim small' }, 'Remember to give the copy its own port in Settings, so both can run at once.')),
    foot: [h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => m.close() }, 'Cancel'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: async () => {
        try {
          const r = await api('POST', `/api/servers/${s.id}/duplicate`, { name: name.value, dir: copy.checked ? dir.value : null, copyFiles: copy.checked });
          m.close();
          if (r.job) { toast(`Copying ${name.value}…`); watchInstalls(r.job.id); }
          else { toast(`Added ${r.server.summary.name}.`, 'good'); state.servers.push(r.server.summary); select(r.server.summary.id); }
        } catch (e) { toast(e.message, 'bad'); }
      } }, 'Duplicate')],
  });
}

// ---------------- Notes (server Settings) ----------------

function notesGroup(c) {
  const t = h('textarea', { class: 'input', rows: 3, placeholder: 'Anything worth remembering: the password friends use, mods, what to do after an update…' });
  t.value = c.notes || '';
  t.addEventListener('input', () => c.notes = t.value);
  return h('section', { class: 'set-group' }, h('h2', {}, 'Notes'), h('p', {}, 'Only people who can change this server\'s settings see these.'), t);
}

// ---------------- App settings: backups, notifications, moving to another PC ----------------

function extrasAppGroups(st, putSettings) {
  const groups = [];
  // Backup folder
  const broot = h('input', { class: 'input mono', type: 'text', value: st.backupRoot || '', spellcheck: 'false' });
  const saveRoot = v => putSettings({ backupRoot: v }, 'Saved. New backups go there; older ones stay where they were.').catch(e => toast(e.message, 'bad'));
  broot.addEventListener('change', () => saveRoot(broot.value));
  groups.push(h('div', { class: 'set-group' },
    h('h2', {}, 'Backups'),
    h('p', {}, 'Each server\'s Backups tab saves copies of its world and config files. Its Schedule tab can make them automatically.'),
    h('div', { class: 'set-rows' }, h('div', { class: 'set-row' }, h('label', {}, 'Backup folder'),
      h('div', {}, h('div', { class: 'row gap' }, broot, h('button', { class: 'btn btn-small', type: 'button', onclick: async () => {
        const p = await browse({ title: 'Choose where backups go', start: broot.value });
        if (p) { broot.value = p; saveRoot(p); }
      } }, 'Browse')), h('div', { class: 'help' }, 'A different drive keeps backups safe if this one fails.'))))));

  // Notifications
  const n = structuredClone(st.notifications || {});
  const hook = h('input', { class: 'input mono', type: 'url', value: n.webhookUrl || '', spellcheck: 'false', placeholder: 'https://discord.com/api/webhooks/…' });
  const saveN = async ok => {
    n.webhookUrl = hook.value.trim();
    try { await api('PUT', '/api/notifications', n); st.notifications = n; if (ok) toast(ok, 'good'); } catch (e) { toast(e.message, 'bad'); }
  };
  hook.addEventListener('change', () => saveN(hook.value.trim() ? 'Saved. Use Send a test to check it.' : 'Discord notifications are off.'));
  const kind = (key, label) => h('label', { class: 'check' }, (() => {
    const cb = h('input', { type: 'checkbox', ...(n[key] ? { checked: true } : {}) });
    cb.addEventListener('change', () => { n[key] = cb.checked; saveN(); });
    return cb;
  })(), ' ', label);
  groups.push(h('div', { class: 'set-group' },
    h('h2', {}, 'Notifications'),
    h('p', {}, 'Hear about crashes and other events even when Emberkeep is in the tray, or away from the PC.'),
    h('div', { class: 'set-rows' },
      h('div', { class: 'set-row win-only' }, h('label', {}, 'Windows notifications'),
        h('div', { class: 'inline' }, toggleEl(n.windows, on => { n.windows = on; saveN(); }), h('span', { class: 'dim' }, 'Shown from Emberkeep\'s tray icon on this PC'))),
      h('div', { class: 'set-row' }, h('label', {}, 'Discord channel', h('span', { class: 'label-sub' }, 'Webhook URL')),
        h('div', {}, h('div', { class: 'row gap' }, hook, h('button', { class: 'btn btn-small', type: 'button', onclick: async () => {
          await saveN();
          try { const r = await api('POST', '/api/notifications/test'); toast(r.webhook === 'sent' ? 'Sent a test to Discord.' : 'Sent a test. Add a Discord webhook to get them there too.', 'good'); }
          catch (e) { toast(e.message, 'bad'); }
        } }, 'Send a test')),
          h('div', { class: 'help' }, 'In Discord: channel settings → Integrations → Webhooks → New Webhook → Copy Webhook URL.'))),
      h('div', { class: 'set-row' }, h('label', {}, 'Tell me about'),
        h('div', { class: 'check-list' }, kind('crashes', 'Crashes'), kind('startStop', 'Servers starting and stopping'), kind('backups', 'Backups (failures are always sent)'),
          kind('updates', 'Installs and updates'), kind('schedules', 'Scheduled tasks (failures are always sent)'))))));

  // Export / import
  const file = h('input', { type: 'file', accept: '.json,application/json', hidden: true });
  file.addEventListener('change', async () => {
    const f = file.files[0];
    if (!f) return;
    try {
      const data = JSON.parse(await f.text());
      const r = await api('POST', '/api/import', { servers: data.servers });
      toast(`Imported ${r.added} server${r.added === 1 ? '' : 's'}${r.skipped ? `; ${r.skipped} were already here` : ''}.`, r.added ? 'good' : '');
    } catch (e) { toast(e instanceof SyntaxError ? 'That isn\'t an Emberkeep export file.' : e.message, 'bad'); }
    file.value = '';
  });
  groups.push(h('div', { class: 'set-group' },
    h('h2', {}, 'Moving to another PC'),
    h('p', {}, 'Export saves your server list with each server\'s settings, schedules and notes. Import it into Emberkeep on another PC (servers whose folder is already listed are skipped). It doesn\'t include the servers\' files, accounts or the Discord bot.'),
    h('div', { class: 'row gap' },
      h('a', { class: 'btn btn-small', href: '/api/export', download: '' }, 'Export servers'),
      h('button', { class: 'btn btn-small', type: 'button', onclick: () => file.click() }, 'Import servers'), file)));
  return groups;
}

function jobVerb(j) { return { install: 'Installing', copy: 'Copying' }[j.kind] || 'Updating'; }

// ---------------- Basic / Advanced ----------------
// Basic (the default) hides technical details: anything marked .adv. The choice is saved per browser.
function setUiMode(mode) {
  mode = mode === 'advanced' ? 'advanced' : 'basic';
  document.documentElement.setAttribute('data-mode', mode);
  try { localStorage.setItem('emberkeep-mode', mode); } catch { }
  $$('.mode-switch .seg-btn').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.uiMode === mode)));
}
$$('.mode-switch .seg-btn').forEach(b => b.onclick = () => setUiMode(b.dataset.uiMode));
setUiMode(document.documentElement.getAttribute('data-mode'));
