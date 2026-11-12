// End-to-end check of the Linux edition on a real Linux machine (the GitHub Actions workflow runs it after installing
// the .deb). Usage: node smoke.js <sign-in link> <test server folder> [--steamcmd <install folder>]
'use strict';
const [link, serverDir] = process.argv.slice(2);
const scIdx = process.argv.indexOf('--steamcmd');
const steamDir = scIdx > 0 ? process.argv[scIdx + 1] : null;
const base = new URL(link).origin;
let cookie = '';
let failed = 0;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function check(ok, what, detail) {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}${!ok && detail !== undefined ? ' -> ' + JSON.stringify(detail).slice(0, 400) : ''}`);
  if (!ok) failed++;
  return ok;
}
async function api(method, path, body) {
  const r = await fetch(base + path, { method, headers: { cookie, 'X-Emberkeep': '1', 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  const t = await r.text();
  let j; try { j = JSON.parse(t); } catch { j = t; }
  if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${t.slice(0, 300)}`);
  return j;
}
// The console backlog (the logs endpoint streams; read what's there for a moment).
async function logText(id) {
  const ac = new AbortController();
  setTimeout(() => ac.abort(), 2500);
  let text = '';
  try {
    const r = await fetch(`${base}/api/servers/${id}/logs`, { headers: { cookie }, signal: ac.signal });
    const reader = r.body.getReader();
    for (;;) { const { value, done } = await reader.read(); if (done) break; text += Buffer.from(value).toString('utf8'); }
  } catch { /* aborted: done reading */ }
  return text;
}
async function waitState(id, want, seconds) {
  let s;
  for (let i = 0; i < seconds; i++) {
    s = (await api('GET', '/api/servers')).find((x) => x.id === id);
    if (String(s?.state).toLowerCase() === want) return s;
    await sleep(1000);
  }
  return s;
}

(async () => {
  // Sign in with the host link.
  const r = await fetch(link, { redirect: 'manual' });
  cookie = (r.headers.get('set-cookie') || '').split(';')[0];
  check(r.status >= 300 && r.status < 400 && cookie, 'the sign-in link signs in', r.status);

  const me = await api('GET', '/api/me');
  check(me.os === 'linux', '/api/me reports linux', me);
  check(me.full === true, 'signed in as the host', me);

  const games = Object.values(await api('GET', '/api/games'));
  const winOnly = games.filter((g) => g.windowsOnly).map((g) => g.id).sort();
  check(winOnly.join() === ['ace', 'ark-sa', 'conan', 'enshrouded', 'sotf', 'vrising'].sort().join(), 'Windows-only games are marked', winOnly);
  const valheim = games.find((g) => g.id === 'valheim');
  check(valheim && !valheim.windowsOnly, 'Valheim is available');

  const settings = await api('GET', '/api/settings');
  check(typeof settings.dataDir === 'string' && settings.dataDir.startsWith('/var/lib/emberkeep/'), 'data folder is in /var/lib/emberkeep', settings.dataDir);

  // A custom server: a bash script that answers console input and saves on Ctrl+C (SIGINT).
  const created = await api('POST', '/api/servers', { gameId: 'generic', installDir: serverDir, name: 'CI test server' });
  const id = created.config?.id || created.id;
  check(!!id, 'custom server added', created);
  check(/run\.sh$/.test(created.config?.executable || ''), 'run.sh picked as the program', created.config?.executable);

  await api('POST', `/api/servers/${id}/start`);
  const running = await waitState(id, 'running', 30);
  check(String(running?.state).toLowerCase() === 'running', 'server starts (run.sh had no execute bit)', running);
  await sleep(2000);
  await api('POST', `/api/servers/${id}/command`, { command: 'hello from CI' });
  await sleep(2000);
  let log = await logText(id);
  check(log.includes('server ready'), 'console output is captured', log.slice(-500));
  check(log.includes('you said: hello from CI'), 'console commands reach the server', log.slice(-500));

  await api('POST', `/api/servers/${id}/stop`);
  const stopped = await waitState(id, 'stopped', 30);
  check(String(stopped?.state).toLowerCase() === 'stopped', 'server stops', stopped);
  log = await logText(id);
  check(log.includes('got SIGINT, saving'), 'stop sends Ctrl+C (SIGINT) so the server can save', log.slice(-500));

  // A real game through SteamCMD (Linux tarball, steamcmd.sh, progress, install, start, stop).
  if (steamDir) {
    let job = await api('POST', '/api/install', { gameId: 'valheim', dir: steamDir, name: 'CI Valheim' });
    let last = '';
    for (let i = 0; i < 1800 && !job.finished; i++) {
      await sleep(1000);
      job = (await api('GET', '/api/install')).jobs.find((j) => j.id === job.id);
      const line = `${job.state} ${Math.round(job.percent)}% ${job.message}`;
      if (line !== last) { console.log('     ' + line); last = line; }
    }
    check(job.finished && !job.error, 'Valheim installs through SteamCMD', job);
    if (job.serverId) {
      const detail = await api('GET', `/api/servers/${job.serverId}`);
      check(/valheim_server\.x86_64$/.test(detail.config?.executable || ''), 'Valheim uses its Linux program', detail.config?.executable);
      await api('POST', `/api/servers/${job.serverId}/start`);
      const vr = await waitState(job.serverId, 'running', 60);
      check(String(vr?.state).toLowerCase() === 'running', 'Valheim starts', vr);
      await sleep(45000); // let it load and create its world
      const vlog = await logText(job.serverId);
      console.log(vlog.split('\n').filter((l) => l.startsWith('data:')).slice(-15).join('\n'));
      await api('POST', `/api/servers/${job.serverId}/stop`);
      const vs = await waitState(job.serverId, 'stopped', 120);
      check(String(vs?.state).toLowerCase() === 'stopped', 'Valheim stops', vs);
    }
  }

  console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error('FAIL', e); process.exit(1); });
