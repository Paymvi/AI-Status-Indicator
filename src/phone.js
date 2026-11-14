// Claude Status phone dashboard. Shows the laptop's own Worker Board: rows, names, numbering, order and
// states all come from the running indicator (api/board); this script only formats timers and shows the
// connection state honestly. No notifications, sounds, vibration, service worker or offline cache.
(function () {
  'use strict';
  var DOT = ' · ';

  // ---------- timer text: the same rules as Board.Clock / Span / Ago / StatusText in indicator.cs ----------
  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function parts(ms) {
    var s = Math.floor(Math.max(0, ms) / 1000);
    return { m: Math.floor(s / 60), h: Math.floor(s / 3600), d: Math.floor(s / 86400), sec: s % 60, min: Math.floor(s / 60) % 60, hour: Math.floor(s / 3600) % 24 };
  }

  // WORKING: 04:32, then 1h 05m, then 2d 3h.
  function clock(ms) {
    var p = parts(ms);
    if (p.h < 1) return pad2(p.m) + ':' + pad2(p.sec);
    if (p.d < 1) return p.h + 'h ' + pad2(p.min) + 'm';
    return p.d + 'd ' + p.hour + 'h';
  }

  // Waiting: <1m, 2m, 1h 5m, 3d.
  function span(ms) {
    var p = parts(ms);
    if (p.m < 1) return '<1m';
    if (p.h < 1) return p.m + 'm';
    if (p.d < 1) return p.h + 'h' + (p.min > 0 ? ' ' + p.min + 'm' : '');
    return p.d + 'd';
  }

  function ago(ms) {
    var p = parts(ms);
    if (p.m < 1) return 'just now';
    if (p.h < 1) return p.m + 'm ago';
    if (p.d < 1) return p.h + 'h ago';
    return p.d + 'd ago';
  }

  // [state word, rest], e.g. ["WORKING", " · 04:32"]; brief drops the error type (narrow rows).
  function statusParts(state, err, ms, brief) {
    switch (state) {
      case 'WORKING': return ['WORKING', DOT + clock(ms)];
      case 'NEEDS_YOU': return ['NEEDS YOU', DOT + span(ms)];
      case 'DONE': return ['DONE', DOT + ago(ms)];
      case 'ERROR': return ['ERROR', DOT + (err && !brief ? err + DOT : '') + span(ms)];
      default: return ['IDLE', ''];
    }
  }

  function statusText(state, err, ms, brief) { var s = statusParts(state, err, ms, brief); return s[0] + s[1]; }

  // ---------- Claude Usage text: the same rules as Usage.Countdown / PercentText / RemainingText ----------
  // Countdown to a reset: <1m, 12m, 4h 07m, 2d 3h.
  function countdown(ms) {
    var p = parts(ms);
    if (p.m < 1) return '<1m';
    if (p.h < 1) return p.m + 'm';
    if (p.d < 1) return p.h + 'h ' + pad2(p.min) + 'm';
    return p.d + 'd ' + p.hour + 'h';
  }
  function percentText(p) { return p > 0 && p < 0.5 ? '<1%' : Math.round(p) + '%'; }
  function remainingText(used) { var l = 100 - used; return l <= 0 ? '0% left' : l < 0.5 ? '<1% left' : Math.round(l) + '% left'; }

  // ---------- creatures: 16x16 pixel art, drawn as SVG squares ----------
  // Squat, wide bodies (12 x 7) with side nubs, four little feet and a two-dot face; a small hat on top.
  // The laptop gives each worker a look number (row field "c", stable while it's on the board):
  // hat = HATS[c % 13], body colour = BODIES[floor(c / 13)] (phone.cs: Hats = 13, Bodies = 8; the first
  // four bodies are the orange family, which the laptop hands out to about two in three workers).
  // Status colours (yellow / green / red) stay reserved for status, so bodies and hats avoid them.
  var BODIES = [
    { b: '#e8793a', s: '#b9582a' },   // bright orange
    { b: '#d97757', s: '#ac553b' },   // Claude coral
    { b: '#b65a2c', s: '#87401e' },   // burnt orange
    { b: '#f1ab80', s: '#c98559' },   // peach
    { b: '#a58fd6', s: '#7d69ad' },   // lavender
    { b: '#7f97bd', s: '#5d7396' },   // slate blue
    { b: '#d98fa6', s: '#b06a81' },   // rose
    { b: '#cfb68c', s: '#a68f67' }    // sand
  ];
  // Each hat: rows from the top of the 16x16 grid ('.' = empty), letters coloured by c. Hats sit on the
  // body's top edge (row 6).
  var HATS = [
    { n: 'party hat', c: { a: '#a371f7', b: '#ff9bce', c: '#f0f0f0' }, r: [
      '................',
      '.......cc.......',
      '.......aa.......',
      '......abba......',
      '......aaaa......',
      '.....babbab.....'] },
    { n: 'beanie', c: { a: '#58a6ff', b: '#2f6fc4', c: '#f0f0f0' }, r: [
      '................',
      '.......cc.......',
      '......aaaa......',
      '.....aaaaaa.....',
      '.....aaaaaa.....',
      '....bbbbbbbb....'] },
    { n: 'top hat', c: { a: '#3a3a3a', b: '#a371f7', c: '#5c5c5c' }, r: [
      '................',
      '......aaaa......',
      '......caaa......',
      '......aaaa......',
      '......bbbb......',
      '....aaaaaaaa....'] },
    { n: 'propeller cap', c: { a: '#39c5cf', b: '#ff7eb6', c: '#58a6ff', d: '#8b949e' }, r: [
      '................',
      '.....bbbccc.....',
      '.......dd.......',
      '......aaaa......',
      '.....aaaaaa.....',
      '.....aaaaaa.....'] },
    { n: 'crown', c: { a: '#c9d1d9', b: '#58a6ff' }, r: [
      '................',
      '................',
      '.....a.aa.a.....',
      '.....aaaaaa.....',
      '.....abaaba.....',
      '.....aaaaaa.....'] },
    { n: 'cowboy hat', c: { a: '#7a4f30', b: '#c99a6b' }, r: [
      '................',
      '................',
      '......aaaa......',
      '.....aaaaaa.....',
      '.....bbbbbb.....',
      '..aaaaaaaaaaaa..'] },
    { n: 'headphones', c: { a: '#8b949e', b: '#58a6ff' }, r: [
      '................',
      '................',
      '................',
      '.....aaaaaa.....',
      '....a......a....',
      '...a........a...',
      '..a..........a..',
      '.bb..........bb.',
      '.bb..........bb.'] },
    { n: 'chef hat', c: { a: '#f0f0f0', b: '#c8c8c8' }, r: [
      '......a..a......',
      '.....aaaaaa.....',
      '.....aaaaaa.....',
      '......aaaa......',
      '......aaaa......',
      '......bbbb......'] },
    { n: 'bow', c: { a: '#ff7eb6', b: '#c4407f' }, r: [
      '................',
      '................',
      '................',
      '.........aa.aa..',
      '.........aabaa..',
      '.........aa.aa..'] },
    { n: 'antennae', c: { a: '#8b949e', b: '#d2a8ff' }, r: [
      '................',
      '....bb....bb....',
      '....bb....bb....',
      '.....a....a.....',
      '.....a....a.....',
      '.....a....a.....'] },
    { n: 'wizard hat', c: { a: '#6e40c9', b: '#f0f0f0' }, r: [
      '.........a......',
      '........aa......',
      '.......aaa......',
      '......abaa......',
      '......aaaa......',
      '....aaaaaaaa....'] },
    { n: 'baseball cap', c: { a: '#2f81f7', b: '#1b4f99', c: '#f0f0f0' }, r: [
      '................',
      '................',
      '.......cc.......',
      '......aaaa......',
      '.....aaaaaa.....',
      '.....aaaaaabbbb.'] },
    { n: 'flower', c: { a: '#d2a8ff', b: '#f0f0f0' }, r: [
      '................',
      '................',
      '................',
      '...a............',
      '..aba...........',
      '...a............'] }
  ];
  var EYE = '#1a1a1a';

  // [x, y, colour] squares from rows of letters
  function fromRows(rows, colors) {
    var out = [];
    rows.forEach(function (row, y) { for (var x = 0; x < row.length; x++) if (row[x] !== '.') out.push([x, y, colors[row[x]]]); });
    return out;
  }
  function at(list, color) { return list.map(function (p) { return [p[0], p[1], color]; }); }

  // Layers of one creature, each [class, squares]; the CSS shows / animates layers by status (and sleep).
  function creatureLayers(look) {
    look = Math.abs(Math.floor(look)) || 0;
    var hat = HATS[look % HATS.length], body = BODIES[Math.floor(look / HATS.length) % BODIES.length];
    var torso = [];
    for (var y = 6; y <= 12; y++) for (var x = 2; x <= 13; x++) torso.push([x, y, y === 12 ? body.s : body.b]);
    return [
      ['shadow', at([[3, 15], [4, 15], [5, 15], [6, 15], [7, 15], [8, 15], [9, 15], [10, 15], [11, 15], [12, 15]], '#0e0e0e')],
      ['fig', [
        ['legs-a', at([[3, 13], [3, 14], [5, 13], [5, 14], [10, 13], [10, 14], [12, 13], [12, 14]], body.s)],
        ['legs-b', at([[3, 13], [5, 13], [5, 14], [10, 13], [12, 13], [12, 14]], body.s)],
        ['legs-sit', at([[3, 13], [5, 13], [10, 13], [12, 13]], body.s)],
        ['torso', torso],
        ['arm-l', at([[1, 8], [1, 9]], body.b)],
        ['arm-r', at([[14, 8], [14, 9]], body.b)],
        ['arm-up', at([[14, 7], [14, 6], [15, 6], [15, 5]], body.b)],
        ['e-open', at([[5, 8], [5, 9], [10, 8], [10, 9]], EYE)],
        ['e-blink', at([[4, 9], [5, 9], [10, 9], [11, 9]], EYE)],
        ['e-happy', at([[4, 9], [5, 8], [6, 9], [9, 9], [10, 8], [11, 9]], EYE)],
        ['e-worry', at([[5, 9], [10, 9], [4, 8], [5, 7], [11, 8], [10, 7]], EYE)],
        ['m-smile', at([[6, 10], [7, 11], [8, 11], [9, 10]], EYE)],
        ['m-frown', at([[6, 11], [7, 10], [8, 10], [9, 11]], EYE)],
        ['blush', at([[4, 10], [11, 10]], '#f08da0')],
        ['hat', fromRows(hat.r, hat.c)]
      ]],
      ['fx-bang', at([[0, 1], [0, 2], [0, 3], [0, 5]], '#f85149')],
      ['fx-sweat', at([[14, 4], [14, 5], [13, 5]], '#79c0ff')],
      ['fx-spark', at([[1, 2], [0, 3], [2, 3], [1, 4], [14, 0], [13, 1], [15, 1], [14, 2]], '#fff3c4')],
      // sleeping: a big Z and a small z drifting up to the right
      ['fx-z2', at([[12, 0], [13, 0], [14, 0], [15, 0], [14, 1], [13, 2], [12, 3], [13, 3], [14, 3], [15, 3]], '#b8b8b8')],
      ['fx-z1', at([[13, 5], [14, 5], [15, 5], [14, 6], [14, 7], [15, 7]], '#8a8a8a')]
    ];
  }

  // A DONE worker sleeps once it has been done for 3 hours: a look only, layered on DONE (its status text,
  // timer, ordering and counts stay DONE). t = when it became DONE, on the laptop's clock.
  var SLEEP_MS = 3 * 3600 * 1000;
  function isAsleep(state, t, now) { return state === 'DONE' && typeof t === 'number' && now - t >= SLEEP_MS; }

  // Per-worker context-window line: x = {p: percent, o: reading older than the worker's latest activity}
  // or null when Claude Code hasn't reported one for that session. Missing is never shown as 0%.
  function contextInfo(x) {
    if (!x || typeof x.p !== 'number' || !isFinite(x.p) || x.p < 0) return { text: 'Context unavailable', p: null, old: false };
    var p = Math.min(100, x.p);
    return { text: 'Context ' + percentText(p) + (x.o ? ' · old' : ''), p: p, old: !!x.o };
  }

  // ---------- Scheduled prompts (the laptop's Queue, read-only) ----------
  // Task kinds come from the desktop (QLook.Kind in queue.cs); each one belongs to one section here.
  var Q_GROUP = { needs_you: 'attn', attention: 'attn', blocked: 'attn', starting: 'run', running: 'run', usage: 'up', waiting: 'up',
    scheduled: 'up', ready: 'up', draft: 'up', done: 'past', failed: 'past', skipped: 'past', canceled: 'past' };
  var Q_COLOR = { needs_you: 'red', attention: 'red', blocked: 'red', failed: 'red', starting: 'yellow', running: 'yellow', usage: 'orange',
    waiting: 'blue', scheduled: 'blue', ready: 'blue', done: 'green' };
  // Upcoming in the order they will run: ready now, then by time, then waiting on something with no time yet, then drafts.
  function schedGroups(tasks) {
    var g = { attn: [], run: [], up: [], past: [] };
    (tasks || []).forEach(function (t) { (g[Q_GROUP[t.kd]] || g.up).push(t); });
    var rank = function (t) { return t.kd === 'ready' ? 0 : t.kd === 'draft' ? 3 : t.at ? 1 : 2; };
    g.up.sort(function (a, b) { return rank(a) - rank(b) || (a.at || 0) - (b.at || 0); });
    return g;
  }

  // ---------- Scheduling from the phone: time helpers and the form's checks (the laptop checks again) ----------
  var PROMPT_MAX = 20000;   // PhoneServer.QueuePromptMax
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  // ms <-> the value of an <input type="datetime-local"> (this phone's local time, whole minutes)
  function localInput(ms) { var d = new Date(ms); return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes()); }
  function parseLocalInput(s) {
    var m = /^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d)/.exec(s || '');
    return m ? new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5]).getTime() : NaN;
  }
  // "In 30 min" etc. count from the laptop's clock (now), rounded up to a whole minute; "Tomorrow 9 AM" is this phone's.
  function quickTime(chip, now) {
    if (chip.min) return Math.ceil((now + chip.min * 60000) / 60000) * 60000;
    var n = new Date(now);
    return new Date(n.getFullYear(), n.getMonth(), n.getDate() + 1, chip.tomorrow, 0).getTime();
  }
  // f: { pj, p, at (ms or null), keep (edit: leave the schedule as it is), timed (the time was set in this form) }
  function schedProblem(f, now) {
    if (!f.pj) return 'Choose the project.';
    if (!f.p || !f.p.trim()) return 'Write the prompt.';
    if (f.p.length > PROMPT_MAX) return 'The prompt is too long for the phone (at most 20,000 characters). Schedule it on the laptop.';
    if (f.keep && !f.timed) return '';
    if (!f.at || isNaN(f.at)) return 'Choose when it should run.';
    if (f.at < now - 60000) return 'That time has already passed. Choose a later time.';
    if (f.at > now + 366 * 86400000) return 'Choose a time within the next year.';
    return '';
  }

  // ---------- Commit & push (git.cs): the same rules as Git.Commands / Git.SelectionProblem ----------
  // The exact commands Send runs for these files (review d, picked files in review order); the laptop
  // rebuilds this list itself and refuses to run anything that differs from what was shown here.
  function gitCommands(d, picked) {
    var add = ['git', '--literal-pathspecs', 'add', '--'];
    picked.forEach(function (f) { if (f.a) add.push(f.p); });
    var cmds = add.length > 4 ? [add] : [];
    cmds.push(['git', 'commit', '--cleanup=verbatim', '--file=-']);
    cmds.push(['git', 'push', d.remote, 'refs/heads/' + d.branch + ':' + d.merge]);
    return cmds;
  }
  function fileNames(list) { return list.slice(0, 5).map(function (f) { return f.p; }).join(', ') + (list.length > 5 ? ' and ' + (list.length - 5) + ' more' : ''); }
  // Why Send stays disabled ('' = it can be sent).
  function gitProblem(d, picked, msg) {
    if (d.block && d.block.length) return d.block[0];
    if (!picked.length) return 'Select at least one file.';
    var on = {};
    picked.forEach(function (f) { on[f.p] = true; });
    var other = d.files.filter(function (f) { return f.st && !on[f.p]; });
    if (other.length) return 'Already staged on the laptop but not selected: ' + fileNames(other) + '. Select them too, or unstage them on the laptop.';
    if (!msg.trim()) return 'Write a commit message.';
    if (msg.indexOf('\u0000') >= 0) return "The message can't contain a NUL character.";
    return '';
  }
  // One argument for display only (nothing is ever run through a shell): quoted when it isn't plain.
  function showArg(a) { return /^[A-Za-z0-9_.\/:=@+,-]+$/.test(a) ? a : "'" + a.replace(/'/g, "'\\''") + "'"; }

  // Calendar dates are the laptop's ("2026-11-14"), so they're moved by whole days without any time zone involved.
  function addDays(ymd, n) { var d = new Date(ymd + 'T12:00:00Z'); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); }

  if (typeof module === 'object' && module.exports) module.exports = { addDays: addDays, clock: clock, span: span, ago: ago, statusText: statusText, HATS: HATS, BODIES: BODIES, creatureLayers: creatureLayers, countdown: countdown, percentText: percentText, remainingText: remainingText, isAsleep: isAsleep, SLEEP_MS: SLEEP_MS, contextInfo: contextInfo, schedGroups: schedGroups, localInput: localInput, parseLocalInput: parseLocalInput, quickTime: quickTime, schedProblem: schedProblem, gitCommands: gitCommands, gitProblem: gitProblem, showArg: showArg };
  if (typeof document === 'undefined') return;

  // ---------- connection ----------
  var STALE_MS = 30000;     // a live long-poll answers at least every ~20 s
  var OFFLINE_MS = 15000;   // failing this long (or 3 times in a row) = laptop unreachable
  var $ = function (id) { return document.getElementById(id); };
  var boardEl = $('board'), connEl = $('conn'), summaryEl = $('summary'), rowsEl = $('rows'), noteEl = $('note');
  var bannerEl = $('banner'), bannerTitle = $('banner-title'), bannerWhen = $('banner-when'), bannerWhy = $('banner-why');
  var creaturesEl = $('creatures'), petsEl = $('pets'), hintEl = $('pet-hint'), viewBtns = [$('view-list'), $('view-creatures'), $('view-sched'), $('view-git'), $('view-cal')];
  var schedEl = $('sched'), schedInfo = $('sched-info'), schedList = $('sched-list'), schedDetail = $('sched-detail'), schedBody = $('sched-body');
  var schedRefresh = $('sched-refresh'), schedBack = $('sched-back');

  var data = null;          // last board received from the laptop
  var skew = 0;             // laptop clock minus phone clock, so timers match the laptop's
  var lastOk = 0;           // phone time of the last successful contact
  var conn = 'connecting';  // live | connecting | reconnecting | offline
  var why = '';
  var fails = 0, failSince = 0;   // failures in a row, and when the first of them happened
  var seq = 0, ctrl = null, retry = null, noteTimer = null;
  var dismissed = {};       // "key|occurrence" dismissed from this phone: never shown again
  var pending = {};         // key -> true while its dismiss request is in flight
  var rowEls = [];
  var petEls = {};          // key -> creature card, kept across updates so animations and taps aren't interrupted
  var focusing = null, lastFocusKey = '', lastFocusAt = 0;   // one "bring up" request at a time
  var reduceMotion = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : { matches: false };

  // List (default) or Creatures; remembered in this browser only.
  var VIEW_KEY = 'claudeStatus.view';
  var view = 'list';
  try { var v0 = localStorage.getItem(VIEW_KEY); if (v0 === 'creatures' || v0 === 'sched' || v0 === 'git' || v0 === 'cal') view = v0; } catch (e) { }

  // Creatures view options, each remembered in this browser only and off unless turned on: Compact (three
  // columns instead of two; unrelated to the desktop's Compact view) and Show context (a context-window
  // bar on each card). Both only change classes, so the connection and the cards themselves are untouched.
  var COMPACT_KEY = 'claudeStatus.compact', CONTEXT_KEY = 'claudeStatus.context';
  var compact = false, contextShown = false;
  try { compact = localStorage.getItem(COMPACT_KEY) === '1'; contextShown = localStorage.getItem(CONTEXT_KEY) === 'shown'; } catch (e) { }
  var petbarEl = $('petbar'), compactToggle = $('compact-toggle'), contextToggle = $('context-toggle');

  // Claude Usage: shown or hidden, and expanded or not, remembered in this browser only (the desktop has
  // its own Show usage setting). Hiding only hides it; the board keeps the figures up to date.
  var USAGE_KEY = 'claudeStatus.usage', USAGE_OPEN_KEY = 'claudeStatus.usageOpen';
  var usageShown = true, usageOpen = false, usageBusy = false;
  try { usageShown = localStorage.getItem(USAGE_KEY) !== 'hidden'; usageOpen = localStorage.getItem(USAGE_OPEN_KEY) === '1'; } catch (e) { }
  var usageEl = $('usage'), usageSum = $('usage-sum'), usageMini = $('usage-mini'), usageDetail = $('usage-detail'), usageToggle = $('usage-toggle');
  var usageState = $('usage-state'), usageMsg = $('usage-msg'), usageLimits = $('usage-limits'), usageNotes = $('usage-notes'), usageSrc = $('usage-src'), usageRefresh = $('usage-refresh');
  var usageSig = '', limEls = [];

  function poll(wait) {
    var my = ++seq;
    if (ctrl) ctrl.abort();
    clearTimeout(retry);
    var c = ctrl = new AbortController();
    var timer = setTimeout(function () { c.abort(); }, wait ? 28000 : 10000);
    var url = 'api/board' + (wait && data ? '?v=' + encodeURIComponent(data.v) + '&wait=1' : '');
    fetch(url, { cache: 'no-store', credentials: 'same-origin', signal: c.signal, headers: { Accept: 'application/json' } })
      .then(function (r) { if (!r.ok) throw { status: r.status }; return r.json(); })
      .then(function (b) {
        clearTimeout(timer);
        if (my !== seq) return;
        if (!b || !Array.isArray(b.rows) || typeof b.now !== 'number') throw { status: 0 };
        accept(b);
        poll(true);   // wait for the next change
      })
      .catch(function (e) {
        clearTimeout(timer);
        if (my !== seq) return;
        failed(e, c.signal.aborted);
      });
  }

  function accept(b) {
    if (schedAsked) { schedAsked = false; note('Schedule is up to date.'); }
    var present = {};
    b.rows.forEach(function (r) { present[r.k + '|' + r.d] = true; });
    Object.keys(dismissed).forEach(function (k) { if (!present[k]) delete dismissed[k]; });
    var changed = !data || data.v !== b.v;
    data = b;
    skew = b.now - Date.now();
    lastOk = Date.now();
    fails = 0; failSince = 0; why = '';
    var was = conn;
    conn = 'live';
    render(changed || was !== 'live');
    gitOnBoard();
    if (view === 'cal' && changed && Date.now() - calAt > 3000) loadCal();   // a worker changed: its block did too
  }

  function failed(e, aborted) {
    if (schedAsked) { schedAsked = false; note("Couldn't refresh: no answer from the laptop."); }
    fails++;
    if (e && e.status === 421) why = "This address isn't one Claude Status accepts. Open the address shown by phone-setup.ps1.";
    else if (e && e.status >= 500) why = "The laptop is reachable, but Claude Status isn't answering. It may not be running, or the phone dashboard is turned off.";
    else if (e && e.status) why = 'Claude Status answered with an error (' + e.status + ').';
    else if (aborted) why = 'The laptop stopped responding. It may be asleep or have lost its connection.';
    else why = "Can't reach the laptop. It may be asleep, offline or off Tailscale, or this phone isn't connected to Tailscale.";
    if (!failSince) failSince = Date.now();
    conn = fails >= 3 || Date.now() - failSince > OFFLINE_MS ? 'offline' : (data ? 'reconnecting' : 'connecting');
    render(true);
    retry = setTimeout(function () { poll(false); }, Math.min(10000, 1000 * Math.pow(2, Math.min(fails - 1, 4))));
  }

  // Android may pause the page in the background: stop while hidden, refetch the moment it's back.
  function resume() {
    if (document.hidden) return;
    if (conn === 'live' && Date.now() - lastOk > 3000) conn = data ? 'reconnecting' : 'connecting';
    fails = 0;
    render(true);
    poll(false);
  }

  document.addEventListener('visibilitychange', function () {
    document.documentElement.classList.toggle('bg', document.hidden);   // pauses creature animations
    if (document.hidden) { seq++; if (ctrl) ctrl.abort(); clearTimeout(retry); return; }
    resume();
  });
  window.addEventListener('pageshow', function (e) { if (e.persisted) resume(); });
  window.addEventListener('online', resume);

  setInterval(function () {
    if (document.hidden) return;
    if (conn === 'live' && Date.now() - lastOk > STALE_MS) {
      why = 'The laptop stopped responding. It may be asleep or have lost its connection.';
      conn = 'reconnecting';
      if (!failSince) failSince = Date.now();
      render(true);
      poll(false);
    } else if (conn === 'reconnecting' && failSince && Date.now() - failSince > OFFLINE_MS) {
      conn = 'offline';
      render(true);
    }
    tick();
  }, 1000);

  // ---------- rendering (textContent only: board data is never parsed as HTML) ----------
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

  function visibleRows() { return data ? data.rows.filter(function (r) { return !dismissed[r.k + '|' + r.d]; }) : []; }

  // Live: the laptop's clock now. Not live: frozen at the last contact, so nothing pretends to be current.
  function boardNow() { return conn === 'live' ? Date.now() + skew : data.now; }

  function since(ms) { var s = Math.max(0, Math.floor(ms / 1000)); return s < 60 ? s + 's ago' : ago(ms); }

  function render(full) {
    var live = conn === 'live';
    var labels = { live: 'LIVE', connecting: 'CONNECTING…', reconnecting: 'RECONNECTING…', offline: 'OFFLINE' };
    connEl.textContent = labels[conn];
    connEl.dataset.conn = conn;
    boardEl.classList.toggle('stale', !live);
    var rows = visibleRows();
    boardEl.dataset.top = live && rows.length ? rows[0].s : '';

    var showSummary = !!(data && data.summary && data.text);
    summaryEl.hidden = !showSummary;
    if (showSummary) summaryEl.textContent = data.text;

    if (live || (conn === 'connecting' && !data)) bannerEl.hidden = true;
    else {
      bannerEl.hidden = false;
      bannerEl.dataset.conn = conn;
      bannerTitle.textContent = conn === 'offline' ? 'Laptop unreachable' : 'Reconnecting…';
      bannerWhy.textContent = conn === 'offline' || why ? why : '';
      updateWhen();
    }

    if (full) renderOffers();
    if (full && view === 'cal') {
      rowsEl.textContent = '';
      rowEls = [];
      clearPets();
      calLive();   // the week itself comes from api/calendar (loadCal); here only the connection state changes
    } else if (full && view === 'git') {
      rowsEl.textContent = '';
      rowEls = [];
      clearPets();
      if (!gitShown) renderGit(); else updatePreview();   // never rebuilt by a board update: open diffs and your typing stay
    } else if (full && view === 'sched') {
      rowsEl.textContent = '';
      rowEls = [];
      clearPets();
      renderSched(false);
    } else if (full && view === 'creatures') {
      rowsEl.textContent = '';
      rowEls = [];
      renderPets(rows);
    } else if (full) {
      clearPets();
      rowsEl.textContent = '';
      rowEls = [];
      if (!data) {
        var p = el('li', 'row placeholder');
        p.appendChild(el('span', 'ring'));
        p.appendChild(el('span', 'name', conn === 'offline' ? "Can't reach Claude Status on the laptop" : 'Connecting to the laptop…'));
        rowsEl.appendChild(p);
      } else if (!rows.length) {
        var e = el('li', 'row placeholder');
        e.appendChild(el('span', 'ring'));
        e.appendChild(el('span', 'name', 'No Claude workers'));
        rowsEl.appendChild(e);
      } else rows.forEach(function (r) { rowsEl.appendChild(buildRow(r)); });
    }
    renderUsage(full);
    tick();
    if (full && reveal && data) showReveal();
  }

  // ---------- Claude Usage ----------
  // Only what the laptop reported is shown: no figure is estimated, and a passed reset time doesn't zero
  // anything (it says the new allowance hasn't been reported yet).
  function usageNowState(u) {
    if (!u) return data ? 'unavailable' : 'loading';
    var st = u.st;
    if (conn !== 'live' && st !== 'unavailable') return 'stale';
    if (st === 'fresh' && u.obs && boardNow() - u.obs > u.fresh) return 'stale';
    return st;
  }
  var STATE_LABEL = { fresh: 'Fresh', stale: 'Stale', loading: 'Loading…', unavailable: 'Unavailable' };

  function resetClock(resetMs, nowMs) {
    var r = new Date(resetMs), n = new Date(nowMs);
    var time = r.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    var day = function (d) { return d.getFullYear() + '-' + d.getMonth() + '-' + d.getDate(); };
    var tomorrow = new Date(n.getFullYear(), n.getMonth(), n.getDate() + 1);
    if (day(r) === day(n)) return 'today ' + time;
    if (day(r) === day(tomorrow)) return 'tomorrow ' + time;
    return r.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' }) + ', ' + time;
  }

  function resetText(l, now) {
    if (!l.r) return 'No reset time reported';
    if (l.r <= now) return 'Reset ' + resetClock(l.r, now) + ' (' + countdown(now - l.r) + ' ago) · waiting for Claude Code to report the new allowance';
    return 'Resets ' + resetClock(l.r, now) + ' (in ' + countdown(l.r - now) + ')';
  }

  function barClass(l, st, now) { return st === 'stale' || (l.r && l.r <= now) ? 'dim' : l.p >= 95 ? 'max' : l.p >= 80 ? 'hi' : ''; }

  function bar(cls, p) {
    var b = el('span', cls), i = document.createElement('i');
    i.style.width = Math.max(0, Math.min(100, p)) + '%';   // CSSOM, allowed by the page's CSP
    if (p > 0 && p < 3) i.style.minWidth = '2px';
    b.appendChild(i);
    return b;
  }

  function renderUsage(full) {
    usageToggle.setAttribute('aria-pressed', String(usageShown));
    usageEl.hidden = !usageShown;
    if (!usageShown) return;
    usageSum.setAttribute('aria-expanded', String(usageOpen));
    usageDetail.hidden = !usageOpen;
    var u = data && data.usage, now = data ? boardNow() : Date.now(), st = usageNowState(u);
    var sig = JSON.stringify(u || null) + '|' + st;
    if (!full && sig === usageSig) { tickUsage(); return; }
    usageSig = sig;
    var lim = u && Array.isArray(u.lim) ? u.lim : [];

    usageMini.textContent = '';
    if (!lim.length) usageMini.appendChild(el('span', 'muted', st === 'loading' ? 'Loading…' : 'Unavailable · tap for details'));
    lim.slice(0, 2).forEach(function (l) {
      var mi = el('span', 'mi ' + barClass(l, st, now));
      mi.appendChild(el('span', 'mk', l.k));
      mi.appendChild(bar('mbar', l.p));
      mi.appendChild(el('span', 'mp', percentText(l.p)));
      usageMini.appendChild(mi);
    });
    if (lim.length > 2) usageMini.appendChild(el('span', 'muted', '+' + (lim.length - 2)));

    usageMsg.hidden = !(u && u.msg);
    usageMsg.textContent = u && u.msg ? u.msg : '';
    usageLimits.textContent = '';
    limEls = [];
    lim.forEach(function (l) {
      var li = el('li', 'lim ' + barClass(l, st, now)), top = el('div', 'top');
      top.appendChild(el('span', 'ttl', l.t));
      top.appendChild(el('span', 'used', percentText(l.p) + ' used'));
      li.appendChild(top);
      li.appendChild(bar('bar', l.p));
      var sub = el('div', 'sub');
      li.appendChild(sub);
      if (l.x) li.appendChild(el('div', 'sub', l.x));
      var older = el('div', 'sub');
      li.appendChild(older);
      usageLimits.appendChild(li);
      limEls.push({ l: l, sub: sub, older: older });
    });
    usageNotes.textContent = '';
    (u && u.notes || []).forEach(function (n) { usageNotes.appendChild(el('li', '', n)); });
    var src = u && u.src && u.src.length ? u.src.map(function (s) { return s === 'status line' ? 'Claude Code status line (documented)' : 'Claude Code /usage cache (undocumented)'; }).join(' + ') : '';
    usageSrc.textContent = (src ? 'Source: ' + src + '. ' : '') + 'Read from local files on the laptop; checking never uses your Claude allowance.';
    tickUsage();
  }

  // Countdowns and "x ago" (every second, like the worker timers).
  function tickUsage() {
    if (!usageShown) return;
    var u = data && data.usage, now = data ? boardNow() : Date.now(), st = usageNowState(u);
    usageState.dataset.st = st;
    usageState.textContent = STATE_LABEL[st] + (u && u.obs ? ' · Last updated ' + new Date(u.obs).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) + ' (' + ago(now - u.obs) + ')' : '');
    limEls.forEach(function (e) {
      e.sub.textContent = remainingText(e.l.p) + ' · ' + resetText(e.l, now);
      e.older.textContent = u && u.obs && e.l.o && u.obs - e.l.o > 300000 && u.lim.length > 1 ? 'From ' + e.l.src + ', as of ' + ago(now - e.l.o) : '';
    });
    usageRefresh.disabled = conn !== 'live' || usageBusy;
  }

  usageSum.addEventListener('click', function () {
    usageOpen = !usageOpen;
    try { localStorage.setItem(USAGE_OPEN_KEY, usageOpen ? '1' : '0'); } catch (e) { }
    renderUsage(true);
  });
  usageToggle.addEventListener('click', function () {
    usageShown = !usageShown;
    try { localStorage.setItem(USAGE_KEY, usageShown ? 'shown' : 'hidden'); } catch (e) { }
    renderUsage(true);
  });

  // Asks the laptop to re-read its local usage files now; new figures arrive with the board's next update.
  usageRefresh.addEventListener('click', function () {
    if (conn !== 'live' || usageBusy) return;
    usageBusy = true;
    tickUsage();
    var c = new AbortController();
    var timer = setTimeout(function () { c.abort(); }, 10000);
    fetch('api/usage-refresh', {
      method: 'POST', cache: 'no-store', credentials: 'same-origin', signal: c.signal,
      headers: { 'Content-Type': 'application/json', 'X-Claude-Status': 'usage-refresh' }, body: '{}'
    })
      .then(function (res) { return res.json().catch(function () { return {}; }); })
      .then(function (j) {
        if (j.result === 'refreshing') note('Checked the laptop for newer usage.');
        else if (j.result === 'busy') note('Just checked. Try again in a moment.');
        else note("The laptop couldn't check usage right now.");
      })
      .catch(function () { note("Couldn't reach the laptop."); })
      .then(function () { clearTimeout(timer); setTimeout(function () { usageBusy = false; tickUsage(); }, 2000); });
  });

  function updateWhen() {
    if (!data || !lastOk) { bannerWhen.textContent = 'Not connected yet.'; return; }
    var at = new Date(lastOk).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    bannerWhen.textContent = 'Last update ' + at + ' (' + since(Date.now() - lastOk) + '). The board below may be out of date.';
  }

  function buildRow(r) {
    var li = el('li', 'row s-' + r.s);
    li.appendChild(el('span', 'dot'));
    var name = el('span', 'name', r.n.replace(/ (#\d+)$/, ' $1'));   // never wrap "#2" onto its own line
    name.title = r.n;
    li.appendChild(name);
    var st = el('span', 'status'), word = el('span', 'word'), rest = el('span', 'rest');
    st.appendChild(word); st.appendChild(rest);
    li.appendChild(st);
    var x = null;
    if (r.s === 'DONE' && r.d) {
      x = el('button', 'x', '×');
      x.type = 'button';
      x.setAttribute('aria-label', 'Dismiss ' + r.n + ' (only removes this row; Claude is not affected)');
      x.addEventListener('click', function () { dismiss(r, li, x); });
      li.appendChild(x);
    } else li.appendChild(el('span', 'xspace'));
    if (pending[r.k]) li.classList.add('busy');
    rowEls.push({ r: r, name: name, word: word, rest: rest, x: x, li: li });
    return li;
  }

  function tick() {
    if (!bannerEl.hidden) updateWhen();
    renderUsage(false);
    if (!data) return;
    var now = boardNow();
    rowEls.forEach(function (e) {
      var ms = now - e.r.t;
      var s = statusParts(e.r.s, e.r.e, ms, false);
      e.word.textContent = s[0];
      e.rest.textContent = s[1];
      // like the desktop: when the name gets squeezed, drop the error type first
      if (e.r.e && e.name.clientWidth < 84) e.rest.textContent = statusParts(e.r.s, e.r.e, ms, true)[1];
      if (e.x) e.x.disabled = conn !== 'live' || !!pending[e.r.k];
    });
    Object.keys(petEls).forEach(function (k) {
      var e = petEls[k], s = statusParts(e.r.s, e.r.e, now - e.r.t, false);
      // falls asleep by itself 3 h after finishing (laptop clock; frozen like the timers while not live)
      e.li.classList.toggle('asleep', isAsleep(e.r.s, e.r.t, now));
      e.word.textContent = s[0];
      // narrow cards wrap only between the " · " parts, never inside "1h 05m" or "just now"
      e.rest.textContent = s[1].split(DOT).map(function (p) { return p.replace(/ /g, ' '); }).join(DOT);
      if (e.x) e.x.disabled = conn !== 'live' || !!pending[e.r.k];
    });
    if (view === 'sched') renderSched(true);
  }

  // ---------- Scheduled prompts ----------
  // Looking (the list, details, Refresh) only uses the same GET api/board as the rest of the page. Changes are
  // explicit POSTs to api/queue (Schedule / Save, Cancel task, Stop run), each with a one-time id: see the form below.
  var schedOpen = '', schedPastOpen = false, schedSig = '', schedTimes = [], schedAsked = false, schedScroll = 0;
  var schedNew = $('sched-new'), formEl = $('sched-form'), formOpen = false;
  var schedSaved = null;   // { k, task }: the task just saved from here, shown with a confirmation until the board has it
  var tzName = '';
  try { tzName = Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch (e) { }

  function fullTime(ms) {
    return new Date(ms).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short' });
  }
  function rel(ms, now) { return ms > now ? 'in ' + countdown(ms - now) : ago(now - ms); }
  function whenAt(ms, now) { return resetClock(ms, now) + ' (' + rel(ms, now) + ')'; }

  // The one-line "when" of a task in the list, by section.
  function schedWhen(t, now) {
    var g = Q_GROUP[t.kd] || 'up';
    if (g === 'past') return t.fi ? whenAt(t.fi, now) : '';
    if (g === 'attn' && t.why) return t.why;
    if (g === 'run' || g === 'attn') return t.sa ? 'started ' + whenAt(t.sa, now) : '';
    if (t.kd === 'draft') return "won't run until it's queued on the laptop";
    if (t.kd === 'ready') return 'next in line';
    if (t.at) return (t.at <= now ? 'due ' : 'runs ') + whenAt(t.at, now);
    if (t.sc === 'after') return t.af ? 'after “' + t.af + '” succeeds' : 'after its predecessor succeeds';
    if (t.sc === 'usage') return 'when usage is available again';
    return '';
  }

  function schedStatus(t, paused) {
    var up = (Q_GROUP[t.kd] || 'up') === 'up' && t.kd !== 'draft';
    return { text: paused && up ? 'Paused · ' + t.lb : t.lb, color: paused && up ? 'grey' : Q_COLOR[t.kd] || 'grey' };
  }

  function preview(p) { var l = (p || '').split('\n').map(function (s) { return s.trim(); }).filter(Boolean)[0] || ''; return l.length > 140 ? l.slice(0, 139) + '…' : l; }

  function renderSched(timesOnly) {
    var q = data && data.queue, now = data ? boardNow() : Date.now();
    schedRefresh.disabled = schedAsked;
    if (formOpen) updateForm();
    if (timesOnly) { schedTimes.forEach(function (x) { x.el.textContent = x.fn(now); }); return; }
    var sig = JSON.stringify(q || null) + '|' + !!data + '|' + conn + '|' + schedPastOpen + '|' + schedOpen + '|' + formOpen + '|' + JSON.stringify(schedSaved);
    if (sig === schedSig) return;
    schedSig = sig;
    schedTimes = [];
    schedInfo.textContent = 'Times in ' + (tzName ? tzName.replace(/_/g, ' ') : "this phone's time zone");
    schedList.textContent = '';
    schedBody.textContent = '';
    schedList.hidden = !!schedOpen || formOpen;
    schedDetail.hidden = !schedOpen || formOpen;
    formEl.hidden = !formOpen;
    schedNew.hidden = !!schedOpen || formOpen || !(q && q.can);
    schedNew.textContent = draftOf('new') ? '＋ Schedule task (draft saved)' : '＋ Schedule task';
    if (formOpen) return;
    var empty = function (text) { var p = el('p', 'sched-empty', text); schedList.appendChild(p); };
    if (!data) { if (schedOpen) schedBody.appendChild(el('p', 'sched-empty', 'Connecting to the laptop…')); empty(conn === 'offline' ? "Can't reach Claude Status on the laptop" : 'Connecting to the laptop…'); return; }
    if (!q) { empty("The laptop didn't send its schedule. Restart Claude Status on the laptop after updating it."); if (schedOpen) schedBody.appendChild(el('p', 'sched-empty', 'Task details are unavailable.')); return; }
    if (schedOpen) { renderDetail(q, now); return; }
    if (q.warn) schedList.appendChild(el('p', 'sched-warn', q.warn));
    if (q.paused) schedList.appendChild(el('p', 'sched-warn', 'The queue is paused on the laptop: nothing new starts until you resume it there.'));
    var g = schedGroups(q.tasks);
    if (!q.tasks.length) { empty(q.can ? 'Nothing scheduled yet. Tap Schedule task to have Claude run a prompt on the laptop later.' : 'Nothing scheduled. Schedule prompts from the Queue view on the laptop.'); return; }
    [['attn', 'NEEDS ATTENTION'], ['run', 'RUNNING'], ['up', 'UPCOMING']].forEach(function (s) {
      if (!g[s[0]].length) return;
      schedList.appendChild(el('h2', 'sched-h', s[1] + ' · ' + g[s[0]].length));
      var ul = el('ul', 'qlist');
      g[s[0]].forEach(function (t) { ul.appendChild(schedItem(t, q.paused)); });
      schedList.appendChild(ul);
    });
    if (!g.up.length && !g.run.length && !g.attn.length) empty('Nothing upcoming.');
    if (g.past.length) {
      var ph = el('button', 'sched-h sched-past', 'PAST · ' + g.past.length + (schedPastOpen ? '  ▾' : '  ▸ show'));
      ph.type = 'button';
      ph.setAttribute('aria-expanded', String(schedPastOpen));
      ph.addEventListener('click', function () { schedPastOpen = !schedPastOpen; renderSched(false); });
      schedList.appendChild(ph);
      if (schedPastOpen) {
        var pl = el('ul', 'qlist past');
        g.past.forEach(function (t) { pl.appendChild(schedItem(t, false)); });
        schedList.appendChild(pl);
      }
    }
  }

  function schedItem(t, paused) {
    var li = el('li'), b = el('button', 'qitem');
    b.type = 'button';
    var st = schedStatus(t, paused);
    b.appendChild(el('span', 'qtitle', t.ti || preview(t.p) || 'Untitled'));
    b.appendChild(el('span', 'qmeta', (t.pj || 'Unknown project') + DOT + 'Claude Code'));
    var pv = preview(t.p);
    if (pv) b.appendChild(el('span', 'qprev', pv));
    var line = el('span', 'qstat'), word = el('span', 'qword c-' + st.color, st.text), when = el('span', 'qwhen');
    line.appendChild(word); line.appendChild(when);
    b.appendChild(line);
    schedTimes.push({ el: when, fn: function (now) { var w = schedWhen(t, now); return w ? DOT + w : ''; } });
    b.addEventListener('click', function () { schedScroll = window.scrollY; schedOpen = t.k; renderSched(false); window.scrollTo(0, schedEl.offsetTop); });
    li.appendChild(b);
    return li;
  }

  var SCHED_TEXT = { asap: 'As soon as possible', at: 'At a set time', after: 'After another task succeeds', usage: 'When usage is available again' };
  function renderDetail(q, now) {
    var t = q.tasks.filter(function (x) { return x.k === schedOpen; })[0];
    if (!t && schedSaved && schedSaved.k === schedOpen) t = schedSaved.task;   // saved a moment ago; the board catches up
    if (!t) { schedBody.appendChild(el('p', 'sched-empty', "This task is no longer on the laptop's schedule. It may have been removed, or cleared from history.")); return; }
    var st = schedStatus(t, q.paused);
    if (schedSaved && schedSaved.k === t.k) {
      var ok = el('p', 'sched-ok', schedSaved.text);
      ok.setAttribute('role', 'status');
      schedBody.appendChild(ok);
    }
    schedBody.appendChild(el('h2', 'qd-title', t.ti || 'Untitled'));
    var s = el('p', 'qd-status'); s.appendChild(el('span', 'qword c-' + st.color, st.text)); schedBody.appendChild(s);
    if (q.can) taskActions(t, q);
    var dl = el('dl', 'qd-facts');
    var fact = function (k, v, live) {
      if (!v && !live) return;
      dl.appendChild(el('dt', '', k));
      var dd = el('dd', '', live ? live(now) : v);
      if (live) schedTimes.push({ el: dd, fn: live });
      dl.appendChild(dd);
    };
    fact('Project', t.pj || 'Unknown');
    fact('Runs with', 'Claude Code' + (t.md ? ' (' + t.md + ')' : '') + DOT + (t.rn === 'headless' ? 'in the background' : "in the project's VS Code window"));
    fact('Schedule', (SCHED_TEXT[t.sc] || t.sc) + (t.sc === 'after' && t.af ? ': “' + t.af + '”' : ''));
    if (t.sc === 'at' && t.ra) fact('Scheduled for', null, function () { return fullTime(t.ra); });
    if (t.sc === 'at' && t.lt && laptopZoneDiffers(q, t.ra)) fact('Laptop clock', laptopText(t.lt) + (q.tz ? ' (' + q.tz + ')' : ''));
    if (t.at && (Q_GROUP[t.kd] || 'up') === 'up') fact('Next run', null, function (n) { return fullTime(t.at) + ' (' + rel(t.at, n) + ')'; });
    if (t.sc === 'at') fact('If missed', t.im === 'skip' ? 'Skip it' : 'Run when possible');
    if (t.sa) fact('Last started', null, function (n) { return fullTime(t.sa) + ' (' + rel(t.sa, n) + ')'; });
    if (t.fi) fact('Finished', null, function (n) { return fullTime(t.fi) + ' (' + rel(t.fi, n) + ')'; });
    if (t.n) fact('Runs so far', String(t.n));
    if (t.cr) fact('Created', null, function () { return fullTime(t.cr); });
    schedBody.appendChild(dl);
    if (t.why) { schedBody.appendChild(el('h3', 'qd-h', 'Status note')); schedBody.appendChild(el('p', 'qd-text', t.why)); }
    if (t.sum) { schedBody.appendChild(el('h3', 'qd-h', 'Result summary')); schedBody.appendChild(el('p', 'qd-text', t.sum)); }
    schedBody.appendChild(el('h3', 'qd-h', 'Prompt'));
    schedBody.appendChild(el('div', 'qd-prompt', t.p || '(empty)'));
    if (t.pc) schedBody.appendChild(el('p', 'qd-note', 'Shortened: the full prompt is on the laptop.'));
    if (t.cp) schedBody.appendChild(el('p', 'qd-note', 'This task may also commit, push or delete files (set on the laptop). Editing it here keeps that.'));
    if (!q.can) schedBody.appendChild(el('p', 'qd-note', 'View only. Change or run tasks from the Queue view on the laptop.'));
  }

  // Edit / Cancel task (not started yet) and Stop run (already running) are different actions, said plainly.
  var RUNNING = { starting: 1, running: 1, needs_you: 1 };
  function taskActions(t, q) {
    var live = conn === 'live', box = el('div', 'qd-actions'), btns = 0;
    var add = function (text, cls, fn) {
      var b = el('button', 'usage-btn' + (cls ? ' ' + cls : ''), text);
      b.type = 'button'; b.disabled = !live || !!acting;
      b.addEventListener('click', fn);
      box.appendChild(b); btns++;
    };
    if (t.ed) add('Edit or reschedule', '', function () { openForm(t); });
    if (t.cx && !RUNNING[t.kd]) add('Cancel task', 'danger', function () {
      if (confirm('Cancel “' + (t.ti || 'this task') + '”?\n\nIt hasn\'t started, so it simply won\'t run. Tasks waiting for it will be blocked.')) taskAct('cancel', t);
    });
    if (RUNNING[t.kd]) add('Stop run', 'danger', function () {
      if (confirm('Stop “' + (t.ti || 'this task') + '” now?\n\nClaude is stopped on the laptop' + (t.rn === 'headless' ? '' : ' (its VS Code terminal stays open so you can read it)') + '. Changes it already made to files are NOT undone. Tasks waiting for it will be blocked.')) taskAct('stop', t);
    });
    if (!btns) return;
    schedBody.appendChild(box);
    var why = RUNNING[t.kd] ? 'Stop run ends the run that is already in progress.' : t.cx ? 'Cancel task: it hasn\'t started, so it just won\'t run.' : '';
    if (!live) why = 'Not connected to the laptop: actions are unavailable until it is LIVE again.';
    if (why) schedBody.appendChild(el('p', 'qd-explain', why));
  }

  var acting = null;   // the cancel / stop request in flight (one at a time)
  function taskAct(op, t) {
    if (acting || conn !== 'live') return;
    acting = { op: op, k: t.k, id: newId() };
    schedSig = ''; renderSched(false);
    var send = function (tries) {
      queueApi('', { op: op, id: acting.id, k: t.k }).then(function (j) {
        if (j.result === 'pending' && tries < 6) { setTimeout(function () { send(tries + 1); }, 2000); return; }
        acting = null;
        note(j.status === 200 ? (op === 'stop' ? 'Stopped on the laptop.' : "Canceled. It won't run.") : (j.msg || 'The laptop refused (' + j.status + ').'));
        schedSig = ''; renderSched(false);
      }, function () {
        // The same request again is harmless (same id), so one quiet retry before giving up.
        if (tries < 1) { setTimeout(function () { send(tries + 1); }, 1500); return; }
        acting = null;
        note("Not confirmed: the laptop didn't answer. Check the task's status when it's LIVE again.");
        schedSig = ''; renderSched(false);
      });
    };
    send(0);
  }

  function queueApi(q, body) {
    var c = new AbortController(), timer = setTimeout(function () { c.abort(); }, 25000);
    var opt = { cache: 'no-store', credentials: 'same-origin', signal: c.signal, headers: { Accept: 'application/json', 'X-Claude-Status': 'queue' } };
    if (body) { opt.method = 'POST'; opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
    return fetch('api/queue' + (q ? '?' + q : ''), opt).then(function (r) {
      clearTimeout(timer);
      return r.json().catch(function () { return {}; }).then(function (j) { j = j && typeof j === 'object' ? j : {}; j.status = r.status; return j; });
    }, function (e) { clearTimeout(timer); throw e; });
  }

  // The laptop's own clock: shown only when its time zone differs from this phone's at that moment.
  function laptopZoneDiffers(q, ms) { return typeof q.tzo === 'number' && q.tzo !== -new Date(ms || Date.now()).getTimezoneOffset(); }
  function laptopText(lt) {   // "2026-11-14 09:00" (the laptop's wall clock) -> "Sat, Nov 14, 9:00 AM"
    var m = /^(\d{4})-(\d\d)-(\d\d) (\d\d):(\d\d)$/.exec(lt || '');
    if (!m) return lt || '';
    var d = new Date(Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5]));
    return d.toLocaleString([], { timeZone: 'UTC', weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
  }
  function laptopAt(q, ms) { return new Date(ms + q.tzo * 60000).toLocaleString([], { timeZone: 'UTC', weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }); }

  // ---------- the Schedule task form ----------
  // The draft lives in this browser (localStorage) until the laptop confirms it saved the task, so a failed save,
  // a dropped connection, a reload or closing the page never loses it. Its one-time id is kept with it: sending the
  // same draft again (after "not confirmed", or a reload) updates the task the laptop already saved, never a second one.
  var DRAFT_KEY = 'claudeStatus.schedDraft', LASTPROJ_KEY = 'claudeStatus.schedProject';
  var fProject = $('sf-project'), fPrompt = $('sf-prompt'), fAt = $('sf-at'), fTitle = $('sf-title'), fRunner = $('sf-runner'), fMode = $('sf-mode');
  var fModel = $('sf-model'), fMissed = $('sf-missed'), fAdv = $('sf-adv'), fReview = $('sf-review'), fError = $('sf-error'), fSave = $('sf-save');
  var fHeading = $('sf-heading'), fSub = $('sf-sub'), fCount = $('sf-count'), fKeep = $('sf-keep'), fProjNote = $('sf-project-note');
  var draft = null;          // the form's content: { mode, k, rv, id, pj, pjName, p, ti, at, timed, keep, keepText, rn, pm, md, im, open }
  var projects = null, projErr = '', projBusy = false, saving = false, saveMsg = '', tried = false;

  // Two slots: a new task's draft, and the latest edit, so editing a task never replaces an unsent new one.
  function draftOf(mode) { try { return JSON.parse(localStorage.getItem(DRAFT_KEY + '.' + mode) || 'null'); } catch (e) { return null; } }
  function storeDraft(mode) {
    mode = mode || (draft && draft.mode);
    try { if (draft && draft.mode === mode) localStorage.setItem(DRAFT_KEY + '.' + mode, JSON.stringify(draft)); else if (mode) localStorage.removeItem(DRAFT_KEY + '.' + mode); } catch (e) { }
  }

  function openForm(t) {
    var saved = draftOf(t ? 'edit' : 'new');
    if (saved && (!t || saved.k === t.k)) draft = saved;   // your unsent changes win
    else if (t) draft = { mode: 'edit', k: t.k, rv: t.rv, id: newId(), pj: t.pk, pjName: t.pj, p: t.p, ti: t.ti, at: t.sc === 'at' ? t.ra : null, timed: false,
      keepText: t.sc === 'at' ? '' : (SCHED_TEXT[t.sc] || t.sc) + (t.sc === 'after' && t.af ? ': “' + t.af + '”' : ''), rn: t.rn, pm: t.pm, md: t.md, im: t.im };
    else {
      var last = null; try { last = JSON.parse(localStorage.getItem(LASTPROJ_KEY) || 'null'); } catch (e) { }
      draft = { mode: 'new', id: newId(), pj: last ? last.k : '', pjName: last ? last.n : '', p: '', ti: '', at: null, timed: false, rn: 'vscode', pm: 'acceptEdits', md: '', im: 'run' };
    }
    draft.open = true;
    storeDraft();
    formOpen = true; saveMsg = ''; tried = false; schedSaved = null;
    fillForm();
    schedSig = ''; renderSched(false);
    window.scrollTo(0, schedEl.offsetTop);
    loadProjects(false);
  }

  function closeForm(discard) {
    formOpen = false;
    var mode = draft && draft.mode;
    if (draft) { draft.open = false; storeDraft(); }
    if (discard) { draft = null; storeDraft(mode); }
    schedSig = ''; renderSched(false);
  }

  function fillForm() {
    var edit = draft.mode === 'edit';
    fHeading.textContent = edit ? 'Edit scheduled task' : 'Schedule a task';
    fSave.textContent = edit ? 'Save changes' : 'Schedule';
    fPrompt.value = draft.p || '';
    fTitle.value = draft.ti || '';
    fAt.value = draft.at ? localInput(draft.at) : '';
    fRunner.value = draft.rn || 'vscode'; fMode.value = draft.pm || 'acceptEdits'; fModel.value = draft.md || ''; fMissed.value = draft.im || 'run';
    fAdv.open = edit && (draft.rn === 'headless' || draft.pm !== 'acceptEdits' || !!draft.md || draft.im === 'skip');
    fillProjects();
  }

  function fillProjects() {
    fProject.textContent = '';
    var opts = (projects || []).slice();
    if (draft.pj && !opts.some(function (p) { return p.k === draft.pj; })) {
      // not on the laptop's list (yet): a stale token after a laptop restart, or a project whose folder is gone
      var byName = opts.filter(function (p) { return draft.pjName && p.n === draft.pjName; })[0];
      if (byName && projects) { draft.pj = byName.k; storeDraft(); }
      else if (draft.mode === 'edit' || !projects) opts.unshift({ k: draft.pj, n: draft.pjName || 'Current project' });
      else { draft.pj = ''; storeDraft(); }
    }
    var first = el('option', '', projects ? (opts.length ? 'Choose a project…' : 'No projects found') : projErr ? "Couldn't load the projects" : 'Loading projects…');
    first.value = '';
    fProject.appendChild(first);
    opts.forEach(function (p) { var o = el('option', '', p.n); o.value = p.k; fProject.appendChild(o); });
    fProject.value = draft.pj || '';
    fProjNote.hidden = !(projErr || (projects && !opts.length));
    fProjNote.textContent = projErr || 'No projects yet. Projects appear here once Claude Code has worked in them on the laptop, or once a task is scheduled for them there.';
  }

  function loadProjects(fresh) {
    if (projBusy) return;
    projBusy = true;
    queueApi('op=projects' + (fresh ? '&fresh=1' : '')).then(function (j) {
      if (j.status === 200 && Array.isArray(j.projects)) { projects = j.projects; projErr = ''; }
      else projErr = j.status === 404 ? 'The laptop is running an older Claude Status. Re-run install.ps1 there.' : (j.msg || "Couldn't load the projects (" + j.status + ').');
    }, function () { projErr = "Couldn't load the projects: no answer from the laptop."; })
      .then(function () { projBusy = false; if (formOpen && draft) { fillProjects(); updateForm(); } });
  }

  function readForm() {
    var at = parseLocalInput(fAt.value);
    draft.pj = fProject.value;
    var o = fProject.options[fProject.selectedIndex];
    draft.pjName = draft.pj && o ? o.textContent : '';
    draft.p = fPrompt.value; draft.ti = fTitle.value;
    draft.rn = fRunner.value; draft.pm = fMode.value; draft.md = fModel.value.trim(); draft.im = fMissed.value;
    if (!isNaN(at) && at !== draft.at) { draft.at = at; draft.timed = true; }
    if (isNaN(at)) { draft.at = null; draft.timed = false; }
    storeDraft();
  }

  // Everything that depends on the inputs, the connection and the clock; never touches what you typed.
  function updateForm() {
    if (!draft) return;
    var q = data && data.queue, now = data ? boardNow() : Date.now(), live = conn === 'live';
    var edit = draft.mode === 'edit';
    fCount.textContent = draft.p ? draft.p.length.toLocaleString() + ' characters · sent to Claude exactly as written' + (draft.p.length > PROMPT_MAX ? ' · too long (max 20,000)' : '') : '';
    fKeep.hidden = !(edit && draft.keepText && !draft.timed);
    fKeep.textContent = 'Currently: ' + (draft.keepText || '') + '. Pick a time below only to change it.';
    fSub.textContent = (draft.rn === 'headless' ? 'Runs on the laptop with Claude Code, in the background.' : "Runs on the laptop with Claude Code, in the project's VS Code window.");
    // An edit keeps the task's time unless you set a new one here.
    var problem = schedProblem({ pj: draft.pj, p: draft.p, at: draft.at, keep: edit, timed: draft.timed }, now);
    // the exact time, its time zone, and what happens if the laptop isn't ready then (rebuilt only when it reads differently)
    var rv = el('div');
    if (draft.at) {
      var line = el('div');
      line.appendChild(document.createTextNode('Runs '));
      line.appendChild(el('strong', '', fullTime(draft.at)));
      line.appendChild(document.createTextNode(' (' + (draft.at > now ? 'in ' + countdown(draft.at - now) : 'now') + ')'));
      rv.appendChild(line);
      rv.appendChild(el('p', 'sf-note', 'Time zone: ' + (tzName ? tzName.replace(/_/g, ' ') : "this phone's") + ' (this phone).' +
        (q && laptopZoneDiffers(q, draft.at) ? ' On the laptop' + (q.tz ? ' (' + q.tz + ')' : '') + ' that is ' + laptopAt(q, draft.at) + '.' : ' The laptop uses the same time.')));
    } else if (edit && draft.keepText) rv.appendChild(el('div', '', 'Keeps its schedule: ' + draft.keepText + '.'));
    if (rv.childNodes.length) {
      rv.appendChild(el('p', 'sf-note', draft.im === 'skip'
        ? "The laptop must be on, awake and running Claude Status then. If it isn't, the task is skipped."
        : "The laptop must be on, awake and running Claude Status then. If it's asleep or off, the task runs once it's back (after a 1-minute chance to skip it)."));
      rv.appendChild(el('p', 'sf-note', 'If Claude is already busy in this project, or your usage limit is reached, it waits until it can run.'));
      if (q && q.paused) rv.appendChild(el('p', 'sf-note', 'The queue is paused on the laptop: nothing starts until you resume it there.'));
    }
    if (rv.textContent !== fReview.textContent) { fReview.textContent = ''; while (rv.firstChild) fReview.appendChild(rv.firstChild); }
    var why = !data || !q ? 'Connecting to the laptop…' : !q.can ? 'The laptop is running an older Claude Status that can’t take tasks from the phone. Re-run install.ps1 there.' :
      !live ? 'Not connected to the laptop, so nothing can be saved now. Your draft is kept on this phone.' : '';
    fError.hidden = !(saveMsg || why || (problem && (tried || draft.p)));
    fError.textContent = saveMsg || why || problem;
    fSave.disabled = saving || !!why || !!problem;
    if (saving) fSave.textContent = 'Saving on the laptop…';
    else fSave.textContent = edit ? 'Save changes' : 'Schedule';
    fPrompt.setAttribute('aria-invalid', String(tried && !draft.p.trim()));
  }

  function onInput() { if (!draft) return; readForm(); if (!saving) saveMsg = ''; updateForm(); }
  [fProject, fPrompt, fAt, fTitle, fRunner, fMode, fModel, fMissed].forEach(function (x) { x.addEventListener('input', onInput); x.addEventListener('change', onInput); });
  fProject.addEventListener('change', function () { if (draft && draft.pj) try { localStorage.setItem(LASTPROJ_KEY, JSON.stringify({ k: draft.pj, n: draft.pjName })); } catch (e) { } });
  [].forEach.call(document.querySelectorAll('.chip'), function (c) {
    c.addEventListener('click', function () {
      if (!draft) return;
      fAt.value = localInput(quickTime(c.dataset.min ? { min: +c.dataset.min } : { tomorrow: +c.dataset.tomorrow }, data ? boardNow() : Date.now()));
      onInput();
    });
  });
  $('sf-back').addEventListener('click', function () { closeForm(false); window.scrollTo(0, schedScroll); });
  $('sf-discard').addEventListener('click', function () {
    if (draft && (draft.p || '').trim() && !confirm('Discard this draft? Nothing was ' + (draft.mode === 'edit' ? 'changed' : 'scheduled') + '.')) return;
    closeForm(true);
  });
  schedNew.addEventListener('click', function () { schedScroll = window.scrollY; openForm(null); fProject.value ? fPrompt.focus() : fProject.focus(); });

  // Success is shown only after the laptop says it saved the task in its queue file.
  formEl.addEventListener('submit', function (e) {
    e.preventDefault();
    if (!draft || saving) return;
    readForm();
    tried = true;
    updateForm();
    if (fSave.disabled) return;
    var edit = draft.mode === 'edit', d = draft;
    var body = { op: 'save', id: d.id, pj: d.pj, p: d.p, ti: d.ti.trim(), rn: d.rn, pm: d.pm, md: d.md, im: d.im, ai: 'claude' };
    if (edit) { body.k = d.k; body.rv = d.rv; }
    if (!edit || d.timed) body.at = d.at;
    saving = true; saveMsg = '';
    updateForm();
    var finish = function (msg) { saving = false; saveMsg = msg; updateForm(); };
    var send = function (tries) {
      queueApi('', body).then(function (j) {
        if (j.status === 200 && j.result === 'saved' && j.task) {
          saving = false;
          var t = j.task;
          draft = null; storeDraft(d.mode);
          formOpen = false;
          schedSaved = { k: t.k, task: t, text: (edit ? 'Saved on the laptop. ' : 'Scheduled on the laptop. ') + (t.sc === 'at' && t.ra ? 'It runs ' + fullTime(t.ra) + '.' : 'It keeps its schedule.') + ' You can close this page: the laptop runs it.' };
          schedOpen = t.k;
          schedSig = ''; renderSched(false);
          window.scrollTo(0, schedEl.offsetTop);
          return;
        }
        if (j.result === 'pending' && tries < 8) { setTimeout(function () { send(tries + 1); }, 2000); return; }
        if (j.result === 'changed' && j.task && edit) { d.rv = j.task.rv; storeDraft(); return finish('This task changed on the laptop after you opened it (now: ' + j.task.lb + '). Your version is kept here: Save again to replace the laptop\'s, or go Back to see it.'); }
        if (j.result === 'project') { projects = null; loadProjects(true); }
        finish(j.msg || 'The laptop didn\'t save it (' + j.status + '). Your draft is kept.');
      }, function () {
        finish("Not confirmed: the laptop didn't answer, so it may not have been saved. Your draft is kept. Tap " + (edit ? 'Save changes' : 'Schedule') + ' again when it’s LIVE: it won’t create a second task.');
      });
    };
    send(0);
  });

  // A draft left open (reload, the browser closed it in the background) comes back where it was.
  (function () {
    var d = [draftOf('new'), draftOf('edit')].filter(function (x) { return x && x.open; })[0];
    if (d && view === 'sched') { draft = d; formOpen = true; fillForm(); loadProjects(false); }
  })();

  schedBack.addEventListener('click', function () { schedOpen = ''; schedSaved = null; renderSched(false); window.scrollTo(0, schedScroll); });
  schedRefresh.addEventListener('click', function () {
    if (schedAsked) return;
    schedAsked = true;
    schedRefresh.disabled = true;
    poll(false);   // the same read-only GET the page always makes; never an action
  });

  // ---------- Commit view: review changes & commit (git.cs) ----------
  // Looking only GETs (status, diffs) and never changes anything. The only actions are Send: Commit & Push
  // and Retry Push, each a POST carrying a one-time id, so a double tap or a resend after a dropped
  // connection gets the same operation back instead of a second one. The laptop re-checks everything you
  // reviewed (branch, destination, staging, each selected file, the commands) before it runs anything.
  var gitEl = $('git'), gitList = $('git-list'), gitDetail = $('git-detail'), gitBody = $('git-body'), gitBack = $('git-back'), gitRefresh = $('git-refresh'), offersEl = $('git-offers');
  var GIT_SEEN_KEY = 'claudeStatus.gitSeen', GIT_JOB_KEY = 'claudeStatus.gitJob';
  var OLD_LAPTOP = 'The laptop is running an older Claude Status without Commit. Re-run install.ps1 there and restart it.';
  var gitSeen = {};   // repository path -> the status it had when you hid its "Review changes" item
  try { gitSeen = JSON.parse(localStorage.getItem(GIT_SEEN_KEY) || '{}') || {}; } catch (e) { gitSeen = {}; }
  var repos = null, reposErr = '', reposAt = 0, reposBusy = false, doneSig = null;
  var gitOpen = '', gitShown = false;   // token of the repository on screen ('' = the list); built since the last view switch
  var rv = null;                         // the review on screen: { r, d, sel: {path: true}, id, loading, err, sending }
  var job = null, jobTimer = null, jobBusy = false;
  var msgEl = el('textarea', 'git-msg');   // one element for the whole page, so re-renders never touch what you typed
  msgEl.rows = 5; msgEl.placeholder = 'Write your commit message'; msgEl.setAttribute('aria-label', 'Your commit message'); msgEl.spellcheck = true;
  msgEl.addEventListener('input', updatePreview);

  function gitApi(q, body) {
    var c = new AbortController(), timer = setTimeout(function () { c.abort(); }, 45000);
    var opt = { cache: 'no-store', credentials: 'same-origin', signal: c.signal, headers: { Accept: 'application/json', 'X-Claude-Status': 'git' } };
    if (body) { opt.method = 'POST'; opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
    return fetch('api/git' + (q ? '?' + q : ''), opt)
      .then(function (r) { if (!r.ok) throw { status: r.status }; return r.json(); })
      .then(function (j) { clearTimeout(timer); return j; }, function (e) { clearTimeout(timer); throw e; });
  }
  var enc = encodeURIComponent;
  function newId() {
    var b = new Uint8Array(16);
    crypto.getRandomValues(b);
    return btoa(String.fromCharCode.apply(null, b)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }
  function remember(v) { try { if (v) localStorage.setItem(GIT_JOB_KEY, JSON.stringify(v)); else localStorage.removeItem(GIT_JOB_KEY); } catch (e) { } }
  function recall() { try { return JSON.parse(localStorage.getItem(GIT_JOB_KEY) || 'null'); } catch (e) { return null; } }
  function saveSeen() {
    var k = Object.keys(gitSeen);
    if (k.length > 50) k.slice(0, k.length - 50).forEach(function (x) { delete gitSeen[x]; });
    try { localStorage.setItem(GIT_SEEN_KEY, JSON.stringify(gitSeen)); } catch (e) { }
  }

  // Repositories (with change counts) are re-read when a worker finishes, and now and then while the
  // Commit view's list is open; never more than the laptop's own short cache allows.
  function gitOnBoard() {
    var sig = visibleRows().filter(function (r) { return r.s === 'DONE'; }).map(function (r) { return r.k + r.t; }).join(',');
    var age = Date.now() - reposAt;
    if (sig !== doneSig || (view === 'git' && !gitOpen && age > 5000) || (sig && age > 30000)) { doneSig = sig; loadRepos(false); }
  }

  function loadRepos(fresh) {
    if (reposBusy) return;
    reposBusy = true;
    gitApi('op=repos' + (fresh ? '&fresh=1' : ''))
      .then(function (j) { repos = j.repos || []; reposErr = ''; }, function (e) { reposErr = e && e.status === 404 ? OLD_LAPTOP : "Couldn't reach the laptop."; })
      .then(function () {
        reposBusy = false; reposAt = Date.now();
        renderOffers();
        if (view === 'git' && !gitOpen) renderGit();
      });
  }

  // "<Project> — Review changes & commit": only for a finished worker whose repository has changes, nobody
  // is working in it, and you haven't hidden it for exactly this state of the repository.
  function renderOffers() {
    offersEl.textContent = '';
    var items = [];
    if (job) items.push(null);
    (repos || []).forEach(function (r) {
      var done = r.workers.filter(function (w) { return w.s === 'DONE'; });
      if (r.n > 0 && !r.busy && done.length && gitSeen[r.path] !== r.sig && !(job && job.r === r.r)) items.push({ r: r, w: done[0] });
    });
    offersEl.hidden = !items.length || view === 'git' || view === 'sched';
    items.forEach(function (it) {
      var li = el('li', 'git-offer'), b = el('button', 'git-offer-open');
      b.type = 'button';
      if (!it) {
        b.textContent = job.name + ' — ' + (job.state === 'running' ? 'Commit & Push running…' : 'Commit & Push result');
        b.addEventListener('click', function () { gitOpen = job.r; setView('git', true); });
        li.appendChild(b);
        li.appendChild(el('span', 'xspace'));
      } else {
        b.textContent = it.w.n + ' — Review changes & commit';
        b.addEventListener('click', function () { openReview(it.r.r); setView('git', true); });
        var x = el('button', 'x', '×');
        x.type = 'button';
        x.setAttribute('aria-label', 'Hide until ' + it.r.name + ' changes again');
        x.addEventListener('click', function () { gitSeen[it.r.path] = it.r.sig; saveSeen(); renderOffers(); });
        li.appendChild(b); li.appendChild(x);
      }
      offersEl.appendChild(li);
    });
  }

  function renderGit() {
    gitShown = true;
    gitList.hidden = !!gitOpen;
    gitDetail.hidden = !gitOpen;
    gitList.textContent = '';
    gitBody.textContent = '';
    if (gitOpen) { if (job && job.r === gitOpen) renderJob(); else renderReview(); return; }
    var empty = function (t) { gitList.appendChild(el('p', 'sched-empty', t)); };
    if (job) {
      var jl = el('ul', 'qlist'), jb = el('button', 'qitem'), jli = el('li');
      jb.type = 'button';
      jb.appendChild(el('span', 'qtitle', job.name));
      jb.appendChild(el('span', 'qstat c-' + jobHead(job)[1], jobHead(job)[0]));
      jb.addEventListener('click', function () { gitOpen = job.r; renderGit(); });
      jli.appendChild(jb); jl.appendChild(jli); gitList.appendChild(jl);
    }
    if (!repos) { empty(reposErr || 'Looking for repositories on the laptop…'); return; }
    if (reposErr) gitList.appendChild(el('p', 'sched-warn', reposErr));
    if (!repos.length) { empty('No Git repositories yet. This lists the repositories your Claude workers (and scheduled tasks) work in.'); return; }
    gitList.appendChild(el('h2', 'sched-h', 'REPOSITORIES · ' + repos.length));
    var ul = el('ul', 'qlist');
    repos.forEach(function (r) {
      var li = el('li'), b = el('button', 'qitem');
      b.type = 'button';
      b.appendChild(el('span', 'qtitle', r.name));
      b.appendChild(el('span', 'qmeta', r.path));
      var st = !r.ok ? ["Git couldn't read it", 'red'] : r.n ? [r.n + ' changed file' + (r.n === 1 ? '' : 's'), 'yellow'] : ['No changes', 'grey'];
      var line = el('span', 'qstat');
      line.appendChild(el('span', 'qword c-' + st[1], st[0]));
      if (r.busy) line.appendChild(el('span', 'qwhen', DOT + 'a worker is busy here'));
      b.appendChild(line);
      if (r.workers.length) b.appendChild(el('span', 'qprev', r.workers.map(function (w) { return w.n; }).join(', ')));
      b.addEventListener('click', function () { openReview(r.r); });
      li.appendChild(b); ul.appendChild(li);
    });
    gitList.appendChild(ul);
  }

  function openReview(r) {
    if (!rv || rv.r !== r) { msgEl.value = ''; rv = { r: r, d: null, sel: {} }; }
    gitOpen = r;
    loadReview();
    window.scrollTo(0, gitEl.offsetTop);
  }

  function loadReview() {
    var my = rv;
    my.loading = true;
    if (view === 'git') renderGit();
    gitApi('op=review&r=' + enc(my.r)).then(function (d) {
      if (rv !== my) return;
      my.loading = false;
      if (d.result !== 'ok') { my.d = null; my.err = d.result === 'unknown' ? 'The laptop no longer knows this repository (Claude Status may have restarted). Go back and pick it again.' : "Couldn't read the repository."; }
      else {
        var sel = {};   // a refresh keeps your picks of files that are still exactly as they were
        if (my.d) d.files.forEach(function (f) { if (my.sel[f.p] && my.d.files.some(function (o) { return o.p === f.p && o.h === f.h; })) sel[f.p] = true; });
        my.sel = sel; my.d = d; my.err = ''; my.id = newId(); my.sending = false;
        if (d.job && (!job || job.id !== d.job)) checkJob(d.job, false);
      }
      if (view === 'git') renderGit();
    }, function (e) {
      if (rv !== my) return;
      my.loading = false;
      my.err = e && e.status === 404 ? OLD_LAPTOP : "Couldn't reach the laptop.";
      if (view === 'git') renderGit();
    });
  }

  var KIND = { modified: ['M', 'Modified'], added: ['A', 'Added'], deleted: ['D', 'Deleted'], untracked: ['U', 'Untracked (new)'], renamed: ['R', 'Renamed'], conflict: ['!', 'Conflict'] };
  function fact(dl, k, v) { dl.appendChild(el('dt', '', k)); dl.appendChild(el('dd', '', v)); }
  function picked() { return rv && rv.d ? rv.d.files.filter(function (f) { return rv.sel[f.p]; }) : []; }

  function renderReview() {
    if (!rv || (!rv.d && rv.loading)) { gitBody.appendChild(el('p', 'sched-empty', 'Reading the repository on the laptop…')); return; }
    if (!rv.d) { gitBody.appendChild(el('p', 'sched-empty', rv.err || '')); return; }
    var d = rv.d;
    gitBody.appendChild(el('h2', 'qd-title', d.name));
    var dl = el('dl', 'qd-facts');
    fact(dl, 'Repository', d.path);
    fact(dl, 'Branch', d.branch || '(detached HEAD)');
    fact(dl, 'Push to', d.remote && d.merge ? d.remote + ' → ' + d.dest : 'No upstream');
    if (d.url) fact(dl, 'Remote', d.url);
    gitBody.appendChild(dl);
    d.block.forEach(function (t) { gitBody.appendChild(el('p', 'git-block', t)); });
    d.warn.forEach(function (t) { gitBody.appendChild(el('p', 'git-warn', t)); });
    if (rv.loading) gitBody.appendChild(el('p', 'qd-note', 'Refreshing…'));

    gitBody.appendChild(el('h3', 'qd-h', '1 · CHOOSE FILES'));
    if (!d.files.length) gitBody.appendChild(el('p', 'qd-text', 'No changes to commit.'));
    else {
      var bar = el('div', 'git-selbar');
      rv.countEl = el('span', 'git-count');
      var all = el('button', 'usage-btn git-small', 'All'), none = el('button', 'usage-btn git-small', 'None');
      all.type = none.type = 'button';
      all.addEventListener('click', function () { d.files.forEach(function (f) { if (!f.cf) rv.sel[f.p] = true; }); renderGit(); });
      none.addEventListener('click', function () { rv.sel = {}; renderGit(); });
      bar.appendChild(rv.countEl); bar.appendChild(all); bar.appendChild(none);
      gitBody.appendChild(bar);
      var ul = el('ul', 'git-files');
      d.files.forEach(function (f) { ul.appendChild(fileItem(f)); });
      gitBody.appendChild(ul);
    }

    gitBody.appendChild(el('h3', 'qd-h', '2 · YOUR COMMIT MESSAGE'));
    gitBody.appendChild(msgEl);

    gitBody.appendChild(el('h3', 'qd-h', '3 · COMMANDS THAT WILL RUN'));
    gitBody.appendChild(el('p', 'qd-note git-first', 'On the laptop, in ' + d.path + ', one at a time, stopping at the first failure. Each argument goes to Git exactly as shown (no shell; quotes only mark where an argument starts and ends).'));
    rv.cmdEl = el('pre', 'git-cmds');
    gitBody.appendChild(rv.cmdEl);
    gitBody.appendChild(el('p', 'qd-note', 'Message given to git commit (on its input, exactly as written):'));
    rv.msgShow = el('pre', 'git-cmds git-msgshow');
    gitBody.appendChild(rv.msgShow);
    rv.probEl = el('p', 'git-problem');
    gitBody.appendChild(rv.probEl);
    rv.sendBtn = el('button', 'git-send', 'Send: Commit & Push');
    rv.sendBtn.type = 'button';
    rv.sendBtn.addEventListener('click', send);
    gitBody.appendChild(rv.sendBtn);
    gitBody.appendChild(el('p', 'qd-note', 'Nothing is staged, committed or pushed until you press Send. Never: add ., force, amend, reset, clean, stash, pull, rebase, merge or skipping hooks.'));
    updatePreview();
  }

  function fileItem(f) {
    var li = el('li', 'git-file'), row = el('div', 'git-frow'), k = KIND[f.kind] || ['?', f.kind];
    var lab = el('label', 'git-pick'), cb = el('input');
    cb.type = 'checkbox'; cb.checked = !!rv.sel[f.p]; cb.disabled = !!f.cf;
    cb.addEventListener('change', function () { if (cb.checked) rv.sel[f.p] = true; else delete rv.sel[f.p]; updatePreview(); });
    lab.appendChild(cb);
    lab.appendChild(el('span', 'git-badge k-' + f.kind, k[0]));
    var txt = el('span', 'git-path', f.p);
    if (f.o) txt.appendChild(el('span', 'git-orig', ' (from ' + f.o + ')'));
    lab.appendChild(txt);
    row.appendChild(lab);
    var db = el('button', 'usage-btn git-small', 'Diff');
    db.type = 'button';
    db.setAttribute('aria-expanded', 'false');
    row.appendChild(db);
    li.appendChild(row);
    li.appendChild(el('span', 'git-tags', k[1] + (f.st && f.un ? ' · staged and unstaged changes: the whole file is committed' : f.st ? ' · already staged' : '') + (f.sub ? ' · submodule' : '')));
    var box = el('div', 'git-diff');
    box.hidden = true;
    li.appendChild(box);
    db.addEventListener('click', function () { showDiff(f, box, db); });
    return li;
  }

  function size(n) { return n < 1024 ? n + ' bytes' : n < 1048576 ? Math.round(n / 1024) + ' KB' : (n / 1048576).toFixed(1) + ' MB'; }

  function showDiff(f, box, btn) {
    box.hidden = !box.hidden;
    btn.setAttribute('aria-expanded', String(!box.hidden));
    if (box.hidden || box.dataset.loaded) return;
    box.textContent = '';
    box.appendChild(el('p', 'qd-note', 'Loading the diff…'));
    var my = rv;
    gitApi('op=diff&r=' + enc(my.r) + '&p=' + enc(f.p)).then(function (j) {
      if (rv !== my) return;
      box.textContent = '';
      if (j.result !== 'ok') { box.appendChild(el('p', 'qd-note', j.result === 'gone' ? 'This file has no changes any more. Refresh the review.' : "Couldn't load the diff.")); return; }
      box.dataset.loaded = '1';
      if (j.h !== f.h) box.appendChild(el('p', 'git-block', 'This file changed after the review was loaded. Refresh before sending.'));
      j.parts.forEach(function (p) {
        box.appendChild(el('p', 'git-dh', p.t === 'staged' ? 'STAGED CHANGES' : p.t === 'unstaged' ? 'UNSTAGED CHANGES' : 'NEW FILE · ' + size(p.size || 0)));
        if (p.err) box.appendChild(el('p', 'git-nopreview', p.err));
        else if (p.bin) box.appendChild(el('p', 'git-nopreview', 'Binary file: it can’t be previewed here.'));
        else if (!p.text) box.appendChild(el('p', 'qd-note', p.t === 'new' ? 'Empty file.' : 'No text changes (for example, only the file mode changed).'));
        else box.appendChild(diffPre(p.text, p.t === 'new'));
        if (p.trunc) box.appendChild(el('p', 'git-nopreview', 'Not fully previewed: only the first 256 KB is shown. See the rest on the laptop.'));
      });
    }, function () { if (rv !== my) return; box.textContent = ''; box.appendChild(el('p', 'qd-note', "Couldn't reach the laptop.")); });
  }

  function diffPre(text, isNew) {
    var pre = el('pre', 'git-dpre');
    text.replace(/\n$/, '').split('\n').forEach(function (line) {
      var c = isNew ? 'add' : /^(\+\+\+ |--- |diff |index |new file|deleted file|similarity|rename |old mode|new mode)/.test(line) ? 'meta'
        : line[0] === '+' ? 'add' : line[0] === '-' ? 'del' : line.slice(0, 2) === '@@' ? 'hunk' : 'ctx';
      pre.appendChild(el('span', 'l-' + c, (isNew ? '+' : '') + line));
    });
    return pre;
  }

  function updatePreview() {
    if (!rv || !rv.d || !rv.cmdEl || !rv.cmdEl.isConnected) return;
    var d = rv.d, pk = picked(), msg = msgEl.value;
    rv.cmdEl.textContent = d.remote && d.branch && d.merge ? gitCommands(d, pk).map(function (c) { return c.map(showArg).join(' '); }).join('\n') : '(nothing can be pushed from this branch)';
    rv.msgShow.textContent = msg || '(write your message above)';
    rv.msgShow.classList.toggle('empty', !msg);
    if (rv.countEl) rv.countEl.textContent = pk.length + ' of ' + d.files.length + ' selected';
    var prob = gitProblem(d, pk, msg);
    if (!prob && conn !== 'live') prob = 'Not connected to the laptop.';
    if (!prob && rv.sending) prob = 'Sending…';
    rv.probEl.textContent = prob;
    rv.probEl.hidden = !prob;
    rv.sendBtn.disabled = !!prob;
  }

  function send() {
    if (!rv || !rv.d || rv.sending || conn !== 'live') return;
    var d = rv.d, pk = picked(), msg = msgEl.value, my = rv;
    if (gitProblem(d, pk, msg)) return;
    my.sending = true;
    updatePreview();
    remember({ id: my.id, r: d.r });
    gitApi('', { op: 'commit', id: my.id, r: d.r, fp: d.fp, files: pk.map(function (f) { return { p: f.p, h: f.h }; }), msg: msg, cmds: gitCommands(d, pk) })
      .then(function (j) {
        if (j.result === 'job') { my.sending = false; setJob(j); return; }
        my.sending = false;
        remember(null);
        note(j.msg || (j.result === 'unknown' ? 'The laptop no longer knows this repository. Refresh.' : 'Not sent.'));
        updatePreview();
      }, function () { checkJob(my.id, true); });   // it may have arrived: find out before another Send is possible
  }

  // Finds an operation by its id (after a dropped connection or a reload). sent: it's the Send just made.
  function checkJob(id, sent) {
    gitApi('op=job&id=' + enc(id)).then(function (j) {
      if (j.result === 'job') { if (rv && rv.id === id) rv.sending = false; setJob(j); return; }
      var s = recall();
      if (s && s.id === id) remember(null);
      if (sent && rv && rv.id === id) { rv.sending = false; note("The laptop didn't get that Send, so nothing ran. You can send again."); updatePreview(); }
    }, function () { setTimeout(function () { checkJob(id, sent); }, 3000); });
  }

  function setJob(j) {
    var first = !job || job.id !== j.id;
    job = j;
    remember({ id: j.id, r: j.r });
    if (first && view === 'git' && (!gitOpen || gitOpen === j.r)) gitOpen = j.r;
    clearTimeout(jobTimer);
    if (j.state === 'running') jobTimer = setTimeout(function () { if (job && job.id === j.id) checkJob(j.id, false); }, 1000);
    renderOffers();
    if (view === 'git') renderGit();
  }

  var OUTCOME = { pushed: ['Commit and push succeeded', 'green'], push_failed: ['Commit succeeded, push failed', 'red'], commit_failed: ['Commit failed', 'red'], stale: ['Not sent: changed since your review', 'orange'], blocked: ['Not sent', 'orange'] };
  function jobHead(j) {
    if (j.state === 'running') return ['Running on the laptop…', 'yellow'];
    if (j.outcome === 'attention') return [j.commit ? 'Commit succeeded; the push needs attention on the laptop' : 'Needs attention on the laptop', 'orange'];
    return OUTCOME[j.outcome] || ['Finished', 'grey'];
  }
  var STEP = { waiting: 'Waiting', running: 'Running', ok: 'Done', failed: 'Failed', skipped: 'Skipped' };

  function renderJob() {
    var j = job, h = jobHead(j);
    gitBody.appendChild(el('h2', 'qd-title', j.name + ' · Commit & Push'));
    var s = el('p', 'qd-status');
    s.appendChild(el('span', 'qword c-' + h[1], h[0]));
    gitBody.appendChild(s);
    if (j.commit) gitBody.appendChild(el('p', 'qd-text git-hash', 'Commit ' + j.commit));
    if (j.msg) gitBody.appendChild(el('p', 'qd-text', j.msg));
    if (j.state === 'running' && j.running > 8) gitBody.appendChild(el('p', 'git-warn', 'Still running. If a sign-in, passphrase or signing window opened on the laptop, finish it there.'));
    var ol = el('ol', 'git-steps');
    j.steps.forEach(function (st) {
      var li = el('li', 'git-step s-' + st.st);
      li.appendChild(el('span', 'git-sst', STEP[st.st] || st.st));
      li.appendChild(el('code', 'git-scmd', st.c && st.c.length ? st.c.map(showArg).join(' ') : st.l));
      if (st.out) li.appendChild(el('pre', 'git-out', st.out));
      ol.appendChild(li);
    });
    gitBody.appendChild(ol);
    if (j.retry && j.push) {
      gitBody.appendChild(el('h3', 'qd-h', 'RETRY PUSH'));
      gitBody.appendChild(el('p', 'qd-note git-first', 'Pushes the commit that already exists (no new commit). Command:'));
      gitBody.appendChild(el('pre', 'git-cmds', j.push.map(showArg).join(' ')));
      var rb = el('button', 'git-send', 'Retry Push');
      rb.type = 'button';
      rb.disabled = jobBusy || conn !== 'live';
      rb.addEventListener('click', retryPush);
      gitBody.appendChild(rb);
    }
    if (j.state !== 'running') {
      var done = el('button', 'usage-btn', j.retry ? 'Close (keep the commit unpushed)' : 'Done');
      done.type = 'button';
      done.addEventListener('click', closeJob);
      gitBody.appendChild(done);
    }
  }

  function retryPush() {
    if (!job || !job.retry || jobBusy || conn !== 'live') return;
    jobBusy = true;
    var id = job.id;
    renderGit();
    gitApi('', { op: 'push', id: id, n: job.n }).then(function (j) {
      jobBusy = false;
      if (j.result === 'job') setJob(j); else { note('The laptop no longer has this operation.'); renderGit(); }
    }, function () { jobBusy = false; checkJob(id, false); });
  }

  function closeJob() {
    var r = job.r, committed = !!job.commit;
    job = null;
    clearTimeout(jobTimer);
    remember(null);
    if (committed) { msgEl.value = ''; rv = null; }
    renderOffers();
    openReview(r);
  }

  gitBack.addEventListener('click', function () { gitOpen = ''; renderGit(); loadRepos(true); });
  gitRefresh.addEventListener('click', function () {
    if (gitOpen && job && job.r === gitOpen) checkJob(job.id, false);
    else if (gitOpen && rv) loadReview();
    else loadRepos(true);
  });
  (function () { var s = recall(); if (s && typeof s.id === 'string') checkJob(s.id, false); })();

  // ---------- view switch ----------
  function setView(v, remember) {
    view = v === 'creatures' || v === 'sched' || v === 'git' || v === 'cal' ? v : 'list';
    calEl.hidden = view !== 'cal';
    if (view !== 'cal') calSheet.hidden = true;
    if (view === 'cal' && Date.now() - calAt > 3000) loadCal();
    viewBtns.forEach(function (b) { b.setAttribute('aria-pressed', String(b.dataset.view === view)); });
    rowsEl.hidden = view !== 'list';
    schedEl.hidden = view !== 'sched';
    gitEl.hidden = view !== 'git';
    gitShown = false;
    if (view === 'git' && !gitOpen && Date.now() - reposAt > 3000) loadRepos(false);
    creaturesEl.hidden = view !== 'creatures';
    petbarEl.hidden = view !== 'creatures';
    if (remember) try { localStorage.setItem(VIEW_KEY, view); } catch (e) { }
    render(true);   // same data, same connection: only the presentation changes
  }
  viewBtns.forEach(function (b) { b.addEventListener('click', function () { if (b.dataset.view !== view) setView(b.dataset.view, true); }); });

  // ---------- Calendar: one block per prompt run, planned tasks dashed (laid out by the laptop: api/calendar) ----------
  var calEl = $('cal'), calGrid = $('cal-grid'), calScroll = $('cal-scroll'), calRange = $('cal-range'), calDays = $('cal-days');
  var calZone = $('cal-zone'), calNotes = $('cal-notes'), calSheet = $('cal-sheet');
  var calSpanBtns = [].slice.call(document.querySelectorAll('.cal-span .seg'));
  var HOUR = 44;            // px per hour (--hh in phone.css)
  var cal = null;           // the week from the laptop
  var calDate = '';         // the laptop date being looked at ('' = its today)
  var calSpan = 'day';      // day (one column, readable) | week (seven columns, scrolls sideways)
  var calOpen = '', calAt = 0, calBusy = false, calErr = '', calScrolled = '';

  function calApi(d) {
    var c = new AbortController(), timer = setTimeout(function () { c.abort(); }, 15000);
    return fetch('api/calendar' + (d ? '?d=' + encodeURIComponent(d) : ''), { cache: 'no-store', credentials: 'same-origin', signal: c.signal, headers: { Accept: 'application/json', 'X-Claude-Status': 'calendar' } })
      .then(function (r) { clearTimeout(timer); if (!r.ok) throw { status: r.status }; return r.json(); }, function (e) { clearTimeout(timer); throw e; });
  }

  function loadCal() {
    if (calBusy) return;
    calBusy = true; calAt = Date.now();
    var want = calDate;
    calApi(want).then(function (w) {
      if (!w || !Array.isArray(w.days) || w.days.length !== 7 || !Array.isArray(w.blocks)) throw { status: 0 };
      calErr = '';
      w.got = Date.now();   // the now line moves on from here (phone clock), like the desktop's
      if (want === calDate) { cal = w; if (!calDate) calDate = w.today >= 0 ? w.days[w.today] : w.days[0]; }
    }, function (e) {
      calErr = e && e.status === 503 ? "The laptop's Claude Status is too old for the Calendar: update it and restart it." : e && e.status ? 'The laptop answered with an error (' + e.status + ').' : "Couldn't load the calendar: no answer from the laptop.";
    }).then(function () { calBusy = false; if (view === 'cal') renderCal(); if (want !== calDate) loadCal(); });
  }
  setInterval(function () { if (view === 'cal' && !document.hidden && conn === 'live' && Date.now() - calAt > 30000) loadCal(); }, 5000);

  function calLive() { if (view === 'cal') renderCal(); }

  function calIndex() { return cal ? cal.days.indexOf(calDate) : -1; }
  function dayName(ymd, opts) { return new Date(ymd + 'T12:00:00Z').toLocaleDateString([], Object.assign({ timeZone: 'UTC' }, opts)); }

  function calGo(n) {
    calDate = addDays(calDate || (cal ? cal.days[0] : ''), n);
    calOpen = ''; calSheet.hidden = true;
    if (cal && cal.days.indexOf(calDate) >= 0) renderCal(); else { calScrolled = ''; renderCal(); loadCal(); }
  }

  function renderCal() {
    var week = calSpan === 'week';
    calSpanBtns.forEach(function (b) { b.setAttribute('aria-pressed', String(b.dataset.span === calSpan)); });
    $('cal-prev').setAttribute('aria-label', week ? 'Previous week' : 'Previous day');
    $('cal-next').setAttribute('aria-label', week ? 'Next week' : 'Next day');
    calGrid.textContent = ''; calDays.textContent = '';
    var i = calIndex();
    if (!cal || i < 0) {
      calRange.textContent = calDate ? dayName(calDate, { weekday: 'short', month: 'short', day: 'numeric' }) : '';
      calZone.textContent = '';
      calNotes.hidden = false;
      calNotes.textContent = calErr || (conn === 'offline' ? "Can't reach Claude Status on the laptop." : 'Loading the calendar…');
      return;
    }
    calRange.textContent = week ? dayName(cal.days[0], { month: 'short', day: 'numeric' }) + ' – ' + dayName(cal.days[6], { month: 'short', day: 'numeric' })
      : dayName(calDate, { weekday: 'long', month: 'short', day: 'numeric' });
    calZone.textContent = 'Times on the laptop’s clock (' + cal.zone + ')' + (calBusy ? ' · updating…' : '');
    var notes = (cal.notes || []).slice();
    if (calErr) notes.unshift(calErr);
    if (conn !== 'live') notes.unshift('Not live: showing what the laptop last sent.');
    calNotes.hidden = !notes.length;
    calNotes.textContent = notes.join(' ');

    // day chips (Day view): the whole week at a glance, a dot on days with activity
    calDays.hidden = week;
    var busy = {};
    cal.blocks.forEach(function (b) { busy[b.d] = true; });
    cal.days.forEach(function (d, k) {
      var c = el('button', 'cal-day' + (k === cal.today ? ' today' : '') + (busy[k] ? ' busy' : ''));
      c.type = 'button';
      c.setAttribute('aria-pressed', String(k === i));
      c.setAttribute('aria-label', dayName(d, { weekday: 'long', month: 'long', day: 'numeric' }) + (k === cal.today ? ', today' : '') + (busy[k] ? ', has activity' : ''));
      c.appendChild(document.createTextNode(dayName(d, { weekday: 'short' })));
      c.appendChild(el('b', '', dayName(d, { day: 'numeric' })));
      c.addEventListener('click', function () { calDate = d; calOpen = ''; calSheet.hidden = true; renderCal(); });
      calDays.appendChild(c);
    });

    var cols = week ? [0, 1, 2, 3, 4, 5, 6] : [i];
    calGrid.style.setProperty('--days', cols.length);
    calGrid.style.setProperty('--colw', week ? '112px' : '0px');
    calGrid.style.width = week ? (44 + 7 * 112) + 'px' : '';   // as wide as its columns, so the hour labels can stay put while it scrolls sideways
    calGrid.appendChild(el('div', 'cal-head cal-corner'));
    cols.forEach(function (k) { calGrid.appendChild(el('div', 'cal-head' + (k === cal.today ? ' today' : ''), cal.labels[k])); });
    var gut = el('div', 'cal-gut');
    for (var h = 1; h < 24; h++) {
      var lab = el('span', 'cal-hour', new Date(2000, 0, 1, h).toLocaleTimeString([], { hour: 'numeric' }));
      lab.style.top = (h * HOUR) + 'px';
      gut.appendChild(lab);
    }
    calGrid.appendChild(gut);
    var nowMin = cal.nowMin + (conn === 'live' ? (Date.now() - cal.got) / 60000 : 0);
    cols.forEach(function (k) {
      var col = el('div', 'cal-col' + (k === cal.today ? ' today' : ''));
      cal.blocks.forEach(function (b) { if (b.d === k) col.appendChild(calBlock(b)); });
      if (k === cal.today && nowMin < 1440) { var nl = el('div', 'cal-now'); nl.style.top = (nowMin / 60 * HOUR) + 'px'; col.appendChild(nl); }
      calGrid.appendChild(col);
    });
    // open at something worth seeing: two hours before now (today) or the first block of the day / week
    var key = calSpan + '|' + (week ? cal.days[0] : calDate);
    if (calScrolled !== key) {
      calScrolled = key;
      var first = 1440;
      cal.blocks.forEach(function (b) { if (cols.indexOf(b.d) >= 0 && b.s < first) first = b.s; });
      var at = cols.indexOf(cal.today) >= 0 ? Math.max(0, nowMin - 120) : first < 1440 ? Math.max(0, first - 30) : 8 * 60;
      calScroll.scrollTop = at / 60 * HOUR;
      if (week) calScroll.scrollLeft = cal.today >= 0 ? Math.max(0, cal.today * 112 - 40) : 0;
    }
    if (calOpen) showCalSheet();
  }

  function calBlock(b) {
    var cls = 'cal-b ' + (b.cat === 'toahnipi' ? 'tn' : 'other') + (b.pl ? ' plan' : '') + (b.st === 'running' || b.st === 'waiting' ? ' live' : '') +
      (b.unk ? ' unk' : '') + (b.fp ? ' fp' : '') + (b.tn ? ' tnx' : '') + (b.pl && /^(missed|canceled|skipped|nostart)$/.test(b.st) ? ' bad' : '') + (b.k === calOpen ? ' sel' : '');
    var x = el('button', cls);
    x.type = 'button';
    var top = b.s / 60 * HOUR, height = Math.max(b.e - b.s, cal.minV || 12) / 60 * HOUR;
    x.style.top = top + 'px';
    x.style.height = Math.max(8, height - 1) + 'px';
    x.style.left = 'calc(' + (b.c / b.n * 100) + '% + 2px)';
    x.style.width = 'calc(' + (100 / b.n) + '% - 4px)';
    (b.w || []).forEach(function (w) {
      var wt = el('span', 'cal-w');
      wt.style.top = ((w[0] - b.s) / 60 * HOUR) + 'px';
      wt.style.height = Math.max(2, (w[1] - w[0]) / 60 * HOUR) + 'px';
      if ((w[1] - w[0]) / 60 * HOUR >= 16) wt.appendChild(el('span', '', 'WAITING'));
      x.appendChild(wt);
    });
    x.appendChild(el('span', 'cal-t1', b.nm));
    if (height >= 30) x.appendChild(el('span', 'cal-t2', b.l2));
    if (b.ti && height >= 46) x.appendChild(el('span', 'cal-t3', b.ti));
    x.setAttribute('aria-label', b.nm + ', ' + (b.cat === 'toahnipi' ? 'Toah Nipi' : 'other project') + ', ' + b.l2 + (b.ti ? ', ' + b.ti : ''));
    x.addEventListener('click', function () { calOpen = b.k; renderCal(); });
    return x;
  }

  function showCalSheet() {
    var d = cal && cal.info ? cal.info[calOpen] : null;
    if (!d) { calSheet.hidden = true; calOpen = ''; return; }
    $('cal-sh-title').textContent = d.h;
    $('cal-sh-status').textContent = d.st;
    var dl = $('cal-sh-facts');
    dl.textContent = '';
    (d.f || []).forEach(function (f) { dl.appendChild(el('dt', '', f[0])); dl.appendChild(el('dd', '', f[1])); });
    $('cal-sh-note').hidden = !d.nt;
    $('cal-sh-note').textContent = d.nt || '';
    var open = $('cal-sh-open');
    open.hidden = !d.q;
    open.onclick = function () { schedOpen = d.q; calOpen = ''; setView('sched', true); window.scrollTo(0, schedEl.offsetTop); };
    var was = calSheet.hidden;
    calSheet.hidden = false;
    if (was) $('cal-sh-close').focus();
  }

  $('cal-sh-close').addEventListener('click', function () { calOpen = ''; calSheet.hidden = true; renderCal(); });
  $('cal-prev').addEventListener('click', function () { calGo(calSpan === 'week' ? -7 : -1); });
  $('cal-next').addEventListener('click', function () { calGo(calSpan === 'week' ? 7 : 1); });
  $('cal-today').addEventListener('click', function () { calDate = ''; calOpen = ''; calSheet.hidden = true; calScrolled = ''; cal = null; renderCal(); loadCal(); });
  calSpanBtns.forEach(function (b) { b.addEventListener('click', function () { if (calSpan !== b.dataset.span) { calSpan = b.dataset.span; calScrolled = ''; renderCal(); } }); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !calSheet.hidden) { calOpen = ''; calSheet.hidden = true; renderCal(); } });

  // ---------- display settings popover (gear) ----------
  var gearBtn = $('gear'), settingsEl = $('settings');
  function setSettings(open) {
    settingsEl.hidden = !open;
    gearBtn.setAttribute('aria-expanded', String(open));
  }
  gearBtn.addEventListener('click', function () { setSettings(settingsEl.hidden); });
  document.addEventListener('click', function (e) {
    if (!settingsEl.hidden && !settingsEl.contains(e.target) && !gearBtn.contains(e.target)) setSettings(false);
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !settingsEl.hidden) { setSettings(false); gearBtn.focus(); } });

  // A whole number of device pixels per creature square (e.g. 3 CSS px is 8 device px at a 2.625 pixel
  // ratio, not 7.875), so every square of the art is the same crisp size.
  function snapCells() {
    var d = window.devicePixelRatio || 1;
    [3, 4, 5].forEach(function (n) { document.documentElement.style.setProperty('--c' + n, Math.max(1, Math.round(n * d)) / d + 'px'); });
  }
  snapCells();
  window.addEventListener('resize', snapCells);

  function applyPetOptions() {
    compactToggle.setAttribute('aria-pressed', String(compact));
    contextToggle.setAttribute('aria-pressed', String(contextShown));
    petsEl.classList.toggle('compact', compact);
    petsEl.classList.toggle('ctx', contextShown);
  }
  compactToggle.addEventListener('click', function () {
    compact = !compact;
    try { localStorage.setItem(COMPACT_KEY, compact ? '1' : '0'); } catch (e) { }
    applyPetOptions();
  });
  contextToggle.addEventListener('click', function () {
    contextShown = !contextShown;
    try { localStorage.setItem(CONTEXT_KEY, contextShown ? 'shown' : 'hidden'); } catch (e) { }
    applyPetOptions();
  });

  // ---------- creatures view ----------
  var SVGNS = 'http://www.w3.org/2000/svg';

  function drawLayers(parent, layers) {
    layers.forEach(function (l) {
      var g = document.createElementNS(SVGNS, 'g');
      g.setAttribute('class', l[0]);
      if (l[0] === 'fig') drawLayers(g, l[1]);
      else {
        // one rect per horizontal run of same-coloured squares
        var px = l[1].slice().sort(function (a, b) { return a[1] - b[1] || a[0] - b[0]; });
        for (var i = 0; i < px.length;) {
          var j = i + 1;
          while (j < px.length && px[j][1] === px[i][1] && px[j][2] === px[i][2] && px[j][0] === px[j - 1][0] + 1) j++;
          var r = document.createElementNS(SVGNS, 'rect');
          r.setAttribute('x', px[i][0]); r.setAttribute('y', px[i][1]);
          r.setAttribute('width', j - i); r.setAttribute('height', 1);
          r.setAttribute('fill', px[i][2]);
          g.appendChild(r);
          i = j;
        }
      }
      parent.appendChild(g);
    });
  }

  function creatureSvg(look) {
    var svg = document.createElementNS(SVGNS, 'svg');
    svg.setAttribute('viewBox', '0 0 16 16');
    svg.setAttribute('class', 'px');
    svg.setAttribute('shape-rendering', 'crispEdges');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('focusable', 'false');
    drawLayers(svg, creatureLayers(look));
    return svg;
  }

  function clearPets() { petsEl.textContent = ''; petEls = {}; }

  function petPlaceholder(text, sleeping) {
    clearPets();
    var li = el('li', 'card placeholder' + (sleeping ? ' sleep asleep' : ''));
    if (sleeping) { var s = el('span', 'sprite'); s.appendChild(creatureSvg(0)); li.appendChild(s); }
    else li.appendChild(el('span', 'ring'));
    li.appendChild(el('span', 'cname', text));
    petsEl.appendChild(li);
    hintEl.hidden = true;
  }

  // Updates cards in place (by worker token) and only moves the ones whose position changed.
  function renderPets(rows) {
    if (!data) { petPlaceholder(conn === 'offline' ? "Can't reach Claude Status on the laptop" : 'Connecting to the laptop…', false); return; }
    if (!rows.length) { petPlaceholder('No Claude workers', true); return; }
    var keep = {};
    rows.forEach(function (r) { keep[r.k] = true; });
    [].slice.call(petsEl.querySelectorAll('.placeholder')).forEach(function (p) { p.remove(); });
    Object.keys(petEls).forEach(function (k) {
      if (!keep[k] || petEls[k].look !== rows.filter(function (r) { return r.k === k; })[0].c) { petEls[k].li.remove(); delete petEls[k]; }
    });
    rows.forEach(function (r, i) {
      var e = petEls[r.k];
      if (e) updatePet(e, r, true);
      else e = petEls[r.k] = buildPet(r);
      var here = petsEl.children[i];
      if (here !== e.li) petsEl.insertBefore(e.li, here || null);
    });
    hintEl.hidden = false;
  }

  function buildPet(r) {
    var li = el('li', 'card dl' + (r.c % 5));
    li.dataset.look = r.c;
    var pet = el('button', 'pet'), sprite = el('span', 'sprite');
    pet.type = 'button';
    pet.title = r.n;
    pet.setAttribute('aria-describedby', 'pet-hint');
    sprite.appendChild(creatureSvg(r.c));
    var name = el('span', 'cname'), st = el('span', 'cstatus'), word = el('span', 'word'), rest = el('span', 'rest');
    st.appendChild(word); st.appendChild(rest);
    // this session's context window, under everything else (only shown with Show context on)
    var ctx = el('span', 'cctx'), ctxText = el('span', 'ctext'), ctxBar = bar('ctrack', 0);
    ctx.appendChild(ctxText); ctx.appendChild(ctxBar);
    pet.appendChild(sprite); pet.appendChild(name); pet.appendChild(st); pet.appendChild(ctx);
    li.appendChild(pet);
    var e = { r: r, li: li, pet: pet, sprite: sprite, name: name, word: word, rest: rest, x: null, look: r.c, ctx: ctx, ctxText: ctxText, ctxFill: ctxBar.firstChild };
    pet.addEventListener('click', function () { activate(e); });
    li.addEventListener('animationend', function (ev) {
      if (ev.animationName === 'boing') sprite.classList.remove('boing');
      if (ev.animationName === 'hop' || ev.animationName === 'shake') li.classList.remove('celebrate', 'shake');
    });
    updatePet(e, r, false);
    return e;
  }

  function updatePet(e, r, observed) {
    var was = e.r.s;
    e.r = r;
    ['WORKING', 'NEEDS_YOU', 'DONE', 'ERROR'].forEach(function (s) { e.li.classList.toggle('s-' + s, r.s === s); });
    // a brief flourish when a worker finishes or fails while you're watching (not on load or view switch)
    if (observed && was !== r.s && conn === 'live') {
      if (r.s === 'DONE') e.li.classList.add('celebrate');
      else if (r.s === 'ERROR') e.li.classList.add('shake');
    }
    if (r.s !== 'DONE') e.li.classList.remove('celebrate');
    if (r.s !== 'ERROR') e.li.classList.remove('shake');
    e.li.classList.toggle('busy', !!pending[r.k]);
    e.li.classList.toggle('asleep', isAsleep(r.s, r.t, boardNow()));
    var cx = contextInfo(r.x);
    e.ctxText.textContent = cx.text;
    e.ctxFill.style.width = (cx.p == null ? 0 : cx.p) + '%';
    e.ctxFill.style.minWidth = cx.p > 0 && cx.p < 3 ? '2px' : '';
    e.ctx.classList.toggle('na', cx.p == null);
    e.ctx.classList.toggle('old', cx.old);
    e.ctx.classList.toggle('hi', cx.p != null && cx.p >= 80 && cx.p < 95);
    e.ctx.classList.toggle('max', cx.p != null && cx.p >= 95);
    e.ctx.title = cx.p == null ? "Claude Code hasn't reported this session's context window"
      : cx.old ? "Last reported before this worker's latest activity" : "This session's context window";
    e.name.textContent = r.n.replace(/ (#\d+)$/, ' $1');
    if (r.s === 'DONE' && r.d) {
      if (!e.x) {
        // a separate control from the creature: it only ever dismisses
        e.x = el('button', 'x', '×');
        e.x.type = 'button';
        e.x.addEventListener('click', function (ev) { ev.stopPropagation(); dismiss(e.r, e.li, e.x); });
        e.li.appendChild(e.x);
      }
      e.x.setAttribute('aria-label', 'Dismiss ' + r.n + ' (only removes it from the board; Claude is not affected)');
    } else if (e.x) { e.x.remove(); e.x = null; }
  }

  // ---------- tap a creature: react at once, then ask the laptop to bring up that worker's window ----------
  function react(e) {
    if (reduceMotion.matches) {
      e.pet.classList.add('flash');
      setTimeout(function () { e.pet.classList.remove('flash'); }, 250);
    } else {
      e.sprite.classList.remove('boing');
      void e.sprite.offsetWidth;   // restart the hop if it's still running
      e.sprite.classList.add('boing');
    }
  }

  function activate(e) {
    react(e);
    var r = e.r;
    if (focusing) return;                                               // one request at a time
    if (r.k === lastFocusKey && Date.now() - lastFocusAt < 1500) return; // repeated taps: already done
    if (conn !== 'live') { note("Can't bring it up: the laptop isn't connected."); return; }
    focusing = r.k;
    e.li.classList.add('focusing');
    e.pet.setAttribute('aria-busy', 'true');
    var c = new AbortController();
    var timer = setTimeout(function () { c.abort(); }, 12000);
    fetch('api/focus', {
      method: 'POST', cache: 'no-store', credentials: 'same-origin', signal: c.signal,
      headers: { 'Content-Type': 'application/json', 'X-Claude-Status': 'focus' },
      body: JSON.stringify({ k: r.k })
    })
      .then(function (res) { return res.json().catch(function () { return {}; }); })
      .then(function (j) { note(focusText(j.result, r.n)); if (j.result === 'gone') poll(false); })
      .catch(function () { note('No answer from the laptop, so it may not have come up.'); })
      .then(function () {
        clearTimeout(timer);
        focusing = null; lastFocusKey = r.k; lastFocusAt = Date.now();
        e.li.classList.remove('focusing');
        e.pet.removeAttribute('aria-busy');
      });
  }

  // Only "focused" means Windows confirmed the window is now in front.
  function focusText(result, name) {
    var q = '“' + name + '”';
    switch (result) {
      case 'focused': return 'Brought up ' + q + ' on the laptop.';
      case 'refused': return "Windows didn't allow bringing " + q + ' forward.';
      case 'locked': return 'The laptop is locked. Unlock it to switch windows.';
      case 'not_yet': return "Couldn't identify that worker's window yet. It will work after its next prompt.";
      case 'ended': return 'That Claude session is no longer running.';
      case 'ambiguous': return "Couldn't tell which window is " + q + '. Send it a prompt from its window once, then try again.';
      case 'unknown': return "Couldn't identify that worker's window.";
      case 'gone': return 'That worker is no longer on the board.';
      case 'busy': return 'Still bringing up another worker. Try again in a moment.';
      default: return "The laptop couldn't do that right now.";
    }
  }

  // ---------- dismiss ----------
  // Sent once, only while live; never queued or retried. The laptop re-checks that this exact DONE
  // occurrence is still current, so a stale tap can never hide a worker that has started again.
  function dismiss(r, li, x) {
    if (conn !== 'live' || pending[r.k]) return;
    pending[r.k] = true;
    x.disabled = true;
    li.classList.add('busy');
    var c = new AbortController();
    var timer = setTimeout(function () { c.abort(); }, 10000);
    fetch('api/dismiss', {
      method: 'POST', cache: 'no-store', credentials: 'same-origin', signal: c.signal,
      headers: { 'Content-Type': 'application/json', 'X-Claude-Status': 'dismiss' },
      body: JSON.stringify({ k: r.k, d: r.d })
    })
      .then(function (res) { return res.json().catch(function () { return {}; }); })
      .then(function (j) {
        delete pending[r.k];
        if (j.result === 'dismissed' || j.result === 'gone') dismissed[r.k + '|' + r.d] = true;
        else if (j.result === 'changed' || j.result === 'not_done') note('Not dismissed: that worker has changed. Showing the latest board.');
        else note('Not dismissed: the laptop refused the request.');
        render(true);
        poll(false);
      })
      .catch(function () {
        delete pending[r.k];
        note("Not dismissed: couldn't reach the laptop.");
        render(true);
      })
      .then(function () { clearTimeout(timer); });
  }

  // ---------- notifications (Web Push; optional) ----------
  // The laptop sends each NEEDS YOU / DONE once to this phone's push service, which wakes sw.js to show it
  // even with this page closed. Needs this page's HTTPS address. Only the laptop's public key reaches the
  // page; this phone's settings live on the laptop, under this phone's own push address.
  var NOTIFY_OPEN_KEY = 'claudeStatus.notifyOpen', NOTIFY_KEY = 'claudeStatus.notify';
  var notifyOpen = false;
  try { notifyOpen = localStorage.getItem(NOTIFY_OPEN_KEY) === '1'; } catch (e) { }
  var nSum = $('notify-sum'), nMini = $('notify-mini'), nDetail = $('notify-detail'), nState = $('notify-state'), nMsg = $('notify-msg');
  var nPrefsEl = $('notify-prefs'), nNeeds = $('notify-needs'), nDone = $('notify-done');
  var nEnable = $('notify-enable'), nTest = $('notify-test'), nOff = $('notify-off');
  var nSt = 'checking', nInfo = '', nPrefs = { needs: true, done: true }, nBusy = false, nEndpoint = '';
  var pushOk = !!(window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window);
  var N_LABEL = { checking: 'Checking…', unsupported: 'Not available here', denied: 'Blocked', new: 'Not set up', off: 'Off', on: 'On', error: 'Problem' };
  var N_STATE = {
    checking: 'Checking…', unsupported: 'Not supported', denied: 'Permission denied', new: 'Not yet configured',
    off: 'Disabled', on: 'Enabled', error: 'Something went wrong'
  };

  function setNotify(st, info) { nSt = st; nInfo = info || ''; renderNotify(); }

  function renderNotify() {
    nMini.textContent = '';
    nMini.appendChild(el('span', nSt === 'on' ? '' : 'muted', N_LABEL[nSt]));
    nSum.setAttribute('aria-expanded', String(notifyOpen));
    nDetail.hidden = !notifyOpen;
    nState.textContent = N_STATE[nSt];
    nState.dataset.st = nSt;
    nMsg.hidden = !nInfo;
    nMsg.textContent = nInfo;
    var on = nSt === 'on';
    nPrefsEl.hidden = !on;
    nNeeds.setAttribute('aria-pressed', String(nPrefs.needs));
    nDone.setAttribute('aria-pressed', String(nPrefs.done));
    nEnable.hidden = on || nSt === 'unsupported' || nSt === 'denied' || nSt === 'checking';
    nTest.hidden = nOff.hidden = !on;
    [nEnable, nTest, nOff, nNeeds, nDone].forEach(function (b) { b.disabled = nBusy; });
  }

  function pushApi(body) {
    var c = new AbortController(), timer = setTimeout(function () { c.abort(); }, 25000);
    return fetch('api/push', {
      method: 'POST', cache: 'no-store', credentials: 'same-origin', signal: c.signal,
      headers: { 'Content-Type': 'application/json', 'X-Claude-Status': 'push' }, body: JSON.stringify(body)
    })
      .then(function (r) { return r.json().catch(function () { return {}; }).then(function (j) { j.status = r.status; return j; }); })
      .then(function (j) { clearTimeout(timer); return j; }, function (e) { clearTimeout(timer); throw e; });
  }

  function remembered(v) { try { if (v) localStorage.setItem(NOTIFY_KEY, v); return localStorage.getItem(NOTIFY_KEY) || ''; } catch (e) { return ''; } }

  function currentSub() {
    return navigator.serviceWorker.getRegistration().then(function (r) { return r ? r.pushManager.getSubscription() : null; });
  }

  function checkNotify() {
    if (!pushOk) {
      setNotify('unsupported', location.protocol !== 'https:' ? 'Notifications need the HTTPS address of this dashboard (see phone-setup.ps1 -Https).' : "This browser can't receive push notifications.");
      return;
    }
    if (Notification.permission === 'denied') {
      setNotify('denied', 'Notifications are blocked for this site. Allow them in the browser’s site settings, then reload this page.');
      return;
    }
    currentSub().then(function (sub) {
      if (!sub) { nEndpoint = ''; setNotify(remembered() === 'off' ? 'off' : 'new', ''); return; }
      nEndpoint = sub.endpoint;
      return pushApi({ op: 'status', endpoint: sub.endpoint }).then(function (j) {
        if (j.known && Notification.permission === 'granted') {
          nPrefs = { needs: !!j.needs, done: !!j.done };
          setNotify('on', j.last ? 'Last message: ' + j.last + '.' : '');
        } else setNotify('new', j.known ? '' : 'The laptop doesn’t have this phone registered (any more). Tap Enable.');
      });
    }).catch(function () { setNotify('error', "Couldn't check notifications with the laptop."); });
  }

  function keyBytes(b64) {
    var s = b64.replace(/-/g, '+').replace(/_/g, '/'); while (s.length % 4) s += '=';
    var raw = atob(s), out = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
    return out;
  }

  function sameKey(buf, bytes) {
    if (!buf) return false;
    var a = new Uint8Array(buf);
    if (a.length !== bytes.length) return false;
    for (var i = 0; i < a.length; i++) if (a[i] !== bytes[i]) return false;
    return true;
  }

  // "accepted" only means the push service took the message; whether it shows is up to this phone.
  function testText(result, last) {
    switch (result) {
      case 'accepted': return 'Test sent: the push service accepted it. It should appear on this phone within a few seconds; if it doesn’t, see the phone’s battery settings for this browser.';
      case 'rejected': return 'The push service rejected the test' + (last ? ' (' + last + ')' : '') + '.';
      case 'unreachable': return "The laptop couldn't reach the push service. Check the laptop's internet connection.";
      case 'expired': return 'The push service says this phone’s registration has expired. Tap Enable again.';
      case 'too_soon': return 'A test was just sent. Try again in a few seconds.';
      default: return "The laptop couldn't send a test.";
    }
  }

  function busy(on) { nBusy = on; renderNotify(); }

  function enableNotify() {
    if (nBusy || !pushOk) return;
    busy(true);
    // asked straight from the tap, as Android requires
    Notification.requestPermission().then(function (perm) {
      if (perm !== 'granted') {
        if (perm === 'denied') setNotify('denied', 'Notifications are blocked for this site. Allow them in the browser’s site settings, then reload this page.');
        else setNotify('new', 'Notifications weren’t allowed. Tap Enable to try again.');
        return;
      }
      return Promise.all([
        navigator.serviceWorker.register('sw.js').then(function () { return navigator.serviceWorker.ready; }),
        pushApi({ op: 'key' })
      ]).then(function (a) {
        var reg = a[0], key = a[1] && a[1].key;
        if (!key) throw new Error('the laptop has no notification key');
        var bytes = keyBytes(key);
        return reg.pushManager.getSubscription().then(function (old) {
          // after a reinstall the laptop has a new key: the old registration can't be used with it
          if (old && !sameKey(old.options.applicationServerKey, bytes)) return old.unsubscribe().then(function () { return null; });
          return old;
        }).then(function (old) { return old || reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: bytes }); });
      }).then(function (sub) {
        var j = sub.toJSON();
        nEndpoint = j.endpoint;
        return pushApi({ op: 'subscribe', endpoint: j.endpoint, p256dh: j.keys.p256dh, auth: j.keys.auth, needs: nPrefs.needs, done: nPrefs.done });
      }).then(function (j) {
        if (j.result !== 'subscribed') {
          throw new Error(j.result === 'full' ? 'the laptop already has the most phones it allows (10)' : j.result === 'bad_endpoint' ? "this browser's push service isn't one Claude Status sends to" : 'the laptop refused the registration');
        }
        remembered('on');
        setNotify('on', 'Enabled. ' + testText(j.test, j.last));
      });
    }).catch(function (e) {
      setNotify('error', "Couldn't turn on notifications: " + (e && e.message ? e.message : 'no answer from the laptop') + '.');
    }).then(function () { busy(false); });
  }

  function savePrefs(next) {
    if (nBusy || !nEndpoint) return;
    var before = nPrefs;
    nPrefs = next;
    busy(true);
    pushApi({ op: 'prefs', endpoint: nEndpoint, needs: next.needs, done: next.done })
      .then(function (j) {
        if (j.result === 'saved') return;
        nPrefs = before;
        if (j.result === 'unknown') setNotify('new', 'The laptop doesn’t have this phone registered (any more). Tap Enable.');
        else note("Not saved: the laptop refused the change.");
      })
      .catch(function () { nPrefs = before; note("Not saved: couldn't reach the laptop."); })
      .then(function () { busy(false); });
  }

  function sendTest() {
    if (nBusy || !nEndpoint) return;
    busy(true);
    pushApi({ op: 'test', endpoint: nEndpoint })
      .then(function (j) {
        if (j.result === 'unknown' || j.result === 'expired') setNotify('new', testText(j.result, j.last));
        else nInfo = testText(j.result, j.last);
      })
      .catch(function () { nInfo = "Couldn't reach the laptop."; })
      .then(function () { busy(false); });
  }

  function turnOff() {
    if (nBusy) return;
    busy(true);
    var ep = nEndpoint;
    (ep ? pushApi({ op: 'unsubscribe', endpoint: ep }).catch(function () { }) : Promise.resolve())
      .then(currentSub)
      .then(function (sub) { return sub ? sub.unsubscribe() : true; })
      .then(function () { nEndpoint = ''; remembered('off'); setNotify('off', 'Turned off. This phone gets no notifications.'); })
      .catch(function () { setNotify('error', "Couldn't fully turn off notifications. Try again."); })
      .then(function () { busy(false); });
  }

  nSum.addEventListener('click', function () {
    notifyOpen = !notifyOpen;
    try { localStorage.setItem(NOTIFY_OPEN_KEY, notifyOpen ? '1' : '0'); } catch (e) { }
    renderNotify();
  });
  nEnable.addEventListener('click', enableNotify);
  nTest.addEventListener('click', sendTest);
  nOff.addEventListener('click', turnOff);
  nNeeds.addEventListener('click', function () { savePrefs({ needs: !nPrefs.needs, done: nPrefs.done }); });
  nDone.addEventListener('click', function () { savePrefs({ needs: nPrefs.needs, done: !nPrefs.done }); });
  document.addEventListener('visibilitychange', function () { if (!document.hidden && !nBusy) checkNotify(); });   // e.g. permission changed meanwhile

  // A tapped notification opens "#k=<row token>": that worker is scrolled to and briefly highlighted.
  var reveal = '';
  function takeHash() {
    var m = /^#k=([A-Za-z0-9_-]{22})$/.exec(location.hash);
    if (!m) return;
    reveal = m[1];
    try { history.replaceState(null, '', location.pathname + location.search); } catch (e) { }
  }
  function showReveal() {
    var k = reveal, target = null;
    reveal = '';   // once: a token from before a laptop restart simply matches nothing
    rowEls.forEach(function (e) { if (e.r.k === k) target = e.li; });
    if (!target && petEls[k]) target = petEls[k].pet;
    if (!target) return;
    target.scrollIntoView({ block: 'center', behavior: reduceMotion.matches ? 'auto' : 'smooth' });
    target.classList.add('flash');
    setTimeout(function () { target.classList.remove('flash'); }, 2500);
  }
  window.addEventListener('hashchange', function () { takeHash(); if (reveal && data) render(true); });
  if ('serviceWorker' in navigator) navigator.serviceWorker.addEventListener('message', function (e) {
    if (e.data && typeof e.data.k === 'string' && /^[A-Za-z0-9_-]{22}$/.test(e.data.k)) { reveal = e.data.k; if (data) render(true); }
  });
  takeHash();

  function note(text) {
    noteEl.textContent = text;
    noteEl.hidden = false;
    clearTimeout(noteTimer);
    noteTimer = setTimeout(function () { noteEl.hidden = true; }, 4000);
  }

  applyPetOptions();
  setView(view, false);
  renderNotify();
  checkNotify();
  poll(false);
})();
