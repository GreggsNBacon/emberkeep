'use strict';
/* Hostbay UI: plain JS, no build step. Talks to the embedded server over /api and Server-Sent Events. */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

const state = {
  games: {},
  servers: [],
  selectedId: localStorage.getItem('hb.selected') || null,
  tab: localStorage.getItem('hb.tab') || 'console',
  detail: null,
  logSource: null,
  logLines: [],
  cmdHistory: [],
  cmdHistoryPos: -1,
  editor: null,      // { path, name, original, text, format, mode, modified, model }
  configs: null,
  me: null,          // the signed-in account: { username, isLocal, full, permissions, allServers, canAddServers }
};

// What the signed-in account may do on the open server. The server checks every action itself;
// the page only hides what would be refused.
const can = perm => !!(state.me?.full || state.detail?.can?.includes(perm));

// ---------------- hiding IP addresses (screen sharing, streaming) ----------------
// Masks addresses wherever they're shown; Copy buttons still copy the real address.
let hideIps = localStorage.getItem('hb.hideIps') === '1';
const IP_RE = /\b\d{1,3}(?:\.\d{1,3}){3}\b/g;
const showIp = text => (hideIps ? String(text).replace(IP_RE, '•••.•••.•••.•••') : String(text));
function setHideIps(on) {
  hideIps = on;
  localStorage.setItem('hb.hideIps', on ? '1' : '0');
  document.body.classList.toggle('ips-hidden', on);
  renderJoin();
  toast(on ? 'IP addresses are hidden. Copy buttons still copy them.' : 'IP addresses are shown.');
}

// ---------------- helpers ----------------

function h(tag, attrs = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'html') el.innerHTML = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) {
    if (kid == null || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
  return el;
}

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: { 'X-Hostbay': '1', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
    credentials: 'same-origin',
  });
  if (res.status === 401) { location.href = '/login'; throw new Error('Signed out'); }
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    const err = new Error((data && data.error) || `Request failed (${res.status})`);
    err.status = res.status; err.data = data;
    throw err;
  }
  return data;
}

function toast(msg, kind = '') {
  if (typeof msg === 'string') msg = showIp(msg); // never flash a hidden address in a message
  const t = h('div', { class: `toast ${kind}` }, msg);
  $('#toasts').append(t);
  setTimeout(() => t.remove(), kind === 'bad' || kind === 'warn' ? 7000 : 3500);
}

function fmtUptime(s) {
  if (!s) return '–';
  const d = Math.floor(s / 86400), hh = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
  if (d) return `${d}d ${hh}h`;
  if (hh) return `${hh}h ${m}m`;
  if (m) return `${m}m ${s % 60}s`;
  return `${s}s`;
}
function fmtBytes(b) {
  if (!b) return '–';
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return `${b.toFixed(b >= 10 || i === 0 ? 0 : 1)} ${u[i]}`;
}
function stateLabel(s) {
  if (s.state === 'Running') return s.adopted ? 'Running (started outside Hostbay)' : 'Running';
  if (s.state === 'Stopping') return 'Stopping…';
  if (s.state === 'Starting') return 'Restarting soon…';
  if (s.lastError) return 'Failed to start';
  if (s.lastExitCode != null && s.lastExitCode !== 0) return `Stopped (exit code ${s.lastExitCode})`;
  return 'Stopped';
}
// The desktop window signs in with a launch token, so signing out there would strand it.
const isLocal = ['127.0.0.1', 'localhost', '[::1]'].includes(location.hostname);
const game = id => state.games[id] || state.games.generic || { name: id, badge: '?', color: '#8a94a6' };
const current = () => state.servers.find(s => s.id === state.selectedId);
const isCrashed = s => s.state === 'Stopped' && (s.lastError || (s.lastExitCode != null && s.lastExitCode !== 0));

// The game's wide library art behind the server header. Shown only once it has loaded.
function setHero(appId) {
  const head = $('#serverHead');
  const key = String(appId || '');
  if (head.dataset.hero === key) return;
  head.dataset.hero = key;
  head.classList.remove('has-hero');
  head.style.removeProperty('--hero');
  if (!appId) return;
  const img = new Image();
  img.onload = () => {
    if (head.dataset.hero !== key) return; // switched servers meanwhile
    head.style.setProperty('--hero', `url("${img.src}")`);
    head.classList.add('has-hero');
  };
  img.src = artUrl(appId, 'hero');
}

// URL of cached Steam artwork. kind: header | hero | cover | logo
const artUrl = (appId, kind) => `/api/art/${appId}/${kind}`;

// Game badge: the game's cover art when Steam has it, otherwise its two letters. A dot shows server status.
function badge(gameId, s, big, artAppId) {
  const g = game(gameId);
  const el = h('div', { class: `badge${big ? ' big' : ''}` }, g.badge);
  el.style.setProperty('--game', g.color);
  if (artAppId) {
    const img = h('img', { class: 'badge-art', src: artUrl(artAppId, 'cover'), alt: '', loading: 'lazy', draggable: 'false' });
    img.onerror = () => { img.remove(); el.classList.remove('has-art'); };
    el.classList.add('has-art');
    el.append(img);
  }
  setBadgeState(el, s);
  return el;
}
function setBadgeState(el, s) {
  el.dataset.state = s?.state || 'Stopped';
  el.dataset.crashed = s && isCrashed(s) ? 'true' : 'false';
}

// The Steam store header (wide) for scan results, falling back to the letter badge.
function candArt(gameId, artAppId) {
  // Custom servers have no letters worth showing, so they get a plain placeholder instead.
  const fallback = () => gameId === 'generic' ? h('span', { class: 'no-art' }, 'No picture') : badge(gameId);
  if (!artAppId) return h('div', { class: 'cand-art' }, fallback());
  const wrap = h('div', { class: 'cand-art' });
  const img = h('img', { src: artUrl(artAppId, 'header'), alt: '', loading: 'lazy' });
  img.onerror = () => wrap.replaceChildren(fallback());
  wrap.append(img);
  return wrap;
}

// Badges are reused between redraws (every couple of seconds) so their images don't flicker.
const badgeCache = new Map();
function cachedBadge(key, gameId, s, big, artAppId) {
  const sig = `${gameId}|${artAppId || ''}|${big ? 1 : 0}`;
  let el = badgeCache.get(key);
  if (!el || el.dataset.sig !== sig) {
    el = badge(gameId, s, big, artAppId);
    el.dataset.sig = sig;
    badgeCache.set(key, el);
  }
  setBadgeState(el, s);
  return el;
}

function humanize(key) {
  const s = key
    .replace(/^[+\-.!]/, '')
    .replace(/^b(?=[A-Z])/, '') // Unreal's bool prefix: bEnableX -> EnableX
    .replace(/[_\-.]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .trim().toLowerCase();
  const acronyms = { ip: 'IP', id: 'ID', rcon: 'RCON', url: 'URL', pvp: 'PvP', pve: 'PvE', ui: 'UI', exp: 'XP', api: 'API', motd: 'MOTD', eula: 'EULA', uid: 'UID' };
  const out = s.split(' ').map(w => acronyms[w] || w).join(' ');
  return out.charAt(0).toUpperCase() + out.slice(1);
}

const icons = {
  folder: '<svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true"><path fill="currentColor" d="M2 5.5A1.5 1.5 0 0 1 3.5 4h4l1.6 1.8h7.4A1.5 1.5 0 0 1 18 7.3v7.2a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 2 14.5z"/></svg>',
  file: '<svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true"><path fill="currentColor" d="M5 2h6l4 4v11a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1zm6 1v4h4"/></svg>',
  drive: '<svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true"><path fill="currentColor" d="M3 6a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zm11 6.5a1 1 0 1 0 0 .01z"/></svg>',
  copy: '<svg width="14" height="14" viewBox="0 0 20 20" aria-hidden="true"><rect x="7" y="7" width="10" height="10" rx="2" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M13 4.5V4a1.5 1.5 0 0 0-1.5-1.5h-7A1.5 1.5 0 0 0 3 4v7a1.5 1.5 0 0 0 1.5 1.5H5" fill="none" stroke="currentColor" stroke-width="1.8"/></svg>',
  caret: '<svg width="12" height="12" viewBox="0 0 20 20" aria-hidden="true"><path d="M5 8l5 5 5-5" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  eye: '<svg width="14" height="14" viewBox="0 0 20 20" aria-hidden="true"><path d="M1.5 10S4.5 4 10 4s8.5 6 8.5 6-3 6-8.5 6S1.5 10 1.5 10z" fill="none" stroke="currentColor" stroke-width="1.7"/><circle cx="10" cy="10" r="2.6" fill="currentColor"/></svg>',
  eyeOff: '<svg width="14" height="14" viewBox="0 0 20 20" aria-hidden="true"><path d="M1.5 10S4.5 4 10 4s8.5 6 8.5 6-3 6-8.5 6S1.5 10 1.5 10z" fill="none" stroke="currentColor" stroke-width="1.7"/><path d="M3 17L17 3" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/></svg>',
};

// ---------------- clipboard & network addresses ----------------

// navigator.clipboard only exists on https or localhost, so remote browsers on http://192.168… need the old way.
async function copyText(text, message) {
  let ok = false;
  try { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); ok = true; } } catch { }
  if (!ok) {
    const ta = h('textarea', { style: { position: 'fixed', top: '-1000px', opacity: '0' }, readonly: true });
    ta.value = text;
    document.body.append(ta);
    ta.select();
    try { ok = document.execCommand('copy'); } catch { }
    ta.remove();
  }
  if (ok) toast(message || `Copied ${text}`, 'good');
  else window.prompt('Copy this:', text);
}

const network = { local: null, publicIp: undefined, loading: null };

// Local addresses are cheap; the public IP is only looked up (via ipify.org) when asked for.
async function loadNetwork(includePublic) {
  if (network.local && (!includePublic || network.publicIp !== undefined)) return network;
  const r = await api('GET', `/api/network${includePublic ? '?includePublic=true' : ''}`);
  network.local = r.local;
  if (includePublic) network.publicIp = r.publicIp || null;
  return network;
}

function primaryLocal() { return network.local?.find(a => a.primary) || network.local?.find(a => a.kind === 'lan') || network.local?.[0]; }

// Keys like ip, ServerIP, PublicIP, server-ip, bindAddress, multihome, or any value that already looks like an IPv4 address.
function isIpField(f) {
  const k = f.key.toLowerCase().replace(/[^a-z]/g, '');
  const keyLooksIp = /^(|server|public|local|bind|host|lan|wan|external|internal|query|game|listen|rcon|steam)ip$/.test(k)
    || /^(ipaddress|bindaddress|serveraddress|hostaddress|publicaddress|listenaddress|multihome|bindip|serverip)$/.test(k);
  const valueLooksIp = /^\s*\d{1,3}(\.\d{1,3}){3}\s*$/.test(String(f.value));
  return f.type !== 'boolean' && (keyLooksIp || valueLooksIp);
}
// IP fields not named public/external/advertised are usually the address the server listens on (bind address).
const isBindField = f => !/public|external|wan|advertis|announce/i.test(f.key);

// A small dropdown anchored under a button. items: [{ label, sub, onClick }] or 'sep'.
function openMenu(anchor, items) {
  document.querySelector('.menu-pop')?.remove();
  const menu = h('div', { class: 'menu-pop', role: 'menu' });
  for (const it of items) {
    if (it === 'sep') { menu.append(h('div', { class: 'menu-sep' })); continue; }
    if (it.heading) { menu.append(h('div', { class: 'menu-head' }, it.heading)); continue; }
    menu.append(h('button', { class: 'menu-item', type: 'button', role: 'menuitem', disabled: it.disabled || null,
      onclick: () => { close(); it.onClick?.(); } },
      h('span', { class: 'mi-label' }, it.label), it.sub ? h('span', { class: 'mi-sub' }, it.sub) : null));
  }
  document.body.append(menu);
  const r = anchor.getBoundingClientRect();
  const left = Math.min(r.left, window.innerWidth - menu.offsetWidth - 12);
  const top = r.bottom + 6 + menu.offsetHeight > window.innerHeight ? r.top - menu.offsetHeight - 6 : r.bottom + 6;
  Object.assign(menu.style, { left: `${Math.max(8, left)}px`, top: `${Math.max(8, top)}px` });
  const close = () => { menu.remove(); document.removeEventListener('mousedown', outside, true); document.removeEventListener('keydown', esc, true); };
  const outside = e => { if (!menu.contains(e.target) && !anchor.contains(e.target)) close(); };
  const esc = e => { if (e.key === 'Escape') { e.stopPropagation(); close(); anchor.focus(); } };
  setTimeout(() => { document.addEventListener('mousedown', outside, true); document.addEventListener('keydown', esc, true); });
  menu.querySelector('.menu-item:not([disabled])')?.focus();
  return { close, menu };
}

// Menu entries for every address of this PC: local adapters, VPNs, then the public IP.
async function addressMenuItems(format, pick) {
  try { await loadNetwork(true); } catch (e) { toast(e.message, 'bad'); }
  const items = [];
  const lan = (network.local || []).filter(a => a.kind === 'lan');
  const vpn = (network.local || []).filter(a => a.kind === 'vpn');
  if (lan.length) {
    items.push({ heading: 'Friends on your home network' });
    for (const a of lan) items.push({ label: showIp(format(a.address)), sub: a.adapter + (a.primary ? ' (main connection)' : ''), onClick: () => pick(a.address, 'local') });
  }
  if (vpn.length) {
    items.push({ heading: 'Friends on your VPN' });
    for (const a of vpn) items.push({ label: showIp(format(a.address)), sub: a.adapter, onClick: () => pick(a.address, 'vpn') });
  }
  items.push({ heading: 'Friends over the internet' });
  items.push(network.publicIp
    ? { label: showIp(format(network.publicIp)), sub: 'Public IP. Needs port forwarding on your router.', onClick: () => pick(network.publicIp, 'public') }
    : { label: 'Public IP unavailable', sub: 'Couldn\'t reach ipify.org to look it up.', disabled: true });
  return items;
}

// ---------------- modals ----------------

function openModal({ title, body, foot, small, onClose }) {
  const root = $('#modalRoot');
  const close = () => { backdrop.remove(); document.removeEventListener('keydown', onKey); onClose?.(); };
  const onKey = e => { if (e.key === 'Escape') close(); };
  const modal = h('div', { class: `modal${small ? ' small' : ''}`, role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
    h('div', { class: 'modal-head' }, h('h2', {}, title),
      h('button', { class: 'btn btn-icon btn-ghost', type: 'button', 'aria-label': 'Close', onclick: close,
        html: '<svg viewBox="0 0 20 20" width="16" height="16"><path d="M5 5l10 10M15 5L5 15" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>' })),
    h('div', { class: 'modal-body' }, body),
    foot ? h('div', { class: 'modal-foot' }, foot) : null);
  const backdrop = h('div', { class: 'modal-backdrop', onmousedown: e => { if (e.target === backdrop) close(); } }, modal);
  root.append(backdrop);
  document.addEventListener('keydown', onKey);
  setTimeout(() => (modal.querySelector('[autofocus]') || modal.querySelector('.modal-body input, .modal-foot .btn-primary'))?.focus(), 30);
  return { close, modal };
}

function confirmDialog(title, message, { okText = 'OK', danger = false } = {}) {
  return new Promise(resolve => {
    let done = false;
    const finish = v => { if (!done) { done = true; resolve(v); m.close(); } };
    const ok = h('button', { class: `btn ${danger ? 'btn-danger' : 'btn-primary'}`, type: 'button', onclick: () => finish(true) }, okText);
    const m = openModal({
      title, small: true,
      body: h('p', {}, message),
      foot: [h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => finish(false) }, 'Cancel'), ok],
      onClose: () => { if (!done) { done = true; resolve(false); } },
    });
    setTimeout(() => ok.focus(), 40);
  });
}

