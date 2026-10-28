// Checks the phone dashboard in a real browser: headless Chrome emulating Android phones, driven over the
// DevTools protocol (Node 22+ built-ins only, nothing to install). Started by `run-tests.ps1 -Browser`,
// which owns the board; this script asks it to change things through request/ack files in <dir>.
// Usage: node phone-browser.mjs <devtoolsPort> <dashboardPort> <dir> <renderDir>
// These are simulated phones (Chrome's mobile emulation), not a physical Android device.
import fs from 'node:fs';
import path from 'node:path';

const [debugPort, pagePort, dir, renderDir] = process.argv.slice(2);
const base = `http://127.0.0.1:${pagePort}/`;
const results = [];
const check = (name, ok, info = '') => {
  results.push(`${ok ? 'PASS' : 'FAIL'}\t${name}\t${ok ? '' : String(info).replace(/\s+/g, ' ')}`);
  process.stderr.write(`${ok ? 'PASS' : 'FAIL'} ${name} ${ok ? '' : info}\n`);   // progress, if a run gets stuck
};
const note = (text) => results.push(`INFO\t${text}\t`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function connect() {
  const t = await (await fetch(`http://127.0.0.1:${debugPort}/json/new?about:blank`, { method: 'PUT' })).json();
  const ws = new WebSocket(t.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let id = 0;
  const pending = new Map(), listeners = [];
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); }
    else listeners.forEach((l) => l(m));
  };
  const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });
  return { send, on: (l) => listeners.push(l), close: () => ws.close() };
}

const c = await connect();
const ev = async (expr) => {
  const r = await c.send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
};
async function waitFor(expr, ms = 5000) {
  const end = Date.now() + ms;
  while (Date.now() < end) { try { if (await ev(expr)) return true; } catch { } await sleep(100); }
  return false;
}
async function ask(name) {
  fs.writeFileSync(path.join(dir, `req-${name}`), '');
  const ack = path.join(dir, `ack-${name}`);
  for (let i = 0; i < 100; i++) { if (fs.existsSync(ack)) return true; await sleep(100); }
  return false;
}
async function shot(name) {
  const m = await c.send('Page.getLayoutMetrics');
  const s = m.cssContentSize || m.contentSize;
  const r = await c.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip: { x: 0, y: 0, width: s.width, height: s.height, scale: 1 } });
  fs.writeFileSync(path.join(renderDir, `phone-${name}.png`), Buffer.from(r.data, 'base64'));
}
async function phone(width, height = 800) {
  await c.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 2.625, mobile: true });
  await c.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  await c.send('Emulation.setUserAgentOverride', { userAgent: 'Mozilla/5.0 (Linux; Android 14; SM-S921B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36' });
}
async function tap(selector) {
  const box = await ev(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) return null; const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  if (!box) return false;
  const pt = [{ x: box.x, y: box.y, radiusX: 4, radiusY: 4, force: 1, id: 1 }];
  await c.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pt });
  await sleep(60);
  await c.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  return true;
}
const rowSel = (name) => `[...document.querySelectorAll('#rows .row')].find(r => r.querySelector('.name')?.textContent.replace(/\\u00a0/g, ' ') === ${JSON.stringify(name)})`;
const statusOf = (name) => `(${rowSel(name)})?.querySelector('.status')?.textContent`;
const connText = `document.getElementById('conn').textContent`;

