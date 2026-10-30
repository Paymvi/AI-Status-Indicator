// Claude Status - phone push notifications (Web Push: RFC 8030 delivery, RFC 8291 encryption, RFC 8292 VAPID).
// Compiled together with indicator.cs and phone.cs (C# 5). Optional: nothing is stored or sent until a phone
// turns notifications on from the phone dashboard.
//
// After every board reload the indicator hands the board to Observe(). A worker entering NEEDS YOU or DONE is
// one transition, identified by session + state + state_since (hook.ps1 keeps state_since while a state
// repeats and resets it on every real change, so refreshes, polling and repeated hook events never look new).
// Each transition is recorded once in a ledger and, only if it began less than EligibleMinutes ago, queued for
// every phone that wants that kind. The queue is saved before anything is sent and retried until the push
// service accepts it, it expires (LifetimeMinutes, also across restarts) or it's no longer true.
//
// What leaves the laptop: one HTTPS POST per notification to that phone's push service (for Chrome on Android,
// Google's FCM). The body is encrypted to the phone, so the push service can't read it. It holds the title,
// the worker's display name, a short reason, the state and two opaque tokens; never prompts, transcripts,
// paths, session IDs or code. Keys, subscriptions, preferences, ledger and queue live in one file,
// state\push.dat, encrypted with Windows DPAPI for the current Windows user.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeStatus
{
    public class PushSub
    {
        public string Endpoint = "", P256dh = "", Auth = "", Last = "";
        public bool Needs = true, Done = true;
    }

    public class PushItem
    {
        public string Tid = "", Endpoint = "", Session = "", State = "", Title = "", Body = "", Row = "";
        public long Since, Created, NextTry;   // Since = state_since ticks; Created / NextTry = Unix ms
        public int Tries;
    }

    // The whole of state\push.dat (before encryption).
    public class PushState
    {
        public int V = 1;
        public string Key = "", Pub = "", Secret = "";
        public List<PushSub> Subs = new List<PushSub>();
        public Dictionary<string, long> Sent = new Dictionary<string, long>();   // transition id -> recorded (Unix ms)
        public List<PushItem> Queue = new List<PushItem>();
    }

    public sealed class PushNotifier
    {
        public const int EligibleMinutes = 10, LifetimeMinutes = 60, LedgerHours = 24, MaxSubs = 10, TtlSeconds = 3600, TestEverySeconds = 10;
        public const int Unknown = -1, TooSoon = -2;   // Test() results besides HTTP status codes (0 = unreachable)
        // RFC 8292 contact (optional there). Non-identifying; FCM's acceptance is checked on the first test send.
        public const string Contact = "mailto:claude-status@localhost";
        static readonly int[] RetrySeconds = { 5, 30, 120, 300 };
        static readonly string[] PushHosts = { "fcm.googleapis.com", ".push.services.mozilla.com", ".push.apple.com", ".notify.windows.com" };
        static readonly byte[] Entropy = Encoding.ASCII.GetBytes("ClaudeStatus.Push.v1");
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        readonly string path;
        readonly object gate = new object();
        PushState st = new PushState();
        Dictionary<string, string> current;   // session id -> "STATE|state_since ticks" on the latest board; null until the first
        List<SessionInfo> lastBoard = new List<SessionInfo>();
        readonly Dictionary<string, DateTime> lastTest = new Dictionary<string, DateTime>();
        readonly HashSet<PushItem> inFlight = new HashSet<PushItem>();
        Thread worker;
        volatile bool running;
        bool kick;

        // Sends one message: (subscription, payload JSON, Topic) -> the push service's HTTP status, 0 if unreachable.
        // Tests replace it; the default encrypts and POSTs (Send).
        public Func<PushSub, string, string, int> Transport;
        public string Error { get; private set; }
        public string LastReply { get; private set; }   // short reason text from the push service's last non-2xx answer

        public PushNotifier(string path)
        {
            this.path = path;
            Transport = Send;
            Error = ""; LastReply = "";
            Load();
        }

        public void Start()
        {
            if (running) return;
            running = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "ClaudeStatusPush" };
            worker.Start();
        }

        public void Stop() { running = false; lock (gate) { kick = true; Monitor.PulseAll(gate); } }

        // ---------- transitions ----------
        public static string Occurrence(SessionInfo s) { return s.State + "|" + s.StateSince.Ticks; }

        // rowKey: session id -> the phone board's token for that row (so a tap can show it), or null.
        public void Observe(List<SessionInfo> board, DateTime nowUtc, Func<string, string> rowKey)
        {
            lock (gate)
            {
                var cur = new Dictionary<string, string>();
                foreach (var s in board) cur[s.Id] = Occurrence(s);
                current = cur;
                lastBoard = new List<SessionInfo>(board);
                if (st.Subs.Count == 0) return;   // nothing is recorded until a phone turns notifications on
                long now = Ms(nowUtc);
                bool changed = PruneLedger(now);
                foreach (var s in board)
                {
                    if (s.State != Board.NeedsYou && s.State != Board.Done) continue;
                    string tid = Tid(s);
                    if (st.Sent.ContainsKey(tid)) continue;
                    st.Sent[tid] = now; changed = true;
                    // Older than the catch-up window (e.g. it happened while this app was closed): recorded, not sent.
                    if ((nowUtc - s.StateSince).TotalMinutes > EligibleMinutes) continue;
                    string row = "";
                    if (rowKey != null) try { row = rowKey(s.Id) ?? ""; } catch { }
                    foreach (var sub in st.Subs)
                        if (Wants(sub, s.State))
                            st.Queue.Add(new PushItem { Tid = tid, Endpoint = sub.Endpoint, Session = s.Id, State = s.State, Since = s.StateSince.Ticks,
                                Title = Title(s.State), Body = Body(s), Row = row, Created = now, NextTry = now });
                }
                if (changed) Save();
                kick = true; Monitor.PulseAll(gate);
            }
        }

        static bool Wants(PushSub sub, string state) { return state == Board.NeedsYou ? sub.Needs : state == Board.Done && sub.Done; }

        // The ledger only has to outlive the catch-up window: an older transition is never sent anyway.
        bool PruneLedger(long now)
        {
            var old = new List<string>();
            foreach (var kv in st.Sent) if (now - kv.Value > LedgerHours * 3600000L) old.Add(kv.Key);
            foreach (var k in old) st.Sent.Remove(k);
            return old.Count > 0;
        }

        public static string Title(string state) { return state == Board.NeedsYou ? "Claude needs your attention" : "Claude finished a task"; }

        public static string Body(SessionInfo s)
        {
            string name = s.DisplayName != "" ? s.DisplayName : "Claude Code";
            if (s.State != Board.NeedsYou) return name;
            string why = Reason(s.Event, s.Detail);
            return why == "" ? name : name + " — " + why;
        }

        // Only what hook.ps1 stored: a Notification's type, or the tool the permission / question is about.
        public static string Reason(string evt, string detail)
        {
            detail = detail ?? "";
            if (evt == "Notification")
            {
                switch (detail)
                {
                    case "permission_prompt": return "waiting for permission";
                    case "elicitation_dialog": case "elicitation_url_dialog": case "agent_needs_input": return "waiting for your input";
                    default: return "";
                }
            }
            if (detail == "AskUserQuestion") return "has a question for you";
            return Regex.IsMatch(detail, "^[A-Za-z0-9_]{1,64}$") ? "wants to use " + detail : "";
        }

        // Opaque per-install id of one transition: 22 URL-safe Base64 characters, so it is also a valid Topic.
        string Tid(SessionInfo s) { return Token("t|" + s.Id + "|" + Occurrence(s)); }

        string Token(string value)
        {
            using (var h = new HMACSHA256(B64(st.Secret)))
                return B64u(Sub(h.ComputeHash(Encoding.UTF8.GetBytes(value)), 0, 16));
        }

        // A queued notification that is no longer true is dropped, not sent late.
        bool Obsolete(PushItem it)
        {
            string occ;
            bool has = current.TryGetValue(it.Session, out occ);
            string mine = it.State + "|" + it.Since;
            // NEEDS YOU ends when the prompt is answered (a new state) or the session ends (its file is removed).
            if (it.State == Board.NeedsYou) return !has || occ != mine;
            // DONE files are never pruned and survive SessionEnd; only a dismissal removes one. So a missing
            // DONE worker still finished (the message stays true); only a newer state of that session supersedes it.
            return has && occ != mine;
        }

        // ---------- sending ----------
        void Loop()
        {
            while (running)
            {
                int wait;
                try { wait = Pump(DateTime.UtcNow); } catch { wait = 5000; }
                lock (gate)
                {
                    if (!running) return;
                    if (!kick) Monitor.Wait(gate, wait);
                    kick = false;
                }
            }
        }

        // Sends whatever is due; returns how long until the next item is (ms). Public for the tests.
        public int Pump(DateTime nowUtc)
        {
            long now = Ms(nowUtc);
            var due = new List<KeyValuePair<PushItem, PushSub>>();
            lock (gate)
            {
                if (current == null) return 60000;   // no board yet after a restart: can't tell what's still true
                bool changed = false;
                foreach (var it in new List<PushItem>(st.Queue))
                {
                    if (inFlight.Contains(it)) continue;
                    var sub = FindSub(it.Endpoint);
                    if (sub == null || !Wants(sub, it.State) || now - it.Created > LifetimeMinutes * 60000L || Obsolete(it)) { st.Queue.Remove(it); changed = true; continue; }
                    if (it.NextTry <= now) { inFlight.Add(it); due.Add(new KeyValuePair<PushItem, PushSub>(it, sub)); }
                }
                if (changed) Save();
            }
            foreach (var d in due)
            {
                int code;
                try { code = Transport(d.Value, Payload(d.Key), d.Key.Tid); } catch { code = 0; }
                lock (gate)
                {
                    inFlight.Remove(d.Key);
                    Apply(d.Key, d.Value, code, now);
                    Save();
                }
            }
            lock (gate)
            {
                long next = long.MaxValue;
                foreach (var it in st.Queue) if (!inFlight.Contains(it)) next = Math.Min(next, it.NextTry);
                return next == long.MaxValue ? 60000 : (int)Math.Max(200, Math.Min(60000, next - Ms(DateTime.UtcNow)));
            }
        }

        // One result for one item (under gate). The board / session state is never touched here.
        void Apply(PushItem it, PushSub sub, int code, long now)
        {
            if (code >= 200 && code < 300) { st.Queue.Remove(it); sub.Last = Stamp(now) + "accepted by the push service (HTTP " + code + ")"; return; }
            if (code == 404 || code == 410) { RemoveSub(sub.Endpoint); return; }   // expired / unsubscribed / permission revoked
            if (code == 0 || code == 429 || code >= 500)
            {
                it.Tries++;
                it.NextTry = now + 1000L * RetrySeconds[Math.Min(it.Tries - 1, RetrySeconds.Length - 1)];
                sub.Last = Stamp(now) + (code == 0 ? "push service unreachable" : "push service busy (HTTP " + code + ")") + "; retrying";
                return;
            }
            st.Queue.Remove(it);
            sub.Last = Stamp(now) + "rejected by the push service (HTTP " + code + (LastReply != "" ? ": " + LastReply : "") + ")";
        }

        static string Stamp(long ms) { return Epoch.AddMilliseconds(ms).ToLocalTime().ToString("HH:mm") + " "; }

        public static string Payload(PushItem it)
        {
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                { { "v", 1 }, { "id", it.Tid }, { "s", it.State }, { "title", it.Title }, { "body", it.Body }, { "k", it.Row } });
        }

        // ---------- subscriptions (called from the phone server's threads) ----------
        public string PublicKey { get { lock (gate) { EnsureKeys(); return st.Pub; } } }

        // null = subscribed; otherwise the reason ("bad_endpoint", "bad_keys", "full").
        public string Subscribe(string endpoint, string p256dh, string auth, bool needs, bool done)
        {
            if (!ValidEndpoint(endpoint)) return "bad_endpoint";
            if (!ValidKeys(p256dh, auth)) return "bad_keys";
            lock (gate)
            {
                EnsureKeys();
                var sub = FindSub(endpoint);
                if (sub == null)
                {
                    if (st.Subs.Count >= MaxSubs) return "full";
                    // First phone: what's on the board right now is old news, not something to announce.
                    if (st.Subs.Count == 0) { long now = Ms(DateTime.UtcNow); foreach (var s in lastBoard) if (s.State == Board.NeedsYou || s.State == Board.Done) st.Sent[Tid(s)] = now; }
                    sub = new PushSub { Endpoint = endpoint };
                    st.Subs.Add(sub);
                }
                sub.P256dh = p256dh; sub.Auth = auth; sub.Needs = needs; sub.Done = done;
                Save();
                return null;
            }
        }

        // A copy of that phone's subscription, or null if this laptop doesn't know it (any more).
        public PushSub Find(string endpoint)
        {
            lock (gate)
            {
                var s = FindSub(endpoint);
                return s == null ? null : new PushSub { Endpoint = s.Endpoint, Needs = s.Needs, Done = s.Done, Last = s.Last };
            }
        }

        public bool SetPrefs(string endpoint, bool needs, bool done)
        {
            lock (gate)
            {
                var s = FindSub(endpoint);
                if (s == null) return false;
                s.Needs = needs; s.Done = done;
                Save();
                return true;
            }
        }

        public bool Unsubscribe(string endpoint) { lock (gate) { bool had = FindSub(endpoint) != null; if (had) RemoveSub(endpoint); return had; } }

        public int Count { get { lock (gate) return st.Subs.Count; } }
        public int Queued { get { lock (gate) return st.Queue.Count; } }

        // Sends a fixed test message to that one phone, right away (not queued). At most one per TestEverySeconds.
        public int Test(string endpoint)
        {
            PushSub sub;
            var it = new PushItem { State = "TEST", Title = "Claude Status", Body = "Test notification from your laptop." };
            lock (gate)
            {
                var s = FindSub(endpoint);
                if (s == null) return Unknown;
                DateTime t;
                if (lastTest.TryGetValue(endpoint, out t) && (DateTime.UtcNow - t).TotalSeconds < TestEverySeconds) return TooSoon;
                lastTest[endpoint] = DateTime.UtcNow;
                sub = new PushSub { Endpoint = s.Endpoint, P256dh = s.P256dh, Auth = s.Auth };
                it.Tid = Token("test|" + Guid.NewGuid().ToString("N"));
            }
            int code;
            try { code = Transport(sub, Payload(it), it.Tid); } catch { code = 0; }
            lock (gate)
            {
                long now = Ms(DateTime.UtcNow);
                var s = FindSub(endpoint);
                if (s == null) return code;
                if (code == 404 || code == 410) RemoveSub(endpoint);
                else s.Last = Stamp(now) + (code >= 200 && code < 300 ? "test accepted by the push service (HTTP " + code + ")"
                    : code == 0 ? "test: push service unreachable" : "test rejected by the push service (HTTP " + code + (LastReply != "" ? ": " + LastReply : "") + ")");
                Save();
            }
            return code;
        }

        PushSub FindSub(string endpoint) { foreach (var s in st.Subs) if (s.Endpoint == endpoint) return s; return null; }

        void RemoveSub(string endpoint)
        {
            st.Subs.RemoveAll(s => s.Endpoint == endpoint);
            st.Queue.RemoveAll(q => q.Endpoint == endpoint);
            Save();
        }

        // https on a known browser push service only, so the laptop can't be made to POST anywhere else.
        public static bool ValidEndpoint(string e)
        {
            Uri u;
            if (string.IsNullOrEmpty(e) || e.Length > 600 || !Uri.TryCreate(e, UriKind.Absolute, out u) || u.Scheme != "https" || !u.IsDefaultPort || u.UserInfo != "") return false;
            string h = u.Host.ToLowerInvariant();
            foreach (var p in PushHosts) if (p[0] == '.' ? h.EndsWith(p) : h == p) return true;
            return false;
        }

        public static bool ValidKeys(string p256dh, string auth)
        {
            try
            {
                byte[] p = B64(p256dh), a = B64(auth);
                return p.Length == 65 && p[0] == 4 && a.Length == 16 && OnCurve(Sub(p, 1, 32), Sub(p, 33, 32));
            }
            catch { return false; }
        }

        // y^2 = x^3 - 3x + b (mod p) on P-256. Windows imports and uses any 64 bytes as a public key
        // without checking this, so a malformed subscription key is refused here.
        static readonly BigInteger CurveP = BigInteger.Parse("00FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
        static readonly BigInteger CurveB = BigInteger.Parse("005AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B", System.Globalization.NumberStyles.HexNumber);

        static bool OnCurve(byte[] xBe, byte[] yBe)
        {
            BigInteger x = Unsigned(xBe), y = Unsigned(yBe);
            if (x >= CurveP || y >= CurveP) return false;
            BigInteger rhs = ((x * x * x - 3 * x + CurveB) % CurveP + CurveP) % CurveP;
            return y * y % CurveP == rhs;
        }

        static BigInteger Unsigned(byte[] be)
        {
            var le = new byte[be.Length + 1];   // little-endian with a zero sign byte
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        // ---------- storage ----------
        void EnsureKeys()
        {
            if (st.Key != "") return;
            using (var k = new ECDsaCng(ECCurve.NamedCurves.nistP256))
            {
                var p = k.ExportParameters(true);
                st.Key = B64u(p.D);
                st.Pub = B64u(Concat(new byte[] { 4 }, p.Q.X, p.Q.Y));
            }
            var secret = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            st.Secret = B64u(secret);
            Save();
        }

        void Load()
        {
            if (!File.Exists(path)) return;
            byte[] raw = null;
            for (int i = 0; i < 3 && raw == null; i++) { try { raw = File.ReadAllBytes(path); } catch (IOException) { Thread.Sleep(50); } catch { break; } }
            if (raw == null) { Error = "couldn't read push.dat"; return; }
            try
            {
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser));
                var s = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<PushState>(json);
                if (s == null || s.V != 1) throw new InvalidDataException();
                if (s.Subs == null) s.Subs = new List<PushSub>();
                if (s.Sent == null) s.Sent = new Dictionary<string, long>();
                if (s.Queue == null) s.Queue = new List<PushItem>();
                st = s;
            }
            catch
            {
                // Another Windows user's / machine's file, or damaged: set aside (never deleted); phones re-enable.
                Error = "push.dat couldn't be decrypted; notifications need to be enabled again on the phone";
                try { File.Move(path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".unreadable"); } catch { }
            }
        }

        void Save()
        {
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(st);
                var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, data);
                try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
                Error = "";
            }
            catch (Exception e) { Error = "couldn't save push.dat (" + e.GetType().Name + ")"; }
        }

        // ---------- Web Push ----------
        int Send(PushSub sub, string payload, string topic)
        {
            string key, pub;
            lock (gate) { key = st.Key; pub = st.Pub; }
            var u = new Uri(sub.Endpoint);
            byte[] body = WebPushCrypto.Encrypt(B64(sub.P256dh), B64(sub.Auth), Encoding.UTF8.GetBytes(payload), null, null);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(u);
            req.Method = "POST"; req.Timeout = 15000; req.ReadWriteTimeout = 15000; req.AllowAutoRedirect = false;
            req.ContentType = "application/octet-stream";
            req.Headers["Content-Encoding"] = "aes128gcm";
            req.Headers["TTL"] = TtlSeconds.ToString();
            req.Headers["Urgency"] = "high";
            if (!string.IsNullOrEmpty(topic)) req.Headers["Topic"] = topic;   // a retry replaces its own undelivered copy
            req.Headers["Authorization"] = "vapid t=" + WebPushCrypto.VapidJwt(u.Scheme + "://" + u.Host, B64(key), B64(pub), Contact, DateTime.UtcNow.AddHours(12)) + ", k=" + pub;
            try
            {
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var r = (HttpWebResponse)req.GetResponse()) { LastReply = ""; return (int)r.StatusCode; }
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r == null) return 0;
                using (r)
                {
                    string text = "";
                    try { using (var sr = new StreamReader(r.GetResponseStream())) { var buf = new char[200]; text = new string(buf, 0, sr.Read(buf, 0, buf.Length)); } } catch { }
                    LastReply = Regex.Replace(text, "[^A-Za-z0-9 _.:,()-]", " ").Trim();
                    if (LastReply.Length > 120) LastReply = LastReply.Substring(0, 120);
                    return (int)r.StatusCode;
                }
            }
        }

        // ---------- helpers ----------
        public static long Ms(DateTime utc) { return (long)(utc - Epoch).TotalMilliseconds; }

        public static ECParameters Point(byte[] q65)
        {
            return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Sub(q65, 1, 32), Y = Sub(q65, 33, 32) } };
        }

        public static byte[] B64(string s)
        {
            s = (s ?? "").Trim().TrimEnd('=').Replace('-', '+').Replace('_', '/');
            if (!Regex.IsMatch(s, "^[A-Za-z0-9+/]*$")) throw new FormatException();
            while (s.Length % 4 != 0) s += "=";
            return Convert.FromBase64String(s);
        }

        public static string B64u(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        internal static byte[] Sub(byte[] a, int o, int n) { var r = new byte[n]; Array.Copy(a, o, r, 0, n); return r; }

        internal static byte[] Concat(params byte[][] parts)
        {
            int n = 0; foreach (var p in parts) n += p.Length;
            var r = new byte[n]; int o = 0;
            foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
    }

    // RFC 8291 message encryption (aes128gcm, one record) and the RFC 8292 VAPID token.
    public static class WebPushCrypto
    {
        // asKey / salt: null = fresh random ones (always, except in the RFC test vector).
        public static byte[] Encrypt(byte[] uaPublic, byte[] auth, byte[] plaintext, ECParameters? asKey, byte[] salt)
        {
            if (salt == null) { salt = new byte[16]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt); }
            byte[] asPublic, prkKey;
            using (var me = asKey.HasValue ? ECDiffieHellman.Create(asKey.Value) : ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            using (var ua = ECDiffieHellman.Create(PushNotifier.Point(uaPublic)))
            {
                var q = me.ExportParameters(false).Q;
                asPublic = PushNotifier.Concat(new byte[] { 4 }, q.X, q.Y);
                prkKey = me.DeriveKeyFromHmac(ua.PublicKey, HashAlgorithmName.SHA256, auth);   // HKDF-Extract(auth, ECDH secret)
            }
            byte[] ikm = Hmac(prkKey, PushNotifier.Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic, new byte[] { 1 }));
            byte[] prk = Hmac(salt, ikm);
            byte[] cek = PushNotifier.Sub(Hmac(prk, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0\x01")), 0, 16);
            byte[] nonce = PushNotifier.Sub(Hmac(prk, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0\x01")), 0, 12);
            byte[] ct = AesGcm(cek, nonce, PushNotifier.Concat(plaintext, new byte[] { 2 }));   // 0x02: last (only) record
            return PushNotifier.Concat(salt, new byte[] { 0, 0, 16, 0, 65 }, asPublic, ct);    // rs = 4096, keyid = as_public
        }

        public static string VapidJwt(string audience, byte[] d, byte[] pub65, string contact, DateTime expiresUtc)
        {
            var ser = new JavaScriptSerializer();
            string head = PushNotifier.B64u(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
            string body = PushNotifier.B64u(Encoding.UTF8.GetBytes(ser.Serialize(new Dictionary<string, object>
                { { "aud", audience }, { "exp", PushNotifier.Ms(expiresUtc) / 1000 }, { "sub", contact } })));
            var p = PushNotifier.Point(pub65); p.D = d;
            using (var k = ECDsa.Create(p))
                return head + "." + body + "." + PushNotifier.B64u(k.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256));   // r||s
        }

        static byte[] Hmac(byte[] key, byte[] data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(data); }

        // AES-GCM (NIST SP 800-38D) with a 96-bit nonce, no associated data, 128-bit tag: .NET Framework has no
        // managed AES-GCM, so CTR and GHASH are done here on top of AES-ECB. Checked by the RFC 8291 vector.
        public static byte[] AesGcm(byte[] key, byte[] nonce, byte[] p)
        {
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
                using (var enc = aes.CreateEncryptor())
                {
                    var h = Block(enc, new byte[16]);
                    var j0 = new byte[16]; Array.Copy(nonce, j0, 12); j0[15] = 1;
                    var ctr = (byte[])j0.Clone();
                    var c = new byte[p.Length + 16];
                    var y = new byte[16];
                    for (int i = 0; i < p.Length; i += 16)
                    {
                        for (int j = 15; j >= 12; j--) if (++ctr[j] != 0) break;   // inc32
                        var ks = Block(enc, ctr);
                        for (int j = 0; j < 16 && i + j < p.Length; j++) { c[i + j] = (byte)(p[i + j] ^ ks[j]); y[j] ^= c[i + j]; }
                        y = GfMul(y, h);
                    }
                    ulong bits = (ulong)p.Length * 8;
                    for (int j = 0; j < 8; j++) y[15 - j] ^= (byte)(bits >> (8 * j));
                    y = GfMul(y, h);
                    var t = Block(enc, j0);
                    for (int j = 0; j < 16; j++) c[p.Length + j] = (byte)(t[j] ^ y[j]);
                    return c;
                }
            }
        }

        static byte[] Block(ICryptoTransform enc, byte[] b) { var o = new byte[16]; enc.TransformBlock(b, 0, 16, o, 0); return o; }

        static byte[] GfMul(byte[] x, byte[] y)
        {
            var z = new byte[16]; var v = (byte[])y.Clone();
            for (int i = 0; i < 128; i++)
            {
                if ((x[i >> 3] & (0x80 >> (i & 7))) != 0) for (int j = 0; j < 16; j++) z[j] ^= v[j];
                bool lsb = (v[15] & 1) != 0;
                for (int j = 15; j > 0; j--) v[j] = (byte)((v[j] >> 1) | (v[j - 1] << 7));
                v[0] >>= 1;
                if (lsb) v[0] ^= 0xE1;
            }
            return z;
        }
    }
}