// ---------------- sidebar ----------------

function renderSidebar() {
  const list = $('#serverList');
  list.replaceChildren();
  if (!state.servers.length) {
    list.append(h('div', { class: 'list-empty' }, 'No servers yet.'));
    return;
  }
  for (const s of state.servers) {
    const g = game(s.gameId);
    const sub = s.state === 'Running' ? `Running for ${fmtUptime(s.uptimeSeconds)}` : stateLabel(s);
    const item = h('button', {
      class: `server-item${s.id === state.selectedId ? ' active' : ''}`, type: 'button',
      'aria-current': s.id === state.selectedId ? 'true' : null,
      onclick: () => { select(s.id); $('#app').classList.remove('menu'); },
    },
      cachedBadge(`side-${s.id}`, s.gameId, s, false, s.artAppId),
      h('span', { class: 'meta' }, h('span', { class: 'name' }, s.name), h('span', { class: 'sub' }, sub)),
      s.state === 'Running' ? h('span', { class: 'cpu' }, `${Math.round(s.cpu)}%`) : h('span'));
    item.style.setProperty('--game', g.color);
    list.append(item);
  }
}

// ---------------- server view ----------------

function select(id) {
  if (state.selectedId === id && state.detail) return;
  if (state.editor && isDirty() && !window.confirm('You have unsaved changes in a config file. Discard them?')) return;
  state.selectedId = id;
  localStorage.setItem('hb.selected', id || '');
  state.detail = null; state.editor = null; state.configs = null;
  $("#joinValue").textContent = "–";
  renderAll();
  loadDetail();
  connectLogs();
}

async function loadDetail() {
  const id = state.selectedId;
  if (!id) return;
  try {
    const d = await api('GET', `/api/servers/${id}`);
    if (id !== state.selectedId) return;
    state.detail = d;
    renderHeader();
    renderQuickCommands();
    renderJoin();
    renderPorts();
    if (state.tab === 'settings') renderSettings();
    if (state.tab === 'configs') loadConfigs();
  } catch (e) { toast(e.message, 'bad'); }
}

function renderAll() {
  renderSidebar();
  const s = current();
  $('#emptyState').hidden = !!state.servers.length;
  $('#serverView').hidden = !s;
  if (!s && state.servers.length) { select(state.servers[0].id); return; }
  if (!s) return;
  renderHeader();
  setTab(state.tab, true);
}

// ---------------- router ports (PortHop or built in) ----------------

const portsState = { status: null, busy: false, forId: null };
const PORT_LABELS = { open: 'Open', partial: 'Partly open', closed: 'Closed', unknown: 'Unknown', none: 'None set', working: 'Working…' };

// "Router ports" in the header: state, an Open/Close button and a menu with details.
async function renderPorts(refresh = false) {
  const s = current();
  const value = $('#portsValue');
  if (!s) { value.textContent = '–'; return; }
  if (portsState.forId !== s.id) { portsState.status = null; portsState.forId = s.id; value.replaceChildren(h('span', { class: 'dim join-hint' }, 'Checking…')); }
  if (!portsState.busy) {
    try {
      const st = await api('GET', `/api/servers/${s.id}/ports${refresh ? '?refresh=true' : ''}`);
      if (current()?.id !== s.id) return;
      portsState.status = st;
    } catch (e) { value.replaceChildren(h('span', { class: 'dim join-hint' }, e.message)); return; }
  }
  drawPorts();
}

function drawPorts() {
  const st = portsState.status, s = current(), value = $('#portsValue');
  if (!s || !st) return;
  const state = portsState.busy ? 'working' : st.state;
  value.replaceChildren();
  const pill = h('span', { class: `ports-pill ports-${state}`, title: st.message || `${st.spec || 'No ports'} via ${st.backendLabel}` },
    h('span', { class: 'ports-dot' }), PORT_LABELS[state] || state);
  value.append(pill);
  if (st.state === 'none') {
    if (can('settings')) value.append(h('button', { class: 'link join-hint', type: 'button', onclick: () => setTab('settings') }, 'Set ports'));
    return;
  }
  const wantOpen = !(st.state === 'open');
  const act = h('button', { class: 'btn btn-small', type: 'button', disabled: portsState.busy || null,
    title: wantOpen ? `Open ${st.spec} on your router` : `Close ${st.spec} on your router` }, wantOpen ? 'Open' : 'Close');
  act.onclick = () => changePorts(wantOpen);
  const more = h('button', { class: 'btn btn-tiny', type: 'button', title: 'Port details', 'aria-haspopup': 'menu', html: icons.caret });
  more.onclick = () => {
    const items = [{ heading: `${st.spec} (${st.source})` }];
    for (const p of st.ports) items.push({ label: `${p.open ? '✓' : '○'}  ${p.protocol} ${p.port}`, sub: p.error || (p.open ? 'Forwarded to this PC' : 'Not forwarded'), disabled: true });
    if (st.message) items.push({ label: 'Note', sub: st.message, disabled: true });
    items.push('sep', { heading: `Handled by ${st.backendLabel}` },
      { label: 'Check again', sub: 'Ask the router for the current state', onClick: () => renderPorts(true) });
    if (st.backend === 'porthop' && state.me?.full) items.push({ label: 'Open PortHop', sub: 'See every forward and its settings', onClick: openPortHop });
    if (can('settings')) items.push({ label: 'Change ports', sub: 'Settings → Router ports', onClick: () => setTab('settings') });
    openMenu(more, items);
  };
  value.append(...(can('ports') ? [act] : []), more);
}

async function changePorts(open) {
  const s = current();
  if (!s || portsState.busy) return;
  portsState.busy = true; drawPorts();
  try {
    const st = await api('POST', `/api/servers/${s.id}/ports/${open ? 'open' : 'close'}`);
    if (current()?.id === s.id) portsState.status = st;
    if (st.message) toast(st.message, 'bad');
    else toast(open ? `Opened ${st.spec} via ${st.backendLabel}.` : `Closed ${st.spec}.`, 'good');
  } catch (e) { toast(e.message, 'bad'); }
  portsState.busy = false; drawPorts();
}

async function openPortHop() {
  try { await api('POST', '/api/porthop/show'); toast('Opening PortHop…'); }
  catch (e) { toast(e.message, 'bad'); }
}

// Where a join port came from, in words: "the game's default", "from server.properties"…
function portSource(j, capital) {
  const t = j.source === 'game default' ? "the game's default" : j.source === 'set in Settings' ? 'set in Settings' : `from ${j.source}`;
  return capital ? t.charAt(0).toUpperCase() + t.slice(1) : t;
}

// "Join address": this PC's main IP plus the port players connect on, with a menu for other addresses.
async function renderJoin() {
  const label = $('#joinLabel'), value = $('#joinValue');
  const d = state.detail, s = current();
  if (!d || !s || d.config.id !== s.id) { value.textContent = '–'; return; }
  const j = d.join || {};
  value.replaceChildren();

  if (j.code) {
    label.textContent = j.codeLabel || 'Join code';
    value.append(h('span', { class: 'join-addr' }, j.code),
      h('button', { class: 'btn btn-tiny', type: 'button', title: `Copy the ${j.codeLabel || 'code'}`, html: icons.copy,
        onclick: () => copyText(j.code, `Copied the ${j.codeLabel || 'code'}.`) }));
    return;
  }
  label.textContent = 'Join address';
  if (j.codeLabel) { value.append(h('span', { class: 'dim join-hint' }, `Start the server once to get its ${j.codeLabel}`)); return; }
  if (!j.port) {
    value.append(h('button', { class: 'link join-hint', type: 'button', onclick: () => setTab('settings') }, 'Set the join port in Settings'));
    return;
  }
  // Default to the public IP (what friends elsewhere need); fall back to the local one if it can't be looked up.
  try { await loadNetwork(true); } catch { }
  if (state.detail !== d) return; // switched servers while loading
  const isPublic = !!network.publicIp;
  const ip = network.publicIp || primaryLocal()?.address || '127.0.0.1';
  const addr = `${ip}:${j.port}`;
  label.textContent = isPublic ? 'Join address (internet)' : 'Join address (home network)';
  const copyBtn = h('button', { class: 'btn btn-tiny', type: 'button', html: icons.copy,
    title: isPublic ? 'Copy the address for friends over the internet' : 'Copy the address for friends on your network',
    onclick: () => copyText(addr, isPublic ? `Copied ${addr}. Friends outside your home need the port forwarded on your router.` : `Copied ${addr}`) });
  const more = h('button', { class: 'btn btn-tiny', type: 'button', title: 'Other addresses: home network or VPN', 'aria-haspopup': 'menu', html: icons.caret });
  more.onclick = async () => {
    const items = await addressMenuItems(a => `${a}:${j.port}`, a => copyText(`${a}:${j.port}`));
    items.push('sep', { label: `Port ${j.port}`, sub: `${portSource(j, true)}. Change it under Settings → Join port.`, disabled: true });
    openMenu(more, items);
  };
  const eye = h('button', { class: 'btn btn-tiny', type: 'button', 'aria-pressed': hideIps ? 'true' : 'false',
    title: hideIps ? 'Show IP addresses' : 'Hide IP addresses (Copy still works)', html: hideIps ? icons.eyeOff : icons.eye, onclick: () => setHideIps(!hideIps) });
  value.append(h('span', { class: 'join-addr', title: `Port: ${portSource(j)}` }, showIp(addr)), copyBtn, more, eye);
}

function renderHeader() {
  const s = current();
  if (!s) return;
  const g = game(s.gameId);
  $('#serverView').style.setProperty('--game', g.color);
  const hb = cachedBadge('head', s.gameId, s, true, s.artAppId);
  if ($('#headBadge') !== hb) { hb.id = 'headBadge'; $('#headBadge').replaceWith(hb); }
  setHero(s.artAppId);
  $('#headName').textContent = s.name;
  $('#headGame').textContent = g.name;
  $('#headPath').textContent = s.installDir;
  document.title = `${s.name} | Hostbay`;

  const st = $('#statState');
  st.textContent = stateLabel(s);
  st.className = isCrashed(s) ? 'state-crashed' : `state-${s.state}`;
  $('#statUptime').textContent = s.state === 'Running' ? fmtUptime(s.uptimeSeconds) : '–';
  $('#statCpu').textContent = s.state === 'Running' ? `${s.cpu.toFixed(1)}%` : '–';
  $('#statMem').textContent = s.state === 'Running' ? fmtBytes(s.memory) : '–';
  $('#statPid').textContent = s.pid ?? '–';

  const running = s.state === 'Running', busy = s.state === 'Stopping';
  $('#startBtn').disabled = s.state !== 'Stopped';
  $('#stopBtn').disabled = !(running || s.state === 'Starting');
  $('#restartBtn').disabled = !running;
  $('#killBtn').disabled = !(running || busy);
  // Controls this account can't use are hidden (the server refuses them anyway).
  for (const id of ['#startBtn', '#stopBtn', '#restartBtn']) $(id).hidden = !can('control');
  $('#killBtn').hidden = !can('kill');
  $('#clearLogBtn').hidden = !can('console');
  applyTabAccess();

  // Notices: start errors, missing folders, game tips.
  const n = $('#headNotice');
  n.replaceChildren(); n.className = 'notice'; n.hidden = true;
  if (s.lastError && s.state === 'Stopped') {
    n.classList.add('bad'); n.hidden = false;
    n.append(h('span', {}, s.lastError), h('button', { class: 'btn btn-small', type: 'button', onclick: () => setTab('settings') }, 'Open settings'));
  } else if (state.detail && !state.detail.installDirExists) {
    n.classList.add('warn'); n.hidden = false;
    n.append(h('span', {}, `The server folder can't be found: ${s.installDir}`));
  }

  // Command box availability
  const input = $('#cmdInput');
  const ch = s.commandChannel;
  let placeholder = 'Type a command and press Enter';
  let enabled = running || busy;
  if (state.detail && !can('console')) { placeholder = 'Your account can\'t send console commands'; enabled = false; }
  else if (ch === 'None') { placeholder = `${g.name} doesn't accept console commands`; enabled = false; }
  else if (!running && !busy) placeholder = 'Start the server to send commands';
  else if (ch === 'Stdin' && s.adopted) { placeholder = 'Restart the server from Hostbay to send commands'; enabled = false; }
  else if (ch === 'Rcon') placeholder = 'Type an RCON command and press Enter';
  else if (ch === 'Telnet') placeholder = 'Type a console command (sent over Telnet)';
  input.placeholder = placeholder;
  input.disabled = !enabled;
  $('#cmdSend').disabled = !enabled;
  $$('.chip').forEach(c => c.disabled = !enabled);
}

function renderQuickCommands() {
  const q = $('#quickCmds');
  q.replaceChildren();
  const s = current();
  if (!s || s.commandChannel === 'None') return;
  for (const c of game(s.gameId).quickCommands || []) {
    q.append(h('button', { class: 'chip', type: 'button', title: 'Put this command in the box', onclick: () => {
      const i = $('#cmdInput'); i.value = c; i.focus();
    } }, c));
  }
  renderHeader();
}

// Tabs this account can't use are hidden; if the open one is among them, fall back to the console.
function tabAllowed(tab) {
  if (!state.detail && !state.me?.full) return tab === 'console';
  if (tab === 'configs') return can('configs');
  if (tab === 'settings') return can('settings') || can('launch');
  return true;
}
function applyTabAccess() {
  $$('.tab[data-tab]').forEach(t => { t.hidden = !tabAllowed(t.dataset.tab); });
  if (!tabAllowed(state.tab) && state.detail) setTab('console', true);
}

function setTab(tab, silent) {
  if (!tabAllowed(tab)) tab = 'console';
  state.tab = tab;
  localStorage.setItem('hb.tab', tab);
  $$('.tab').forEach(t => t.setAttribute('aria-selected', t.dataset.tab === tab ? 'true' : 'false'));
  for (const p of ['console', 'configs', 'settings']) $(`#panel-${p}`).hidden = p !== tab;
  if (tab === 'console') scrollConsole(true);
  if (tab === 'configs' && !state.configs) loadConfigs();
  if (tab === 'settings' && state.detail && (!silent || !$('#settingsForm').childElementCount)) renderSettings();
}

