# Claude Status

> A tiny always-on-top Windows status indicator for Claude Code that shows when Claude is working, finished, waiting for you, or encountered an error.

```text
┌──────────────────────────────┐
│ Claude · my-project        × │
│ ● WORKING                    │
└──────────────────────────────┘
```

## Why?

When Claude Code is working on something that takes several minutes, you probably switch to something else: a browser, another editor, email. Then you keep jumping back to the Claude window just to check whether it has finished, or whether it has been sitting there for ten minutes waiting for you to approve a command.

**Claude Status** fixes that. It keeps a very small indicator floating above your other windows, so you can see Claude's state at a glance from anywhere on your desktop. It plays a soft sound when Claude finishes and shows a Windows notification when Claude needs you.

## States

| Indicator | Meaning |
|---|---|
| 🟡 **WORKING** | Claude is processing your prompt. |
| 🟢 **DONE** | Claude finished and is waiting for your next prompt. Plays a subtle sound. |
| 🔴 **NEEDS YOU** | Claude is blocked waiting for input or a permission approval. Shows a notification. |
| 🔴 **ERROR** | The Claude turn ended because of an API error (rate limit, auth, server error, …). Shows a notification with the error type. |
| ⚪ **IDLE** | No active Claude Code sessions. |

Every state is shown with **both color and text**, so it's readable regardless of color perception.

## How it works

Claude Status uses Claude Code's official **lifecycle hooks**. It does **not**:

- scrape the screen or watch VS Code pixels;
- poll Anthropic or make any network requests;
- store your prompts or Claude's responses. (Claude Code hands each hook its event data, which for a new prompt includes the prompt text. `hook.ps1` only extracts a few fields from it and never saves the prompt.)

```text
Claude Code
    │
    │ lifecycle hooks (UserPromptSubmit, Stop, StopFailure, Notification, ...)
    ▼
hook.ps1
    │ writes a tiny JSON file (state, folder name, timestamp)
    ▼
state\sessions\<session-id>.json
    │
    │ file-system watcher (instant, no polling)
    ▼
Claude Status indicator
    │
    ├── 🟡 WORKING
    ├── 🟢 DONE
    ├── 🔴 NEEDS YOU
    └── 🔴 ERROR
```

Each hook call runs `hook.ps1`, which writes one small file per Claude Code session. The indicator watches that folder and redraws as soon as a file changes.

### Which hooks set which state

| Claude Code hook | State |
|---|---|
| `UserPromptSubmit` | WORKING |
| `Stop` | DONE |
| `StopFailure` | ERROR (with the error type, e.g. `rate_limit`) |
| `Notification` (`permission_prompt`, `elicitation_dialog`, `elicitation_url_dialog`, `agent_needs_input`) | NEEDS YOU |
| `PermissionRequest` | NEEDS YOU |
| `PreToolUse` for `AskUserQuestion` | NEEDS YOU |
| `PostToolUse` / `PostToolUseFailure` | back to WORKING, but only if the state was NEEDS YOU (i.e. you answered) |
| `SessionEnd` | removes that session's state file |

Ordinary notifications (such as the "Claude is idle" reminder) are ignored. The hooks that don't need to block (`PermissionRequest`, `PreToolUse`, `PostToolUse*`) run asynchronously so they never slow Claude down.

### Privacy: what is stored

Each session file contains only:

```json
{"v":1,"session_id":"…","state":"WORKING","event":"UserPromptSubmit","detail":"","project":"my-project","updated":"2026-10-23T21:00:00Z"}
```

`project` is just the name of the folder Claude is working in (not the full path), and `detail` is only the notification type, the name of the tool awaiting approval (e.g. `Bash`), or the API error type. **Prompts, Claude's messages, tool inputs, and file contents are never written anywhere.**

The only other files Claude Status writes are the indicator's window position (`position.txt`), the `settings.json` backups the installer/uninstaller make next to your settings, and, only if you turn on debugging, `debug.log`. All of them stay on your computer; nothing is sent anywhere.

## Features

- Tiny always-on-top window (about 124 × 38 px), DPI-aware and readable on dark desktops
- Draggable anywhere; remembers its position
- **Never steals keyboard focus** when the state changes
- Subtle sound when Claude is DONE; Windows notifications for NEEDS YOU and ERROR (all configurable)
- System-tray icon whose color matches the state
- Uses real Claude Code lifecycle hooks, works **globally across all your projects**
- Session-aware: each Claude Code session has its own state file; shows "(2 sessions)" and lists them on right-click when you run several
- User-level install, no administrator rights
- No Node.js, Vite, Python, or other runtime needed; it uses only what ships with Windows
- No network connection required; negligible CPU while idle

## Requirements

