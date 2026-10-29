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
  await tap('#usage-toggle');
  const hid = await ev(`({ hidden: document.getElementById('usage').hidden, h: document.getElementById('usage').getBoundingClientRect().height, saved: localStorage.getItem('claudeStatus.usage'), conn: ${connText} })`);
  await c.send('Page.navigate', { url: base });
  await waitFor(`${connText} === 'LIVE'`, 8000);
  const uAfter = await ev(`({ hidden: document.getElementById('usage').hidden, pressed: document.getElementById('usage-toggle').getAttribute('aria-pressed') })`);
  check('usage: Show usage off hides it without leaving space, stays LIVE, and is remembered on this phone', hid.hidden && hid.h === 0 && hid.saved === 'hidden' && hid.conn === 'LIVE' && uAfter.hidden && uAfter.pressed === 'false', JSON.stringify({ hid, uAfter }));
  await tap('#usage-toggle');
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

  const PETS = `(async () => {
    const api = await (await fetch('api/board', { cache: 'no-store' })).json();
    const de = document.documentElement, problems = [];
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
      const sz = svg.getBoundingClientRect().width;
      if (sz % 16 !== 0 || sz < 64) problems.push(tag + 'sprite ' + sz + 'px is not a whole multiple of 16');
      if (d.s === 'DONE') {
        if (!x) problems.push(tag + 'no dismiss x');
        else { const xr = x.getBoundingClientRect(); if (xr.width < 44 || xr.height < 44) problems.push(tag + 'x target ' + xr.width + 'x' + xr.height); if (xr.right > cr.right + .5 || xr.top < cr.top - .5) problems.push(tag + 'x outside card'); if (pet.contains(x)) problems.push(tag + 'x inside the creature button'); }
      } else if (x) problems.push(tag + 'x on a ' + d.s + ' card');
    });
    if (hats.size !== cards.length) problems.push('two creatures share a hat');
    const conn = document.getElementById('conn').getBoundingClientRect();
    if (conn.width === 0 || conn.right > innerWidth) problems.push('connection label not visible');
    return { problems, cards: cards.length };
  })()`;
  for (const [w, h] of [[320, 640], [360, 800], [412, 915]]) {
    await phone(w, h);
    await c.send('Page.navigate', { url: base });
    const ok = await waitFor(`${connText} === 'LIVE' && !document.getElementById('creatures').hidden && document.querySelectorAll('#pets .card:not(.placeholder)').length > 5`, 8000);
    check(`${w}px creatures: opens in the remembered Creatures view and goes LIVE`, ok, await ev(connText));
    const l = await ev(PETS);
    check(`${w}px creatures: same data as the laptop; names wrap inside cards; no horizontal scroll; crisp sprites; distinct hats; 44px x on DONE only`, l.problems.length === 0, l.problems.slice(0, 6).join('; '));
    await shot(`creatures-${w}`);
  }

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
  const petsStale = await ev(`({ stale: document.getElementById('board').classList.contains('stale'), banner: !document.getElementById('banner').hidden && /Last update \\d/.test(document.getElementById('banner-when').textContent), cards: document.querySelectorAll('#pets .card:not(.placeholder)').length, x: [...document.querySelectorAll('#pets .x')].every(b => b.disabled), paused: ${anim(workingFig)} })`);
  check('Creatures view, connection lost: RECONNECTING with last update, creatures kept but stale and still, x disabled', petsRecon && petsStale.stale && petsStale.banner && petsStale.cards === cardsBefore && petsStale.x && petsStale.paused === 'bob/paused', JSON.stringify(petsStale));
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
} catch (e) {
  check('browser test script ran to completion', false, e.stack || e.message);
} finally {
  fs.writeFileSync(path.join(dir, 'results.txt'), results.join('\n') + '\n');
  c.close();
}
