// The phone's Prompts view in a real browser: headless Chrome emulating Android phones over the DevTools protocol
// (Node 22+ built-ins only). Started by `prompts-tests.ps1 -Browser`, which owns the laptop side (a PhoneServer with
// saved prompts). Simulated phones, not a physical device.
// Usage: node prompts-browser.mjs <devtoolsPort> <dashboardPort> <renderDir>
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
const cards = "[...document.querySelectorAll('#pr-list .pr-card')]";
const state = `${cards}.map(c => c.querySelector('.pr-title').textContent + '=' + c.querySelector('.pr-toggle').getAttribute('aria-expanded') + (c.querySelector('.pr-body').hidden ? '/hidden' : '/shown')).join(' | ')`;
const card = (title) => `${cards}.find(c => c.querySelector('.pr-title').textContent === ${JSON.stringify(title)})`;
const expanded = (title) => `(${card(title)}.querySelector('.pr-toggle').getAttribute('aria-expanded') === 'true')`;

try {
  await phone(360);
  await send('Emulation.setFocusEmulationEnabled', { enabled: true });   // the clipboard needs a focused page
  await send('Browser.grantPermissions', { origin: base.replace(/\/$/, ''), permissions: ['clipboardReadWrite', 'clipboardSanitizedWrite'] });
  await send('Page.navigate', { url: base });
  check('the page connects to the laptop', await waitFor("document.getElementById('conn').dataset.conn === 'live'", 20000));
  await ev("document.getElementById('view-pr').click()");
  check('a Prompts view in the navigation; it loads the saved prompts', await waitFor(`${cards}.length === 3`), await ev("document.getElementById('pr-info').textContent + ' / hidden=' + document.getElementById('pr').hidden") + ' / ' + await ev(state));
  check('every card starts collapsed (aria-expanded=false, text hidden), newest first', await ev(`${state} === 'Second line test=false/hidden | Explain simply=false/hidden | Code review=false/hidden'`), await ev(state));
  check('each card shows its status dot (read-only here): Code review is yellow, the others none', await ev(`${cards}.map(c => c.querySelector('.pr-dot').getAttribute('aria-label')).join(',') === 'Status: none,Status: none,Status: yellow' && getComputedStyle(${card('Code review')}.querySelector('.pr-dot')).backgroundColor === 'rgb(255, 200, 61)'`), await ev(`${cards}.map(c => c.querySelector('.pr-dot').getAttribute('aria-label')).join(',')`));
  check('the header is a button that names the text it controls', await ev(`(c => { const t = c.querySelector('.pr-toggle'); return t.tagName === 'BUTTON' && document.getElementById(t.getAttribute('aria-controls')) === c.querySelector('.pr-body'); })(${card('Code review')})`));
  await ev(`${card('Code review')}.querySelector('.pr-toggle').click()`);
  await ev(`${card('Second line test')}.querySelector('.pr-toggle').click()`);
  check('tapping headers expands them; several stay open', await ev(`${expanded('Code review')} && ${expanded('Second line test')} && !${expanded('Explain simply')}`), await ev(state));
  check('the full text is shown exactly, line breaks kept (pre-wrap)', await ev(`(b => b.textContent.includes('  - keep the indentation') && b.textContent.includes('"quotes" {braces} & <html>') && getComputedStyle(b).whiteSpace === 'pre-wrap')(${card('Code review')}.querySelector('.pr-body'))`));
  check('no sideways scrolling at 360 px (expanded)', await noSideways());
  await shot('pr-expanded-360');
  await ev(`${card('Second line test')}.querySelector('.pr-toggle').click()`);
  check('tapping again collapses it', await ev(`!${expanded('Second line test')} && ${card('Second line test')}.querySelector('.pr-body').hidden`));

  // Copy: collapsed card, exact text, Copied!, no toggle
  await ev(`${card('Second line test')}.querySelector('.pr-copy').click()`);
  check('Copy on a collapsed card says Copied! and doesn\'t expand it', await waitFor(`${card('Second line test')}.querySelector('.pr-copy').textContent === 'Copied!'`) && await ev(`!${expanded('Second line test')}`));
  const clip = await ev("navigator.clipboard.readText().catch(e => 'ERR ' + e.message)");
  check('the clipboard has the exact full text (line breaks kept)', clip.replace(/\r\n/g, '\n') === 'Line one\nLine two', JSON.stringify(clip));
  check('Copied! goes back to Copy after a moment', await waitFor(`${card('Second line test')}.querySelector('.pr-copy').textContent === 'Copy'`, 4000));
  await ev(`${card('Code review')}.querySelector('.pr-copy').click()`);
  check('Copy on an expanded card leaves it expanded', await waitFor(`${card('Code review')}.querySelector('.pr-copy').textContent === 'Copied!'`) && await ev(expanded('Code review')));
  const clip2 = await ev("navigator.clipboard.readText().catch(e => 'ERR ' + e.message)");
  check('…and copies its exact text', clip2.replace(/\r\n/g, '\n').startsWith('Review this diff.\n\n  - keep the indentation\n') && clip2.includes('(tab) "quotes"'), JSON.stringify(clip2));

  // search
  await ev("(s => { s.value = 'INDENTATION'; s.dispatchEvent(new Event('input')); })(document.getElementById('pr-search'))");
  check('search matches prompt text, ignoring case; the open card stays open', await ev(`${state} === 'Code review=true/shown'`), await ev(state));
  await ev("(s => { s.value = 'explain'; s.dispatchEvent(new Event('input')); })(document.getElementById('pr-search'))");
  check('search matches titles', await ev(`${state} === 'Explain simply=false/hidden'`), await ev(state));
  await ev("(s => { s.value = 'nothing like this'; s.dispatchEvent(new Event('input')); })(document.getElementById('pr-search'))");
  check('no matches: a helpful message', await ev(`${cards}.length === 0 && document.getElementById('pr-info').textContent.includes('No prompts match “nothing like this”')`), await ev("document.getElementById('pr-info').textContent"));
  await shot('pr-nomatch-360');
  await ev("(s => { s.value = ''; s.dispatchEvent(new Event('input')); })(document.getElementById('pr-search'))");

  // no Clipboard API (plain http on the home network): the older copy command, or the text shown to copy by hand
  await ev("Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true }), true");
  await ev(`${card('Explain simply')}.querySelector('.pr-copy').click()`);
  check('without the Clipboard API: Copied! via the older copy command, or the text is shown selected to copy by hand', await waitFor(`(c => c.querySelector('.pr-copy').textContent === 'Copied!' || (c.querySelector('textarea') && c.querySelector('textarea').value === 'Explain this code simply.'))(${card('Explain simply')})`));

  await phone(320);
  check('no sideways scrolling at 320 px', await noSideways());
  await shot('pr-320');
  check('the view is remembered: a reload opens Prompts again', await (async () => { await send('Page.reload'); await sleep(500); return waitFor(`document.getElementById('view-pr').getAttribute('aria-pressed') === 'true' && ${cards}.length === 3`); })());
  check('no script errors', errors.length === 0, errors.join(' | '));
} catch (e) { check('browser run', false, e.message); }
ws.close();
process.stdout.write(results.join('\n') + '\n');
