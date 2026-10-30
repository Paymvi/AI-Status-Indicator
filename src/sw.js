// Claude Status - phone notifications (service worker). The laptop sends each NEEDS YOU / DONE transition to
// this phone's push service (see push.cs); the browser wakes this worker to show it, even with the dashboard
// closed. It only shows what arrives; it never fetches anything and handles no page requests.
'use strict';

var SEEN_MS = 24 * 3600 * 1000;

function openDb() {
  return new Promise(function (resolve, reject) {
    var r = indexedDB.open('claude-status-push', 1);
    r.onupgradeneeded = function () { r.result.createObjectStore('seen'); };
    r.onsuccess = function () { resolve(r.result); };
    r.onerror = function () { reject(r.error); };
  });
}

// Best-effort duplicate filter: true unless this transition id was shown in the last 24 h. The laptop sends each
// transition once, but a retry after a crash could repeat one; browser storage can be cleared or evicted, so
// this is protection, not a guarantee. Any storage failure shows the notification.
function firstTime(id) {
  return openDb().then(function (db) {
    return new Promise(function (resolve) {
      var tx = db.transaction('seen', 'readwrite'), store = tx.objectStore('seen'), now = Date.now(), fresh = true;
      store.get(id).onsuccess = function (e) {
        var t = e.target.result;
        fresh = !(typeof t === 'number' && now - t < SEEN_MS);
        if (fresh) store.put(now, id);
      };
      store.openCursor().onsuccess = function (e) {
        var c = e.target.result;
        if (!c) return;
        if (now - c.value >= SEEN_MS) c.delete();
        c.continue();
      };
      tx.oncomplete = function () { db.close(); resolve(fresh); };
      tx.onerror = tx.onabort = function () { db.close(); resolve(true); };
    });
  }).catch(function () { return true; });
}

self.addEventListener('install', function () { self.skipWaiting(); });
self.addEventListener('activate', function (e) { e.waitUntil(self.clients.claim()); });

self.addEventListener('push', function (e) {
  var m = null;
  try { m = e.data ? e.data.json() : null; } catch (x) { }
  if (!m || m.v !== 1 || typeof m.id !== 'string' || typeof m.title !== 'string') return;
  var k = typeof m.k === 'string' && /^[A-Za-z0-9_-]{22}$/.test(m.k) ? m.k : '';
  e.waitUntil(firstTime(m.id).then(function (fresh) {
    if (!fresh) return;
    // One notification per worker: a newer one for the same worker replaces the older, and still alerts.
    return self.registration.showNotification(m.title, {
      body: typeof m.body === 'string' ? m.body : '',
      tag: 'claude-' + (k || m.id),
      renotify: true,
      data: { k: k }
    });
  }));
});

// Opens the dashboard (or brings an open one forward) and points it at that worker.
self.addEventListener('notificationclick', function (e) {
  e.notification.close();
  var k = e.notification.data && e.notification.data.k || '';
  var url = new URL(k ? './#k=' + k : './', self.registration.scope).href;
  e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (list) {
    for (var i = 0; i < list.length; i++) {
      if (list[i].url.indexOf(self.registration.scope) === 0 && 'focus' in list[i]) {
        list[i].postMessage({ k: k });
        return list[i].focus();
      }
    }
    return self.clients.openWindow(url);
  }));
});