| Requirement | Notes |
|---|---|
| **Windows 10 or 11** | Uses WinForms, Windows notifications, and the Startup folder. |
| **Windows PowerShell 5.1** | Built into Windows (`powershell.exe`). PowerShell 7 is *not* required; if you run the installer from PowerShell 7 it automatically hands off to 5.1. |
| **.NET Framework 4.5+** | Built into Windows 10/11. The indicator's small C# source is compiled on the fly by PowerShell's `Add-Type`; no Visual Studio or SDK needed. |
| **Claude Code** with hooks support | The CLI and/or the Claude Code VS Code extension. Developed and tested with Claude Code **2.1.308**. Needs a version that supports the `StopFailure` and `PermissionRequest` hook events. |

You do **not** need administrator rights, Node.js, Git (you can download the ZIP), or an internet connection for the indicator itself.

## Installation

1. **Get the code.** Clone it anywhere you like (it does *not* need to live inside `.claude`):

   ```powershell
   git clone https://github.com/<your-account>/claude-status.git
   cd claude-status
   ```

   Or download the ZIP from GitHub, extract it, and open PowerShell in the extracted folder.

2. **Run the installer:**

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

   To also start Claude Status automatically when you sign in to Windows:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -Autostart
   ```

3. **Pick up the hooks in Claude Code.** New Claude Code sessions use them automatically. In a session that was already open, run `/hooks` (or restart the session).

The indicator appears near the bottom-right of your screen. Send Claude a prompt and it turns 🟡.

### What the installer does

```text
SOURCE REPOSITORY (anywhere)            INSTALLED APPLICATION
claude-status\src\*  ── copies ──►  %USERPROFILE%\.claude\status-indicator\
                                                │
                                                ▼  merges hooks into
                                     %USERPROFILE%\.claude\settings.json
```

1. Checks for Windows, Windows PowerShell 5.1, and .NET Framework 4.5+.
2. Reads `%USERPROFILE%\.claude\settings.json` first. **If it isn't valid JSON, the installer stops and changes nothing.**
3. Copies the app into `%USERPROFILE%\.claude\status-indicator\` and creates `state\sessions\`. It also clears the "downloaded from the internet" mark (`Unblock-File`) from those copied files only, so Windows doesn't show a security prompt each time the indicator starts.
4. Makes a **timestamped backup** of `settings.json` (`settings.json.bak-claude-status-YYYYMMDD-HHMMSS`).
5. **Merges** the Claude Status hooks into it. Every unrelated setting and hook is preserved.
6. Starts the indicator (unless you pass `-NoStart`).

The paths are resolved for whoever runs it (e.g. `C:\Users\Alice\.claude\status-indicator\`). Paths containing spaces are fine. If you've set `CLAUDE_CONFIG_DIR`, that directory is used instead of `%USERPROFILE%\.claude`.

### Updating / re-running

Run `install.ps1` again after pulling a newer version. It's safe to run repeatedly:

- application files are replaced with the new version, and a running indicator is restarted;
- your `config.json` choices are kept (new options are added with their defaults);
- Claude Status hooks are never duplicated, and old entries are replaced;
- if nothing changed, `settings.json` isn't touched (and no backup is made);
- the autostart shortcut is overwritten, never duplicated.

| Installer option | Effect |
|---|---|
| `-Autostart` | Also start Claude Status when you sign in to Windows. |
| `-NoStart` | Don't launch the indicator after installing. |
| `-ClaudeDir <path>` | Use a different Claude config directory. |

## Usage

| To… | Do this |
|---|---|
| **Start** | Double-click `%USERPROFILE%\.claude\status-indicator\start-indicator.vbs` |
| **Stop** | Click the **×** on the indicator, right-click it → **Exit**, or right-click the tray icon → **Exit** |
| **Move** | Drag it anywhere |
| **Reset position** | Right-click → **Reset position** |
| **See all sessions** | Right-click the indicator or the tray icon |
| **Bring it back if hidden** | Left-click the tray icon |

From PowerShell:

```powershell
# start
wscript "$env:USERPROFILE\.claude\status-indicator\start-indicator.vbs"
# stop
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\stop-indicator.ps1"
```

Stopping or restarting the indicator never affects Claude Code. The hooks keep writing their tiny state files, and the indicator picks up the current state when it starts again. Only one indicator runs at a time; starting it twice does nothing.

### Start automatically with Windows

```powershell
# enable
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\autostart.ps1" -Enable
# disable
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\autostart.ps1" -Disable
```

This creates (or removes) a shortcut named `Claude Status Indicator` in your Windows Startup folder.

### Create a desktop shortcut

If you close Claude Status with the **×**, you can reopen it at any time by running:

```text
%USERPROFILE%\.claude\status-indicator\start-indicator.vbs
```

To make that a one-click desktop shortcut:

1. Right-click an empty area of the desktop.
2. Choose **New → Shortcut**.
3. For the item location, enter:

   ```text
   %USERPROFILE%\.claude\status-indicator\start-indicator.vbs
   ```

4. Click **Next**.
5. Name the shortcut **Claude Status**.
6. Click **Finish**.

From then on, just double-click the **Claude Status** shortcut on your desktop whenever you want to reopen the indicator.

If the wizard says it can't find the file, use this instead: press **Win + R**, paste `%USERPROFILE%\.claude\status-indicator`, and press **Enter**. In the folder that opens, right-click `start-indicator.vbs`, choose **Send to → Desktop (create shortcut)** (on Windows 11, choose **Show more options** first), then rename the new shortcut to **Claude Status**.

> If you enable autostart (above), Claude Status normally launches automatically when you sign in. The desktop shortcut is still handy for reopening it after you've closed the indicator.

## Configuration

Edit `%USERPROFILE%\.claude\status-indicator\config.json`, then restart the indicator:

```json
{
  "soundOnDone": true,
  "toastOnDone": false,
  "soundOnNeedsYou": true,
  "toastOnNeedsYou": true,
  "soundOnError": true,
  "toastOnError": true,
  "staleHours": 24
}
```

| Option | Meaning |
|---|---|
| `soundOn…` | Play a Windows system sound on that state. |
| `toastOn…` | Show a Windows notification on that state. |
| `staleHours` | Forget sessions that haven't changed for this long (e.g. a crashed session that never sent `SessionEnd`). |

Notifications follow Windows' Do Not Disturb / Focus settings and appear as coming from "Windows PowerShell".

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\uninstall.ps1"
```

