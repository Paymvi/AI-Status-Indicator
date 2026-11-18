// The phone's batch screens in a real browser: headless Chrome emulating Android phones over the DevTools protocol
// (Node 22+ built-ins only). Started by `batch-tests.ps1 -Browser`, which owns the laptop side (the real app, its
// queue and a batch paused for review) and keeps it ticking meanwhile. Simulated phones, not a physical device.
// Usage: node batch-browser.mjs <devtoolsPort> <dashboardPort> <renderDir>
import fs from 'node:fs';
import path from 'node:path';

const [debugPort, pagePort, renderDir] = process.argv.slice(2);
const base = `http://127.0.0.1:${pagePort}/`;
const results = [];
const check = (name, ok, info = '') => { results.push(`${ok ? 'PASS' : 'FAIL'}\t${name}\t${ok ? '' : String(info).replace(/\s+/g, ' ').slice(0, 400)}`); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const t = await (await fetch(`http://127.0.0.1:${debugPort}/json/new?about:blank`, { method: 'PUT' })).json();
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let id = 0;
const pending = new Map();
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } };
const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
};
const waitFor = async (expr, ms = 8000) => { const end = Date.now() + ms; while (Date.now() < end) { try { if (await ev(expr)) return true; } catch { } await sleep(100); } return false; };
const shot = async (name) => {
  const m = await send('Page.getLayoutMetrics'); const s = m.cssContentSize || m.contentSize;
  const r = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip: { x: 0, y: 0, width: s.width, height: s.height, scale: 1 } });
  fs.writeFileSync(path.join(renderDir, `phone-${name}.png`), Buffer.from(r.data, 'base64'));
};
const phone = async (width) => {
  await send('Emulation.setDeviceMetricsOverride', { width, height: 780, deviceScaleFactor: 2.625, mobile: true });
  await send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
};
const errors = [];
await send('Runtime.enable');
ws.addEventListener('message', (e) => { const m = JSON.parse(e.data); if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text); });
const noSideways = () => ev('document.documentElement.scrollWidth <= window.innerWidth + 1');
const bodyText = "document.getElementById('sched-body').textContent";

try {
  await phone(360);
  await send('Page.navigate', { url: base });
  check('the page connects to the laptop', await waitFor("document.getElementById('conn').dataset.conn === 'live'", 20000));
  await ev("document.getElementById('view-sched').click()");
  const heads = "[...document.querySelectorAll('#sched-list .sched-h')].map(h => h.textContent)";
  const listed = await waitFor(`(h => h.includes('BATCHES · 1') && h.some(x => x.startsWith('SAVED BATCHES')) && h.some(x => x.startsWith('PAST BATCHES')))(${heads})`);
  check('Scheduled view: running batches, saved batches (Run now) and past batches', listed, await ev(heads));
  check('saved batches have a Run now button; it is disabled for a batch whose run is unfinished', await ev("(() => { const d = [...document.querySelectorAll('.bdef')]; const b = d.find(x => x.textContent.includes('Browser review')); const o = d.find(x => !x.textContent.includes('Browser review')); return !!b && b.querySelector('button').disabled && !!o && !o.querySelector('button').disabled && o.querySelector('button').textContent === 'Run now'; })()"));
  check('no sideways scrolling at 360 px (list)', await noSideways());
  await shot('batches-list-360');
  await ev("[...document.querySelectorAll('#sched-list button.qitem')].find(b => b.textContent.includes('Browser review')).click()");
  const opened = await waitFor(`(t => t.includes('Waiting for your review') && t.includes('Step 1 of 2') && t.includes('Review step 1'))(${bodyText})`);
  check('the run: status, "Step 1 of 2", the review reason', opened, await ev(bodyText));
  check('the actions are the laptop\'s: Continue and Cancel remaining steps…', await ev("[...document.querySelectorAll('#sched-body .qd-actions button')].map(b => b.textContent).join('|') === 'Continue|Cancel remaining steps…'"), await ev("[...document.querySelectorAll('#sched-body .qd-actions button')].map(b => b.textContent).join('|')"));
  await ev("document.querySelector('.bsteps button.qitem').click()");
  check('tapping a step shows its result and its prompt as written', await waitFor(`(t => t.includes('Prompt (as written)') && t.includes('MOCK:ok') && t.includes('nothing needed changing'))(${bodyText})`));
  check('no sideways scrolling at 360 px (run details)', await noSideways());
  await shot('batch-review-360');
  await phone(320);
  check('no sideways scrolling at 320 px', await noSideways());
  await phone(360);
  await ev("[...document.querySelectorAll('#sched-body .qd-actions button')].find(b => b.textContent === 'Continue').click()");
  check('right after Continue: the buttons are disabled while the laptop answers (no second tap)', await ev("[...document.querySelectorAll('#sched-body .qd-actions button')].every(b => b.disabled)"));
  check('the state shown changes only when the laptop reports it: the batch completes', await waitFor(`${bodyText}.includes('All 2 steps done')`, 40000), await ev(bodyText));
  await shot('batch-done-360');
  await ev("document.getElementById('sched-back').click()");
  check('back to the list: no batch is running any more; the finished run is under past batches', await waitFor(`!${heads}.includes('BATCHES · 1')`));
  check('no script errors', errors.length === 0, errors.join(' | '));
} catch (e) { check('browser run', false, e.message); }
ws.close();
process.stdout.write(results.join('\n') + '\n');
