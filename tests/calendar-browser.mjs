// The phone's Calendar view in headless Chrome emulating Android phones (Node 22+ built-ins only). Started by
// calendar-tests.ps1 under run-tests.ps1 -Browser, against a phone server showing a fixture week (never real history).
// Usage: node calendar-browser.mjs <devtoolsPort> <dashboardPort> <renderDir>   Prints PASS/FAIL lines.
import fs from 'node:fs';
import path from 'node:path';

const [debugPort, pagePort, renderDir] = process.argv.slice(2);
const results = [];
const check = (name, ok, info = '') => results.push(`${ok ? 'PASS' : 'FAIL'}\t${name}\t${ok ? '' : String(info).replace(/\s+/g, ' ').slice(0, 400)}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const t = await (await fetch(`http://127.0.0.1:${debugPort}/json/new?about:blank`, { method: 'PUT' })).json();
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let id = 0;
const pending = new Map();
ws.onmessage = (ev) => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } };
const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
};
const waitFor = async (expr, ms = 6000) => { const end = Date.now() + ms; while (Date.now() < end) { try { if (await ev(expr)) return true; } catch { } await sleep(100); } return false; };
const tap = (sel) => ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return false; e.click(); return true; })()`);
async function shot(name, full = false) {
  const m = await send('Page.getLayoutMetrics');
  const s = full ? (m.cssContentSize || m.contentSize) : m.cssLayoutViewport;
  const r = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: full, clip: { x: 0, y: 0, width: s.clientWidth || s.width, height: s.clientHeight || s.height, scale: 1 } });
  fs.writeFileSync(path.join(renderDir, `phone-${name}.png`), Buffer.from(r.data, 'base64'));
}
async function phone(width, height = 800) {
  await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 2.625, mobile: true });
  await send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
}

await send('Page.enable'); await send('Runtime.enable');
// Pairs of drawn blocks in the shown columns that cover each other, blocks outside their column, and small text.
const layout = `(() => {
  const cols = [...document.querySelectorAll('.cal-col')];
  const bad = [], over = [], tiny = [];
  cols.forEach((c) => {
    const cr = c.getBoundingClientRect();
    const bs = [...c.querySelectorAll('.cal-b')].map((b) => ({ b, r: b.getBoundingClientRect() }));
    bs.forEach((x) => { if (x.r.left < cr.left - 0.5 || x.r.right > cr.right + 0.5) bad.push(x.b.textContent); });
    for (let i = 0; i < bs.length; i++) for (let j = i + 1; j < bs.length; j++) {
      const a = bs[i].r, b = bs[j].r;
      if (a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1) over.push(bs[i].b.textContent + ' / ' + bs[j].b.textContent);
    }
    c.querySelectorAll('.cal-t1, .cal-t2').forEach((e) => { if (parseFloat(getComputedStyle(e).fontSize) < 11) tiny.push(e.textContent); });
  });
  const targets = [...document.querySelectorAll('.cal-day, #cal-prev, #cal-next, #cal-today, .cal-span .seg, .viewbar .seg')].map((e) => e.getBoundingClientRect().height);
  return { cols: cols.length, blocks: document.querySelectorAll('.cal-col .cal-b').length, bad, over, tiny, minTarget: Math.min(...targets),
    pageScroll: document.documentElement.scrollWidth > innerWidth + 1, viewbarFits: document.querySelector('.viewbar').scrollWidth <= document.querySelector('.viewbar').clientWidth + 1 };
})()`;

for (const w of [320, 360, 412]) {
  await phone(w);
  await send('Page.navigate', { url: `http://127.0.0.1:${pagePort}/` });
  await waitFor(`document.getElementById('conn').dataset.conn === 'live'`, 8000);
  await tap('#view-cal');
  const loaded = await waitFor(`document.querySelectorAll('.cal-day').length === 7 && document.querySelectorAll('.cal-col').length === 1`, 6000);
  check(`${w}px: Calendar opens in Day view with seven day buttons`, loaded, await ev(`document.getElementById('cal').textContent.slice(0, 200)`));
  // Thursday 12 Nov: a running block next to a waiting one, a planned block
  await ev(`[...document.querySelectorAll('.cal-day')][4].click(), true`);
  await sleep(150);
  let L = await ev(layout);
  check(`${w}px Day: no sideways page scroll, view buttons fit, touch targets >= 36 px, text >= 11 px`, !L.pageScroll && L.viewbarFits && L.minTarget >= 36 && L.tiny.length === 0, JSON.stringify(L));
  check(`${w}px Day: overlapping workers sit side by side (nothing covered, nothing outside its day)`, L.blocks >= 3 && L.over.length === 0 && L.bad.length === 0, JSON.stringify(L));
  const kinds = await ev(`({ live: document.querySelectorAll('.cal-col .cal-b.live').length, plan: document.querySelectorAll('.cal-col .cal-b.plan').length, dashed: getComputedStyle(document.querySelector('.cal-col .cal-b.plan')).borderTopStyle, wait: !!document.querySelector('.cal-col .cal-b.live .cal-w') })`);
  check(`${w}px Day: running and waiting blocks are live, the waiting part is striped, planned is dashed`, kinds.live === 2 && kinds.plan === 1 && kinds.dashed === 'dashed' && kinds.wait, JSON.stringify(kinds));
  const now = await ev(`!!document.querySelector('.cal-col.today .cal-now')`);
  check(`${w}px Day: today shows the current-time line`, now);
  if (w === 360) await shot('cal-day-360');
  // Wednesday 11 Nov: the overnight run continues from Tuesday, with two waits
  await tap('#cal-prev');
  await sleep(150);
  const night = await ev(`(() => { const b = [...document.querySelectorAll('.cal-col .cal-b')].find((x) => /Portal/.test(x.textContent)); return b ? { fp: b.classList.contains('fp'), waits: b.querySelectorAll('.cal-w').length, top: parseFloat(b.style.top) } : null; })()`);
  check(`${w}px: ‹ goes back a day; an overnight run continues from midnight with both of its waits`, night && night.fp && night.waits === 2 && night.top === 0, JSON.stringify(night));
  await ev(`[...document.querySelectorAll('.cal-col .cal-b')].find((x) => /Portal/.test(x.textContent)).click(), true`);
  const sheet = await waitFor(`!document.getElementById('cal-sheet').hidden && /Elapsed/.test(document.getElementById('cal-sh-facts').textContent)`, 2000);
  const facts = await ev(`document.getElementById('cal-sh-facts').textContent`);
  check(`${w}px: tapping a block opens its details (elapsed, active, waiting with 2 pauses)`, sheet && /Active/.test(facts) && /2 pauses/.test(facts), facts);
  if (w === 360) await shot('cal-sheet-360');
  await tap('#cal-sh-close');
  check(`${w}px: the details close`, await waitFor(`document.getElementById('cal-sheet').hidden`, 1000));
  // Week view: seven readable columns that scroll sideways inside the calendar, not the page
  await ev(`document.querySelector('.cal-span .seg[data-span="week"]').click(), true`);
  await sleep(200);
  L = await ev(layout);
  const sc = await ev(`(() => { const s = document.getElementById('cal-scroll'); s.scrollLeft = s.scrollWidth; const g = document.querySelector('.cal-gut').getBoundingClientRect(); return { sw: s.scrollWidth, cw: s.clientWidth, col: document.querySelector('.cal-col').getBoundingClientRect().width, gutter: Math.abs(g.left - s.getBoundingClientRect().left) < 2 }; })()`);
  check(`${w}px Week: seven columns of >= 110 px, scrolling sideways inside the calendar only, hour labels stay`, L.cols === 7 && sc.col >= 110 && sc.sw > sc.cw && sc.gutter && !L.pageScroll && L.over.length === 0 && L.bad.length === 0, JSON.stringify({ L, sc }));
  if (w === 360) { await shot('cal-week-360'); }
  await ev(`document.querySelector('.cal-span .seg[data-span="day"]').click(), true`);
}