// Layout / content checks against the board the page itself was given.
const LAYOUT = `(async () => {
  const api = await (await fetch('api/board', { cache: 'no-store' })).json();
  const de = document.documentElement, problems = [];
  if (de.scrollWidth > innerWidth || document.body.scrollWidth > innerWidth) problems.push('horizontal scroll ' + de.scrollWidth + '>' + innerWidth);
  const rows = [...document.querySelectorAll('#rows .row:not(.placeholder)')];
  const names = rows.map(r => r.querySelector('.name').textContent.replace(/\\u00a0/g, ' ')).join('|');   // "#n" is kept on the name's line with a no-break space
  const want = api.rows.map(r => r.n).join('|');
  if (names !== want) problems.push('names/order differ from the laptop: ' + names + ' vs ' + want);
  const re = { WORKING: /^WORKING \u00b7 (\\d\\d:\\d\\d|\\d+h \\d\\dm|\\d+d \\d+h)$/, NEEDS_YOU: /^NEEDS YOU \u00b7 (<1m|\\d+m|\\d+h( \\d+m)?|\\d+d)$/,
               DONE: /^DONE \u00b7 (just now|\\d+[mhd] ago)$/, ERROR: /^ERROR \u00b7 ([A-Za-z0-9_.\\-\u2026]+ \u00b7 )?(<1m|\\d+m|\\d+h( \\d+m)?|\\d+d)$/ };
  rows.forEach((r, i) => {
    const d = api.rows[i], name = r.querySelector('.name'), st = r.querySelector('.status'), x = r.querySelector('.x');
    const rr = r.getBoundingClientRect(), nr = name.getBoundingClientRect(), sr = st.getBoundingClientRect();
    const tag = '#' + i + ' ' + d.n + ': ';
    if (!re[d.s].test(st.textContent)) problems.push(tag + 'status text "' + st.textContent + '"');
    if (!r.classList.contains('s-' + d.s)) problems.push(tag + 'state class');
    if (sr.right > rr.right + .5 || sr.left < rr.left) problems.push(tag + 'status outside row');
    if (nr.right > sr.left + .5) problems.push(tag + 'name overlaps status');
    if (nr.width < 60) problems.push(tag + 'name squeezed to ' + Math.round(nr.width) + 'px');
    if (parseFloat(getComputedStyle(name).fontSize) < 15) problems.push(tag + 'name text too small');
    if (parseFloat(getComputedStyle(st).fontSize) < 14) problems.push(tag + 'status text too small');
    if (st.scrollWidth > st.clientWidth + 1) problems.push(tag + 'status clipped');
    if (d.s === 'DONE') {
      if (!x) problems.push(tag + 'no dismiss button');
      else { const xr = x.getBoundingClientRect(); if (xr.width < 44 || xr.height < 40) problems.push(tag + 'dismiss target ' + Math.round(xr.width) + 'x' + Math.round(xr.height)); if (xr.right > innerWidth) problems.push(tag + 'dismiss off screen'); }
    } else if (x) problems.push(tag + 'dismiss button on a ' + d.s + ' row');
    if (r.onclick || getComputedStyle(r).cursor === 'pointer') problems.push(tag + 'row looks clickable');
  });
  return { problems, rows: rows.length, conn: ${connText} };
})()`;