async function control(action) {
  const s = current();
  if (!s) return;
  if (action === 'kill' && !(await confirmDialog('Force-close the server?',
    `${s.name} will be closed immediately without saving. Players lose anything since the last save. Use Stop for a normal shutdown.`, { okText: 'Force-close', danger: true }))) return;
  try {
    await api('POST', `/api/servers/${s.id}/${action}`);
  } catch (e) { toast(e.message, 'bad'); }
}

// ---------------- logs ----------------

const MAX_DOM_LINES = 4000;
let stickToBottom = true;

function connectLogs() {
  state.logSource?.close();
  state.logLines = [];
  $('#console').replaceChildren();
  const id = state.selectedId;
  if (!id) return;
  const es = new EventSource(`/api/servers/${id}/logs`);
  state.logSource = es;
  es.addEventListener('backlog', e => {
    if (state.selectedId !== id) return;
    state.logLines = JSON.parse(e.data);
    redrawConsole();
  });
  es.addEventListener('lines', e => {
    if (state.selectedId !== id) return;
    appendLines(JSON.parse(e.data));
  });
}

function lineClass(l) {
  let c = `ln s-${l.source}`;
  if (l.source === 'out' || l.source === 'file') {
    if (/\b(error|exception|fatal|failed)\b/i.test(l.text)) c += ' lvl-error';
    else if (/\bwarn(ing)?\b/i.test(l.text)) c += ' lvl-warn';
  }
  return c;
}

function lineNode(l, filter) {
  const el = h('div', { class: lineClass(l) });
  const t = new Date(l.time);
  el.append(h('span', { class: 't' }, t.toLocaleTimeString([], { hour12: false })));
  if (filter) {
    const lower = l.text.toLowerCase(); let i = 0, j;
    while ((j = lower.indexOf(filter, i)) >= 0) {
      el.append(l.text.slice(i, j), h('mark', {}, l.text.slice(j, j + filter.length)));
      i = j + filter.length;
    }
    el.append(l.text.slice(i));
  } else el.append(l.text);
  return el;
}

function currentFilter() { return $('#logFilter').value.trim().toLowerCase(); }

function redrawConsole() {
  const c = $('#console');
  const f = currentFilter();
  const lines = f ? state.logLines.filter(l => l.text.toLowerCase().includes(f)) : state.logLines;
  const frag = document.createDocumentFragment();
  for (const l of lines.slice(-MAX_DOM_LINES)) frag.append(lineNode(l, f));
  c.replaceChildren(frag);
  if (!lines.length) {
    const s = current();
    c.append(h('div', { class: 'console-empty' },
      f ? 'No lines match the filter.' : s?.state === 'Running' ? 'Waiting for output…' : 'The log is empty. Start the server to see its output here.'));
  }
  scrollConsole(true);
}

function appendLines(lines) {
  if (!lines.length) return;
  state.logLines.push(...lines);
  if (state.logLines.length > 6000) state.logLines.splice(0, state.logLines.length - 5000);
  const c = $('#console');
  c.querySelector('.console-empty')?.remove();
  const f = currentFilter();
  const frag = document.createDocumentFragment();
  for (const l of lines) if (!f || l.text.toLowerCase().includes(f)) frag.append(lineNode(l, f));
  c.append(frag);
  while (c.childElementCount > MAX_DOM_LINES) c.firstElementChild.remove();
  scrollConsole(false);
}

function scrollConsole(force) {
  const c = $('#console');
  if (force) stickToBottom = true;
  if (stickToBottom) c.scrollTop = c.scrollHeight;
  $('#jumpBtn').hidden = stickToBottom;
}

async function sendCommand(e) {
  e.preventDefault();
  const s = current();
  const input = $('#cmdInput');
  const cmd = input.value.trim();
  if (!s || !cmd) return;
  state.cmdHistory = [cmd, ...state.cmdHistory.filter(c => c !== cmd)].slice(0, 50);
  state.cmdHistoryPos = -1;
  input.value = '';
  try { await api('POST', `/api/servers/${s.id}/command`, { command: cmd }); }
  catch (err) { toast(err.message, 'bad'); }
  scrollConsole(true);
}

// ---------------- config files ----------------

async function loadConfigs() {
  const s = current();
  if (!s) return;
  const list = $('#fileList');
  list.replaceChildren(h('div', { class: 'file-note' }, 'Looking for config files…'));
  try {
    const data = await api('GET', `/api/servers/${s.id}/configs`);
    if (s.id !== state.selectedId) return;
    state.configs = data;
    renderFileList();
    if (!state.editor) {
      const last = localStorage.getItem(`hb.file.${s.id}`);
      const pick = data.files.find(f => f.path === last) || data.files.find(f => f.known);
      if (pick) openFile(pick);
    }
  } catch (e) { list.replaceChildren(h('div', { class: 'file-note' }, e.message)); }
}

function renderFileList() {
  const list = $('#fileList');
  const data = state.configs;
  list.replaceChildren();
  if (data.template) {
    list.append(h('div', { class: 'notice warn', style: { margin: '0 0 14px' } },
      h('span', {}, 'The server config hasn\'t been created yet. Copy the game\'s defaults to start editing.'),
      h('button', { class: 'btn btn-small', type: 'button', onclick: createFromTemplate }, 'Create config from defaults')));
  }
  const known = data.files.filter(f => f.known), other = data.files.filter(f => !f.known);
  const group = (title, files) => {
    if (!files.length) return;
    const g = h('div', { class: 'file-group' }, h('h3', {}, title));
    for (const f of files) {
      const active = state.editor?.path === f.path;
      g.append(h('button', { class: `file${active ? ' active' : ''}`, type: 'button', title: f.path, onclick: () => openFile(f) },
        h('span', { class: 'fn' }, f.name, active && isDirty() ? h('span', { class: 'dirty' }, ' (edited)') : null),
        h('span', { class: 'fp' }, f.displayPath)));
    }
    list.append(g);
  };
  group('Game config files', known);
  group('Other files in the server folder', other);
  if (!data.files.length) {
    list.append(h('div', { class: 'file-note' }, 'No config files found yet. Many servers create theirs the first time they run, so try starting it once.'));
  }
}

async function createFromTemplate() {
  const s = current();
  try {
    const r = await api('POST', `/api/servers/${s.id}/configs/from-template`);
    toast('Created the config from the game\'s defaults.', 'good');
    state.editor = null;
    await loadConfigs();
    const f = state.configs.files.find(x => x.path.toLowerCase() === r.path.toLowerCase());
    if (f) openFile(f);
  } catch (e) { toast(e.message, 'bad'); }
}

function isDirty() { return !!state.editor && state.editor.text !== state.editor.original; }

async function openFile(f) {
  if (state.editor && state.editor.path !== f.path && isDirty()
    && !(await confirmDialog('Discard changes?', `You have unsaved changes in ${state.editor.name}.`, { okText: 'Discard', danger: true }))) return;
  const s = current();
  try {
    const d = await api('GET', `/api/servers/${s.id}/configs/file?path=${encodeURIComponent(f.path)}`);
    localStorage.setItem(`hb.file.${s.id}`, f.path);
    const model = parseConfig(d.format, d.content);
    state.editor = {
      path: d.path, name: f.name, displayPath: f.displayPath, original: d.content, text: d.content, format: d.format,
      modified: d.modified, running: d.running, model,
      mode: model ? (localStorage.getItem('hb.editorMode') || 'form') : 'text',
    };
    renderEditor();
    renderFileList();
    loadBackups();
  } catch (e) { toast(e.message, 'bad'); }
}

function renderEditor() {
  const ed = state.editor;
  $('#editorEmpty').hidden = !!ed;
  $('#editorBody').hidden = !ed;
  if (!ed) return;
  $('#edName').textContent = ed.name;
  $('#edPath').textContent = ed.path;
  const formOk = !!ed.model;
  if (!formOk) ed.mode = 'text';
  $$('.seg-btn').forEach(b => {
    b.setAttribute('aria-pressed', b.dataset.mode === ed.mode ? 'true' : 'false');
    if (b.dataset.mode === 'form') { b.disabled = !formOk; b.title = formOk ? '' : 'This file type can only be edited as text'; }
  });
  $('#formFilter').hidden = ed.mode !== 'form';
  $('#formView').hidden = ed.mode !== 'form';
  $('#textView').hidden = ed.mode !== 'text';

  const n = $('#edNotice');
  n.replaceChildren(); n.hidden = true; n.className = 'notice';
  const s = current();
  if (s && s.state !== 'Stopped') {
    n.hidden = false; n.classList.add('warn');
    n.append(h('span', {}, 'The server is running. Most games only read their config at startup, and some overwrite it when they shut down. Stop the server before you edit, then start it again.'));
  }
  if (n.hidden && /^default/i.test(ed.name)) {
    n.hidden = false;
    n.append(h('span', {}, 'This is the game\x27s defaults file, kept for reference. Changes here don\x27t affect your server. Edit the other config file instead.'));
  }
  const notes = game(s?.gameId).notes;
  if (n.hidden && notes && ed.name.toLowerCase().includes('palworld')) {
    n.hidden = false; n.append(h('span', {}, notes));
  }

  if (ed.mode === 'form') renderForm();
  else renderText();
  updateDirty();
}

function updateDirty() {
  const dirty = isDirty();
  $('#saveBtn').disabled = !dirty;
  $('#revertBtn').disabled = !dirty;
  const active = $('#fileList .file.active .fn');
  if (active) {
    active.querySelector('.dirty')?.remove();
    if (dirty) active.append(h('span', { class: 'dirty' }, ' (edited)'));
  }
}

function setMode(mode) {
  const ed = state.editor;
  if (!ed || ed.mode === mode) return;
  if (mode === 'form') {
    const model = parseConfig(ed.format, ed.text);
    if (!model) { toast('The file can\'t be shown as a form right now. Check it for syntax errors.', 'bad'); return; }
    ed.model = model;
  }
  ed.mode = mode;
  localStorage.setItem('hb.editorMode', mode);
  renderEditor();
}

function renderText() {
  const ta = $('#textArea');
  ta.value = state.editor.text;
  updateGutter();
}

function updateGutter() {
  const ta = $('#textArea');
  const count = ta.value.split('\n').length;
  const g = $('#gutter');
  if (g.dataset.count !== String(count)) {
    g.dataset.count = count;
    g.textContent = Array.from({ length: count }, (_, i) => i + 1).join('\n');
  }
  g.scrollTop = ta.scrollTop;
}

function renderForm() {
  const ed = state.editor;
  const view = $('#formView');
  view.replaceChildren();
  const filter = $('#formFilter').value.trim().toLowerCase();
  const fields = ed.model.fields;
  const originalModel = parseConfig(ed.format, ed.original);
  const originalValues = new Map((originalModel?.fields || []).map(f => [f.id, f.value]));
  let shown = 0;
  let section = null, sectionEl = null;
  for (const f of fields) {
    if (filter && !f.key.toLowerCase().includes(filter) && !humanize(f.key).toLowerCase().includes(filter) && !String(f.value).toLowerCase().includes(filter)) continue;
    if (f.section !== section || !sectionEl) {
      section = f.section;
      sectionEl = h('div', { class: 'form-section' }, section ? h('h4', {}, section) : null);
      view.append(sectionEl);
    }
    sectionEl.append(fieldRow(f, originalValues.get(f.id)));
    shown++;
  }
  if (!shown) view.append(h('div', { class: 'form-empty' }, filter ? 'No settings match your search.' : 'This file has no settings to show as a form. Switch to Text to edit it.'));
}

function fieldRow(f, originalValue) {
  const id = `f-${f.id}`;
  const row = h('div', { class: `field${originalValue !== undefined && originalValue !== f.value ? ' changed' : ''}` });
  const label = h('label', { for: id }, h('span', { class: 'human' }, humanize(f.key)), h('span', { class: 'key' }, f.key));
  const commit = v => {
    f.value = v;
    state.editor.text = state.editor.model.serialize();
    row.classList.toggle('changed', originalValue !== undefined && originalValue !== v);
    updateDirty();
  };
  let control;
  const kind = valueKind(f);
  if (kind === 'bool') {
    const cb = h('input', { type: 'checkbox', id, ...(isTrue(f.value) ? { checked: true } : {}) });
    cb.addEventListener('change', () => commit(boolLike(f.value, cb.checked)));
    control = h('span', { class: 'toggle' }, cb, h('span'));
  } else if (kind === 'long') {
    control = h('textarea', { class: 'input', id, rows: Math.min(8, String(f.value).split('\n').length + 1) });
    control.value = f.value;
    control.addEventListener('input', () => commit(control.value));
  } else {
    control = h('input', { class: `input${kind === 'num' ? ' num' : ''}`, id, type: 'text', inputmode: kind === 'num' ? 'decimal' : null, spellcheck: 'false' });
    control.value = f.value;
    control.addEventListener('input', () => commit(control.value));
  }
  if (kind !== 'bool' && kind !== 'long' && isIpField(f)) {
    control.classList.add('ip-field'); // shown as dots while IP addresses are hidden
    // "Fill current IP": one click fills the likely address; the caret offers every other choice.
    const set = v => { control.value = v; commit(v); control.focus(); };
    // Fills the public IP. Fields where the server listens (bind addresses) can't use it behind a
    // router, so for those the toast says what to pick instead if the server won't start.
    const listens = isBindField(f);
    const fill = h('button', { class: 'btn btn-small', type: 'button', title: 'Fill in this network\'s public IP' }, 'Fill current IP');
    fill.onclick = async () => {
      fill.disabled = true;
      try {
        await loadNetwork(true);
        const v = network.publicIp;
        if (v) {
          set(v);
          toast(listens
            ? `Filled in ${v}. This may be the address the server listens on. If it won't start, choose 0.0.0.0 or your local IP from the menu.`
            : `Filled in ${v}. Save to keep it.`, listens ? 'warn' : '');
        } else toast('Couldn\'t look up your public IP. Pick an address from the menu instead.', 'bad');
      } catch (e) { toast(e.message, 'bad'); }
      fill.disabled = false;
    };
    const more = h('button', { class: 'btn btn-small btn-icon-sm', type: 'button', title: 'Choose an address', 'aria-haspopup': 'menu', html: icons.caret });
    more.onclick = async () => {
      const items = await addressMenuItems(a => a, a => set(a));
      items.push('sep', { heading: 'Other' },
        { label: '0.0.0.0', sub: 'Listen on every network adapter (a safe default for bind addresses)', onClick: () => set('0.0.0.0') },
        { label: 'Leave empty', sub: 'Let the server decide', onClick: () => set('') });
      openMenu(more, items);
    };
    row.append(label, h('div', { class: 'with-fill' }, control, fill, more));
    return row;
  }
  row.append(label, h('div', {}, control));
  return row;
}

const isTrue = v => /^(true|1|yes|on)$/i.test(String(v).trim());
function valueKind(f) {
  if (f.kind) return f.kind;
  const v = String(f.value).trim();
  if (/^(true|false)$/i.test(v)) return 'bool';
  if (/^-?\d+(\.\d+)?$/.test(v)) return 'num';
  if (v.length > 90 || v.includes('\n')) return 'long';
  return 'text';
}
// Keep the file's own spelling of booleans (True / true / TRUE).
function boolLike(old, on) {
  const o = String(old).trim();
  if (o === 'True' || o === 'False') return on ? 'True' : 'False';
  if (o === 'TRUE' || o === 'FALSE') return on ? 'TRUE' : 'FALSE';
  return on ? 'true' : 'false';
}

