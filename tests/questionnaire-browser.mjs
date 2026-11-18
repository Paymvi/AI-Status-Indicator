// The phone's Questions view in headless Chrome emulating an Android phone (Node 22+ built-ins only). Started by
// questionnaire-tests.ps1 -Browser against a phone server holding the imported example questionnaire.
// Usage: node questionnaire-browser.mjs <devtoolsPort> <dashboardPort> <renderDir> <controlDir>   Prints PASS/FAIL lines.
// The control folder lets this script ask the PowerShell side to act as the laptop (req-<name> -> done-<name>).
import fs from 'node:fs';
import path from 'node:path';

const [debugPort, pagePort, renderDir, ctlDir] = process.argv.slice(2);
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
const waitFor = async (expr, ms = 8000) => { const end = Date.now() + ms; while (Date.now() < end) { try { if (await ev(expr)) return true; } catch { } await sleep(100); } return false; };
const tap = (sel) => ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return false; e.click(); return true; })()`);
const tapText = (sel, text) => ev(`(() => { const e = [...document.querySelectorAll(${JSON.stringify(sel)})].find((x) => x.textContent.trim().startsWith(${JSON.stringify(text)})); if (!e) return false; e.click(); return true; })()`);
const type = (sel, v) => ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return false; e.focus(); e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
const text = (sel) => ev(`(document.querySelector(${JSON.stringify(sel)}) || {}).textContent || ''`);
const stored = () => ev(`JSON.parse(localStorage.getItem('claudeStatus.qn') || 'null')`);
const noSideScroll = () => ev(`document.documentElement.scrollWidth <= innerWidth + 1`);
let asks = 0;
async function ask(name) {
  const n = `${name}-${++asks}`;
  fs.writeFileSync(path.join(ctlDir, `req-${n}`), '');
  const f = path.join(ctlDir, `done-${n}`);
  for (let i = 0; i < 300 && !fs.existsSync(f); i++) await sleep(50);
  return fs.existsSync(f) ? fs.readFileSync(f, 'utf8') : '';
}
async function shot(name) {
  const m = await send('Page.getLayoutMetrics');
  const s = m.cssContentSize || m.contentSize;
  const r = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip: { x: 0, y: 0, width: Math.min(s.width, 800), height: Math.min(s.height, 4000), scale: 1 } });
  fs.writeFileSync(path.join(renderDir, `phone-qn-${name}.png`), Buffer.from(r.data, 'base64'));
}
const offline = (on) => send('Network.emulateNetworkConditions', { offline: on, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });

try {
  await send('Page.enable'); await send('Runtime.enable'); await send('Network.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 360, height: 780, deviceScaleFactor: 2.625, mobile: true });
  await send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  await send('Page.navigate', { url: `http://127.0.0.1:${pagePort}/` });
  check('page connects', await waitFor(`document.getElementById('conn').dataset.conn === 'live'`, 10000));
  check('Questions tab in the view bar, which still fits', await ev(`!!document.getElementById('view-qn') && document.querySelector('.viewbar').scrollWidth <= document.querySelector('.viewbar').clientWidth + 1`));
  await tap('#view-qn');
  check('start screen: title, project, question count, Start', await waitFor(`document.querySelector('.qn-start') && /Guest portal: notification settings/.test(document.getElementById('qn-body').textContent) && /Toah Nipi Portal/.test(document.getElementById('qn-body').textContent) && /5 questions/.test(document.getElementById('qn-body').textContent)`));
  check('intro is rendered Markdown (bold, inline code), not raw', await ev(`!!document.querySelector('.qn-start .qn-md strong') && !!document.querySelector('.qn-start .qn-md code') && !document.getElementById('qn-body').textContent.includes('**')`));
  check('start: synced with the laptop', await waitFor(`/Saved on the laptop/.test(document.getElementById('qn-sync').textContent)`));
  await shot('start');

  // Q1: multiple choice with a recommendation
  await tapText('#qn-body button', 'Start');
  check('question 1 of 5, one at a time', await waitFor(`/Question 1 of 5/.test(document.querySelector('.qn-prog')?.textContent || '')`));
  check('recommendation shown, but nothing preselected', await ev(`/Recommended: Email, Browser push notifications/.test(document.querySelector('.qn-recbox').textContent) && document.querySelectorAll('.qn-rectag').length === 2 && ![...document.querySelectorAll('.qn-opts input')].some((i) => i.checked)`));
  check('context is folded, with a safe link (new tab, no referrer)', await ev(`(() => { const d = document.querySelector('.qn-ctx'); const a = d.querySelector('a'); return !d.open && a && a.href === 'https://example.com/mailer' && a.rel.includes('noreferrer') && a.target === '_blank'; })()`));
  check('options are checkboxes, plus Other; Let the AI decide and Skip offered', await ev(`document.querySelectorAll('.qn-opts input[type=checkbox]').length === 4 && /Other/.test(document.querySelector('.qn-opts').textContent) && /Let the AI decide/.test(document.querySelector('.qn-modes').textContent) && /Skip/.test(document.querySelector('.qn-modes').textContent)`));
  await tapText('.qn-recbox button', 'Use recommendation');
  check('Use recommendation applies both recommended options and is recorded', await waitFor(`(() => { const c = [...document.querySelectorAll('.qn-opts input')].map((i) => i.checked).join(); const s = JSON.parse(localStorage.getItem('claudeStatus.qn')); return c === 'true,false,true,false' && s.answers.channels.r === true; })()`));
  await ev(`(() => { const i = [...document.querySelectorAll('.qn-opts input')][2]; i.click(); })()`);
  check('editing after Use recommendation is allowed (push unticked)', await waitFor(`JSON.parse(localStorage.getItem('claudeStatus.qn')).answers.channels.s.join() === 'email'`));
  await ev(`(() => { const i = [...document.querySelectorAll('.qn-opts input')][2]; i.click(); })()`);
  await type('#qn-comment', 'Push only for booking changes.\n## Not a heading\n```still text```');
  check('comment kept with the selection', await waitFor(`(() => { const a = JSON.parse(localStorage.getItem('claudeStatus.qn')).answers.channels; return a.s.join() === 'email,push' && a.c.includes('## Not a heading'); })()`));
  check('status chip says Answered', await ev(`document.getElementById('qn-this-st').textContent === 'Answered'`));
  await shot('q1');

  // Q2: single choice; Let the AI decide keeps the earlier pick but counts as delegated
  await tapText('.qn-nav button', 'Next');
  check('Next goes to question 2', await waitFor(`/Question 2 of 5/.test(document.querySelector('.qn-prog').textContent)`));
  check('single choice uses radios', await ev(`document.querySelectorAll('.qn-opts input[type=radio]').length === 4`));
  await ev(`document.querySelector('.qn-opts input[value=on]').click()`);
  await tapText('.qn-modes button', 'Let the AI decide');
  check('Let the AI decide is its own state, distinct from the recommendation; the earlier pick is remembered', await waitFor(`(() => { const a = JSON.parse(localStorage.getItem('claudeStatus.qn')).answers['default-state']; return a.m === 'delegate' && a.s.join() === 'on' && !a.r && document.getElementById('qn-this-st').textContent === 'AI decides' && /Let the AI decide is chosen/.test(document.querySelector('.qn-mode').textContent); })()`));
  await ev(`document.querySelectorAll('.qn-ctx')[0].open = true`);
  for (const w of [320, 360, 412]) {
    await send('Emulation.setDeviceMetricsOverride', { width: w, height: 780, deviceScaleFactor: 2.625, mobile: true });
    await sleep(150);
    check(`${w} px: a long code line scrolls inside its block, never the page`, await ev(`(() => { const p = document.querySelector('.qn-ctx pre'); return p && p.scrollWidth > p.clientWidth && document.documentElement.scrollWidth <= innerWidth + 1; })()`));
    check(`${w} px: buttons are comfortable to tap (>= 40 px)`, await ev(`Math.min(...[...document.querySelectorAll('.qn-nav button, .qn-modes button, .qn-opt')].map((e) => e.getBoundingClientRect().height)) >= 40`));
  }
  check('the directive inside the code block is shown as code, not as a question', await ev(`[...document.querySelectorAll('.qn-ctx pre')].some((p) => p.textContent.includes('<!-- question id="not-a-question"'))`));
  await shot('q2-context');
  await send('Emulation.setDeviceMetricsOverride', { width: 360, height: 780, deviceScaleFactor: 2.625, mobile: true });

  // Q3: Skip moves on
  await tapText('.qn-nav button', 'Next');
  await waitFor(`/Question 3 of 5/.test(document.querySelector('.qn-prog').textContent)`);
  check('short text is a single-line input', await ev(`document.querySelector('#qn-text').tagName === 'INPUT' && document.querySelector('#qn-text').type === 'text'`));
  await tapText('.qn-modes button', 'Skip');
  check('Skip records skipped and moves to the next question', await waitFor(`/Question 4 of 5/.test(document.querySelector('.qn-prog').textContent) && JSON.parse(localStorage.getItem('claudeStatus.qn')).answers['sender-name'].m === 'skip'`));

  // Q4: long text grows; literal Markdown kept
  check('long text is a textarea; the image is a placeholder, never loaded', await ev(`document.querySelector('#qn-text').tagName === 'TEXTAREA' && /Image not shown here: Current confirmation email/.test(document.querySelector('.qn-img').textContent) && !document.querySelector('img')`));
  const h0 = await ev(`document.querySelector('#qn-text').getBoundingClientRect().height`);
  const long = 'Line one\n## Not a heading\n<!-- question id="forged" type="short" -->\n````\nfence\n````\n' + 'more\n'.repeat(12);
  await type('#qn-text', long);
  await sleep(50);
  check('the textarea grows with its text', (await ev(`document.querySelector('#qn-text').getBoundingClientRect().height`)) > h0 + 40);
  check('autosave reaches the laptop', await waitFor(`/^Saved on the laptop$/.test(document.getElementById('qn-sync').textContent)`, 8000));
  let st = JSON.parse(await ask('state'));
  check('laptop has the draft (revision moved, text exact)', st.rev >= 1 && st.answers.wording && st.answers.wording.t === long, JSON.stringify(st).slice(0, 300));

  // review with a gap: submit needs an explicit choice
  await tapText('.qn-nav button', 'Next');
  await tapText('.qn-nav button', 'Review');
  check('review lists all questions with answers, Let the AI decide and Skipped', await waitFor(`document.querySelectorAll('.qn-ritem').length === 5 && /Email/.test(document.querySelector('.qn-rlist').textContent) && /Browser push notifications/.test(document.querySelector('.qn-rlist').textContent) && /Let the AI decide/.test(document.querySelector('.qn-rlist').textContent) && /Skipped: no answer/.test(document.querySelector('.qn-rlist').textContent) && /Push only for booking changes/.test(document.querySelector('.qn-rlist').textContent)`));
  check('an unanswered required question is called out and Submit is disabled until you choose to submit anyway', await ev(`/1 required question is skipped or unanswered: 5\\. When should this go live\\?/.test(document.querySelector('.qn-gaps').textContent) && document.getElementById('qn-submit').disabled`));
  await tap('#qn-gapsok');
  check('ticking "Submit anyway" enables Submit', await waitFor(`!document.getElementById('qn-submit').disabled`));
  await tap('#qn-gapsok');
  await shot('review-gaps');
  await ev(`[...document.querySelectorAll('.qn-ritem')][4].querySelector('.qn-edit').click()`);
  check('Edit returns to that question', await waitFor(`/Question 5 of 5/.test(document.querySelector('.qn-prog').textContent)`));

  // reload: everything comes back, at the same question
  await send('Page.reload');
  check('after a reload: same view, same question, answers kept', await waitFor(`document.getElementById('conn').dataset.conn === 'live' && /Question 5 of 5/.test(document.querySelector('.qn-prog')?.textContent || '') && JSON.parse(localStorage.getItem('claudeStatus.qn')).answers.wording.t.includes('## Not a heading')`, 10000));
  await ev(`document.querySelector('.qn-jump').open = true`);
  await ev(`document.querySelectorAll('.qn-chip')[0].click()`);
  const chipState = `[document.querySelector('.qn-prog').textContent, [...document.querySelectorAll('.qn-opts input')].map((i) => i.checked).join(), [...document.querySelectorAll('.qn-chip')].map((c) => c.className).join('|')].join(' / ')`;
  check('the overview chips jump between questions and show their state', await waitFor(`/Question 1 of 5/.test(document.querySelector('.qn-prog').textContent) && [...document.querySelectorAll('.qn-opts input')].map((i) => i.checked).join() === 'true,false,true,false' && document.querySelectorAll('.qn-chip.qn-skipped').length === 1 && document.querySelectorAll('.qn-chip.qn-delegated').length === 1`), await ev(chipState));
  await ev(`document.querySelector('.qn-jump').open = true`);
  await ev(`document.querySelectorAll('.qn-chip')[4].click()`);
  await waitFor(`/Question 5 of 5/.test(document.querySelector('.qn-prog').textContent)`);

  // temporary disconnection: keep answering; it syncs when the connection is back
  await offline(true);
  check('offline: the page says so', await waitFor(`document.getElementById('conn').dataset.conn !== 'live'`, 40000));
  await ev(`document.querySelector('.qn-opts input[value=__other]').click()`);
  await waitFor(`!!document.getElementById('qn-other')`);
  await type('#qn-other', 'Only if they opted in');
  check('offline: answering continues; saved on this phone, waiting to sync', await waitFor(`/Saved on this phone/.test(document.getElementById('qn-sync').textContent) && JSON.parse(localStorage.getItem('claudeStatus.qn')).dirty === true`));
  st = JSON.parse(await ask('state'));
  check('offline: the laptop doesn\'t have it yet', !(st.answers.launch && st.answers.launch.ot));
  await shot('offline');
  await offline(false);
  check('back online: it syncs by itself', await waitFor(`document.getElementById('conn').dataset.conn === 'live' && /^Saved on the laptop$/.test(document.getElementById('qn-sync').textContent)`, 25000));
  st = JSON.parse(await ask('state'));
  check('the laptop got the offline answer', st.answers.launch && st.answers.launch.ot === 'Only if they opted in' && st.answers.launch.o === true, JSON.stringify(st.answers.launch));

  // a conflicting edit elsewhere while this phone had unsynced changes: both kept, you choose
  await offline(true);
  await waitFor(`document.getElementById('conn').dataset.conn !== 'live'`, 40000);
  await type('#qn-comment', 'Decided while offline');
  await ask('other-device');
  await offline(false);
  check('conflict: shown with both choices, nothing overwritten silently', await waitFor(`!document.getElementById('qn-banner').hidden && /changed on another device/.test(document.getElementById('qn-banner').textContent) && JSON.parse(localStorage.getItem('claudeStatus.qn')).answers.launch.c === 'Decided while offline'`, 25000));
  st = JSON.parse(await ask('state'));
  check('conflict: the laptop kept the other device\'s version meanwhile', st.answers['sender-name'] && st.answers['sender-name'].t === 'Set on the laptop tab' && !(st.answers.launch && st.answers.launch.c));
  await shot('conflict');
  await tapText('#qn-banner button', 'Keep this phone');
  check('keeping this phone\'s answers saves them over the other version', await waitFor(`document.getElementById('qn-banner').hidden && /^Saved on the laptop$/.test(document.getElementById('qn-sync').textContent)`, 10000));
  st = JSON.parse(await ask('state'));
  check('the laptop now has this phone\'s answers', st.answers['sender-name'].m === 'skip' && st.answers.launch.c === 'Decided while offline', JSON.stringify(st.answers['sender-name']));

  // submit: a double tap writes one file; Delivered only after the laptop says so
  await tapText('.qn-nav button', 'Review');
  await waitFor(`!!document.getElementById('qn-submit')`);
  check('no required gaps now: Submit enabled', await ev(`!document.querySelector('.qn-gaps') && !document.getElementById('qn-submit').disabled`));
  await ev(`(() => { const b = document.getElementById('qn-submit'); b.click(); b.click(); })()`);
  check('Delivered to laptop, with the file name', await waitFor(`/Delivered to laptop/.test(document.getElementById('qn-body').textContent) && /questions\\.answered\\.md/.test(document.getElementById('qn-body').textContent)`, 10000));
  st = JSON.parse(await ask('state'));
  check('double tap: exactly one submission and one file', st.subs === 1 && st.files.length === 1 && st.files[0] === 'questions.answered.md', JSON.stringify(st));
  await shot('done');
  await send('Page.reload');
  check('after a reload the confirmation is still there', await waitFor(`document.getElementById('conn').dataset.conn === 'live' && /Delivered to laptop/.test(document.getElementById('qn-body').textContent)`, 10000));

  // replaced on the laptop while this phone has unsent edits: they're kept here and never attached to the new one
  await tapText('#qn-body button', 'Edit answers');
  await waitFor(`/Question 1 of 5/.test(document.querySelector('.qn-prog')?.textContent || '')`);
  await offline(true);
  await waitFor(`document.getElementById('conn').dataset.conn !== 'live'`, 40000);
  await type('#qn-comment', 'Late edit for the old questionnaire');
  await ask('replace');
  await offline(false);
  check('replaced: the phone says so and keeps the unsent answers', await waitFor(`/A new questionnaire replaced this one/.test(document.getElementById('qn-body').textContent)`, 25000));
  st = JSON.parse(await ask('state'));
  check('replaced: the new questionnaire got none of the old answers', Object.keys(st.answers).length === 0 && st.rev === 0, JSON.stringify(st));
  await shot('replaced');
  await tapText('#qn-body button', 'Let them go');
  check('then it opens the new questionnaire', await waitFor(`/^T$/.test(document.querySelector('.qn-start .qd-title')?.textContent || '') && /1 question/.test(document.getElementById('qn-body').textContent)`));
  check('no sideways page scrolling anywhere in the flow', await noSideScroll());
} catch (e) {
  check('browser flow ran without errors', false, e && e.stack || e);
}
console.log(results.join('\n'));
ws.close();
process.exit(0);