try {
  // ---------- 1. Android widths, all states, long names, many workers ----------
  for (const [w, h] of [[360, 800], [412, 915], [320, 640]]) {
    await phone(w, h);
    await c.send('Page.navigate', { url: base });
    const live = await waitFor(`${connText} === 'LIVE' && document.querySelectorAll('#rows .row:not(.placeholder)').length > 0`, 8000);
    check(`${w}px: page loads and goes LIVE`, live, await ev(connText).catch((e) => e.message));
    const l = await ev(LAYOUT);
    check(`${w}px: same rows/order as the laptop; no horizontal scroll, overlap, clipping or tiny text; 44px dismiss targets`, l.problems.length === 0, l.problems.slice(0, 6).join('; '));
    await shot(`${w}`);
  }
  await phone(360, 800);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);

  const longName = await ev(`(() => { const n = [...document.querySelectorAll('#rows .name')].find(e => e.textContent.startsWith('A Very Long')); if (!n) return null; const cs = getComputedStyle(n); return { lines: Math.round(n.getBoundingClientRect().height / parseFloat(cs.lineHeight)), clamped: n.scrollHeight > n.clientHeight + 1, title: n.title }; })()`);
  check('long project name wraps to at most 2 lines with the full name kept in its title', longName && longName.lines <= 2 && longName.title.startsWith('A Very Long Project Folder Name'), JSON.stringify(longName));
  const errBrief = await ev(`(${statusOf('A Very Long Project Folder Name That Keeps Going And Going')})`);
  note(`narrow ERROR row shows "${errBrief}"`);
  const notes = await ev(`(() => ({ perm: typeof Notification === 'undefined' ? 'n/a' : Notification.permission, sw: navigator.serviceWorker ? 'available' : 'n/a' }))()`);
  check('no notification permission requested', notes.perm !== 'granted' && notes.perm !== 'denied', JSON.stringify(notes));
  const swRegs = await ev(`navigator.serviceWorker ? navigator.serviceWorker.getRegistrations().then(r => r.length) : 0`).catch(() => 0);
  check('no service worker registered', swRegs === 0, swRegs);

  // ---------- 2. timers tick on their own ----------
  const t1 = await ev(statusOf('Portal'));
  await sleep(2200);
  const t2 = await ev(statusOf('Booking Software #1'));
  const t3 = await ev(statusOf('Booking Software #1'));
  await sleep(1100);
  const t4 = await ev(statusOf('Booking Software #1'));
  check('WORKING timer counts up without a refresh', t3 !== t4 && /^WORKING/.test(t4), `${t3} -> ${t4}`);
  note(`statuses: Portal "${t1}", Booking Software #1 "${t2}"`);

  // ---------- 3. desktop dismiss -> phone ----------
  const hadWebsite = await ev(`!!(${rowSel('Website')})`);
  await ask('desktop-dismiss');
  const goneFromPhone = await waitFor(`!(${rowSel('Website')})`, 3000);
  check('dismissed on the desktop: disappears from the phone without a refresh', hadWebsite && goneFromPhone);

  // ---------- 4. phone dismiss (tap) ----------
  const tapped = await tap(`#rows .row.s-DONE .x`);
  const dismissedName = 'Guest Form';
  const gone = await waitFor(`!(${rowSel(dismissedName)})`, 3000);
  check('tapping x on a DONE row removes it from the phone', tapped && gone);
  await ask('check-phone-dismiss');

  // ---------- 5. page hidden (Android background) and resumed ----------
  await ev(`Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' }); document.dispatchEvent(new Event('visibilitychange')); true`);
  const before = await ev(statusOf('Booking Software #2'));
  await ask('hidden-change');
  await sleep(1500);
  const whileHidden = await ev(statusOf('Booking Software #2'));
  check('while backgrounded the page stops polling (no update while hidden)', /^WORKING/.test(before) && /^WORKING/.test(whileHidden), `${before} / ${whileHidden}`);
  await ev(`Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' }); document.dispatchEvent(new Event('visibilitychange')); true`);
  const resumed = await waitFor(`/^NEEDS YOU/.test(${statusOf('Booking Software #2')}) && ${connText} === 'LIVE'`, 3000);
  check('on resume it refetches the current board immediately (simulated visibility change)', resumed, await ev(statusOf('Booking Software #2')));

  // ---------- 6. the delayed-dismissal race, from the page ----------
  await ask('race-prompt');   // the session got a new prompt; the laptop hasn't redrawn yet, so the phone still shows DONE
  const raceShown = await ev(`/^DONE/.test(${statusOf('Race')})`);
  await tap(`${'#rows .row.s-DONE .x'}`);   // Race is now the only DONE row
  const refused = await waitFor(`/changed/.test(document.getElementById('note').textContent) && !document.getElementById('note').hidden`, 3000);
  await ask('reload');
  const raceWorking = await waitFor(`/^WORKING/.test(${statusOf('Race')})`, 3000);
  check('stale DONE tap after a new prompt: refused, worker stays and shows WORKING', raceShown && refused && raceWorking, `${raceShown} ${refused} ${raceWorking}`);

  // ---------- 7. lost connection (simulated network failure), stale data, recovery ----------
  let mode = 'fail';
  c.on(async (m) => {
    if (m.method !== 'Fetch.requestPaused') return;
    const id = m.params.requestId;
    try {
      if (mode === 'fail') await c.send('Fetch.failRequest', { requestId: id, errorReason: 'InternetDisconnected' });
      else if (mode === '502') await c.send('Fetch.fulfillRequest', { requestId: id, responseCode: 502, responseHeaders: [{ name: 'Content-Type', value: 'text/plain' }], body: Buffer.from('Bad Gateway').toString('base64') });
      else await c.send('Fetch.continueRequest', { requestId: id });
    } catch { }
  });
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/*' }] });
  const rowsBefore = await ev(`document.querySelectorAll('#rows .row:not(.placeholder)').length`);
  await ev(`window.dispatchEvent(new Event('online')); true`);   // makes the page refetch now
  const reconnecting = await waitFor(`${connText} === 'RECONNECTING…'`, 3000);
  const staleUi = await ev(`({ stale: document.getElementById('board').classList.contains('stale'), banner: !document.getElementById('banner').hidden && /Last update \\d/.test(document.getElementById('banner-when').textContent), rows: document.querySelectorAll('#rows .row:not(.placeholder)').length, x: [...document.querySelectorAll('#rows .x')].every(b => b.disabled), empty: /No Claude workers/.test(document.getElementById('rows').textContent) })`);
  check('connection lost: RECONNECTING, last update time shown, rows kept but marked stale, dismiss disabled', reconnecting && staleUi.stale && staleUi.banner && staleUi.rows === rowsBefore && staleUi.x && !staleUi.empty, JSON.stringify(staleUi));
  const frozen1 = await ev(statusOf('Booking Software #1')); await sleep(1100); const frozen2 = await ev(statusOf('Booking Software #1'));
  check('stale timers are frozen at the last contact (not shown as current)', frozen1 === frozen2, `${frozen1} / ${frozen2}`);
  const offline = await waitFor(`${connText} === 'OFFLINE'`, 8000);
  const offlineText = await ev(`document.getElementById('banner').textContent.replace(/\\s+/g, ' ')`);
  check('keeps failing: OFFLINE "Laptop unreachable" with the reason', offline && /Laptop unreachable/.test(offlineText) && /Can't reach the laptop/.test(offlineText), offlineText);
  await shot('offline');
  mode = '502';
  await ev(`window.dispatchEvent(new Event('online')); true`);
  const said502 = await waitFor(`/isn't answering/.test(document.getElementById('banner-why').textContent)`, 4000);
  check('laptop reachable but service down (502 from tailscale serve): says so', said502, await ev(`document.getElementById('banner-why').textContent`));
  await shot('service-down');
  mode = 'pass';
  await c.send('Fetch.disable');
  const back = await waitFor(`${connText} === 'LIVE' && !document.getElementById('board').classList.contains('stale') && document.getElementById('banner').hidden`, 13000);
  check('reconnects automatically and refreshes when the connection returns', back, await ev(connText));

  // ---------- 8. the dashboard is switched off on the laptop, then on again ----------
  await ask('stop');
  const realOffline = await waitFor(`${connText} === 'OFFLINE'`, 25000);
  check('dashboard switched off on the laptop: page goes OFFLINE (real connection failure)', realOffline, await ev(connText));
  await ask('start');
  const realBack = await waitFor(`${connText} === 'LIVE' && /^NEEDS YOU/.test(${statusOf('Booking Software #2')})`, 15000);
  check('switched back on: page recovers by itself with the authoritative board', realBack, await ev(connText));

  // ---------- 9. never connected ----------
  mode = 'fail';
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/*' }] });
  await c.send('Page.navigate', { url: base });
  const neverOk = await waitFor(`${connText} === 'OFFLINE'`, 10000);
  const placeholder = await ev(`document.getElementById('rows').textContent.trim()`);
  check('never connected: says it can\'t reach the laptop, never "No Claude workers"', neverOk && /Can't reach Claude Status/.test(placeholder) && !/No Claude workers/.test(placeholder), placeholder);
  await shot('never-connected');
  await c.send('Fetch.disable');

  // ---------- 10. empty board ----------
  await ask('clear');
  await c.send('Page.navigate', { url: base });
  const empty = await waitFor(`${connText} === 'LIVE' && /No Claude workers/.test(document.getElementById('rows').textContent)`, 8000);
  check('empty board (live): "No Claude workers"', empty);
  await shot('empty');
} catch (e) {
  check('browser test script ran to completion', false, e.stack || e.message);
} finally {
  fs.writeFileSync(path.join(dir, 'results.txt'), results.join('\n') + '\n');
  c.close();
}