async function saveFile(force = false) {
  const ed = state.editor, s = current();
  if (!ed || !isDirty()) return;
  if (ed.format === 'json') {
    try { JSON.parse(stripJsonComments(ed.text)); }
    catch (e) {
      if (!(await confirmDialog('This JSON has an error', `${e.message}. The server may fail to read it. Save anyway?`, { okText: 'Save anyway', danger: true }))) return;
    }
  }
  try {
    const r = await api('PUT', `/api/servers/${s.id}/configs/file`, { path: ed.path, content: ed.text, expectedModified: ed.modified, force });
    ed.original = ed.text;
    ed.modified = r.modified;
    toast(r.backup ? `Saved ${ed.name}. The previous version is in Backups.` : `Saved ${ed.name}.`, 'good');
    renderEditor();
    loadBackups();
    // The join port can live in a config file (server.properties, serverconfig.xml…), so refresh it.
    api('GET', `/api/servers/${s.id}/join`).then(j => { if (state.detail?.config.id === s.id) { state.detail.join = j; renderJoin(); } }).catch(() => { });
  } catch (e) {
    if (e.status === 409) {
      if (await confirmDialog('File changed on disk', 'Something else (often the server itself) changed this file after you opened it. Overwrite it with your version?', { okText: 'Overwrite', danger: true })) saveFile(true);
      return;
    }
    toast(e.message, 'bad');
  }
}

async function loadBackups() {
  const ed = state.editor, s = current();
  const sel = $('#backupSelect');
  sel.replaceChildren(h('option', { value: '' }, 'Backups'));
  if (!ed) return;
  try {
    const list = await api('GET', `/api/servers/${s.id}/configs/backups?path=${encodeURIComponent(ed.path)}`);
    if (!list.length) sel.append(h('option', { value: '', disabled: true }, 'No backups yet'));
    for (const b of list) {
      const m = b.name.match(/^(\d{4}-\d\d-\d\d)_(\d\d)-(\d\d)-(\d\d)/);
      const name = m ? new Date(`${m[1]}T${m[2]}:${m[3]}:${m[4]}`).toLocaleString() : b.name;
      sel.append(h('option', { value: b.name }, `Load ${name}`));
    }
  } catch { }
}

async function loadBackup(name) {
  const ed = state.editor, s = current();
  if (!name || !ed) return;
  try {
    const r = await api('GET', `/api/servers/${s.id}/configs/backup?path=${encodeURIComponent(ed.path)}&backup=${encodeURIComponent(name)}`);
    ed.text = r.content.replace(/\r\n/g, '\n');
    ed.model = parseConfig(ed.format, ed.text);
    renderEditor();
    toast('Loaded the backup into the editor. Save to restore it.', '');
  } catch (e) { toast(e.message, 'bad'); }
  $('#backupSelect').value = '';
}

// ---------------- config parsers ----------------
// Each parser returns { fields: [{id, section, key, value}], serialize() } and edits the original
// text in place so comments, ordering and formatting survive.

function parseConfig(format, text) {
  text = text.replace(/\r\n/g, '\n');
  try {
    switch (format) {
      case 'palworld': return parsePalworld(text) || parseIni(text, '=');
      case 'ini': return parseIni(text, '=');
      case 'properties': return parseIni(text, '=', true);
      case 'json': return parseJson(text);
      case 'xml': return parseXml(text);
      default: return null;
    }
  } catch { return null; }
}

function parseIni(text, sep, properties) {
  const lines = text.split('\n');
  const fields = [];
  let section = '';
  lines.forEach((line, i) => {
    const t = line.trim();
    if (!t || t.startsWith(';') || t.startsWith('#') || t.startsWith('//')) return;
    if (!properties && t.startsWith('[') && t.endsWith(']')) { section = t.slice(1, -1); return; }
    let idx = line.indexOf(sep);
    if (properties && idx < 0) idx = line.indexOf(':');
    if (idx <= 0) return;
    const keyPart = line.slice(0, idx);
    const rest = line.slice(idx + 1);
    const lead = rest.match(/^\s*/)[0];
    fields.push({ id: `l${i}`, section, key: keyPart.trim(), value: rest.slice(lead.length), line: i, prefix: line.slice(0, idx + 1) + lead });
  });
  if (!fields.length) return null;
  return {
    fields,
    serialize() {
      const out = lines.slice();
      for (const f of fields) out[f.line] = f.prefix + f.value;
      return out.join('\n');
    },
  };
}

function splitTopLevel(s) {
  const parts = []; let depth = 0, start = 0, q = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '"') q = !q;
    else if (!q && c === '(') depth++;
    else if (!q && c === ')') depth--;
    else if (!q && depth === 0 && c === ',') { parts.push(s.slice(start, i)); start = i + 1; }
  }
  if (start < s.length) parts.push(s.slice(start));
  return parts;
}

// Palworld keeps every setting on one line: OptionSettings=(A=1,B="x",C=(Steam,Xbox))
function parsePalworld(text) {
  const lines = text.split('\n');
  const li = lines.findIndex(l => /^\s*OptionSettings\s*=\s*\(/.test(l));
  if (li < 0) return null;
  const line = lines[li];
  const open = line.indexOf('(');
  const close = line.lastIndexOf(')');
  if (close <= open) return null;
  const head = line.slice(0, open + 1), tail = line.slice(close);
  const parts = splitTopLevel(line.slice(open + 1, close));
  const fields = parts.map((p, i) => {
    const eq = p.indexOf('=');
    const key = eq > 0 ? p.slice(0, eq).trim() : p.trim();
    let raw = eq > 0 ? p.slice(eq + 1) : '';
    const quoted = /^".*"$/.test(raw.trim());
    return { id: `p${i}`, section: 'Palworld server settings', key, value: quoted ? raw.trim().slice(1, -1) : raw, quoted, hasEq: eq > 0, kind: key === 'CrossplayPlatforms' ? 'text' : undefined };
  });
  return {
    fields,
    serialize() {
      const body = fields.map(f => f.hasEq ? `${f.key}=${f.quoted ? `"${String(f.value).replace(/"/g, '')}"` : f.value}` : f.key).join(',');
      const out = lines.slice();
      out[li] = head + body + tail;
      return out.join('\n');
    },
  };
}

function stripJsonComments(t) {
  return t.replace(/("(?:\\.|[^"\\])*")|\/\/[^\n]*|\/\*[\s\S]*?\*\//g, (m, str) => str || '');
}

function parseJson(text) {
  const obj = JSON.parse(stripJsonComments(text));
  if (obj === null || typeof obj !== 'object') return null;
  const indentMatch = text.match(/\n([ \t]+)"/);
  const indent = indentMatch ? (indentMatch[1].includes('\t') ? '\t' : indentMatch[1].length) : 2;
  const fields = [];
  const walk = (node, path, section) => {
    for (const [k, v] of Object.entries(node)) {
      const p = [...path, k];
      if (v !== null && typeof v === 'object' && !Array.isArray(v)) walk(v, p, p.join(' › '));
      else {
        const isArr = Array.isArray(v);
        const kind = typeof v === 'boolean' ? 'bool' : isArr ? 'long' : typeof v === 'number' ? 'num' : undefined;
        fields.push({
          id: `j${fields.length}`, section, key: k, path: p, type: isArr ? 'array' : typeof v, kind,
          value: isArr ? JSON.stringify(v, null, 1).replace(/\n\s*/g, ' ') : v === null ? '' : String(v),
        });
      }
    }
  };
  walk(obj, [], '');
  if (Array.isArray(obj)) return null; // lists (ops.json etc.) are clearer as text
  return {
    fields,
    serialize() {
      const out = structuredClone(obj);
      for (const f of fields) {
        let target = out;
        for (const k of f.path.slice(0, -1)) target = target[k];
        const last = f.path[f.path.length - 1];
        let v = f.value;
        if (f.type === 'boolean') v = isTrue(v);
        else if (f.type === 'number') { const n = Number(v); v = Number.isFinite(n) && String(v).trim() !== '' ? n : v; }
        else if (f.type === 'array') { try { v = JSON.parse(v); } catch { } }
        target[last] = v;
      }
      return JSON.stringify(out, null, indent) + (text.endsWith('\n') ? '\n' : '');
    },
  };
}

// 7 Days to Die style: <property name="X" value="Y"/>
function parseXml(text) {
  const re = /(<property\s+name="([^"]+)"\s+value=")([^"]*)(")/g;
  const fields = []; let m;
  while ((m = re.exec(text))) fields.push({ id: `x${fields.length}`, section: '', key: m[2], value: decodeXml(m[3]), index: m.index, len: m[0].length, pre: m[1], post: m[4] });
  if (!fields.length) return null;
  return {
    fields,
    serialize() {
      let out = '', pos = 0;
      for (const f of fields) {
        out += text.slice(pos, f.index) + f.pre + encodeXml(f.value) + f.post;
        pos = f.index + f.len;
      }
      return out + text.slice(pos);
    },
  };
}
const decodeXml = s => s.replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const encodeXml = s => String(s).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// ---------------- server settings tab ----------------

