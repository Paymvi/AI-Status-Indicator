// Claude Status Bridge: the VS Code side of Claude Status's scheduled tasks. Plain Node, supported VS Code API only.
// Protocol (files in <status-indicator>\state\vscode, nothing on the network):
//   windows\<id>.json   this window's folders, focus and open task terminals (rewritten every few seconds)
//   inbox\<req>.json    a request from the app; one window claims it by renaming it, so it runs exactly once
//   status\<req>.json   what happened: open / closed / revealed / error
// A launch request carries the executable and a ready-quoted Windows argument string; it runs directly in the
// terminal (no shell), so prompt text is never interpreted as a command.
'use strict';
const vscode = require('vscode');
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');

let dir, id, timer, watcher, startedAt = Date.now();
const terms = new Map();   // request id -> terminal (only terminals this window created)

function bridgeDir() {
  if (process.env.CLAUDE_STATUS_BRIDGE_DIR) return process.env.CLAUDE_STATUS_BRIDGE_DIR;
  const cfg = process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), '.claude');
  return path.join(cfg, 'status-indicator', 'state', 'vscode');
}

// Same rule as the app: full path, case-insensitive, Unicode NFC, no trailing separator.
function norm(p) { return path.resolve(p).normalize('NFC').replace(/[\\/]+$/, '').toLowerCase(); }

function writeJson(file, obj) {
  const tmp = file + '.' + process.pid + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(obj), 'utf8');
  fs.renameSync(tmp, file);
}

function folders() {
  return (vscode.workspace.workspaceFolders || []).filter(f => f.uri.scheme === 'file').map(f => f.uri.fsPath);
}

let focusedAt = 0;
function register() {
  try {
    const wf = vscode.workspace.workspaceFile;
    writeJson(path.join(dir, 'windows', id + '.json'), {
      v: 1, id, pid: process.pid, folders: folders(),
      remote: !!vscode.env.remoteName,
      workspaceFile: wf && wf.scheme === 'file' ? wf.fsPath : '',
      focused: vscode.window.state.focused, focusedAt,
      terminals: [...terms.keys()],
      beat: new Date().toISOString()
    });
  } catch (e) { /* the app may not be installed; try again on the next beat */ }
}

function status(req, obj) {
  try { writeJson(path.join(dir, 'status', req + '.json'), Object.assign({ v: 1, req, window: id, at: new Date().toISOString() }, obj)); } catch (e) { }
}

function mine(r) {
  if (r.target) return r.target === id;
  // Untargeted: only a launch into a window that has exactly this one folder open (a window the app just opened).
  const f = folders();
  return r.type === 'launch' && !vscode.workspace.workspaceFile && f.length === 1 && typeof r.folder === 'string' && norm(f[0]) === norm(r.folder);
}

function scan() {
  // A window that just opened is still settling (a terminal created in its first seconds can be dropped): wait.
  if (Date.now() - startedAt < 4000) return;
  let names;
  try { names = fs.readdirSync(path.join(dir, 'inbox')); } catch (e) { return; }
  for (const n of names) {
    if (!/^[A-Za-z0-9_-]+\.json$/.test(n)) continue;
    const file = path.join(dir, 'inbox', n);
    let r;
    try { r = JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { continue; }
    if (!r || r.v !== 1 || r.req + '.json' !== n || !mine(r)) continue;
    const claimed = file + '.' + id + '.claimed';
    try { fs.renameSync(file, claimed); } catch (e) { continue; }   // another window (or the app's timeout) got it first
    try { handle(r); } catch (e) { status(r.req, { state: 'error', error: String(e && e.message || e) }); }
    try { fs.unlinkSync(claimed); } catch (e) { }
  }
}

function handle(r) {
  if (r.type === 'launch') {
    if (terms.has(r.req)) { status(r.req, { state: 'error', error: 'This task already has a terminal.' }); return; }
    if (typeof r.exe !== 'string' || !path.isAbsolute(r.exe) || typeof r.args !== 'string') { status(r.req, { state: 'error', error: 'Malformed launch request.' }); return; }
    const t = vscode.window.createTerminal({
      name: String(r.name || 'Claude task').slice(0, 80),
      cwd: r.cwd,
      shellPath: r.exe,
      shellArgs: r.args,          // Windows command-line string, quoted by the app (argv rules, no shell)
      env: r.env || {},           // null deletes a variable (API keys unless allowed)
      iconPath: new vscode.ThemeIcon('history'),
      isTransient: true           // never re-run on a VS Code restart or reload
    });
    terms.set(r.req, t);
    t.show(true);                 // visible in the panel without taking keyboard focus
    status(r.req, { state: 'open', name: t.name });
    t.processId.then(pid => { if (terms.get(r.req) === t) status(r.req, { state: 'open', name: t.name, pid: pid || 0 }); }, () => { });
    register();
    return;
  }
  const t = terms.get(r.of);
  if (r.type === 'reveal') {
    if (!t) { status(r.req, { state: 'error', error: 'not_found' }); return; }
    t.show(false);
    status(r.req, { state: 'revealed' });
    return;
  }
  if (r.type === 'close') {
    if (!t) { status(r.req, { state: 'error', error: 'not_found' }); return; }
    t.dispose();
    status(r.req, { state: 'closing' });
    return;
  }
  status(r.req, { state: 'error', error: 'Unknown request.' });
}

function activate(ctx) {
  if (process.platform !== 'win32' || vscode.env.remoteName) return;   // the app is Windows-only and works on local folders
  dir = bridgeDir();
  startedAt = Date.now();
  id = crypto.randomBytes(12).toString('hex');
  for (const d of ['windows', 'inbox', 'status']) { try { fs.mkdirSync(path.join(dir, d), { recursive: true }); } catch (e) { } }
  if (vscode.window.state.focused) focusedAt = Date.now();
  ctx.subscriptions.push(
    vscode.workspace.onDidChangeWorkspaceFolders(register),
    vscode.window.onDidChangeWindowState(s => { if (s.focused) focusedAt = Date.now(); register(); }),
    vscode.window.onDidCloseTerminal(t => {
      for (const [req, x] of terms) if (x === t) {
        terms.delete(req);
        status(req, { state: 'closed', exitCode: t.exitStatus && typeof t.exitStatus.code === 'number' ? t.exitStatus.code : null });
      }
      register();
    }));
  register();
  try { watcher = fs.watch(path.join(dir, 'inbox'), () => setTimeout(scan, 50)); } catch (e) { }
  timer = setInterval(() => { register(); scan(); }, 2000);
  scan();
}

function deactivate() {
  if (timer) clearInterval(timer);
  if (watcher) watcher.close();
  if (dir && id) { try { fs.unlinkSync(path.join(dir, 'windows', id + '.json')); } catch (e) { } }
}

module.exports = { activate, deactivate };