// A planned block links to its task in Scheduled
await phone(390);
await send('Page.navigate', { url: `http://127.0.0.1:${pagePort}/` });
await waitFor(`document.getElementById('conn').dataset.conn === 'live'`, 8000);
await tap('#view-cal');
await waitFor(`document.querySelectorAll('.cal-day').length === 7`, 6000);
await ev(`[...document.querySelectorAll('.cal-day')][4].click(), true`);
await sleep(150);
await tap('.cal-col .cal-b.plan');
const planSheet = await waitFor(`!document.getElementById('cal-sheet').hidden && /Planned · tentative/.test(document.getElementById('cal-sh-status').textContent) && /Unknown/.test(document.getElementById('cal-sh-facts').textContent)`, 2000);
check('planned block: "Planned · tentative", duration unknown', planSheet, await ev(`document.getElementById('cal-sheet').textContent`));
check('planned block: links to the existing scheduling view', await ev(`!document.getElementById('cal-sh-open').hidden`));
await tap('#cal-sh-open');
check('"Open in Scheduled" switches to the Scheduled view', await waitFor(`!document.getElementById('sched').hidden && document.getElementById('cal').hidden`, 2000));

fs.writeFileSync(path.join(renderDir, 'calendar-browser-results.txt'), results.join('\n') + '\n');
console.log(results.join('\n'));
ws.close();
process.exit(0);