function renderSettings() {
  const d = state.detail;
  if (!d || !d.config) return;
  const c = structuredClone(d.config);
  const g = game(c.gameId);
  const form = $('#settingsForm');
  form.replaceChildren();
  // Launch settings decide what runs on the host PC; without that permission they're shown read-only or left out.
  const launchOk = can('launch');
  const readOnly = value => h('div', { class: 'mono-path', style: { paddingTop: '8px', whiteSpace: 'normal' } }, value);

  const row = (label, control, help) => h('div', { class: 'set-row' }, h('label', {}, label), h('div', {}, control, help ? h('div', { class: 'help' }, help) : null));
  const text = (key, opts = {}) => {
    const i = h('input', { class: `input${opts.mono ? ' mono' : ''}`, type: opts.type || 'text', spellcheck: 'false', placeholder: opts.placeholder || '' });
    i.value = opts.get ? opts.get() : (c[key] ?? '');
    i.addEventListener('input', () => { if (opts.set) opts.set(i.value); else c[key] = opts.type === 'number' ? Number(i.value) : i.value; });
    return i;
  };
  const withBrowse = (input, opts) => h('div', { class: 'with-btn' }, input,
    h('button', { class: 'btn', type: 'button', onclick: async () => {
      const p = await browse({ title: opts.title, files: opts.files, start: input.value || c.installDir });
      if (p) { input.value = opts.relative && p.toLowerCase().startsWith(c.installDir.toLowerCase() + '\\') ? p.slice(c.installDir.length + 1) : p; input.dispatchEvent(new Event('input')); }
    } }, 'Browse'));

  const gameSel = h('select', { class: 'input' }, Object.values(state.games).map(x => h('option', { value: x.id, ...(x.id === c.gameId ? { selected: true } : {}) }, x.name)));
  // Artwork: the game's Steam art by default; any Steam app can be chosen instead.
  const defaultArt = () => game(c.gameId).artAppId || (c.gameId === 'generic' ? c.steamAppId : null);
  const artPreview = h('div', { class: 'art-preview' });
  const drawArt = () => {
    const id = c.artAppId || defaultArt();
    artPreview.replaceChildren();
    if (!id) { artPreview.append(h('span', { class: 'dim' }, 'No artwork. Find the game on Steam to add some.')); return; }
    const img = h('img', { src: artUrl(id, 'header'), alt: '' });
    img.onerror = () => artPreview.replaceChildren(h('span', { class: 'dim' }, `Steam has no artwork for app ${id}. Find the game on Steam instead.`));
    artPreview.append(img);
  };
  const artInput = text('artAppId', {
    type: 'number', mono: true,
    get: () => c.artAppId ?? '',
    set: v => { c.artAppId = v.trim() === '' ? null : Number(v); drawArt(); },
    placeholder: defaultArt() ? `Default: ${defaultArt()}` : 'Steam app ID',
  });
  const findArt = h('button', { class: 'btn', type: 'button', onclick: async () => {
    const id = await steamGamePicker(cleanServerName(c.name) || game(c.gameId).name);
    if (id) { c.artAppId = id; artInput.value = id; drawArt(); }
  } }, 'Find game on Steam');
  gameSel.addEventListener('change', () => { c.gameId = gameSel.value; drawArt(); });
  drawArt();

  form.append(h('section', { class: 'set-group' },
    h('h2', {}, 'General'),
    h('div', { class: 'set-rows' },
      row('Name', text('name')),
      launchOk ? row('Game', gameSel, 'Sets where Hostbay looks for config and log files, and how it talks to the server.') : row('Game', h('div', { style: { paddingTop: '7px' } }, g.name)),
      launchOk ? row('Server folder', withBrowse(text('installDir', { mono: true }), { title: 'Choose the server folder' })) : row('Server folder', readOnly(c.installDir)),
      d.join?.code || d.join?.codeLabel ? null : row('Join port', text('joinPort', {
        type: 'number', mono: true,
        get: () => c.joinPort ?? '',
        set: v => { c.joinPort = v.trim() === '' ? null : Number(v); },
        placeholder: d.join?.port && d.join.source !== 'set in Settings' ? `Detected: ${d.join.port}` : 'For example 27015',
      }), d.join?.port && d.join.source !== 'set in Settings'
        ? `The port friends connect on. Leave it empty to keep using ${d.join.port} (${portSource(d.join)}).`
        : 'The port friends connect on, used by Copy address. Leave it empty to detect it from the launch arguments or config.'),
      row('Artwork', h('div', {}, artPreview, h('div', { class: 'with-btn' }, artInput, findArt)),
        'Pictures come from the game\'s Steam page. Leave the box empty to use the game\'s own.'),
    )));

  // Router ports: which ports to open and whether to follow the server's start/stop.
  const ps = portsState.forId === c.id ? portsState.status : null;
  const autoPorts = h('input', { type: 'checkbox', ...(c.forwardWhileRunning ? { checked: true } : {}) });
  autoPorts.addEventListener('change', () => c.forwardWhileRunning = autoPorts.checked);
  form.append(h('section', { class: 'set-group' },
    h('h2', {}, 'Router ports'),
    h('p', {}, 'The ports friends outside your home need. Hostbay opens them on your router with PortHop when it\'s installed, or with its own UPnP otherwise.'),
    h('div', { class: 'set-rows' },
      row('Ports to open', text('forwardPorts', {
        mono: true,
        get: () => c.forwardPorts ?? '',
        set: v => { c.forwardPorts = v.trim() === '' ? null : v; },
        placeholder: ps?.spec ? `Automatic: ${ps.spec}` : 'For example UDP 2456-2458, TCP 8888',
      }), ps?.spec
        ? `Leave it empty to use ${ps.spec} (${ps.source}). Write entries like "UDP 2456-2458, TCP 8888".`
        : 'Write entries like "UDP 2456-2458, TCP 8888". TCP/UDP opens both.'),
      row('Open while running', h('div', { class: 'inline' }, h('span', { class: 'toggle' }, autoPorts, h('span')),
        h('span', { class: 'dim' }, 'Open these ports when the server starts and close them when it stops'))),
      ps ? row('Handled by', h('div', { class: 'inline' }, h('span', {}, ps.backendLabel),
        ps.backend === 'porthop' && state.me?.full ? h('button', { class: 'btn btn-small', type: 'button', onclick: openPortHop }, 'Open PortHop') : null)) : null,
    )));

  const restartRow = () => row('Restart after a crash', (() => {
    const cb = h('input', { type: 'checkbox', ...(c.autoRestart ? { checked: true } : {}) });
    cb.addEventListener('change', () => c.autoRestart = cb.checked);
    return h('div', { class: 'inline' }, h('span', { class: 'toggle' }, cb, h('span')), h('span', { class: 'dim' }, 'Restart automatically if the server stops without being asked to'));
  })());
  if (!launchOk) {
    form.append(h('section', { class: 'set-group' },
      h('h2', {}, 'Launch'),
      h('div', { class: 'notice', style: { margin: '0 0 12px' } }, 'The executable, launch arguments, folders and environment can only be changed by accounts with the "Change launch settings" permission, because they decide what runs on the host PC.'),
      h('div', { class: 'set-rows' },
        row('Executable', readOnly(d.resolvedExecutable || c.executable)),
        restartRow())));
  }
  const exeInput = text('executable', { mono: true });
  if (launchOk) form.append(h('section', { class: 'set-group' },
    h('h2', {}, 'Launch'),
    h('p', {}, g.id === 'valheim' ? g.notes : 'How Hostbay starts the server. Paths can be relative to the server folder.'),
    h('div', { class: 'set-rows' },
      row('Executable', withBrowse(exeInput, { title: 'Choose the server program', files: true, relative: true }),
        d.resolvedExecutable && d.resolvedExecutable !== c.executable ? `Runs ${d.resolvedExecutable}` : null),
      row('Launch arguments', (() => { const t = h('textarea', { class: 'input mono', rows: 3, spellcheck: 'false' }); t.value = c.arguments || ''; t.addEventListener('input', () => c.arguments = t.value); return t; })(),
        g.defaultArguments ? h('span', {}, 'Default: ', h('code', { class: 'mono-path' }, g.defaultArguments)) : null),
      row('Working folder', text('workingDirectory', { mono: true, placeholder: 'Same as the server folder' })),
      row('Environment', (() => {
        const t = h('textarea', { class: 'input mono', rows: 2, spellcheck: 'false', placeholder: 'KEY=value, one per line' });
        t.value = Object.entries(c.environment || {}).map(([k, v]) => `${k}=${v}`).join('\n');
        t.addEventListener('input', () => {
          c.environment = {};
          for (const l of t.value.split('\n')) { const i = l.indexOf('='); if (i > 0) c.environment[l.slice(0, i).trim()] = l.slice(i + 1).trim(); }
        });
        return t;
      })()),
      restartRow(),
    )));

  // Console & shutdown
  const chSel = h('select', { class: 'input' },
    [['None', 'No console commands'], ['Stdin', 'Console input (like typing in the server window)'], ['Rcon', 'RCON'], ['Telnet', 'Telnet']]
      .map(([v, l]) => h('option', { value: v, ...(v === c.commandChannel ? { selected: true } : {}) }, l)));
  const stopSel = h('select', { class: 'input' },
    [['CtrlC', 'Press Ctrl+C (the server saves and exits)'], ['Commands', 'Send stop commands'], ['Kill', 'Force-close immediately']]
      .map(([v, l]) => h('option', { value: v, ...(v === c.stopMethod ? { selected: true } : {}) }, l)));
  const rcRows = h('div', { class: 'set-rows' });
  const fromCfg = h('input', { type: 'checkbox', ...(c.rconFromConfig ? { checked: true } : {}) });
  const drawRc = () => {
    rcRows.replaceChildren(
      row('Commands', chSel, 'How commands typed in the console reach the server.'),
      ...(['Rcon', 'Telnet'].includes(c.commandChannel) ? [
        g.commands === c.commandChannel ? row('Read from game config', h('div', { class: 'inline' }, h('span', { class: 'toggle' }, fromCfg, h('span')),
          h('span', { class: 'dim' }, d.remoteConsole ? `Using port ${d.remoteConsole.port}${d.remoteConsole.hasPassword ? ' with the password from the config' : ' (no password set)'}` : 'Not enabled in the game config yet'))) : null,
        row('Host', text('rconHost', { mono: true, get: () => c.rcon.host, set: v => c.rcon.host = v, placeholder: '127.0.0.1' })),
        row('Port', text('rconPort', { mono: true, type: 'number', get: () => c.rcon.port || '', set: v => c.rcon.port = Number(v) || 0 })),
        row('Password', text('rconPw', { type: 'password', get: () => c.rcon.password, set: v => c.rcon.password = v }),
          c.rconFromConfig && g.commands === c.commandChannel ? 'Used only if the game config doesn\'t have RCON details.' : null),
      ] : []),
      row('Stopping', stopSel),
      c.stopMethod === 'Commands' ? row('Stop commands', (() => {
        const t = h('textarea', { class: 'input mono', rows: 2, spellcheck: 'false', placeholder: 'One command per line' });
        t.value = (c.stopCommands || []).join('\n');
        t.addEventListener('input', () => c.stopCommands = t.value.split('\n').map(x => x.trim()).filter(Boolean));
        return t;
      })(), 'Sent in order. If they can\'t be sent, Hostbay presses Ctrl+C instead.') : null,
      row('Wait before force-closing', h('div', { class: 'with-btn' }, text('stopTimeoutSeconds', { type: 'number', mono: true }), h('span', { class: 'dim', style: { alignSelf: 'center' } }, 'seconds'))),
    );
  };
  chSel.addEventListener('change', () => { c.commandChannel = chSel.value; drawRc(); });
  stopSel.addEventListener('change', () => { c.stopMethod = stopSel.value; drawRc(); });
  fromCfg.addEventListener('change', () => { c.rconFromConfig = fromCfg.checked; });
  drawRc();
  form.append(h('section', { class: 'set-group' }, h('h2', {}, 'Console and shutdown'), rcRows));

  const logs = h('textarea', { class: 'input mono', rows: 2, spellcheck: 'false', placeholder: 'logs\\server.log' });
  logs.value = (c.extraLogFiles || []).join('\n');
  logs.addEventListener('input', () => c.extraLogFiles = logs.value.split('\n').map(x => x.trim()).filter(Boolean));
  if (launchOk) form.append(h('section', { class: 'set-group' },
    h('h2', {}, 'Logs'),
    h('p', {}, 'Hostbay shows the server\'s console output and follows these log files live.'),
    h('div', { class: 'set-rows' },
      (g.logFiles || []).length ? row('Game log files', h('div', { class: 'mono-path', style: { paddingTop: '8px', whiteSpace: 'normal' } }, g.logFiles.join(', '))) : null,
      row('Extra log files', logs, 'One path per line, relative to the server folder or absolute. * works as a wildcard.'))));

  if (launchOk) form.append(h('section', { class: 'set-group' },
    h('h2', {}, 'Remove'),
    h('div', { class: 'set-rows danger-zone' },
      row('Remove from Hostbay', h('button', { class: 'btn btn-danger', type: 'button', onclick: removeServer }, 'Remove server'),
        'Only removes it from this list. The server\'s files stay on disk.'))));

  form.append(h('div', { class: 'set-actions' },
    h('button', { class: 'btn btn-primary', type: 'submit' }, 'Save settings'),
    h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => renderSettings() }, 'Discard changes')));

  form.onsubmit = async e => {
    e.preventDefault();
    try {
      const r = await api('PUT', `/api/servers/${c.id}`, c);
      state.detail = r;
      toast('Settings saved.', 'good');
      renderSettings(); renderQuickCommands(); renderJoin(); renderPorts(true);
      state.configs = null;
    } catch (err) { toast(err.message, 'bad'); }
  };
}

async function removeServer() {
  const s = current();
  if (!(await confirmDialog(`Remove ${s.name}?`, 'It will be removed from Hostbay. Its files stay on disk, and you can add it again later.', { okText: 'Remove', danger: true }))) return;
  try {
    await api('DELETE', `/api/servers/${s.id}`);
    state.selectedId = null; state.detail = null;
    toast(`Removed ${s.name}.`);
  } catch (e) { toast(e.message, 'bad'); }
}

// ---------------- add server ----------------

function addServerDialog(startTab = 'scan') {
  const body = h('div');
  const tabs = h('div', { class: 'mtabs', role: 'tablist' });
  const panel = h('div');
  const tabBtn = (id, label) => h('button', { class: 'tab', type: 'button', role: 'tab', 'data-t': id, onclick: () => show(id) }, label);
  tabs.append(tabBtn('scan', 'Found on this PC'), tabBtn('folder', 'Add a folder'));
  body.append(tabs, panel);
  const m = openModal({ title: 'Add a server', body });

  const show = id => {
    $$('[data-t]', tabs).forEach(b => b.setAttribute('aria-selected', b.dataset.t === id ? 'true' : 'false'));
    panel.replaceChildren();
    if (id === 'scan') scanPanel(panel, m); else folderPanel(panel, m);
  };
  show(startTab);
}

async function addCandidate(c, m, name) {
  try {
    const r = await api('POST', '/api/servers', { gameId: c.gameId, installDir: c.installDir, name: name || c.name, steamAppId: c.steamAppId });
    m.close();
    toast(`Added ${r.config.name}.`, 'good');
    state.servers.push(r.summary);
    select(r.config.id);
  } catch (e) { toast(e.message, 'bad'); }
}

async function scanPanel(panel, m) {
  const status = h('div', { class: 'scan-status' }, 'Checking your Steam libraries…');
  const list = h('div', { class: 'cand-list' });
  panel.append(status, list);
  try {
    const r = await api('GET', '/api/scan/steam');
    if (!r.steamFound) { status.textContent = 'Steam isn\'t installed on this PC. Use "Add a folder" instead.'; return; }
    const n = r.candidates.filter(c => !c.alreadyAdded).length;
    status.textContent = r.candidates.length
      ? `Checked ${r.libraries.length} Steam ${r.libraries.length === 1 ? 'library' : 'libraries'} and found ${r.candidates.length} server${r.candidates.length === 1 ? '' : 's'}${n < r.candidates.length ? ` (${r.candidates.length - n} already added)` : ''}.`
      : `Checked ${r.libraries.length} Steam ${r.libraries.length === 1 ? 'library' : 'libraries'} and found no dedicated servers. Install one from Steam's Tools library, or add a folder.`;
    for (const c of r.candidates) {
      const g = game(c.gameId);
      const el = h('div', { class: `cand${c.alreadyAdded ? ' added' : ''}` },
        candArt(c.gameId, g.artAppId),
        h('div', { class: 'meta' }, h('div', { class: 'name' }, c.name), h('span', { class: 'mono-path', title: c.installDir }, c.installDir)),
        c.alreadyAdded ? h('span', { class: 'dim' }, 'Added') : h('button', { class: 'btn btn-small btn-primary', type: 'button', onclick: () => addCandidate(c, m, g.id === 'generic' ? c.name : g.name) }, 'Add'));
      el.style.setProperty('--game', g.color);
      list.append(el);
    }
  } catch (e) { status.textContent = e.message; }
}

function folderPanel(panel, m) {
  const path = h('input', { class: 'input mono', type: 'text', placeholder: 'D:\\Servers\\minecraft', spellcheck: 'false', autofocus: true });
  const result = h('div', { style: { marginTop: '14px' } });
  const check = async () => {
    result.replaceChildren();
    if (!path.value.trim()) return;
    try {
      const cands = await api('GET', `/api/scan/folder?path=${encodeURIComponent(path.value.trim())}`);
      const best = cands[0];
      const gameSel = h('select', { class: 'input' }, Object.values(state.games).map(g => h('option', { value: g.id, ...(g.id === best.gameId ? { selected: true } : {}) }, g.name)));
      const nameIn = h('input', { class: 'input', type: 'text' });
      nameIn.value = best.name;
      gameSel.addEventListener('change', () => { if (Object.values(state.games).some(g => g.name === nameIn.value) || !nameIn.value) nameIn.value = game(gameSel.value).name; });
      result.append(h('div', { class: 'field-stack' },
        best.gameId !== 'generic'
          ? h('div', { class: 'notice' }, `Looks like a ${game(best.gameId).name} server.`)
          : h('div', { class: 'notice warn' }, 'Hostbay doesn\'t recognise this server, so it will be added as a custom server. Pick the game below if you know it.'),
        best.alreadyAdded ? h('div', { class: 'notice warn' }, 'This folder has already been added. You can add it again with different settings.') : null,
        h('label', {}, 'Game', gameSel),
        h('label', {}, 'Name', nameIn),
        h('div', { class: 'row gap' }, h('span', { class: 'spacer' }),
          h('button', { class: 'btn btn-primary', type: 'button', onclick: () => addCandidate({ ...best, gameId: gameSel.value }, m, nameIn.value.trim()) }, 'Add server'))));
    } catch (e) { result.append(h('div', { class: 'notice bad' }, e.message)); }
  };
  path.addEventListener('change', check);
  path.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); check(); } });
  panel.append(
    h('p', {}, 'Choose the folder the server is installed in. Hostbay recognises the game from the files inside.'),
    h('div', { class: 'row gap' }, path, h('button', { class: 'btn', type: 'button', onclick: async () => {
      const p = await browse({ title: 'Choose the server folder', start: path.value });
      if (p) { path.value = p; check(); }
    } }, 'Browse')),
    result);
  path.style.flex = '1';
}

// ---------------- Steam game picker (artwork) ----------------

// "Abiotic Factor - Dedicated Server" -> "Abiotic Factor" (same rules as ServerNames.Clean on the host).
function cleanServerName(name) {
  let s = String(name || '').trim();
  for (let i = 0; i < 4; i++) {
    const next = s.replace(/[\s\-–—:|_,]*[([]?\s*\b(?:dedicated\s+server(?:\s+tool)?|dedicated|server\s+tool|server|ds|for\s+windows|windows\s+x64)\s*[)\]]?\s*$/i, '')
      .replace(/[\s\-–—:|_,]+$/, '');
    if (next === s) break;
    if (!next) return String(name || '').trim();
    s = next;
  }
  return s;
}

