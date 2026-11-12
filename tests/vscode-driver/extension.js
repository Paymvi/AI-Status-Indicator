// Test-only (loaded with --extensionDevelopmentPath by tests\vscode-tests.ps1, never installed): acts like the person
// at the keyboard. cmd-<n>.json {op:"type", name, text} types a line into the terminal whose name contains `name`;
// every window keeps state-<window>.json with its folders, terminal names and which terminal is active.
'use strict';
const vscode = require('vscode'), fs = require('fs'), path = require('path'), crypto = require('crypto');
function activate() {
  const dir = process.env.CLAUDE_STATUS_TEST_DRIVER;
  if (!dir) return;
  const me = crypto.randomBytes(6).toString('hex');
  const report = () => {
    const f = (vscode.workspace.workspaceFolders || []).map(x => x.uri.fsPath);
    const a = vscode.window.activeTerminal;
    try { fs.writeFileSync(path.join(dir, 'state-' + me + '.json'), JSON.stringify({ folders: f, terminals: vscode.window.terminals.map(t => (t.creationOptions && t.creationOptions.name) || t.name), active: a ? ((a.creationOptions && a.creationOptions.name) || a.name) : '', focused: vscode.window.state.focused, at: Date.now() })); } catch (e) { }
  };
  setInterval(() => {
    report();
    for (const n of fs.readdirSync(dir)) {
      if (!/^cmd-.*\.json$/.test(n)) continue;
      let c; try { c = JSON.parse(fs.readFileSync(path.join(dir, n), 'utf8')); } catch (e) { continue; }
      const t = vscode.window.terminals.find(x => x.name.includes(c.name) || String((x.creationOptions && x.creationOptions.name) || '').includes(c.name));
      if (!t) continue;   // another window's terminal
      try { fs.renameSync(path.join(dir, n), path.join(dir, n + '.done')); } catch (e) { continue; }
      if (c.op === 'type') t.sendText(c.text, c.enter !== false);
    }
  }, 500);
}
module.exports = { activate, deactivate() { } };
