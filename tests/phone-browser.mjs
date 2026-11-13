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
  const box = await ev(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) return null; const v = e.getBoundingClientRect(); if (v.top < 0 || v.bottom > innerHeight) e.scrollIntoView({ block: 'center' }); const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  if (!box) return false;
  const pt = [{ x: box.x, y: box.y, radiusX: 4, radiusY: 4, force: 1, id: 1 }];
  await c.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pt });
  await sleep(60);
  await c.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  return true;
}
// Show usage / Compact / Show context live in the gear's popover: open it, tap, close it again.
async function tapSetting(selector) {
  await tap('#gear');
  const ok = await tap(selector);
  await tap('#gear');
  return ok;
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
    if (w === 360) {
      const dv = await ev(`({ pressed: document.getElementById('view-list').getAttribute('aria-pressed'), rows: !document.getElementById('rows').hidden, pets: document.getElementById('creatures').hidden, saved: localStorage.getItem('claudeStatus.view') })`);
      check('List is the default view when nothing is saved', dv.pressed === 'true' && dv.rows && dv.pets && dv.saved === null, JSON.stringify(dv));
    }
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

  // ---------- 1b. Claude Usage (account-wide) ----------
  const uSum = await ev(`({ shown: !document.getElementById('usage').hidden, mini: document.getElementById('usage-mini').textContent, open: document.getElementById('usage-detail').hidden, toggle: document.getElementById('usage-toggle').getAttribute('aria-pressed') })`);
  check('usage: shown by default as one compact line (5h 14%, Wk 57%, +1) above the workers, collapsed', uSum.shown && /5h.*14%.*Wk.*57%.*\+1/.test(uSum.mini) && uSum.open && uSum.toggle === 'true', JSON.stringify(uSum));
  await tap('#usage-sum');
  const opened = await waitFor(`!document.getElementById('usage-detail').hidden && document.querySelectorAll('#usage-limits .lim').length === 3`, 3000);
  const uDet = await ev(`(() => { const t = document.getElementById('usage-detail').textContent; const de = document.documentElement; const small = [...document.querySelectorAll('#usage *')].filter(e => e.childElementCount === 0 && e.textContent.trim() && parseFloat(getComputedStyle(e).fontSize) < 12).length; return { t, hscroll: de.scrollWidth > innerWidth, small, btn: document.getElementById('usage-refresh').getBoundingClientRect().height, saved: localStorage.getItem('claudeStatus.usageOpen') }; })()`);
  check('usage: tap expands the full breakdown (titles, % used and left, local reset time + countdown, state, last updated, extra info)', opened && /Account-wide/.test(uDet.t) && /Current session \(5-hour window\)14% used/.test(uDet.t) && /86% left · Resets .+\(in 2h 5\dm\)/.test(uDet.t) && /Current week \(Sonnet only\)91% used/.test(uDet.t) && /Fresh · Last updated/.test(uDet.t) && /Extra usage: off/.test(uDet.t) && uDet.saved === '1', JSON.stringify(uDet).slice(0, 400));
  check('usage: readable on a phone (no horizontal scroll, no text under 12px, 40px Refresh button)', !uDet.hscroll && uDet.small === 0 && uDet.btn >= 40, JSON.stringify({ h: uDet.hscroll, small: uDet.small, btn: uDet.btn }));
  await shot('usage-360');
  const refreshed = await tap('#usage-refresh') && await waitFor(`/Checked the laptop/.test(document.getElementById('note').textContent)`, 4000);
  check('usage: Refresh asks the laptop and says so', refreshed, await ev(`document.getElementById('note').textContent`));
  const g0 = await ev(`({ open: !document.getElementById('settings').hidden, exp: document.getElementById('gear').getAttribute('aria-expanded'), w: document.getElementById('gear').getBoundingClientRect().width })`);
  await tap('#gear');
  const g1 = await ev(`(() => { const p = document.getElementById('settings'), r = p.getBoundingClientRect(), de = document.documentElement; return { open: !p.hidden, exp: document.getElementById('gear').getAttribute('aria-expanded'), inside: r.left >= 0 && r.right <= innerWidth, hscroll: de.scrollWidth > innerWidth, usage: !!p.querySelector('#usage-toggle'), pet: !!p.querySelector('#compact-toggle') && !!p.querySelector('#context-toggle') }; })()`);
  await shot('settings-360');
  await tap('#gear');
  const g2 = await ev(`!document.getElementById('settings').hidden`);
  await tap('#gear'); await tap('h1');
  const g3 = await ev(`!document.getElementById('settings').hidden`);
  check('gear: settings closed by default; tap opens a popover with Show usage / Compact / Show context, inside the screen, no horizontal scroll; tap again or outside closes it', !g0.open && g0.exp === 'false' && g0.w >= 36 && g1.open && g1.exp === 'true' && g1.inside && !g1.hscroll && g1.usage && g1.pet && !g2 && !g3, JSON.stringify({ g0, g1, g2, g3 }));

  await tapSetting('#usage-toggle');
  const hid = await ev(`({ hidden: document.getElementById('usage').hidden, h: document.getElementById('usage').getBoundingClientRect().height, saved: localStorage.getItem('claudeStatus.usage'), conn: ${connText} })`);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);
  const uAfter = await ev(`({ hidden: document.getElementById('usage').hidden, pressed: document.getElementById('usage-toggle').getAttribute('aria-pressed') })`);
  check('usage: Show usage off hides it without leaving space, stays LIVE, and is remembered on this phone', hid.hidden && hid.h === 0 && hid.saved === 'hidden' && hid.conn === 'LIVE' && uAfter.hidden && uAfter.pressed === 'false', JSON.stringify({ hid, uAfter }));
  await tapSetting('#usage-toggle');
  await tap('#usage-sum');   // collapse again for the rest of the run
  check('usage: Show usage on again brings it back', await waitFor(`!document.getElementById('usage').hidden && document.getElementById('usage-detail').hidden`, 2000));

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
  const held = [];
  c.on(async (m) => {
    if (m.method !== 'Fetch.requestPaused') return;
    const id = m.params.requestId;
    try {
      if (mode === 'hold') held.push(id);
      else if (mode === 'fail') await c.send('Fetch.failRequest', { requestId: id, errorReason: 'InternetDisconnected' });
      else if (mode === '502') await c.send('Fetch.fulfillRequest', { requestId: id, responseCode: 502, responseHeaders: [{ name: 'Content-Type', value: 'text/plain' }], body: Buffer.from('Bad Gateway').toString('base64') });
      else await c.send('Fetch.continueRequest', { requestId: id });
    } catch { }
  });
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/*' }] });
  const rowsBefore = await ev(`document.querySelectorAll('#rows .row:not(.placeholder)').length`);
  await ev(`window.dispatchEvent(new Event('online')); true`);   // makes the page refetch now
  const reconnecting = await waitFor(`${connText} === 'RECONNECTING…'`, 3000);
  const staleUi = await ev(`({ stale: document.getElementById('board').classList.contains('stale'), banner: !document.getElementById('banner').hidden && /Last update \\d/.test(document.getElementById('banner-when').textContent), rows: document.querySelectorAll('#rows .row:not(.placeholder)').length, x: [...document.querySelectorAll('#rows .x')].every(b => b.disabled), empty: /No Claude workers/.test(document.getElementById('rows').textContent), tag: /stale/i.test(document.getElementById('usage-mini').textContent) })`);
  check('connection lost: RECONNECTING, last update time shown, rows kept but marked stale, dismiss disabled', reconnecting && staleUi.stale && staleUi.banner && staleUi.rows === rowsBefore && staleUi.x && !staleUi.empty, JSON.stringify(staleUi));
  check('connection lost: usage dims but shows no "stale" label on the phone', !staleUi.tag, JSON.stringify(staleUi));
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

  // ---------- 11. Creatures view ----------
  await ask('repopulate');
  await phone(360, 800);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE' && document.querySelectorAll('#rows .row:not(.placeholder)').length > 5`, 8000);
  const listNames = await ev(`[...document.querySelectorAll('#rows .name')].map(n => n.textContent).join('|')`);
  await tap('#view-creatures');
  const switched = await waitFor(`!document.getElementById('creatures').hidden && document.getElementById('rows').hidden && document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 3000);
  const sw = await ev(`({ conn: ${connText}, names: [...document.querySelectorAll('#pets .cname')].map(n => n.textContent).join('|'), pressed: document.getElementById('view-creatures').getAttribute('aria-pressed'), saved: localStorage.getItem('claudeStatus.view') })`);
  check('toggle to Creatures: same workers, names, #n and order as the list; still LIVE; choice saved', switched && sw.conn === 'LIVE' && sw.names === listNames && sw.pressed === 'true' && sw.saved === 'creatures', JSON.stringify(sw));

  // cols: 2 (default) or 3 (Compact); ctx: Show context on
  const PETS = (cols = 2, ctx = false) => `(async () => {
    const api = await (await fetch('api/board', { cache: 'no-store' })).json();
    const de = document.documentElement, problems = [], cols = ${cols}, ctx = ${ctx};
    if (de.scrollWidth > innerWidth || document.body.scrollWidth > innerWidth) problems.push('horizontal scroll ' + de.scrollWidth + '>' + innerWidth);
    const cards = [...document.querySelectorAll('#pets .card:not(.placeholder)')];
    const names = cards.map(c => c.querySelector('.cname').textContent.replace(/\\u00a0/g, ' ')).join('|');
    if (names !== api.rows.map(r => r.n).join('|')) problems.push('names/order differ: ' + names);
    const re = { WORKING: /^WORKING \u00b7 (\\d\\d:\\d\\d|\\d+h \\d\\dm|\\d+d \\d+h)$/, NEEDS_YOU: /^NEEDS YOU \u00b7 (<1m|\\d+m|\\d+h( \\d+m)?|\\d+d)$/,
                 DONE: /^DONE \u00b7 (just now|\\d+[mhd] ago)$/, ERROR: /^ERROR \u00b7 ([A-Za-z0-9_.\\-\u2026]+ \u00b7 )?(<1m|\\d+m|\\d+h( \\d+m)?|\\d+d)$/ };
    const hats = new Set();
    cards.forEach((c, i) => {
      const d = api.rows[i], tag = '#' + i + ' ' + d.n + ': ';
      const name = c.querySelector('.cname'), st = c.querySelector('.cstatus'), pet = c.querySelector('.pet'), x = c.querySelector('.x'), svg = c.querySelector('svg.px');
      const cr = c.getBoundingClientRect();
      if (!re[d.s].test(st.textContent.replace(/\\u00a0/g, ' '))) problems.push(tag + 'status "' + st.textContent + '"');
      if (!c.classList.contains('s-' + d.s)) problems.push(tag + 'state class');
      if (name.scrollHeight > name.clientHeight + 1) problems.push(tag + 'name cut off');
      if (String(d.c) !== c.dataset.look) problems.push(tag + 'look ' + c.dataset.look + ' vs ' + d.c);
      hats.add(d.c % 13);
      if (cr.left < 0 || cr.right > innerWidth + .5) problems.push(tag + 'card off screen');
      if (name.scrollWidth > name.clientWidth + 1 || st.scrollWidth > st.clientWidth + 1) problems.push(tag + 'text overflows its card');
      if (name.getBoundingClientRect().right > cr.right + .5 || st.getBoundingClientRect().right > cr.right + .5) problems.push(tag + 'text outside card');
      if (parseFloat(getComputedStyle(name).fontSize) < 15 || parseFloat(getComputedStyle(st).fontSize) < 14) problems.push(tag + 'tiny text');
      if (pet.tagName !== 'BUTTON' || !pet.textContent.includes(d.n.replace(/ (#\\d+)$/, '\u00a0$1'))) problems.push(tag + 'creature is not a button labelled with the full name');
      if (!svg || svg.getAttribute('shape-rendering') !== 'crispEdges' || svg.getAttribute('viewBox') !== '0 0 16 16') problems.push(tag + 'sprite not crisp');
      const sr = svg.getBoundingClientRect(), dp = sr.width / 16 * devicePixelRatio;
      if (Math.abs(dp - Math.round(dp)) > .02 || sr.width < (cols === 3 ? 48 : 64)) problems.push(tag + 'sprite ' + sr.width + 'px: squares are not whole device pixels / too small');
      if (Math.abs((sr.left + sr.right) / 2 - (cr.left + cr.right) / 2) > 1.5) problems.push(tag + 'creature not centred');
      const nr = name.getBoundingClientRect(), tr = st.getBoundingClientRect(), cx = c.querySelector('.cctx'), xr2 = cx.getBoundingClientRect();
      if (nr.bottom > tr.top + .5) problems.push(tag + 'name overlaps status');
      if (ctx) {
        const want = d.x && typeof d.x.p === 'number' ? new RegExp('^Context ' + (d.x.p > 0 && d.x.p < .5 ? '<1%' : Math.round(d.x.p) + '%') + (d.x.o ? ' \u00b7 old' : '') + '$') : /^Context unavailable$/;
        if (!want.test(cx.textContent)) problems.push(tag + 'context "' + cx.textContent + '" for ' + JSON.stringify(d.x));
        const fill = cx.querySelector('i').getBoundingClientRect().width;
        if (!d.x && fill !== 0) problems.push(tag + 'unavailable context has a filled bar');
        if (xr2.height === 0 || tr.bottom > xr2.top + .5 || xr2.bottom > cr.bottom + .5 || xr2.left < cr.left - .5 || xr2.right > cr.right + .5) problems.push(tag + 'context bar not under the status inside the card');
        if (parseFloat(getComputedStyle(cx).fontSize) < 13) problems.push(tag + 'context text too small');
        if (cx.scrollWidth > cx.clientWidth + 1) problems.push(tag + 'context text overflows');
      } else if (xr2.height !== 0 || cx.offsetParent !== null) problems.push(tag + 'context shown / space kept while off');
      const asleep = d.s === 'DONE' && api.now - d.t >= 3 * 3600e3;
      if (c.classList.contains('asleep') !== asleep) problems.push(tag + 'asleep=' + c.classList.contains('asleep') + ' want ' + asleep);
      if (d.s === 'DONE') {
        if (!x) problems.push(tag + 'no dismiss x');
        else {
          const xr = x.getBoundingClientRect();
          if (xr.width < 44 || xr.height < 44) problems.push(tag + 'x target ' + xr.width + 'x' + xr.height);
          if (xr.right > cr.right + .5 || xr.top < cr.top - .5 || xr.bottom > cr.bottom + .5) problems.push(tag + 'x outside card');
          if (pet.contains(x)) problems.push(tag + 'x inside the creature button');
          if (cols === 3 && xr.top < pet.getBoundingClientRect().bottom - .5) problems.push(tag + 'Compact x overlaps the creature button');
        }
      } else if (x) problems.push(tag + 'x on a ' + d.s + ' card');
    });
    const lefts = new Set(cards.map(c => Math.round(c.getBoundingClientRect().left)));
    if (lefts.size !== Math.min(cols, cards.length)) problems.push(lefts.size + ' columns, want ' + cols);
    const widths = cards.map(c => c.getBoundingClientRect().width);
    if (Math.max(...widths) - Math.min(...widths) > 1) problems.push('unequal card widths ' + Math.min(...widths) + '-' + Math.max(...widths));
    const gc = getComputedStyle(document.getElementById('pets')).gridTemplateColumns.split(' ').length;
    if (gc !== cols) problems.push('grid has ' + gc + ' columns');
    if (hats.size !== cards.length) problems.push('two creatures share a hat');
    const conn = document.getElementById('conn').getBoundingClientRect();
    if (conn.width === 0 || conn.right > innerWidth) problems.push('connection label not visible');
    return { problems, cards: cards.length };
  })()`;
  // defaults: two columns, no context bars; the options row only in this view
  const opt = await ev(`({ compact: document.getElementById('compact-toggle').getAttribute('aria-pressed'), ctx: document.getElementById('context-toggle').getAttribute('aria-pressed'), bar: !document.getElementById('petbar').hidden, cols: getComputedStyle(document.getElementById('pets')).gridTemplateColumns.split(' ').length, saved: [localStorage.getItem('claudeStatus.compact'), localStorage.getItem('claudeStatus.context')] })`);
  check('Creatures defaults: Compact off (two columns) and Show context off, nothing saved yet; the options row shows in this view', opt.compact === 'false' && opt.ctx === 'false' && opt.bar && opt.cols === 2 && opt.saved[0] === null && opt.saved[1] === null, JSON.stringify(opt));
  for (const [w, h] of [[320, 640], [360, 800], [390, 844], [412, 915]]) {
    await phone(w, h);
    await c.send('Page.navigate', { url: base });
    const ok = await waitFor(`${connText} === 'LIVE' && !document.getElementById('creatures').hidden && document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 8000);
    check(`${w}px creatures: opens in the remembered Creatures view and goes LIVE`, ok, await ev(connText));
    for (const [cols, ctx] of [[2, false], [2, true], [3, true], [3, false]]) {
      await ev(`localStorage.setItem('claudeStatus.compact', '${cols === 3 ? 1 : 0}'); localStorage.setItem('claudeStatus.context', '${ctx ? 'shown' : 'hidden'}'); true`);
      await c.send('Page.navigate', { url: base });
      await waitFor(`${connText} === 'LIVE' && document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 8000);
      const l = await ev(PETS(cols, ctx));
      check(`${w}px creatures, ${cols === 3 ? 'Compact (3 columns)' : '2 columns'}${ctx ? ' + context' : ''}: ${cols} equal columns; same data as the laptop; names wrap, nothing cut off or overlapping; no horizontal scroll; crisp centred sprites; sleep; distinct hats; 44px x on DONE only`, l.problems.length === 0, l.problems.slice(0, 6).join('; '));
      if (ctx || w === 360) await shot(`creatures-${w}-${cols}col${ctx ? '-context' : ''}`);
    }
  }
  await ev(`localStorage.removeItem('claudeStatus.compact'); localStorage.removeItem('claudeStatus.context'); true`);

  // identity and in-place updates
  await phone(360, 800);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE' && document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 8000);
  const petSel = (name) => `[...document.querySelectorAll('#pets .card')].find(c => c.querySelector('.cname')?.textContent.replace(/\\u00a0/g, ' ') === ${JSON.stringify(name)})`;
  const looks = `JSON.stringify([...document.querySelectorAll('#pets .card:not(.placeholder)')].map(c => c.querySelector('.cname').textContent + '=' + c.dataset.look).sort())`;
  const looks1 = await ev(looks);
  await ev(`document.querySelectorAll('#pets .card').forEach(c => c.__same = true); new MutationObserver(ms => ms.forEach(m => { if (m.target.classList?.contains('celebrate')) m.target.__celebrated = true; })).observe(document.getElementById('pets'), { subtree: true, attributes: true, attributeFilter: ['class'] }); true`);
  await ask('pets-change');
  const changed = await waitFor(`(${petSel('Side Project 1')})?.classList.contains('s-DONE') && (${petSel('Booking Software #1')})?.classList.contains('s-NEEDS_YOU')`, 4000);
  const after = await ev(`({ same: [...document.querySelectorAll('#pets .card:not(.placeholder)')].every(c => c.__same), celebrate: (${petSel('Side Project 1')}).classList.contains('celebrate') || (${petSel('Side Project 1')}).__celebrated === true, conn: ${connText} })`);
  const looks2 = await ev(looks);
  check('live update in Creatures view: statuses change in place (same card elements, no rebuild)', changed && after.same && after.conn === 'LIVE', JSON.stringify(after));
  check('a worker finishing while watched gets a brief celebration', after.celebrate, JSON.stringify(after));
  check('each worker keeps its creature across updates', JSON.stringify(looks1) === JSON.stringify(looks2), JSON.stringify(looks1) + ' / ' + JSON.stringify(looks2));
  await tap('#view-list');
  const backList = await waitFor(`!document.getElementById('rows').hidden && document.querySelectorAll('#rows .row:not(.placeholder)').length > 5 && ${connText} === 'LIVE'`, 3000);
  await tap('#view-creatures');
  await waitFor(`document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 3000);
  const looks3 = await ev(looks);
  check('switching to List and back: same live board, same creatures', backList && JSON.stringify(looks3) === JSON.stringify(looks2), JSON.stringify(looks3));

  // ---------- Compact and Show context: options of this phone only, never touching the connection ----------
  // a "plain" board fetch (not the long-poll's own wait=1 renewal) would mean a reconnect / refetch
  await ev(`window.__plain = 0; (() => { const o = window.fetch; window.fetch = function (u) { if (String(u).includes('api/board') && !String(u).includes('wait=1')) window.__plain++; return o.apply(this, arguments); }; })(); document.querySelectorAll('#pets .card').forEach(c => c.__keep = true); true`);
  const keepAll = `[...document.querySelectorAll('#pets .card:not(.placeholder)')].every(c => c.__keep)`;
  const colsNow = `getComputedStyle(document.getElementById('pets')).gridTemplateColumns.split(' ').length`;
  const ctxVisible = `[...document.querySelectorAll('#pets .card:not(.placeholder) .cctx')].every(e => e.offsetHeight > 0)`;
  const looksA = await ev(looks);
  await tapSetting('#compact-toggle');
  const cmp = await ev(`({ cols: ${colsNow}, pressed: document.getElementById('compact-toggle').getAttribute('aria-pressed'), saved: localStorage.getItem('claudeStatus.compact'), keep: ${keepAll}, conn: ${connText} })`);
  await tapSetting('#context-toggle');
  const ctxOn = await ev(`({ shown: ${ctxVisible}, saved: localStorage.getItem('claudeStatus.context'), compact: localStorage.getItem('claudeStatus.compact'), view: localStorage.getItem('claudeStatus.view'), usage: localStorage.getItem('claudeStatus.usage'), keep: ${keepAll} })`);
  await sleep(700);
  const plain = await ev('window.__plain');
  check('Compact on: three columns at once, saved on this phone; the same card elements and creatures, still LIVE', cmp.cols === 3 && cmp.pressed === 'true' && cmp.saved === '1' && cmp.keep && cmp.conn === 'LIVE' && JSON.stringify(await ev(looks)) === JSON.stringify(looksA), JSON.stringify(cmp));
  check('Show context on: a context line under every card, saved separately from Compact, the view and Show usage', ctxOn.shown && ctxOn.saved === 'shown' && ctxOn.compact === '1' && ctxOn.view === 'creatures' && ctxOn.usage !== 'hidden' && ctxOn.keep, JSON.stringify(ctxOn));
  check('switching Compact / Show context makes no request (no reconnect, no refetch)', plain === 0, plain);
  await shot('creatures-360-3col-context-toggled');

  await tap('#view-list');
  const lv = await ev(`({ barHidden: document.getElementById('petbar').hidden, rows: document.querySelectorAll('#rows .row:not(.placeholder)').length, extra: document.querySelectorAll('#rows .cctx, #rows .sprite').length })`);
  const ll = await ev(LAYOUT);
  check('List view with Compact + Show context on: options row hidden, list unchanged (same layout checks pass)', lv.barHidden && lv.rows > 5 && lv.extra === 0 && ll.problems.length === 0, JSON.stringify(lv) + ' ' + ll.problems.slice(0, 3).join('; '));
  await tap('#view-creatures');
  await waitFor(`document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 3000);
  const back2 = await ev(`({ cols: ${colsNow}, ctx: ${ctxVisible} })`);
  check('back to Creatures: Compact and Show context are kept', back2.cols === 3 && back2.ctx, JSON.stringify(back2));

  // context values: each worker its own session's reading
  const ctxOf = (n) => `(${petSel(n)})?.querySelector('.cctx')?.textContent`;
  const cv = await ev(`({ b1: ${ctxOf('Booking Software #1')}, b2: ${ctxOf('Booking Software #2')}, portal: ${ctxOf('Portal')}, long: ${ctxOf('A Very Long Project Folder Name That Keeps Going And Going')}, sp1: ${ctxOf('Side Project 1')}, an: ${ctxOf('Analytics')}, anFill: (${petSel('Analytics')}).querySelector('.cctx i').getBoundingClientRect().width, anNa: (${petSel('Analytics')}).querySelector('.cctx').classList.contains('na') })`);
  check('context: two workers of one project show their own readings (31% vs 76%), a real 0% shows 0%, 97% full, an old reading marked old, none = "Context unavailable" with an empty track',
    /^Context 31%( · old)?$/.test(cv.b1) && /^Context 76%( · old)?$/.test(cv.b2) && /^Context 0%/.test(cv.portal) && /^Context 97%/.test(cv.long) && cv.sp1 === 'Context 12% · old' && cv.an === 'Context unavailable' && cv.anFill === 0 && cv.anNa, JSON.stringify(cv));
  const statusOfPet = (n) => `(${petSel(n)})?.querySelector('.cstatus')?.textContent.replace(/\\u00a0/g, ' ')`;
  // the List layout check above fetched the board itself, and List -> Creatures rebuilt the cards
  await ev(`window.__plain = 0; document.querySelectorAll('#pets .card').forEach(c => c.__keep = true); true`);
  const timerBefore = await ev(statusOfPet('Booking Software #2'));
  await ask('ctx-update');
  const ctxUpd = await waitFor(`/^Context 18%/.test(${ctxOf('Booking Software #2')}) && /^Context 64%/.test(${ctxOf('Almost Asleep')})`, 4000);
  const timerAfter = await ev(statusOfPet('Booking Software #2'));
  const secs = (t) => { const m = /(\d\d):(\d\d)$/.exec(t || ''); return m ? +m[1] * 60 + +m[2] : -1; };
  const upd = { ctxUpd, keep: await ev(keepAll), plain: await ev('window.__plain'), b2: await ev(ctxOf('Booking Software #2')), aa: await ev(ctxOf('Almost Asleep')) };
  check('context update arrives live, in place: same cards, the WORKING timer keeps counting (not reset), no refetch', upd.ctxUpd && upd.keep && secs(timerAfter) >= secs(timerBefore) && secs(timerBefore) > 0 && upd.plain === 0, `${timerBefore} -> ${timerAfter} ${JSON.stringify(upd)}`);

  // ---------- sleeping: a look layered on DONE, from 3 hours after it finished ----------
  const asleepOf = (n) => `(${petSel(n)})?.classList.contains('asleep')`;
  const disp = (n, layer) => `getComputedStyle((${petSel(n)}).querySelector('.px .${layer}')).display`;
  const sl = await ev(`({ sleepy: ${asleepOf('Sleepy Project')}, text: ${statusOfPet('Sleepy Project')}, x: !!(${petSel('Sleepy Project')}).querySelector('.x'), done: (${petSel('Sleepy Project')}).classList.contains('s-DONE'),
    z1: ${disp('Sleepy Project', 'fx-z1')}, z2: ${disp('Sleepy Project', 'fx-z2')}, shut: ${disp('Sleepy Project', 'e-blink')}, open: ${disp('Sleepy Project', 'e-open')}, happy: ${disp('Sleepy Project', 'e-happy')},
    almost: ${asleepOf('Almost Asleep')}, long: ${asleepOf('Long Runner')}, portal: ${asleepOf('Portal')}, an: ${asleepOf('Analytics')}, website: ${asleepOf('Website')} })`);
  check('DONE 3 h 10 m ago: asleep (eyes shut, z / Z), still "DONE · 3h ago", DONE colours and its x', sl.sleepy && sl.text === 'DONE · 3h ago' && sl.x && sl.done && sl.z1 === 'inline' && sl.z2 === 'inline' && sl.shut === 'inline' && sl.open === 'none' && sl.happy === 'none', JSON.stringify(sl));
  check('never asleep: WORKING 5 h, NEEDS YOU 4 h, ERROR 4 h; DONE 1 h ago is still awake', !sl.long && !sl.portal && !sl.an && !sl.almost && !sl.website, JSON.stringify(sl));
  await shot('creatures-360-3col-asleep');
  await ask('sleep-edge');   // Almost Asleep: DONE 3 h minus 4 s
  const edgeIn = await waitFor(`/^DONE · 2h ago$/.test(${statusOfPet('Almost Asleep')})`, 4000);
  const awakeAtEdge = await ev(`!${asleepOf('Almost Asleep')}`);
  const plainBefore = await ev('window.__plain');
  const fell = await waitFor(asleepOf('Almost Asleep'), 9000);
  const fellText = await ev(statusOfPet('Almost Asleep'));
  check('4 s before 3 h: awake; at the 3 h mark it falls asleep by itself (no new data, no reload) and still says DONE · 3h ago', edgeIn && awakeAtEdge && fell && fellText === 'DONE · 3h ago' && await ev('window.__plain') === plainBefore, `${edgeIn} ${awakeAtEdge} ${fell} ${fellText}`);
  await ev(`Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); true`);
  await sleep(1200);
  await ev(`Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); true`);
  const bgOk = await waitFor(`${connText} === 'LIVE'`, 3000) && await ev(`${asleepOf('Sleepy Project')} && ${asleepOf('Almost Asleep')} && !${asleepOf('Long Runner')}`);
  check('background and resume: sleepers stay asleep, others awake', bgOk);
  await c.send('Page.navigate', { url: base });
  const reopened = await waitFor(`${connText} === 'LIVE' && !!(${petSel('Sleepy Project')})`, 8000) && await ev(`${asleepOf('Sleepy Project')} && ${asleepOf('Almost Asleep')} && !${asleepOf('Website')}`);
  check('reopening the page: sleepers are asleep as soon as the board arrives (the clock is the laptop\'s DONE time, not page load)', reopened);
  check('reopening the page: Compact and Show context are remembered', await ev(`${colsNow} === 3 && ${ctxVisible}`));
  await tap('#view-list');
  await tap('#view-creatures');
  check('view switch: sleepers still asleep', await waitFor(`${asleepOf('Sleepy Project')} && ${asleepOf('Almost Asleep')}`, 3000));
  await c.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
  const rm = await ev(`({ z2: getComputedStyle((${petSel('Sleepy Project')}).querySelector('.px .fx-z2')).animationName, z2d: ${disp('Sleepy Project', 'fx-z2')}, z1d: ${disp('Sleepy Project', 'fx-z1')}, shut: ${disp('Sleepy Project', 'e-blink')} })`);
  check('reduced motion: sleeping art is still (no animation) and still recognisable (eyes shut, z and Z shown)', rm.z2 === 'none' && rm.z2d === 'inline' && rm.z1d === 'inline' && rm.shut === 'inline', JSON.stringify(rm));
  await c.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'no-preference' }] });
  await ask('wake');   // a new prompt in Sleepy Project
  const woke = await waitFor(`!${asleepOf('Sleepy Project')} && (${petSel('Sleepy Project')}).classList.contains('s-WORKING') && /^WORKING · 00:0/.test(${statusOfPet('Sleepy Project')})`, 4000);
  check('a new prompt wakes it at once into WORKING', woke, await ev(statusOfPet('Sleepy Project')));
  // Compact / Show context survive a reload; turning them off removes the third column and the bars
  await tapSetting('#compact-toggle'); await tapSetting('#context-toggle');
  const off = await ev(`({ cols: ${colsNow}, h: Math.max(...[...document.querySelectorAll('#pets .cctx')].map(e => e.offsetHeight)), saved: [localStorage.getItem('claudeStatus.compact'), localStorage.getItem('claudeStatus.context')] })`);
  check('Compact and Show context off again: two columns, no context space kept, saved', off.cols === 2 && off.h === 0 && off.saved[0] === '0' && off.saved[1] === 'hidden', JSON.stringify(off));

  // tap: immediate reaction, then the laptop's own focus action for that worker
  await ask('focus-clear-1');
  await ev(`window.__reacted = false; new MutationObserver(() => { if (document.querySelector('#pets .sprite.boing')) window.__reacted = performance.now(); }).observe(document.getElementById('pets'), { subtree: true, attributes: true, attributeFilter: ['class'] }); true`);
  await ev(`(() => { const o = window.fetch; window.__focusSentAt = 0; window.fetch = function (u, opt) { if (String(u).includes('api/focus')) window.__focusSentAt = performance.now(); return o.apply(this, arguments); }; return true; })()`);
  // the tap helper needs a unique selector: mark the card and tap it
  await ev(`(${petSel('Booking Software #2')}).setAttribute('data-t', 'b2'); true`);
  await ask('focus-clear-2');
  await ev(`window.__reacted = false; true`);
  const tapAt = await ev('performance.now()');
  await tap('#pets .card[data-t="b2"] .pet');
  const reacted = await waitFor(`window.__reacted !== false`, 1000);
  const timing = await ev(`({ reacted: window.__reacted, sent: window.__focusSentAt })`);
  const said = await waitFor(`/Brought up \u201cBooking Software #2\u201d on the laptop/.test(document.getElementById('note').textContent) && !document.getElementById('note').hidden`, 4000);
  await ask('focus-log-1');
  const log1 = fs.readFileSync(path.join(dir, 'focus-log.txt'), 'utf8');
  check('tapping a creature: it reacts at once (before the laptop answers) and the request goes out without waiting for the animation', reacted && timing.reacted - tapAt < 300 && timing.sent > 0 && timing.sent - tapAt < 300, JSON.stringify({ tapAt, ...timing }));
  check('...the laptop runs its own focus action for exactly that worker (Booking Software #2, not #1), and the phone reports what the laptop confirmed', said && log1 === 'pb2', `log=${log1} note=${await ev(`document.getElementById('note').textContent`)}`);

  await ev(`(${petSel('Portal')}).setAttribute('data-t', 'portal'); true`);
  for (let i = 0; i < 4; i++) { await tap('#pets .card[data-t="portal"] .pet'); await sleep(40); }
  await sleep(1500);
  await ask('focus-log-2');
  const log2 = fs.readFileSync(path.join(dir, 'focus-log.txt'), 'utf8');
  check('rapid repeated taps on a creature: one activation on the laptop', log2 === 'pb2,pny', log2);

  // dismissal and activation stay separate
  await ev(`(${petSel('Website')}).setAttribute('data-t', 'web'); (${petSel('Guest Form')}).setAttribute('data-t', 'guest'); true`);
  await sleep(1600);
  await tap('#pets .card[data-t="web"] .pet');
  await sleep(900);
  await ask('focus-log-3');
  const log3 = fs.readFileSync(path.join(dir, 'focus-log.txt'), 'utf8');
  const stillThere = await ev(`!!(${petSel('Website')})`);
  check('tapping a DONE creature brings it up and never dismisses it', log3 === 'pb2,pny,pd1' && stillThere, `${log3} ${stillThere}`);
  await tap('#pets .card[data-t="web"] .x');
  const xGone = await waitFor(`!(${petSel('Website')})`, 3000);
  await ask('check-pet-dismiss');
  await ask('focus-log-4');
  const log4 = fs.readFileSync(path.join(dir, 'focus-log.txt'), 'utf8');
  check('the x on a DONE creature dismisses only that worker and never brings anything up', xGone && log4 === log3 && await ev(`!!(${petSel('Guest Form')})`), `${xGone} ${log4}`);

  // the laptop's refusal is shown as such
  await ev(`(${petSel('Analytics')}).setAttribute('data-t', 'an'); true`);
  await tap('#pets .card[data-t="an"] .pet');
  const lockedMsg = await waitFor(`/laptop is locked/.test(document.getElementById('note').textContent)`, 4000);
  check('a locked laptop is reported as locked (not as success)', lockedMsg, await ev(`document.getElementById('note').textContent`));

  // keyboard
  await ev(`(${petSel('Side Project 2')}).setAttribute('data-t', 'sp2'); document.querySelector('#pets .card[data-t="sp2"] .pet').focus(); true`);
  await sleep(1600);
  await c.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
  await c.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
  await sleep(900);
  await ask('focus-log-5');
  const log5 = fs.readFileSync(path.join(dir, 'focus-log.txt'), 'utf8');
  const focusRing = await ev(`(() => { const b = document.querySelector('#pets .card[data-t="sp2"] .pet'); return document.activeElement === b && [...document.styleSheets[0].cssRules].some(r => r.selectorText === '.pet:focus-visible'); })()`);
  check('keyboard: a creature is a focusable button with a focus outline; Enter brings it up', log5.endsWith(',px2') && focusRing, `${log5} ${focusRing}`);

  // motion: working animations run, pause in the background, and are off with reduced motion
  const anim = (sel) => `(() => { const g = document.querySelector(${JSON.stringify(sel)}); if (!g) return null; const s = getComputedStyle(g); return s.animationName + '/' + s.animationPlayState; })()`;
  const workingFig = '#pets .card.s-WORKING .fig';
  const running = await ev(anim(workingFig));
  await ev(`Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); true`);
  const paused = await ev(anim(workingFig));
  await ev(`Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); true`);
  const resumedLive = await waitFor(`${connText} === 'LIVE'`, 3000);
  const resumedAnim = await ev(anim(workingFig));
  check('working creatures bounce; animations pause while the page is in the background and resume after', running === 'bob/running' && paused === 'bob/paused' && resumedAnim === 'bob/running' && resumedLive, `${running} ${paused} ${resumedAnim}`);
  await c.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
  const reducedAnim = await ev(anim(workingFig));
  await ev(`(${petSel('Side Project 3')}).setAttribute('data-t', 'sp3'); true`);
  await sleep(1600);
  await tap('#pets .card[data-t="sp3"] .pet');
  const flashed = await ev(`({ flash: document.querySelector('#pets .card[data-t="sp3"] .pet').classList.contains('flash'), boing: document.querySelector('#pets .card[data-t="sp3"] .sprite').classList.contains('boing') })`);
  const stillWorks = await waitFor(`/Brought up \u201cSide Project 3\u201d/.test(document.getElementById('note').textContent)`, 4000);
  check('reduced motion: no animations; a tap still gives instant (non-moving) feedback and still works', reducedAnim === 'none/running' && flashed.flash && !flashed.boing && stillWorks, `${reducedAnim} ${JSON.stringify(flashed)} ${stillWorks}`);
  await c.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'no-preference' }] });

  // connection trouble in the Creatures view
  await ask('focus-log-6');
  const log6 = fs.readFileSync(path.join(dir, 'focus-log.txt'), 'utf8');
  mode = 'fail';
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/*' }] });
  const cardsBefore = await ev(`document.querySelectorAll('#pets .card:not(.placeholder)').length`);
  await ev(`window.dispatchEvent(new Event('online')); true`);
  const petsRecon = await waitFor(`${connText} === 'RECONNECTING…'`, 3000);
  const petsStale = await ev(`({ stale: document.getElementById('board').classList.contains('stale'), banner: !document.getElementById('banner').hidden && /Last update \\d/.test(document.getElementById('banner-when').textContent), cards: document.querySelectorAll('#pets .card:not(.placeholder)').length, x: [...document.querySelectorAll('#pets .x')].every(b => b.disabled), paused: ${anim(workingFig)}, asleep: document.querySelectorAll('#pets .card.asleep').length })`);
  check('Creatures view, connection lost: RECONNECTING with last update, creatures kept (sleepers still asleep) but stale and still, x disabled', petsRecon && petsStale.stale && petsStale.banner && petsStale.cards === cardsBefore && petsStale.x && petsStale.paused === 'bob/paused' && petsStale.asleep >= 1, JSON.stringify(petsStale));
  await ev(`(${petSel('Side Project 2')}).setAttribute('data-t', 'sp2'); true`);
  await tap('#pets .card[data-t="sp2"] .pet');
  const offlineTap = await waitFor(`/isn't connected/.test(document.getElementById('note').textContent)`, 2000);
  const petsOffline = await waitFor(`${connText} === 'OFFLINE' && /Laptop unreachable/.test(document.getElementById('banner').textContent)`, 8000);
  await ask('focus-log-7');
  const log7 = fs.readFileSync(path.join(dir, 'focus-log.txt'), 'utf8');
  check('...tapping a creature while disconnected says so and sends nothing; then OFFLINE "Laptop unreachable"', offlineTap && petsOffline && log7 === log6, `${offlineTap} ${petsOffline} ${log6} -> ${log7}`);
  await shot('creatures-offline');
  await c.send('Page.navigate', { url: base });
  const petsNever = await waitFor(`${connText} === 'OFFLINE' && /Can't reach Claude Status/.test(document.getElementById('pets').textContent)`, 10000);
  check('Creatures view, never connected: says it can\'t reach the laptop, never "No Claude workers"', petsNever && !(await ev(`/No Claude workers/.test(document.getElementById('pets').textContent)`)));
  mode = 'hold';
  await c.send('Page.navigate', { url: base });
  const petsLoading = await waitFor(`${connText} === 'CONNECTING…' && /Connecting to the laptop/.test(document.getElementById('pets').textContent)`, 3000);
  check('Creatures view, loading: "Connecting to the laptop…"', petsLoading, await ev(`document.getElementById('pets').textContent`));
  mode = 'pass';
  for (const id of held.splice(0)) await c.send('Fetch.continueRequest', { requestId: id }).catch(() => { });
  await c.send('Fetch.disable');
  const petsBack = await waitFor(`${connText} === 'LIVE' && document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 13000);
  check('Creatures view reconnects by itself', petsBack, await ev(connText));
  await ask('clear-2');
  await c.send('Page.navigate', { url: base });
  const petsEmpty = await waitFor(`${connText} === 'LIVE' && /No Claude workers/.test(document.getElementById('pets').textContent) && !!document.querySelector('#pets .placeholder.sleep svg')`, 8000);
  check('Creatures view, empty board: one sleeping creature and "No Claude workers"', petsEmpty);
  await shot('creatures-empty');

  // ---------- Scheduled prompts (read-only view of the laptop's Queue) ----------
  await phone(320, 700);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);
  await tap('#view-sched');
  const schedEmpty = await waitFor(`/Nothing scheduled/.test(document.getElementById('sched-list').textContent)`, 5000);
  check('Scheduled view, empty schedule: says nothing is scheduled', schedEmpty, await ev(`document.getElementById('sched-list').textContent`));
  await ev(`window.__acts = 0; (() => { const o = window.fetch; window.fetch = function (u, opt) { if (opt && opt.method && opt.method !== 'GET') window.__acts++; return o.apply(this, arguments); }; })(); true`);
  await ask('sched-add');
  const titles = `[...document.querySelectorAll('#sched-list .qtitle')].map(e => e.textContent)`;
  const schedShown = await waitFor(`${titles}.length === 4`, 25000);
  const order = await ev(`${titles}.join('|')`);
  check('Scheduled view: tasks scheduled on the desktop appear by themselves, upcoming in run order', schedShown && order === 'Sooner refactor|Later review|Follow-up docs|Idea draft', order);
  const first = await ev(`(() => { const b = document.querySelector('#sched-list .qitem'); return { meta: b.querySelector('.qmeta').textContent, word: b.querySelector('.qword').textContent, when: b.querySelector('.qwhen').textContent, prev: b.querySelector('.qprev').textContent }; })()`);
  check('Scheduled view: card shows project, agent, prompt preview, status and local run time', first.meta === 'Website Redesign · Claude Code' && first.word === 'Scheduled' && /runs (today|tomorrow) .*\(in 1h 5\dm\)/.test(first.when) && /^Refactor the booking flow/.test(first.prev), JSON.stringify(first));
  const layout = await ev(`({ sw: document.documentElement.scrollWidth, w: innerWidth, minTap: Math.min(...[...document.querySelectorAll('#sched-list .qitem, #view-sched, #sched-refresh')].map(e => e.getBoundingClientRect().height)), segs: document.querySelector('.viewbar').scrollWidth <= document.querySelector('.viewbar').clientWidth, tz: document.getElementById('sched-info').textContent })`);
  check('Scheduled view at 320 px: no horizontal scroll, tap targets >= 36 px, view switch fits, time zone named', layout.sw <= layout.w && layout.minTap >= 36 && layout.segs && /^Times in /.test(layout.tz), JSON.stringify(layout));
  await shot('sched-320');
  await tap('#sched-list .qitem');
  const detail = await waitFor(`!document.getElementById('sched-detail').hidden && /summary_with_a_very_long/.test(document.querySelector('.qd-prompt')?.textContent || '')`, 3000);
  const wrap = await ev(`(() => { const p = document.querySelector('.qd-prompt'); return { fits: p.scrollWidth <= p.clientWidth + 1, sw: document.documentElement.scrollWidth, w: innerWidth, facts: document.querySelector('.qd-facts').textContent }; })()`);
  check('Scheduled details: full prompt wraps (no horizontal scroll) with schedule facts', detail && wrap.fits && wrap.sw <= wrap.w && /Scheduled for/.test(wrap.facts) && /Next run/.test(wrap.facts) && /If missed/.test(wrap.facts), JSON.stringify(wrap));
  await shot('sched-detail-320');
  await ask('sched-cancel');
  const canceled = await waitFor(`document.querySelector('.qd-status')?.textContent === 'Canceled'`, 25000);
  check('Scheduled details: a status change on the desktop shows up while open', canceled, await ev(`document.querySelector('.qd-status')?.textContent`));
  await tap('#sched-back');
  await waitFor(`/PAST · 1/.test(document.getElementById('sched-list').textContent)`, 3000);
  const past = await ev(`({ up: ${titles}.join('|'), past: document.querySelector('.sched-past')?.textContent })`);
  check('Scheduled view: finished tasks move to a collapsed Past section', !/Sooner refactor/.test(past.up) && /PAST · 1/.test(past.past || ''), JSON.stringify(past));
  await sleep(2000);   // let the desktop's follow-on changes (e.g. the dependent task) settle before tapping
  await tap('.sched-past');
  const pastOpen = await waitFor(`${titles}.includes('Sooner refactor')`, 2000);
  check('Scheduled view: Past expands on tap', pastOpen, await ev(`document.getElementById('sched').textContent.slice(0, 300)`));
  await tap('#sched-refresh');
  const schedRefreshed = await waitFor(`/up to date/.test(document.getElementById('note').textContent)`, 5000);
  check('Scheduled view: Refresh re-reads the schedule', schedRefreshed, await ev(`document.getElementById('note').textContent`));
  await ev(`[...document.querySelectorAll('#sched-list .qitem')].find(b => b.querySelector('.qtitle').textContent === 'Later review').setAttribute('data-t', 'later'); true`);
  await tap('#sched-list .qitem[data-t="later"]');
  await waitFor(`!document.getElementById('sched-detail').hidden`, 2000);
  await ask('sched-remove');
  const schedGone = await waitFor(`/no longer on the laptop's schedule/.test(document.getElementById('sched-body').textContent)`, 25000);
  check('Scheduled details: a task removed on the desktop says it is no longer available', schedGone, await ev(`document.getElementById('sched-body').textContent`));
  await tap('#sched-back');
  check('Scheduled view never sent an action request (view / refresh / details are read-only)', (await ev('window.__acts')) === 0, await ev('window.__acts'));
  mode = 'fail';
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/*' }] });
  await ev(`window.dispatchEvent(new Event('online')); true`);
  const schedOff = await waitFor(`${connText} === 'OFFLINE' && document.getElementById('board').classList.contains('stale') && /Laptop unreachable/.test(document.getElementById('banner').textContent) && document.querySelectorAll('#sched-list .qitem').length > 0`, 12000);
  check('Scheduled view, laptop disconnected: OFFLINE banner, list kept but marked stale', schedOff);
  await shot('sched-offline');
  mode = 'pass';
  await c.send('Fetch.disable');
  await waitFor(`${connText} === 'LIVE'`, 13000);
  await tap('#view-creatures');
  const backToPets = await waitFor(`!document.getElementById('creatures').hidden && document.getElementById('sched').hidden && document.getElementById('view-creatures').getAttribute('aria-pressed') === 'true'`, 3000);
  check('switching from Scheduled back to Creatures works', backToPets);
  await ask('sched-clear');

  // ---------- Schedule a task from the phone (the laptop's own scheduler; temporary projects, mock Claude) ----------
  let qv = 0;
  const verify = async () => { await ask(`qs-verify-${++qv}`); return JSON.parse(fs.readFileSync(path.join(dir, 'qs.json'), 'utf8').replace(/^﻿/, '')); };
  const formText = `document.getElementById('sched-form').textContent.replace(/\\s+/g, ' ')`;
  const errText = `document.getElementById('sf-error').hidden ? '' : document.getElementById('sf-error').textContent`;
  const pick = (name) => ev(`(() => { const s = document.getElementById('sf-project'); const o = [...s.options].find(o => o.textContent === ${JSON.stringify(name)}); if (!o) return false; s.value = o.value; s.dispatchEvent(new Event('change')); return true; })()`);
  const typeIn = async (sel, text) => { await ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); e.focus(); e.select && e.select(); return true; })()`); await c.send('Input.insertText', { text }); };
  const setAt = (expr) => ev(`(() => { const a = document.getElementById('sf-at'); const d = new Date(${expr}); const p = n => String(n).padStart(2, '0'); a.value = d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + 'T' + p(d.getHours()) + ':' + p(d.getMinutes()); a.dispatchEvent(new Event('change')); return a.value; })()`);
  await ask('qs-setup');
  await phone(360, 740);
  // the phone is in another time zone than the laptop: times must still mean the same instant
  await c.send('Emulation.setTimezoneOverride', { timezoneId: 'Asia/Tokyo' });
  await ev(`['new', 'edit'].forEach(m => localStorage.removeItem('claudeStatus.schedDraft.' + m)); localStorage.removeItem('claudeStatus.schedProject'); localStorage.setItem('claudeStatus.view', 'sched'); true`);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);
  const nb = await ev(`(() => { const b = document.getElementById('sched-new'); const r = b.getBoundingClientRect(); return { shown: !b.hidden, text: b.textContent, h: r.height, w: r.width }; })()`);
  check('Scheduled view: an obvious full-width "Schedule task" button', nb.shown && nb.text === '＋ Schedule task' && nb.h >= 44 && nb.w >= 300, JSON.stringify(nb));
  await shot('sched-empty-new-360');
  await tap('#sched-new');
  const formUp = await waitFor(`!document.getElementById('sched-form').hidden && document.getElementById('sched-list').hidden && document.querySelectorAll('#sf-project option').length > 2`, 8000);
  const fo = await ev(`({ opts: [...document.querySelectorAll('#sf-project option')].map(o => o.textContent), save: document.getElementById('sf-save').disabled, codex: [...document.querySelectorAll('.sf-ai .seg')].map(b => b.textContent + ':' + b.getAttribute('aria-pressed')).join(','), adv: document.getElementById('sf-adv').open })`);
  check('form: projects by friendly name (no paths to type), Claude chosen, Codex shown as unavailable, Advanced folded', formUp && fo.opts.includes('Portal') && fo.opts.includes('Booking App') && !fo.opts.some(o => /[\\/]/.test(o)) && fo.save && fo.codex === 'Claude Code:true,Codex:false' && !fo.adv && /Codex isn't available/.test(await ev(formText)), JSON.stringify(fo));
  await pick('Portal');
  const prompt1 = 'Phone task: run the tests and summarise.\nSecond line — “quotes” & $HOME; rm -rf / # just text\n  indented 日本';
  await typeIn('#sf-prompt', prompt1);
  await setAt(`Date.now() - 3600e3`);
  const qPast = await ev(`({ err: ${errText}, save: document.getElementById('sf-save').disabled })`);
  check('invalid input: a time in the past is refused on the phone, Save disabled', /already passed/.test(qPast.err) && qPast.save, JSON.stringify(qPast));
  await tap('.chip[data-min="60"]');
  const rv1 = await ev(`({ review: document.getElementById('sf-review').textContent, err: ${errText}, save: document.getElementById('sf-save').disabled, at: document.getElementById('sf-at').value })`);
  check('"In 1 hour": exact date and time with the phone\'s time zone, the laptop\'s own clock (other zone), and what happens if it\'s asleep', /^Runs .*\d{4}.*GMT\+9.* \(in (59m|1h 00m)\)/.test(rv1.review) && /Asia\/Tokyo \(this phone\)/.test(rv1.review) && /On the laptop .* that is /.test(rv1.review) && /asleep or off, the task runs once it's back/.test(rv1.review) && !rv1.save && !rv1.err, JSON.stringify(rv1));
  const lay = await ev(`(() => { const f = document.getElementById('sched-form'); const small = [...f.querySelectorAll('input, select, textarea')].filter(e => e.offsetParent && parseFloat(getComputedStyle(e).fontSize) < 16).map(e => e.id); const taps = [...f.querySelectorAll('button, select, input, summary')].filter(e => e.offsetParent).map(e => [e.id || e.textContent.trim(), Math.round(e.getBoundingClientRect().height)]).filter(x => x[1] < 40); return { sw: document.documentElement.scrollWidth, w: innerWidth, small, taps }; })()`);
  check('form at 360 px: no horizontal scroll, fields 16 px (no zoom on focus), tap targets >= 40 px', lay.sw <= lay.w && !lay.small.length && !lay.taps.length, JSON.stringify(lay));
  await shot('sched-form-360');
  // keyboard open: the visible height shrinks to ~half; the form keeps working and nothing goes sideways
  await phone(360, 380);
  await ev(`document.getElementById('sf-prompt').focus(); true`);
  const kb = await ev(`(() => { const s = document.getElementById('sf-save'); s.scrollIntoView({ block: 'nearest' }); const r = s.getBoundingClientRect(); return { sw: document.documentElement.scrollWidth, w: innerWidth, saveVisible: r.top >= 0 && r.bottom <= innerHeight, pr: document.getElementById('sf-prompt').getBoundingClientRect().width }; })()`);
  check('keyboard open (half-height screen): no horizontal scroll, Save reachable by scrolling, prompt full width', kb.sw <= kb.w && kb.saveVisible && kb.pr >= 300, JSON.stringify(kb));
  await phone(360, 740);
  await c.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Tab' }); await c.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Tab' });
  const draft1 = await ev(`JSON.parse(localStorage.getItem('claudeStatus.schedDraft.new') || 'null')`);
  check('the draft is kept on the phone while you type', draft1 && draft1.p === prompt1 && draft1.open === true && /^[A-Za-z0-9_-]{22}$/.test(draft1.id), JSON.stringify(draft1));
  // 1) the connection drops before the request reaches the laptop
  await ev(`window.__qposts = 0; (() => { const o = window.fetch; window.fetch = function (u, opt) { if (/api\\/queue/.test(u) && opt && opt.method === 'POST') window.__qposts++; return o.apply(this, arguments); }; })(); true`);
  mode = 'fail';
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/queue*', requestStage: 'Request' }] });
  await tap('#sf-save');
  const nc1 = await waitFor(`/Not confirmed/.test(${errText})`, 8000);
  const v1 = await verify();
  check('save fails (no connection): "Not confirmed", draft kept, nothing on the laptop', nc1 && v1.tasks.length === 0 && (await ev(`document.getElementById('sf-prompt').value`)) === prompt1, JSON.stringify(v1));
  // 2) the laptop saves it but the answer is lost on the way back
  mode = 'fail';
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/queue*', requestStage: 'Response' }] });
  await tap('#sf-save');
  const nc2 = await waitFor(`/Not confirmed/.test(${errText}) && !document.getElementById('sf-save').disabled`, 8000);
  const v2 = await verify();
  check('answer lost after the laptop saved it: the phone still says "Not confirmed" (never claims success)', nc2 && v2.tasks.length === 1 && !(await ev(`!!document.querySelector('.sched-ok')`)), JSON.stringify(v2));
  mode = 'pass';
  await c.send('Fetch.disable');
  // 3) reload the page: the draft comes back; sending it again doesn't make a second task
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);
  const restored = await waitFor(`!document.getElementById('sched-form').hidden && document.getElementById('sf-prompt').value === ${JSON.stringify(prompt1)} && document.getElementById('sf-project').selectedOptions[0]?.textContent === 'Portal'`, 8000);
  check('page reload: the open draft comes back as it was (project, prompt, time)', restored, await ev(formText).catch((e) => e.message));
  await waitFor(`!document.getElementById('sf-save').disabled`, 5000);
  await tap('#sf-save');
  const savedOk = await waitFor(`!!document.querySelector('.sched-ok') && /Scheduled on the laptop/.test(document.querySelector('.sched-ok').textContent)`, 10000);
  const v3 = await verify();
  const qT3 = v3.tasks[0] || {};
  const pageAt = await ev(`(() => { const d = JSON.parse(localStorage.getItem('claudeStatus.schedDraft.new') || 'null'); return d; })()`);
  check('Schedule again: confirmed only now, still one task on the laptop, draft cleared', savedOk && v3.tasks.length === 1 && pageAt === null, JSON.stringify(v3));
  check('on the laptop: that project folder, the prompt exactly as typed, the same instant (phone in Tokyo)', qT3.pj === v3.p1 && qT3.p === prompt1 && qT3.st === 'Scheduled' && qT3.at === draft1.at - (draft1.at % 60000) && qT3.rn === 'vscode', JSON.stringify({ qT3, at: draft1.at }));
  const conf = await ev(`({ ok: document.querySelector('.sched-ok').textContent, title: document.querySelector('.qd-title').textContent, facts: document.querySelector('.qd-facts').textContent, acts: [...document.querySelectorAll('.qd-actions button')].map(b => b.textContent).join('|'), laptop: /Laptop clock/.test(document.querySelector('.qd-facts').textContent) })`);
  check('after saving: a clear confirmation and the new task, with Edit and Cancel, and the laptop\'s clock', /You can close this page/.test(conf.ok) && conf.title === 'Phone task: run the tests and summarise.' && conf.acts === 'Edit or reschedule|Cancel task' && conf.laptop, JSON.stringify(conf));
  await shot('sched-saved-360');
  await tap('#sched-back');
  const inList = await waitFor(`[...document.querySelectorAll('#sched-list .qitem')].some(b => /Phone task/.test(b.textContent) && b.querySelector('.qmeta').textContent === 'Portal · Claude Code' && /Scheduled/.test(b.querySelector('.qword').textContent))`, 5000);
  check('the list shows it under UPCOMING with project, AI and status', inList && /UPCOMING · 1/.test(await ev(`document.getElementById('sched-list').textContent`)), await ev(`document.getElementById('sched-list').textContent`));
  await shot('sched-list-360');
  // edit / reschedule
  await tap('#sched-list .qitem');
  await waitFor(`!!document.querySelector('.qd-actions')`, 3000);
  await ev(`[...document.querySelectorAll('.qd-actions button')].find(b => b.textContent === 'Edit or reschedule').id = 'qa-edit'; true`);
  await tap('#qa-edit');
  const editUp = await waitFor(`!document.getElementById('sched-form').hidden && document.getElementById('sf-heading').textContent === 'Edit scheduled task' && document.getElementById('sf-prompt').value === ${JSON.stringify(prompt1)}`, 5000);
  await typeIn('#sf-prompt', 'Edited on the phone');
  await tap('.chip[data-min="180"]');
  await tap('#sf-save');
  const edited = await waitFor(`/Saved on the laptop/.test(document.querySelector('.sched-ok')?.textContent || '')`, 10000);
  const v4 = await verify();
  check('edit and reschedule from the phone: same task, new prompt and time on the laptop', editUp && edited && v4.tasks.length === 1 && v4.tasks[0].p === 'Edited on the phone' && Math.abs(v4.tasks[0].at - (Date.now() + 3 * 3600e3)) < 120e3, JSON.stringify(v4.tasks));
  // a change on the desktop shows up on the phone by itself
  await ask('qs-desktop-edit');
  const synced = await waitFor(`document.querySelector('.qd-prompt')?.textContent === 'Edited on the desktop'`, 25000);
  check('desktop edit appears on the phone while the task is open', synced, await ev(`document.querySelector('.qd-prompt')?.textContent`));
  // cancel (not started)
  await ev(`window.confirm = () => true; [...document.querySelectorAll('.qd-actions button')].find(b => b.textContent === 'Cancel task').id = 'qa-cancel'; true`);
  const why = await ev(`document.querySelector('.qd-explain')?.textContent || ''`);
  await tap('#qa-cancel');
  const qCanceled = await waitFor(`document.querySelector('.qd-status')?.textContent === 'Canceled'`, 10000);
  const v5 = await verify();
  check('Cancel task (explained as "it just won\'t run"): canceled on the laptop, never ran', /won't run/.test(why) && qCanceled && v5.tasks[0].st === 'Canceled' && v5.tasks[0].n === 0, JSON.stringify(v5.tasks));
  await tap('#sched-back');
  // double tap on Schedule: one task
  await tap('#sched-new');
  await waitFor(`!document.getElementById('sched-form').hidden`, 3000);
  const remembered = await ev(`document.getElementById('sf-project').selectedOptions[0]?.textContent`);
  await pick('Booking App');
  await typeIn('#sf-prompt', 'Double tap test MOCK:sleep=60');
  await ev(`document.getElementById('sf-adv').open = true; const r = document.getElementById('sf-runner'); r.value = 'headless'; r.dispatchEvent(new Event('change')); true`);
  await setAt(`Date.now() + 60e3`);
  await ev(`window.__qposts = 0; (() => { const o = window.fetch; window.fetch = function (u, opt) { if (String(u).indexOf('api/queue') >= 0 && opt && opt.method === 'POST') window.__qposts++; return o.apply(this, arguments); }; })(); true`);
  await ev(`(() => { const f = document.getElementById('sched-form'); f.requestSubmit(); f.requestSubmit(); document.getElementById('sf-save').click(); return true; })()`);
  await waitFor(`/Scheduled on the laptop/.test(document.querySelector('.sched-ok')?.textContent || '')`, 10000);
  const v6 = await verify();
  check('rapid repeated taps on Schedule: one request, one task', (await ev('window.__qposts')) === 1 && v6.tasks.filter((t) => /Double tap/.test(t.p)).length === 1 && remembered === 'Portal', `${await ev('window.__qposts')} ${JSON.stringify(v6.tasks)} ${remembered}`);
  // running: Stop run (not Cancel)
  await ask('qs-run');
  const qRunning = await waitFor(`/Running|Starting/.test(document.querySelector('.qd-status')?.textContent || '') && [...document.querySelectorAll('.qd-actions button')].map(b => b.textContent).join('|') === 'Stop run'`, 15000);
  const runWhy = await ev(`document.querySelector('.qd-explain')?.textContent || ''`);
  check('a running task offers Stop run (no Edit or Cancel), explained as ending the run in progress', qRunning && /already in progress/.test(runWhy), await ev(`document.getElementById('sched-body').textContent.slice(0, 300)`));
  await shot('sched-running-360');
  await ev(`window.confirm = () => true; document.querySelector('.qd-actions button').id = 'qa-stop'; true`);
  await tap('#qa-stop');
  const stopped = await waitFor(`document.querySelector('.qd-status')?.textContent === 'Canceled'`, 15000);
  const v7 = await verify();
  const dt = v7.tasks.find((t) => /Double tap/.test(t.p)) || {};
  check('Stop run: stopped on the laptop (Canceled); it ran once, in that project, with the mock', stopped && dt.st === 'Canceled' && dt.n === 1 && dt.pj === v7.p2 && v7.calls === 1, JSON.stringify(v7));
  // disconnected: nothing can be saved, and the page says so
  await tap('#sched-back');
  await tap('#sched-new');
  await waitFor(`!document.getElementById('sched-form').hidden`, 3000);
  await typeIn('#sf-prompt', 'Offline draft');
  await tap('.chip[data-min="30"]');
  mode = 'fail';
  await c.send('Fetch.enable', { patterns: [{ urlPattern: '*api/*' }] });
  await ev(`window.dispatchEvent(new Event('online')); true`);
  const offForm = await waitFor(`${connText} !== 'LIVE' && document.getElementById('sf-save').disabled && /Not connected to the laptop/.test(${errText})`, 12000);
  check('disconnected: Save disabled with the reason, the draft stays', offForm && (await ev(`document.getElementById('sf-prompt').value`)) === 'Offline draft', await ev(errText));
  await shot('sched-form-offline');
  mode = 'pass';
  await c.send('Fetch.disable');
  await waitFor(`${connText} === 'LIVE' && !document.getElementById('sf-save').disabled`, 13000);
  // 320 px
  await phone(320, 640);
  const l320 = await ev(`({ sw: document.documentElement.scrollWidth, w: innerWidth })`);
  check('form at 320 px: no horizontal scroll', l320.sw <= l320.w, JSON.stringify(l320));
  await shot('sched-form-320');
  await ev(`window.confirm = () => true; true`);
  await tap('#sf-discard');
  const discarded = await waitFor(`document.getElementById('sched-form').hidden && !localStorage.getItem('claudeStatus.schedDraft.new')`, 3000);
  check('Discard draft closes the form and forgets the draft', discarded);
  await c.send('Emulation.setTimezoneOverride', { timezoneId: '' });
  await ask('qs-clear');

  // ---------- Commit: review changes & commit (a temporary repository + local bare remote) ----------
  await phone(360, 800);
  await ev(`localStorage.removeItem('claudeStatus.gitSeen'); localStorage.setItem('claudeStatus.view', 'list'); true`);
  await ask('git-setup');
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);
  await ev(`window.__acts = 0; (() => { const o = window.fetch; window.fetch = function (u, opt) { if (opt && opt.method && opt.method !== 'GET') window.__acts++; return o.apply(this, arguments); }; })(); true`);
  const offerSel = `[...document.querySelectorAll('#git-offers .git-offer-open')].find(b => b.textContent === 'Git Demo — Review changes & commit')`;
  const offered = await waitFor(`!document.getElementById('git-offers').hidden && !!(${offerSel})`, 15000);
  check('a finished worker whose repository has changes gets a small "Review changes & commit" item', offered, await ev(`document.getElementById('git-offers').textContent`));
  await shot('git-offer-360');
  await tap('#git-offers .x');
  const offerHid = await waitFor(`!(${offerSel})`, 3000);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);
  await sleep(2500);
  check('dismissing it hides it, and it stays hidden for the same unchanged repository (after a reload too)', offerHid && !(await ev(`!!(${offerSel})`)));
  await ev(`window.__acts = 0; (() => { const o = window.fetch; window.fetch = function (u, opt) { if (opt && opt.method && opt.method !== 'GET') window.__acts++; return o.apply(this, arguments); }; })(); true`);
  await tap('#view-git');
  const listed = await waitFor(`[...document.querySelectorAll('#git-list .qitem')].some(b => b.querySelector('.qtitle').textContent === 'Git_Demo' && /3 changed files/.test(b.textContent))`, 10000);
  check('Commit view (opened by hand): lists the repository with its change count', listed, await ev(`document.getElementById('git-list').textContent`));
  await ev(`[...document.querySelectorAll('#git-list .qitem')].find(b => b.querySelector('.qtitle').textContent === 'Git_Demo').setAttribute('data-t', 'demo'); true`);
  await tap('#git-list .qitem[data-t="demo"]');
  const reviewed = await waitFor(`document.querySelectorAll('#git-body .git-file').length === 3`, 10000);
  const rv = await ev(`(() => ({ facts: document.querySelector('#git-body .qd-facts').textContent, files: [...document.querySelectorAll('#git-body .git-file')].map(e => e.querySelector('.git-badge').textContent + ' ' + e.querySelector('.git-path').textContent), msg: document.querySelector('.git-msg').value, send: document.querySelector('.git-send').disabled, why: document.querySelector('.git-problem').textContent, checked: document.querySelectorAll('#git-body input:checked').length }))()`);
  check('review: repository, branch, push destination and the changed files with their status', reviewed && /Branch\s*main/.test(rv.facts) && /Push to\s*origin → main/.test(rv.facts) && /Git_Demo/.test(rv.facts) && rv.files.join('|') === 'M a.txt|M bin.dat|U new file.txt', JSON.stringify(rv));
  check('review starts with nothing selected, an empty message and Send disabled', rv.msg === '' && rv.send && rv.checked === 0 && /Select at least one file/.test(rv.why), JSON.stringify(rv));
  await ev(`[...document.querySelectorAll('#git-body .git-file')].forEach(e => e.querySelector('.git-small').setAttribute('data-p', e.querySelector('.git-path').textContent)); true`);
  await tap('#git-body .git-small[data-p="a.txt"]');
  const diffShown = await waitFor(`!!document.querySelector('#git-body .git-dpre .l-add')`, 8000);
  const dl = await ev(`(() => { const p = document.querySelector('#git-body .git-dpre'); return { add: [...p.querySelectorAll('.l-add')].map(e => e.textContent).join('|'), del: [...p.querySelectorAll('.l-del')].map(e => e.textContent).join('|'), boxScroll: p.scrollWidth > p.clientWidth, page: document.documentElement.scrollWidth <= innerWidth, head: document.querySelector('#git-body .git-dh').textContent }; })()`);
  check('diff: unstaged changes, added/removed lines marked; long lines scroll inside the diff, not the page', diffShown && /\+one changed/.test(dl.add) && /-one/.test(dl.del) && dl.boxScroll && dl.page && dl.head === 'UNSTAGED CHANGES', JSON.stringify(dl));
  await tap('#git-body .git-small[data-p="bin.dat"]');
  const binShown = await waitFor(`/Binary file/.test(document.querySelector('#git-body .git-file:nth-child(2) .git-diff').textContent)`, 8000);
  check('diff: a binary file is clearly labelled as not previewable', binShown);
  await tap('#git-body .git-small[data-p="new file.txt"]');
  const newShown = await waitFor(`/NEW FILE/.test(document.querySelector('#git-body .git-file:nth-child(3) .git-diff').textContent) && /\\+brand new/.test(document.querySelector('#git-body .git-file:nth-child(3) .git-dpre')?.textContent || '')`, 8000);
  check('diff: a new (untracked) file shows its content', newShown);
  await ev(`[...document.querySelectorAll('#git-body .git-file')].forEach(e => e.querySelector('input').setAttribute('data-p', e.querySelector('.git-path').textContent)); true`);
  await tap('#git-body input[data-p="a.txt"]');
  await tap('#git-body input[data-p="new file.txt"]');
  const message = 'Phone commit: "quotes" & $HOME `tick` ; rm -rf /\n\n# kept line\n  indented\tüñï 🚀\n';
  await ev(`(() => { const m = document.querySelector('.git-msg'); m.focus(); m.value = ${JSON.stringify(message)}; m.dispatchEvent(new Event('input')); return true; })()`);
  const pv = await ev(`(() => { const p = [...document.querySelectorAll('#git-body .git-cmds')]; return { cmds: p[0].textContent, msg: p[1].textContent, send: document.querySelector('.git-send').disabled, page: document.documentElement.scrollWidth <= innerWidth, why: document.querySelector('.git-problem').textContent, count: document.querySelector('.git-count').textContent }; })()`);
  check('command preview follows the selection and message exactly', pv.cmds === "git --literal-pathspecs add -- a.txt 'new file.txt'\ngit commit --cleanup=verbatim --file=-\ngit push origin refs/heads/main:refs/heads/main" && pv.msg === message && !pv.send && pv.count === '2 of 3 selected' && pv.page, JSON.stringify(pv));
  check('nothing was sent while reviewing, diffing, selecting and typing (no action request)', (await ev('window.__acts')) === 0, await ev('window.__acts'));
  await shot('git-review-360');
  await tap('.git-send');
  const pushed = await waitFor(`/Commit and push succeeded/.test(document.getElementById('git-body').textContent)`, 30000);
  const res = await ev(`(() => ({ hash: document.querySelector('.git-hash')?.textContent || '', steps: [...document.querySelectorAll('.git-step')].map(e => e.querySelector('.git-sst').textContent + ' ' + e.querySelector('.git-scmd').textContent), page: document.documentElement.scrollWidth <= innerWidth, acts: window.__acts }))()`);
  check('Send: progress steps with Git output, then "Commit and push succeeded" with the commit hash', pushed && /^Commit [0-9a-f]{40}$/.test(res.hash) && res.steps.length === 4 && res.steps.every(s => s.startsWith('Done')) && res.page && res.acts === 1, JSON.stringify(res));
  await shot('git-result-360');
  await ask('git-verify');
  const gv = JSON.parse(fs.readFileSync(path.join(dir, 'git-verify.json'), 'utf8'));
  check('on the laptop: committed exactly the selected files with the typed message, and pushed', gv.head === gv.remote && res.hash === 'Commit ' + gv.head && gv.files.split('\n').sort().join('|') === 'a.txt|new file.txt' && Buffer.from(gv.msg64, 'base64').toString('utf8') === message && /bin\.dat/.test(gv.status), JSON.stringify(gv));
  await tap('#git-body .usage-btn');
  const gitAfter = await waitFor(`document.querySelectorAll('#git-body .git-file').length === 1 && document.querySelector('.git-msg').value === ''`, 10000);
  check('Done: a fresh review with the remaining change and an empty message', gitAfter, await ev(`document.getElementById('git-body').textContent.slice(0, 200)`));
  await phone(320, 700);
  const fits = await ev(`({ segs: document.querySelector('.viewbar').scrollWidth <= document.querySelector('.viewbar').clientWidth, page: document.documentElement.scrollWidth <= innerWidth })`);
  check('320 px: the four views fit the view switch and the review has no horizontal scroll', fits.segs && fits.page, JSON.stringify(fits));
  await shot('git-review-320');
  await tap('#view-list');
  const gListBack = await waitFor(`!document.getElementById('rows').hidden && document.getElementById('git').hidden && [...document.querySelectorAll('#rows .name')].some(n => n.textContent === 'Git Demo')`, 4000);
  await tap('#view-creatures');
  const gPetsBack = await waitFor(`!document.getElementById('creatures').hidden && document.querySelectorAll('#pets .pet').length > 0`, 4000);
  await tap('#view-sched');
  const gSchedBack = await waitFor(`!document.getElementById('sched').hidden && document.getElementById('git').hidden`, 4000);
  check('List, Creatures and Scheduled views still work after using Commit', gListBack && gPetsBack && gSchedBack, JSON.stringify({ gListBack, gPetsBack, gSchedBack }));
} catch (e) {
  check('browser test script ran to completion', false, e.stack || e.message);
} finally {
  fs.writeFileSync(path.join(dir, 'results.txt'), results.join('\n') + '\n');
  c.close();
}