// Searches the Steam store by name and resolves to the chosen app ID, or null.
function steamGamePicker(initial) {
  return new Promise(resolve => {
    let done = false;
    const finish = v => { if (done) return; done = true; resolve(v); m.close(); };
    const q = h('input', { class: 'input', type: 'search', placeholder: 'Game name', 'aria-label': 'Game name', autofocus: true });
    q.value = initial || '';
    const results = h('div', { class: 'cand-list art-results' });
    const status = h('div', { class: 'scan-status' });
    let seq = 0;
    const search = async () => {
      const term = q.value.trim();
      if (!term) { results.replaceChildren(); status.textContent = ''; return; }
      const mine = ++seq;
      status.textContent = 'Searching Steam…';
      try {
        const list = await api('GET', `/api/art/search?q=${encodeURIComponent(term)}`);
        if (mine !== seq) return;
        status.textContent = list.length ? 'Pick the game whose pictures to use.' : 'No games found on Steam. Try another name.';
        results.replaceChildren(...list.map(r => h('button', { class: 'cand art-pick', type: 'button', onclick: () => finish(r.id) },
          candArt('generic', r.id),
          h('div', { class: 'meta' }, h('div', { class: 'name' }, r.name), h('span', { class: 'mono-path' }, `App ${r.id}`)))));
      } catch (e) { if (mine === seq) status.textContent = e.message; }
    };
    let t;
    q.addEventListener('input', () => { clearTimeout(t); t = setTimeout(search, 350); });
    q.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); clearTimeout(t); search(); } });
    const m = openModal({
      title: 'Find the game on Steam',
      body: h('div', {}, q, status, results),
      onClose: () => { if (!done) { done = true; resolve(null); } },
    });
    q.style.width = '100%';
    search();
  });
}

// ---------------- folder browser ----------------

function browse({ title, files = false, start = '' }) {
  return new Promise(resolve => {
    let cur = '', picked = null, done = false;
    const crumbs = h('div', { class: 'crumbs' });
    const entries = h('div', { class: 'entries' });
    const sel = h('div', { class: 'mono-path', style: { flex: '1' } });
    const useBtn = h('button', { class: 'btn btn-primary', type: 'button', onclick: () => finish(files ? picked : cur) }, files ? 'Select' : 'Use this folder');
    const finish = v => { if (done) return; done = true; resolve(v || null); m.close(); };
    const m = openModal({
      title,
      body: h('div', { class: 'browser' }, crumbs, entries),
      foot: [sel, h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => finish(null) }, 'Cancel'), useBtn],
      onClose: () => { if (!done) { done = true; resolve(null); } },
    });
    const load = async p => {
      try {
        const r = await api('GET', `/api/fs?path=${encodeURIComponent(p)}${files ? '&files=true' : ''}`);
        cur = r.path; picked = null;
        crumbs.replaceChildren(h('button', { type: 'button', onclick: () => load('') }, 'This PC'));
        if (cur) {
          let acc = '';
          for (const part of cur.split('\\').filter(Boolean)) {
            acc += part + '\\';
            const target = acc;
            crumbs.append(h('span', { class: 'dim' }, '\\'), h('button', { type: 'button', onclick: () => load(target) }, part));
          }
        }
        entries.replaceChildren();
        if (!r.entries.length) entries.append(h('div', { class: 'file-note' }, 'This folder is empty.'));
        for (const e of r.entries) {
          const el = h('button', { class: `entry${e.isDir ? '' : ' file'}`, type: 'button', title: e.path, html: !cur ? icons.drive : e.isDir ? icons.folder : icons.file });
          el.append(h('span', {}, e.name));
          if (e.isDir) el.addEventListener('click', () => load(e.path));
          else {
            el.addEventListener('click', () => { $$('.entry', entries).forEach(x => x.classList.remove('sel')); el.classList.add('sel'); picked = e.path; sel.textContent = e.name; useBtn.disabled = false; });
            el.addEventListener('dblclick', () => finish(e.path));
          }
          entries.append(el);
        }
        sel.textContent = files ? '' : cur;
        useBtn.disabled = files ? true : !cur;
      } catch (err) { toast(err.message, 'bad'); if (p) load(''); }
    };
    let s = start || '';
    if (files && /\.\w{2,4}$/.test(s)) s = s.replace(/\\[^\\]*$/, '');
    load(s);
  });
}

// ---------------- app settings ----------------

async function appSettingsDialog() {
  network.local = null; network.publicIp = undefined; // adapters may have changed since last time
  let st;
  try { st = await api('GET', '/api/settings'); } catch (e) { toast(e.message, 'bad'); return; }
  const body = h('div', { class: 'field-stack' });
  const draw = () => {
    body.replaceChildren();
    const remote = h('input', { type: 'checkbox', ...(st.remoteAccess ? { checked: true } : {}) });
    const port = h('input', { class: 'input mono', type: 'number', min: 1024, max: 65535, value: st.configuredPort });
    const tray = h('input', { type: 'checkbox', ...(st.minimizeToTray ? { checked: true } : {}) });

    // "Reach Hostbay from the internet": opens Hostbay's own port on the router with UPnP.
    // The switch applies immediately; the status box follows the router's answer.
    const internetGroup = () => {
      const inet = h('input', { type: 'checkbox', ...(st.internetAccess ? { checked: true } : {}) });
      const blocked = !st.listeningOnLan ? 'Turn on remote access and save first.'
        : !st.longPassword ? `Every enabled account needs a password of at least ${st.minInternetPasswordLength} characters first.` : null;
      inet.disabled = !!blocked && !st.internetAccess;
      const status = h('div', { class: 'fwd-status' });
      const showStatus = f => {
        status.replaceChildren();
        if (!st.internetAccess || !f || f.state === 'off') return;
        if (f.state === 'working') { status.append(h('div', { class: 'notice' }, f.message)); return; }
        if (f.state === 'failed') { status.append(h('div', { class: 'notice bad' }, f.message)); return; }
        const url = f.externalIp ? `http://${f.externalIp}:${f.port}` : null;
        status.append(h('div', { class: `notice ${f.state === 'limited' ? 'warn' : ''}` }, f.message));
        if (url) status.append(h('div', { class: 'fwd-url' }, h('span', { class: 'dim' }, 'Open from anywhere:'), copyLine(url, 'the internet link')));
      };
      const poll = async (tries = 0) => {
        try { st = await api('GET', '/api/settings'); } catch { return; }
        showStatus(st.forwarding);
        if (st.forwarding?.state === 'working' && tries < 20 && document.body.contains(status)) setTimeout(() => poll(tries + 1), 1000);
      };
      inet.addEventListener('change', async () => {
        try {
          await api('PUT', '/api/settings', { internetAccess: inet.checked });
          st.internetAccess = inet.checked;
          toast(inet.checked ? 'Opening Hostbay\'s port on your router…' : 'Closed Hostbay\'s port on your router.');
          setTimeout(() => poll(), 600);
        } catch (e) { inet.checked = !inet.checked; toast(e.message, 'bad'); }
      });
      showStatus(st.forwarding);
      if (st.forwarding?.state === 'working') setTimeout(() => poll(), 1000);
      return h('div', { class: 'set-group' },
        h('h2', {}, 'Reach Hostbay from the internet'),
        h('p', {}, 'Hostbay can ask your router to open its port (UPnP), so you can manage your servers from anywhere, not just your home network. Your game servers\' ports aren\'t touched.'),
        h('div', { class: 'notice warn', style: { margin: '0 0 12px' } },
          'Anyone on the internet will be able to reach Hostbay\'s sign-in page, and Hostbay can start programs on this PC. '
          + 'The connection isn\'t encrypted (plain http), so only sign in from networks you trust, and use a long password you don\'t use anywhere else.'),
        h('div', { class: 'set-rows' },
          h('div', { class: 'set-row' }, h('label', {}, 'Open port on my router'),
            h('div', {},
              h('div', { class: 'inline' }, h('span', { class: 'toggle' }, inet, h('span')),
                h('span', { class: 'dim' }, blocked && !st.internetAccess ? blocked : st.internetAccess ? `On. Port ${st.port} stays open while Hostbay runs.` : 'Off.')),
              status))));
    };

    // Game server port forwarding: who does it (PortHop or Hostbay itself).
    const portForwardGroup = () => {
      const line = h('div', { class: 'inline' }, h('span', { class: 'dim' }, 'Checking for PortHop…'));
      api('GET', '/api/porthop').then(ph => {
        line.replaceChildren(
          h('span', {}, ph.running ? `PortHop ${ph.version || ''} is running. Hostbay opens servers' ports through it.`
            : ph.installed ? 'PortHop is installed. Hostbay starts it in the background when a server\'s ports need opening.'
            : 'PortHop isn\'t installed, so Hostbay opens ports itself with UPnP.'),
          ph.installed ? h('button', { class: 'btn btn-small', type: 'button', onclick: openPortHop }, 'Open PortHop') : null);
      }).catch(e => line.replaceChildren(h('span', { class: 'dim' }, e.message)));
      return h('div', { class: 'set-group' },
        h('h2', {}, 'Port forwarding for game servers'),
        h('p', {}, 'Each server\'s Router ports control (next to its join address) opens the ports friends need. PortHop also works on its own, so you can see and manage every forward there too.'),
        h('div', { class: 'set-rows' }, h('div', { class: 'set-row' }, h('label', {}, 'Handled by'), line)));
    };

    // Discord bot: a short status line here, everything else in its own dialog.
    const discordGroup = () => {
      const line = h('div', { class: 'inline' }, h('span', { class: 'dim' }, 'Checking…'));
      api('GET', '/api/discord').then(d => {
        line.replaceChildren(discordDot(d), h('span', {}, discordSummary(d)),
          h('button', { class: 'btn btn-small', type: 'button', onclick: () => { m.close(); discordDialog(); } }, d.hasToken ? 'Discord settings' : 'Set up'));
      }).catch(e => line.replaceChildren(h('span', { class: 'dim' }, e.message)));
      return h('div', { class: 'set-group' },
        h('h2', {}, 'Discord bot'),
        h('p', {}, 'Add your own bot to your Discord server so friends can see which servers are up and get the address to join, without asking you.'),
        h('div', { class: 'set-rows' }, h('div', { class: 'set-row' }, h('label', {}, 'Bot'), line)));
    };

    // This PC's addresses, each with a Copy button. Filled in once the public IP lookup returns.
    const copyLine = (value, what) => h('div', { class: 'addr-line' }, h('code', {}, showIp(value)),
      h('button', { class: 'btn btn-small', type: 'button', onclick: () => copyText(value, `Copied ${what || value}`) }, h('span', { html: icons.copy }), 'Copy'));
    const addrRow = (title, sub, valueEl, help) => h('div', { class: 'set-row' },
      h('label', {}, title, sub ? h('span', { class: 'label-sub' }, sub) : null),
      h('div', {}, valueEl, help ? h('div', { class: 'help' }, help) : null));
    const addrRows = h('div', { class: 'set-rows' }, h('div', { class: 'dim' }, 'Looking up your addresses…'));
    loadNetwork(true).then(() => {
      addrRows.replaceChildren();
      for (const a of network.local || []) {
        addrRows.append(addrRow(a.kind === 'vpn' ? 'VPN IP' : 'Local IP', a.adapter, copyLine(a.address),
          a.kind === 'vpn' ? 'For friends connected to the same VPN.' : a.primary ? 'For friends on your home network (same Wi-Fi or router).' : null));
      }
      if (!(network.local || []).length) addrRows.append(h('div', { class: 'dim' }, 'No network connection found.'));
      addrRows.append(addrRow('Public IP', 'Your internet address',
        network.publicIp ? copyLine(network.publicIp) : h('span', { class: 'dim' }, 'Couldn\'t look it up. Check your internet connection.'),
        'For friends outside your home. Only works after you forward the server\'s port on your router.'));
    }).catch(e => addrRows.replaceChildren(h('div', { class: 'dim' }, e.message)));

    body.append(
      h('div', { class: 'set-group' },
        h('h2', {}, 'This PC\'s addresses'),
        h('p', {}, 'Friends join with an address and the server\'s port, separated by a colon, like 192.168.1.20:8211. The Copy button next to each server\'s join address includes the right port for you.'),
        addrRows),
      h('div', { class: 'set-group' },
        h('h2', {}, 'Remote access'),
        h('p', {}, 'Control your servers from another PC or your phone on the same network. Open the address below in a browser and sign in with an account.'),
        h('div', { class: 'set-rows' },
          h('div', { class: 'set-row' }, h('label', {}, 'Accounts'),
            h('div', { class: 'inline' }, h('span', { class: 'dim' }, st.hasAccounts ? 'People sign in with their own username and password.' : 'No accounts yet.'),
              h('button', { class: 'btn btn-small', type: 'button', onclick: () => { m.close(); usersDialog(); } }, 'Users & groups'))),
          h('div', { class: 'set-row' }, h('label', {}, 'Allow remote access'),
            h('div', { class: 'inline' }, h('span', { class: 'toggle' }, remote, h('span')),
              h('span', { class: 'dim' }, st.hasAccounts ? (st.listeningOnLan ? 'On. Other devices can connect.' : 'Off. Only this PC can open Hostbay.') : 'Create an account first.'))),
          st.listeningOnLan ? h('div', { class: 'set-row' }, h('label', {}, 'Address'),
            h('div', { class: 'url-list' }, st.lanUrls.length ? st.lanUrls.map(u => copyLine(u, 'the link')) : h('span', { class: 'dim' }, 'No network connection found.'))) : null,
          h('div', { class: 'set-row' }, h('label', {}, 'Port'), h('div', {}, port,
            h('div', { class: 'help' }, st.port !== st.configuredPort ? `Port ${st.configuredPort} was busy, so Hostbay is using ${st.port}.` : 'When you first turn on remote access, Windows asks whether to allow Hostbay through the firewall. Allow it on private networks.'))),
        )),
      internetGroup(),
      portForwardGroup(),
      discordGroup(),
      h('div', { class: 'set-group' },
        h('h2', {}, 'Window'),
        h('div', { class: 'set-rows' },
          h('div', { class: 'set-row' }, h('label', {}, 'Keep running in the tray'),
            h('div', { class: 'inline' }, h('span', { class: 'toggle' }, tray, h('span')), h('span', { class: 'dim' }, 'Closing the window while servers run hides Hostbay in the tray instead of quitting'))),
          h('div', { class: 'set-row' }, h('label', {}, 'Data folder'), h('div', { class: 'mono-path', style: { paddingTop: '8px', whiteSpace: 'normal' } }, st.dataDir)),
        )),
      h('p', { class: 'dim', style: { fontSize: '12.5px' } }, `Hostbay ${st.version || ''}`),
    );
    remote.disabled = !st.hasAccounts;
    save.onclick = async () => {
      try {
        const r = await api('PUT', '/api/settings', { remoteAccess: remote.checked, port: Number(port.value), minimizeToTray: tray.checked });
        if (r.restarting) {
          toast('Applying network settings…');
          m.close();
          // The server restarts; reconnect once it's back.
          setTimeout(() => location.reload(), 1500);
        } else { toast('Settings saved.', 'good'); m.close(); }
      } catch (e) { toast(e.message, 'bad'); }
    };
  };
  const save = h('button', { class: 'btn btn-primary', type: 'button' }, 'Save');
  const m = openModal({ title: 'App settings', body, foot: [isLocal ? null : h('a', { class: 'btn btn-ghost', href: '/auth/logout' }, 'Sign out'), h('span', { class: 'spacer' }), save] });
  draw();
}

// ---------------- your account ----------------