Or, from your cloned repository (it does the same thing):

```powershell
powershell -ExecutionPolicy Bypass -File .\src\uninstall.ps1
```

The uninstaller:

1. stops the indicator;
2. removes the autostart shortcut, if it points to this install;
3. backs up `settings.json`, then removes **only** Claude Status's hook entries;
4. deletes `%USERPROFILE%\.claude\status-indicator\`.

It does **not** delete your cloned repository, Claude Code, the `.claude` folder, your other settings, or your other hooks. It's safe to run even if parts of Claude Status are already missing.

## Works with

- ✅ **Claude Code CLI** (any terminal)
- ✅ **Claude Code VS Code extension**: hooks from `~/.claude/settings.json` apply there too.
- Works for every project and repository, because the hooks are installed at user level.

## Limitations

- **Interrupting Claude (Esc)** doesn't fire a hook, so the indicator stays 🟡 WORKING until your next prompt.
- **Permission prompts inside the VS Code extension**: the extension handles approvals in its own UI, and the docs don't guarantee the `PermissionRequest` / `Notification` hooks fire there. If NEEDS YOU never appears for approvals in VS Code, that's why. WORKING / DONE / ERROR work normally.
- In **auto / bypass permission modes**, NEEDS YOU only appears when Claude actually shows you a prompt.
- Notifications and sounds only happen while the indicator is running.
- If a session ends abruptly (crash, killed terminal), its last state lingers until `staleHours` passes.
- With several sessions, the main display shows the **most recently updated** session; the others are listed on right-click.
- Each hook call starts a short-lived `powershell.exe` (about 0.3 s). The blocking ones (`UserPromptSubmit`, `Stop`, `StopFailure`, `Notification`, `SessionEnd`) add that small delay; the rest run in the background.

## Troubleshooting

**The indicator never changes state.**
Run `/hooks` in Claude Code and check that the Claude Status hooks are listed under `UserPromptSubmit`, `Stop`, etc. Open sessions need `/hooks` or a restart after installing.

**I want to see whether hooks fire.**
Create an empty file named `debug.on` in `%USERPROFILE%\.claude\status-indicator\`. Each hook call is then logged (time, event, state, session ID only) to `debug.log` in that folder. Delete `debug.on` when done.

**The window disappeared off-screen.**
Right-click the tray icon → **Reset position**.

**"Running scripts is disabled on this system".**
Use the commands exactly as shown above; they include `-ExecutionPolicy Bypass`, which applies only to that one command.

## Repository layout

```text
install.ps1              installer / updater (run from the repo)
src/
  hook.ps1               called by Claude Code hooks; writes state files
  indicator.cs           the indicator window (C#, WinForms)
  indicator.ps1          compiles indicator.cs with Add-Type and runs it
  start-indicator.vbs    launches the indicator with no console window
  stop-indicator.ps1     stops the indicator
  autostart.ps1          enable/disable start with Windows
  uninstall.ps1          complete, safe uninstall
  config.json            default settings
```

Runtime files (`state/`, `position.txt`, `debug.log`) are created only in the install folder and are git-ignored.

## Future: multiple simultaneous sessions

The groundwork is already there: every Claude Code session writes its own `state\sessions\<session-id>.json`, and the indicator already reads all of them. Possible next steps:

- show one row per active session (the window grows a line per session);
- always surface the **most urgent** state first (NEEDS YOU / ERROR → WORKING → DONE);
- click a session's DONE to dismiss it;
- show how long each session has been working.
