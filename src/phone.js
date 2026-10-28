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

  if (typeof module === 'object' && module.exports) module.exports = { clock: clock, span: span, ago: ago, statusText: statusText };
  if (typeof document === 'undefined') return;

  // ---------- connection ----------
  var STALE_MS = 30000;     // a live long-poll answers at least every ~20 s
  var OFFLINE_MS = 15000;   // failing this long (or 3 times in a row) = laptop unreachable
  var $ = function (id) { return document.getElementById(id); };
  var boardEl = $('board'), connEl = $('conn'), summaryEl = $('summary'), rowsEl = $('rows'), noteEl = $('note');
  var bannerEl = $('banner'), bannerTitle = $('banner-title'), bannerWhen = $('banner-when'), bannerWhy = $('banner-why');

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

    if (full) {
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
    tick();
  }

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

  render(true);
  poll(false);
})();