function accountDialog() {
  const me = state.me;
  const cur = h('input', { class: 'input', type: 'password', autocomplete: 'current-password' });
  const next = h('input', { class: 'input', type: 'password', autocomplete: 'new-password' });
  const again = h('input', { class: 'input', type: 'password', autocomplete: 'new-password' });
  const save = h('button', { class: 'btn btn-primary', type: 'button' }, 'Change password');
  save.onclick = async () => {
    if (next.value !== again.value) { toast('The new passwords don\'t match.', 'bad'); return; }
    save.disabled = true;
    try {
      await api('POST', '/api/me/password', { current: cur.value, next: next.value });
      toast('Password changed. Your other devices have been signed out.', 'good');
      m.close();
    } catch (e) { toast(e.message, 'bad'); }
    save.disabled = false;
  };
  const m = openModal({
    title: `Your account: ${me.username}`, small: true,
    body: h('div', { class: 'field-stack' },
      h('p', {}, 'Changing your password signs you out on every other device.'),
      h('label', {}, 'Current password', cur),
      h('label', {}, 'New password', next),
      h('label', {}, 'New password again', again),
      h('div', { class: 'dim', style: { fontSize: '12.5px' } }, 'At least 8 characters. Use 10 or more if the host lets Hostbay be reached from the internet.')),
    foot: [h('a', { class: 'btn btn-ghost', href: '/auth/logout' }, 'Sign out'), h('span', { class: 'spacer' }), save],
  });
}

// ---------------- users & groups (full access) ----------------

function passwordPrompt(title, help) {
  return new Promise(resolve => {
    let done = false;
    const pw = h('input', { class: 'input', type: 'password', autocomplete: 'new-password', autofocus: true });
    const finish = v => { if (!done) { done = true; resolve(v); m.close(); } };
    const ok = h('button', { class: 'btn btn-primary', type: 'button', onclick: () => finish(pw.value) }, 'Save password');
    pw.addEventListener('keydown', e => { if (e.key === 'Enter') finish(pw.value); });
    const m = openModal({ title, small: true, body: h('div', { class: 'field-stack' }, h('p', {}, help), h('label', {}, 'New password', pw)),
      foot: [h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => finish(null) }, 'Cancel'), ok],
      onClose: () => { if (!done) { done = true; resolve(null); } } });
  });
}

async function usersDialog(tab = 'users') {
  let data;
  try { data = await api('GET', '/api/accounts'); } catch (e) { toast(e.message, 'bad'); return; }
  const body = h('div');
  const tabs = h('div', { class: 'mtabs', role: 'tablist' });
  const panel = h('div');
  const show = t => {
    tab = t;
    $$('[data-t]', tabs).forEach(b => b.setAttribute('aria-selected', b.dataset.t === t ? 'true' : 'false'));
    panel.replaceChildren(t === 'users' ? usersPanel() : groupsPanel());
  };
  tabs.append(
    h('button', { class: 'tab', type: 'button', role: 'tab', 'data-t': 'users', onclick: () => show('users') }, 'Users'),
    h('button', { class: 'tab', type: 'button', role: 'tab', 'data-t': 'groups', onclick: () => show('groups') }, 'Groups'));
  body.append(tabs, panel);
  const m = openModal({ title: 'Users & groups', body, wide: true });
  m.modal.classList.add('wide');

  const apply = async (method, url, payload, ok) => {
    try { data = await api(method, url, payload); if (ok) toast(ok, 'good'); show(tab); return true; }
    catch (e) { toast(e.message, 'bad'); return false; }
  };
  const groupName = id => data.groups.find(g => g.id === id)?.name || 'No group';
  const groupSelect = (value, onchange) => {
    const sel = h('select', { class: 'input select' }, data.groups.map(g => h('option', { value: g.id, ...(g.id === value ? { selected: true } : {}) }, g.name)));
    if (onchange) sel.onchange = () => onchange(sel.value);
    return sel;
  };

  function usersPanel() {
    const name = h('input', { class: 'input', type: 'text', placeholder: 'Username', autocomplete: 'off', spellcheck: 'false' });
    const pw = h('input', { class: 'input', type: 'password', placeholder: `Password (${data.minPasswordLength}+ characters)`, autocomplete: 'new-password' });
    const grp = groupSelect(data.groups.find(g => !g.fullAccess)?.id || data.groups[0]?.id);
    const add = h('button', { class: 'btn btn-primary', type: 'button' }, 'Add user');
    add.onclick = () => apply('POST', '/api/accounts/users', { username: name.value, password: pw.value, groupId: grp.value }, `Added ${name.value}.`);
    const list = h('div', { class: 'acct-list' });
    if (!data.users.length) list.append(h('div', { class: 'file-note' }, 'No accounts yet. Add one above; remote sign-in needs at least one.'));
    for (const u of data.users) {
      const last = u.lastSignIn ? `Last signed in ${new Date(u.lastSignIn).toLocaleString()}` : 'Never signed in';
      list.append(h('div', { class: `acct-row${u.disabled ? ' off' : ''}` },
        h('div', { class: 'acct-name' }, h('strong', {}, u.username),
          h('span', { class: 'dim' }, u.disabled ? 'Disabled' : last),
          !u.longPassword ? h('span', { class: 'acct-flag', title: `Shorter than ${data.minInternetPasswordLength} characters: blocks internet access` }, 'Short password') : null),
        groupSelect(u.groupId, gid => apply('PUT', `/api/accounts/users/${u.id}`, { groupId: gid }, `${u.username} is now in ${groupName(gid)}.`)),
        h('div', { class: 'acct-actions' },
          h('button', { class: 'btn btn-small', type: 'button', onclick: async () => {
            const p = await passwordPrompt(`New password for ${u.username}`, 'They\'ll be signed out everywhere and need this password next time.');
            if (p) apply('PUT', `/api/accounts/users/${u.id}`, { password: p }, `Password for ${u.username} changed.`);
          } }, 'Reset password'),
          h('button', { class: 'btn btn-small', type: 'button', onclick: () => apply('PUT', `/api/accounts/users/${u.id}`, { disabled: !u.disabled }, u.disabled ? `${u.username} can sign in again.` : `${u.username} is disabled and signed out.`) }, u.disabled ? 'Enable' : 'Disable'),
          h('button', { class: 'btn btn-small btn-danger', type: 'button', onclick: async () => {
            if (await confirmDialog(`Delete ${u.username}?`, 'They\'ll be signed out and won\'t be able to sign in again.', { okText: 'Delete', danger: true }))
              apply('DELETE', `/api/accounts/users/${u.id}`, undefined, `Deleted ${u.username}.`);
          } }, 'Delete'))));
    }
    return h('div', { class: 'field-stack' },
      h('p', {}, 'Each person gets their own sign-in. Their group decides what they can do and which servers they see.'),
      h('div', { class: 'acct-add' }, name, pw, grp, add),
      list);
  }

  function groupsPanel() {
    const wrap = h('div', { class: 'field-stack' },
      h('p', {}, 'Groups decide what their members can do and which servers they see. Changes apply straight away, even to people who are signed in.'));
    const card = g => {
      const draft = structuredClone(g);
      const nameIn = h('input', { class: 'input', type: 'text', value: draft.name, disabled: g.builtIn || null });
      nameIn.oninput = () => draft.name = nameIn.value;
      const full = h('input', { type: 'checkbox', ...(draft.fullAccess ? { checked: true } : {}), disabled: g.builtIn || null });
      const perms = h('div', { class: 'perm-grid' });
      const servers = h('div', { class: 'perm-servers' });
      const drawInner = () => {
        perms.replaceChildren(...data.permissions.map(p => {
          const cb = h('input', { type: 'checkbox', ...(draft.fullAccess || draft.permissions.includes(p.id) ? { checked: true } : {}), disabled: draft.fullAccess || null });
          cb.onchange = () => { draft.permissions = cb.checked ? [...draft.permissions, p.id] : draft.permissions.filter(x => x !== p.id); };
          return h('label', { class: `perm${p.risky ? ' risky' : ''}`, title: p.help }, cb, h('span', {}, h('b', {}, p.label), h('small', {}, p.help)));
        }));
        const all = h('input', { type: 'checkbox', ...(draft.fullAccess || draft.allServers ? { checked: true } : {}), disabled: draft.fullAccess || null });
        all.onchange = () => { draft.allServers = all.checked; drawInner(); };
        servers.replaceChildren(h('label', { class: 'perm' }, all, h('span', {}, h('b', {}, 'All servers'), h('small', {}, 'Including ones added later'))),
          ...(!draft.fullAccess && !draft.allServers ? data.servers.map(s => {
            const cb = h('input', { type: 'checkbox', ...(draft.serverIds.includes(s.id) ? { checked: true } : {}) });
            cb.onchange = () => { draft.serverIds = cb.checked ? [...draft.serverIds, s.id] : draft.serverIds.filter(x => x !== s.id); };
            return h('label', { class: 'perm' }, cb, h('span', {}, h('b', {}, s.name), h('small', {}, game(s.gameId).name)));
          }) : []));
      };
      full.onchange = () => { draft.fullAccess = full.checked; drawInner(); };
      drawInner();
      return h('div', { class: 'group-card' },
        h('div', { class: 'group-head' }, nameIn,
          h('label', { class: 'inline', title: 'Everything, including accounts, app settings and every server' }, h('span', { class: 'toggle' }, full, h('span')), h('span', {}, 'Full access')),
          h('span', { class: 'dim' }, `${g.members || 0} member${g.members === 1 ? '' : 's'}`)),
        g.builtIn ? h('p', { class: 'dim' }, 'The Owners group always has full access, so the host can never be locked out.') : null,
        h('h4', {}, 'Can do'), perms,
        h('h4', {}, 'Servers'), servers,
        h('div', { class: 'row gap' }, h('span', { class: 'spacer' }),
          !g.builtIn && g.id ? h('button', { class: 'btn btn-small btn-danger', type: 'button', onclick: async () => {
            if (await confirmDialog(`Delete the ${g.name} group?`, 'Move its members to another group first.', { okText: 'Delete', danger: true }))
              apply('DELETE', `/api/accounts/groups/${g.id}`, undefined, `Deleted ${g.name}.`);
          } }, 'Delete group') : null,
          h('button', { class: 'btn btn-small btn-primary', type: 'button', onclick: () => apply('POST', '/api/accounts/groups', draft, `Saved ${draft.name || 'the group'}.`) }, g.id ? 'Save group' : 'Create group')));
    };
    for (const g of data.groups) wrap.append(card(g));
    wrap.append(h('button', { class: 'btn', type: 'button', onclick: () => wrap.insertBefore(card({ id: '', name: '', fullAccess: false, permissions: ['control'], allServers: true, serverIds: [], builtIn: false, members: 0 }), wrap.lastChild) }, 'New group'));
    return wrap;
  }

  show(tab);
}

// ---------------- Discord bot (full access) ----------------

const discordDot = d => h('span', { class: `dot-status ${d.status.state === 'online' ? 'on' : d.status.state === 'error' ? 'bad' : d.status.state === 'connecting' ? 'wait' : ''}` });
const discordSummary = d => {
  const s = d.status;
  if (!d.hasToken) return 'Not set up.';
  if (s.state === 'online') return `Online as ${s.botName || 'your bot'} in ${s.guilds} Discord server${s.guilds === 1 ? '' : 's'}${d.board.posted ? `, status board in #${d.board.channelName}` : ''}.`;
  return s.message;
};

