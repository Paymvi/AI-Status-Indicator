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

  if (typeof module === 'object' && module.exports) module.exports = { clock: clock, span: span, ago: ago, statusText: statusText, HATS: HATS, BODIES: BODIES, creatureLayers: creatureLayers, countdown: countdown, percentText: percentText, remainingText: remainingText, isAsleep: isAsleep, SLEEP_MS: SLEEP_MS, contextInfo: contextInfo };
  if (typeof document === 'undefined') return;

  // ---------- connection ----------
  var STALE_MS = 30000;     // a live long-poll answers at least every ~20 s
  var OFFLINE_MS = 15000;   // failing this long (or 3 times in a row) = laptop unreachable
  var $ = function (id) { return document.getElementById(id); };
  var boardEl = $('board'), connEl = $('conn'), summaryEl = $('summary'), rowsEl = $('rows'), noteEl = $('note');
  var bannerEl = $('banner'), bannerTitle = $('banner-title'), bannerWhen = $('banner-when'), bannerWhy = $('banner-why');
  var creaturesEl = $('creatures'), petsEl = $('pets'), hintEl = $('pet-hint'), viewBtns = [$('view-list'), $('view-creatures')];

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
  try { if (localStorage.getItem(VIEW_KEY) === 'creatures') view = 'creatures'; } catch (e) { }

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
  }

  function failed(e, aborted) {
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

    if (full && view === 'creatures') {
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
    if (lim.length && st === 'stale') usageMini.appendChild(el('span', 'tag', 'stale'));

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
  }

  // ---------- view switch ----------
  function setView(v, remember) {
    view = v === 'creatures' ? 'creatures' : 'list';
    viewBtns.forEach(function (b) { b.setAttribute('aria-pressed', String(b.dataset.view === view)); });
    rowsEl.hidden = view !== 'list';
    creaturesEl.hidden = view !== 'creatures';
    petbarEl.hidden = view !== 'creatures';
    if (remember) try { localStorage.setItem(VIEW_KEY, view); } catch (e) { }
    render(true);   // same data, same connection: only the presentation changes
  }
  viewBtns.forEach(function (b) { b.addEventListener('click', function () { if (b.dataset.view !== view) setView(b.dataset.view, true); }); });

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

  function note(text) {
    noteEl.textContent = text;
    noteEl.hidden = false;
    clearTimeout(noteTimer);
    noteTimer = setTimeout(function () { noteEl.hidden = true; }, 4000);
  }

  applyPetOptions();
  setView(view, false);
  poll(false);
})();