async function discordDialog() {
  let d;
  try { d = await api('GET', '/api/discord'); } catch (e) { toast(e.message, 'bad'); return; }
  const body = h('div', { class: 'field-stack' });
  let channels = null, pollTimer = null, closed = false;
  const m = openModal({ title: 'Discord bot', body, onClose: () => { closed = true; clearTimeout(pollTimer); } });
  m.modal.classList.add('wide');

  const apply = async (method, url, payload, ok) => {
    try { d = await api(method, url, payload); if (ok) toast(ok, 'good'); draw(); return true; }
    catch (e) { toast(e.message, 'bad'); return false; }
  };
  // Straight to this bot's Bot page (where Reset Token is) once we know the application, else the portal home.
  const portalLink = (text = 'your bot\'s page in the Discord Developer Portal') => h('a', {
    href: d.applicationId ? `https://discord.com/developers/applications/${d.applicationId}/bot` : 'https://discord.com/developers/applications',
    target: '_blank', rel: 'noopener', class: 'ext-link' }, text);
  // While connecting, keep the status fresh.
  const poll = () => {
    clearTimeout(pollTimer);
    if (closed || d.status.state !== 'connecting') return;
    pollTimer = setTimeout(async () => { try { d = await api('GET', '/api/discord'); draw(); } catch { } }, 2000);
  };

  const setupGroup = () => {
    const token = h('input', { class: 'input mono', type: 'password', placeholder: 'Bot token', autocomplete: 'off', spellcheck: 'false' });
    const go = h('button', { class: 'btn btn-primary', type: 'button' }, 'Connect');
    go.onclick = async () => {
      go.disabled = true; go.textContent = 'Checking…';
      const ok = await apply('POST', '/api/discord/token', { token: token.value }, 'Bot connected.');
      if (!ok) { go.disabled = false; go.textContent = 'Connect'; }
    };
    token.addEventListener('keydown', e => { if (e.key === 'Enter') go.click(); });
    return h('div', { class: 'set-group' },
      h('h2', {}, 'Connect your bot'),
      h('p', {}, 'Hostbay uses a bot you create, so it\'s yours: your name, your picture, your Discord servers. It takes about a minute:'),
      h('ol', { class: 'steps' },
        h('li', {}, 'Open the ', h('a', { href: 'https://discord.com/developers/applications', target: '_blank', rel: 'noopener' }, 'Discord Developer Portal'), ', click ', h('b', {}, 'New Application'), ' and give it a name, such as "Game Servers".'),
        h('li', {}, 'Open the ', h('b', {}, 'Bot'), ' page, click ', h('b', {}, 'Reset Token'), ' and copy the token.'),
        h('li', {}, 'Paste it here. Hostbay keeps it encrypted on this PC and never shows it again.')),
      h('div', { class: 'acct-add discord-token' }, token, go),
      h('div', { class: 'dim', style: { fontSize: '12.5px', marginTop: '8px' } }, 'The bot doesn\'t need any of the "Privileged Gateway Intents".'));
  };

  const statusGroup = () => {
    const s = d.status;
    const enabled = h('input', { type: 'checkbox', ...(d.enabled ? { checked: true } : {}) });
    enabled.onchange = () => apply('PUT', '/api/discord', { enabled: enabled.checked }, enabled.checked ? 'Bot switched on.' : 'Bot switched off.').then(poll);
    return h('div', { class: 'set-group' },
      h('h2', {}, 'Bot'),
      h('div', { class: 'set-rows' },
        h('div', { class: 'set-row' }, h('label', {}, 'Status'),
          h('div', {}, h('div', { class: 'inline' }, discordDot(d), h('span', {}, discordSummary(d))),
            s.state === 'error' ? h('div', { class: 'help' }, 'Get a new token from ', portalLink(), ' (Reset Token) and paste it below.') : null)),
        h('div', { class: 'set-row' }, h('label', {}, 'Running'),
          h('div', { class: 'inline' }, h('span', { class: 'toggle' }, enabled, h('span')), h('span', { class: 'dim' }, d.enabled ? 'The bot is online while Hostbay runs.' : 'Off. The bot shows as offline in Discord.'))),
        h('div', { class: 'set-row' }, h('label', {}, 'Discord servers'),
          h('div', {},
            h('div', { class: 'inline' },
              d.inviteUrl ? h('a', { class: 'btn btn-small btn-primary', href: d.inviteUrl, target: '_blank', rel: 'noopener' }, 'Add the bot to a Discord server') : null,
              h('button', { class: 'btn btn-small', type: 'button', onclick: async () => { try { d = await api('GET', '/api/discord'); channels = null; draw(); } catch { } } }, 'Refresh')),
            h('div', { class: 'help' }, s.state === 'online' && s.guilds === 0
              ? 'The bot isn\'t in any Discord server yet. Use the button, pick your server and click Authorise. You need the Manage Server permission there.'
              : 'Opens Discord in your browser. Pick a server and click Authorise.'))),
        h('div', { class: 'set-row' }, h('label', {}, s.state === 'error' ? 'New token' : 'Replace token'),
          h('div', {}, setupGroup().querySelector('.discord-token'),
            h('div', { class: 'help' }, 'Need a new token? Open ', portalLink(), ', click ', h('b', {}, 'Reset Token'), ', copy it and paste it here. Resetting stops the old token working straight away.'))),
        h('div', { class: 'set-row' }, h('label', {}, ''),
          h('div', {}, h('button', { class: 'btn btn-small btn-danger', type: 'button', onclick: async () => {
            if (await confirmDialog('Disconnect the bot?', 'Hostbay forgets the token and the bot goes offline. A status board already posted stays in Discord but stops updating.', { okText: 'Disconnect', danger: true }))
              apply('DELETE', '/api/discord/token', undefined, 'Bot disconnected.');
          } }, 'Disconnect bot')))));
  };

  const boardGroup = () => {
    const sel = h('select', { class: 'input select' }, h('option', { value: '' }, channels ? 'Pick a channel' : 'Loading channels…'));
    const post = h('button', { class: 'btn btn-primary', type: 'button' }, d.board.posted ? 'Move here' : 'Post status board');
    const fill = () => {
      sel.replaceChildren(h('option', { value: '' }, channels.length ? 'Pick a channel' : 'The bot isn\'t in any Discord server yet'));
      for (const g of channels) {
        sel.append(h('optgroup', { label: g.name }, g.channels.map(c => h('option', { value: c.id, 'data-name': c.name, ...(c.id === d.board.channelId ? { selected: true } : {}) }, `#${c.name}`))));
      }
    };
    if (channels) fill();
    else if (d.status.state === 'online') api('GET', '/api/discord/channels').then(c => { channels = c; fill(); }).catch(e => sel.replaceChildren(h('option', { value: '' }, e.message)));
    else sel.replaceChildren(h('option', { value: '' }, 'Waiting for the bot to come online…'));
    post.onclick = async () => {
      const opt = sel.selectedOptions[0];
      if (!sel.value) { toast('Pick a channel first.', 'bad'); return; }
      post.disabled = true;
      await apply('POST', '/api/discord/board', { channelId: sel.value, channelName: opt.dataset.name }, `Posted the status board in #${opt.dataset.name}.`);
      post.disabled = false;
    };
    return h('div', { class: 'set-group' },
      h('h2', {}, 'Status board'),
      h('p', {}, 'A message that Hostbay keeps up to date in a channel: each server\'s status and address, with buttons that send someone the address to copy.'),
      d.status.boardError ? h('div', { class: 'notice bad', style: { margin: '0 0 12px' } }, d.status.boardError) : null,
      h('div', { class: 'set-rows' },
        d.board.posted ? h('div', { class: 'set-row' }, h('label', {}, 'Posted in'),
          h('div', { class: 'inline' }, h('span', {}, `#${d.board.channelName || 'a channel'}`),
            h('button', { class: 'btn btn-small btn-danger', type: 'button', onclick: () => apply('DELETE', '/api/discord/board', undefined, 'Removed the status board.') }, 'Remove'))) : null,
        h('div', { class: 'set-row' }, h('label', {}, d.board.posted ? 'Move to' : 'Channel'),
          h('div', {}, h('div', { class: 'inline' }, sel, post),
            h('div', { class: 'help' }, 'The bot needs View Channel, Send Messages and Embed Links there. Tip: a read-only #servers channel works well.')))));
  };

  const shareGroup = () => {
    const host = h('input', { class: 'input mono', type: 'text', value: d.addressHost, placeholder: 'Your public IP (automatic)', spellcheck: 'false' });
    const all = h('input', { type: 'checkbox', ...(d.allServers ? { checked: true } : {}) });
    const offline = h('input', { type: 'checkbox', ...(d.showOffline ? { checked: true } : {}) });
    const priv = h('input', { type: 'checkbox', ...(d.privateReplies ? { checked: true } : {}) });
    let ids = [...d.serverIds];
    const list = h('div', { class: 'perm-grid' });
    const drawList = () => list.replaceChildren(...(all.checked ? [] : d.servers.map(s => {
      const cb = h('input', { type: 'checkbox', ...(ids.includes(s.id) ? { checked: true } : {}) });
      cb.onchange = () => { ids = cb.checked ? [...ids, s.id] : ids.filter(x => x !== s.id); };
      return h('label', { class: 'perm' }, cb, h('span', {}, h('b', {}, s.name), h('small', {}, game(s.gameId).name)));
    })));
    all.onchange = drawList;
    drawList();
    const save = h('button', { class: 'btn btn-primary btn-small', type: 'button' }, 'Save');
    save.onclick = () => apply('PUT', '/api/discord', { addressHost: host.value, allServers: all.checked, serverIds: ids, showOffline: offline.checked, privateReplies: priv.checked }, 'Saved. Discord updates in a few seconds.');
    const toggleRow = (label, cb, text) => h('div', { class: 'set-row' }, h('label', {}, label), h('div', { class: 'inline' }, h('span', { class: 'toggle' }, cb, h('span')), h('span', { class: 'dim' }, text)));
    return h('div', { class: 'set-group' },
      h('h2', {}, 'What to share'),
      h('div', { class: 'notice warn', style: { margin: '0 0 12px' } }, 'Everyone in the Discord servers your bot is in can see these servers and their addresses.'),
      h('div', { class: 'set-rows' },
        h('div', { class: 'set-row' }, h('label', {}, 'Address', h('span', { class: 'label-sub' }, 'Shown with each server\'s port')),
          h('div', {}, host, h('div', { class: 'help' }, 'Leave empty to share your public IP; Hostbay keeps it current. If you have a host name (for example from a dynamic DNS service), enter it to share that instead.'))),
        toggleRow('All servers', all, 'Including ones you add later'),
        h('div', { class: 'set-row' }, h('label', {}, ''), list),
        toggleRow('Offline servers', offline, 'Show servers that aren\'t running too'),
        toggleRow('Private replies', priv, 'Answers to commands and buttons are only visible to the person who asked'),
        h('div', { class: 'set-row' }, h('label', {}, ''), h('div', {}, save))));
  };

  const commandsGroup = () => h('div', { class: 'set-group' },
    h('h2', {}, 'Commands'),
    h('p', {}, 'Anyone in your Discord server can use these. New commands can take a minute to show up in Discord.'),
    h('div', { class: 'set-rows' },
      h('div', { class: 'set-row' }, h('label', {}, h('code', {}, '/servers')), h('span', {}, 'Every shared server with its status and address.')),
      h('div', { class: 'set-row' }, h('label', {}, h('code', {}, '/ip')), h('span', {}, 'The address for one server, by itself so it\'s easy to copy (also on phones).'))));

  function draw() {
    body.replaceChildren(...(d.hasToken ? [statusGroup(), boardGroup(), shareGroup(), commandsGroup()] : [setupGroup()]));
    poll();
  }
  draw();
}

// ---------------- live server list ----------------

function connectEvents() {
  const es = new EventSource('/api/events');
  es.addEventListener('servers', e => {
    const prev = current();
    state.servers = JSON.parse(e.data);
    const now = current();
    renderSidebar();
    $('#emptyState').hidden = !!state.servers.length;
    if (!now) {
      if (state.servers.length) select(state.servers[0].id);
      else { $('#serverView').hidden = true; state.selectedId = null; state.logSource?.close(); }
      return;
    }
    if ($('#serverView').hidden) renderAll();
    renderHeader();
    if (prev && now && prev.state !== now.state) {
      if (state.tab === 'configs' && state.editor) renderEditor();
      if (prev.commandChannel !== now.commandChannel) loadDetail();
      // Automatic port forwarding runs on start/stop; pick up its result shortly after.
      if (state.detail?.config.forwardWhileRunning) setTimeout(() => renderPorts(), 3500);
    }
  });
  es.onerror = () => { /* EventSource reconnects by itself */ };
}

// ---------------- wiring ----------------

function wire() {
  $('#addServerBtn').onclick = () => addServerDialog('scan');
  $('#emptyScanBtn').onclick = () => addServerDialog('scan');
  $('#emptyFolderBtn').onclick = () => addServerDialog('folder');
  $('#appSettingsBtn').onclick = appSettingsDialog;
  $('#menuOpen').onclick = () => $('#app').classList.add('menu');
  $('#menuClose').onclick = () => $('#app').classList.remove('menu');
  $('#startBtn').onclick = () => control('start');
  $('#stopBtn').onclick = () => control('stop');
  $('#restartBtn').onclick = () => control('restart');
  $('#killBtn').onclick = () => control('kill');
  $('#headPath').onclick = () => copyText(current()?.installDir || '', 'Copied the folder path.');
  $$('.tab[data-tab]').forEach(t => t.onclick = () => setTab(t.dataset.tab));

  const c = $('#console');
  c.addEventListener('scroll', () => {
    stickToBottom = c.scrollHeight - c.scrollTop - c.clientHeight < 40;
    $('#jumpBtn').hidden = stickToBottom;
  });
  $('#jumpBtn').onclick = () => scrollConsole(true);
  $('#logFilter').addEventListener('input', redrawConsole);
  const wrap = $('#logWrap'), times = $('#logTimes');
  wrap.checked = localStorage.getItem('hb.wrap') !== '0';
  times.checked = localStorage.getItem('hb.times') === '1';
  const applyView = () => { c.classList.toggle('wrap', wrap.checked); c.classList.toggle('times', times.checked); localStorage.setItem('hb.wrap', wrap.checked ? '1' : '0'); localStorage.setItem('hb.times', times.checked ? '1' : '0'); };
  wrap.onchange = applyView; times.onchange = applyView; applyView();
  $('#copyLogBtn').onclick = () => copyText(state.logLines.map(l => l.text).join('\n'), 'Copied the log to the clipboard.');
  $('#clearLogBtn').onclick = async () => {
    const s = current(); if (!s) return;
    await api('POST', `/api/servers/${s.id}/logs/clear`).catch(() => { });
    state.logLines = []; redrawConsole();
  };
  $('#cmdForm').addEventListener('submit', sendCommand);
  $('#cmdInput').addEventListener('keydown', e => {
    const hist = state.cmdHistory;
    if (e.key === 'ArrowUp' && hist.length) { e.preventDefault(); state.cmdHistoryPos = Math.min(hist.length - 1, state.cmdHistoryPos + 1); e.target.value = hist[state.cmdHistoryPos]; }
    if (e.key === 'ArrowDown') { e.preventDefault(); state.cmdHistoryPos = Math.max(-1, state.cmdHistoryPos - 1); e.target.value = state.cmdHistoryPos < 0 ? '' : hist[state.cmdHistoryPos]; }
  });

  // editor
  $$('.seg-btn').forEach(b => b.onclick = () => setMode(b.dataset.mode));
  $('#formFilter').addEventListener('input', renderForm);
  $('#saveBtn').onclick = () => saveFile();
  $('#revertBtn').onclick = () => {
    const ed = state.editor; if (!ed) return;
    ed.text = ed.original; ed.model = parseConfig(ed.format, ed.text); renderEditor();
  };
  $('#backupSelect').onchange = e => loadBackup(e.target.value);
  const ta = $('#textArea');
  ta.addEventListener('input', () => { state.editor.text = ta.value; updateGutter(); updateDirty(); });
  ta.addEventListener('scroll', () => { $('#gutter').scrollTop = ta.scrollTop; });
  ta.addEventListener('keydown', e => {
    if (e.key === 'Tab' && !e.ctrlKey && !e.altKey) {
      e.preventDefault();
      document.execCommand('insertText', false, '\t');
    }
  });
  document.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
      if (state.tab === 'configs' && state.editor) { e.preventDefault(); saveFile(); }
    }
  });
  window.addEventListener('beforeunload', e => { if (isDirty()) { e.preventDefault(); e.returnValue = ''; } });
}

// Shows who's signed in and only the app-wide buttons this account can use.
function applyAccount() {
  const me = state.me;
  if (!me) return;
  $('#addServerBtn').hidden = !me.canAddServers;
  $('#emptyScanBtn').hidden = !me.canAddServers;
  $('#emptyFolderBtn').hidden = !me.canAddServers;
  if (!me.canAddServers) {
    $('#emptyState h1').textContent = 'No servers here yet';
    $('#emptyState p').textContent = 'The host hasn\'t shared any servers with your account yet.';
  }
  $('#appSettingsBtn').hidden = !me.full;
  const who = $('#whoami');
  who.replaceChildren(h('span', { class: 'who-dot' }), h('span', {}, me.isLocal ? 'On the host PC' : `Signed in as ${me.username}`));
  who.onclick = me.isLocal ? appSettingsDialog : accountDialog;
  who.title = me.isLocal ? 'App settings' : 'Your account: change your password or sign out';
}

async function init() {
  wire();
  document.body.classList.toggle('ips-hidden', hideIps);
  try {
    const [me, games, servers] = await Promise.all([api('GET', '/api/me'), api('GET', '/api/games'), api('GET', '/api/servers')]);
    state.me = me;
    for (const g of games) state.games[g.id] = g;
    state.servers = servers;
    applyAccount();
  } catch (e) { toast(e.message, 'bad'); }
  // Deep links: #server=<id>&tab=configs&dialog=add|settings
  const hash = new URLSearchParams(location.hash.slice(1));
  if (hash.get('server')) state.selectedId = hash.get('server');
  if (['console', 'configs', 'settings'].includes(hash.get('tab'))) state.tab = hash.get('tab');
  if (!state.servers.some(s => s.id === state.selectedId)) state.selectedId = state.servers[0]?.id || null;
  renderAll();
  if (state.selectedId) { loadDetail(); connectLogs(); }
  connectEvents();
  if (hash.get('dialog') === 'add') addServerDialog(hash.get('addtab') || 'scan');
  if (hash.get('dialog') === 'settings') appSettingsDialog();
}

init();
