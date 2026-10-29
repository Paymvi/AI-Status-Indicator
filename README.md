# Claude Status

> A tiny always-on-top Windows utility for Claude Code that shows, at a glance, which of your Claude sessions are working, finished, waiting for you, or stopped with an error.

```text
WORKER BOARD (default)                          COMPACT
┌──────────────────────────────────────┐       ┌──────────────────────────────┐
│ CLAUDE WORKERS            ▭  ⚙  –  × │       │ Claude · Portal            × │
├──────────────────────────────────────┤       │ ● NEEDS YOU                  │
│ ● Portal              NEEDS YOU · 2m │       └──────────────────────────────┘
│ ● Booking Software #1 WORKING · 04:32│
│ ● Booking Software #2 WORKING · 01:08│
│ ● Website             DONE · 6m ago ×│
└──────────────────────────────────────┘
```

## Why?

When Claude Code is working on something that takes several minutes, you probably switch to something else: a browser, another editor, email. Then you keep jumping back to the Claude window just to check whether it has finished, or whether it has been sitting there for ten minutes waiting for you to approve a command. With several Claude sessions running at once, that gets worse.

**Claude Status** fixes that. It keeps a very small window floating above your other windows, so you can see every Claude session's state from anywhere on your desktop, and click one to jump straight to it. It plays a soft sound when Claude finishes and shows a Windows notification when Claude needs you.

## States

| Indicator | Meaning |
|---|---|
| 🟡 **WORKING** | Claude is processing your prompt. |
| 🟢 **DONE** | Claude finished and is waiting for your next prompt. Plays a subtle sound. Stays on the Worker Board until you dismiss it. |
| 🔴 **NEEDS YOU** | Claude is blocked waiting for input or a permission approval. Shows a notification. |
| 🔴 **ERROR** | The Claude turn ended because of an API error (rate limit, auth, server error, …). Shows a notification with the error type. |
| ⚪ **IDLE** / *No Claude workers* | No sessions to show. |

Every state is shown with **both color and text**, so it's readable regardless of color perception.

## Worker Board

**Worker Board** is the default view. Its heading is **Claude Workers**, and it shows one compact row per Claude Code session:

```text
CLAUDE WORKERS

🔴 Portal                NEEDS YOU · 2m
🔴 Analytics             ERROR · rate_limit · 3m
🟡 Booking Software #1   WORKING · 04:32
🟡 Booking Software #2   WORKING · 01:08
🟢 Website               DONE · 6m ago      ×
```

- **What's listed:** every active session (WORKING, NEEDS YOU, ERROR) plus every **DONE** session you haven't dismissed yet.
- **DONE rows stay until you dismiss them.** They don't time out after 10 minutes, 24 hours or when the session closes. Click the small **×** at the end of a DONE row to dismiss it. Dismissing only removes Claude Status's own row: it doesn't touch Claude Code, the session, VS Code or your files, and it sends nothing to Claude. If that same session later gets a new prompt, it simply reappears.
- **Click a row** (anywhere except the ×) to bring that worker's own window to the front: its terminal window, or its VS Code window. See [Clicking a worker](#clicking-a-worker) for exactly how that works and when it declines.
- **Timers** update live:

  | Row | Timer shows |
  |---|---|
  | WORKING | how long it has been working on the current prompt: `04:32`, then `1h 05m` |
  | NEEDS YOU | how long it has been waiting for you: `<1m`, `2m`, `1h 5m` |
  | DONE | how long ago it finished: `just now`, `6m ago`, `2h ago`, `3d ago` |
  | ERROR | the error type (only a short identifier such as `rate_limit` or `auth`) and how long ago: `ERROR · rate_limit · 3m` |

  Timers are calculated from timestamps stored once per state change; nothing is rewritten every second. Tool calls during WORKING don't restart the WORKING timer; going WORKING → NEEDS YOU → WORKING does.
- **Friendly names (aliases).** Rows show a friendly name for each project folder, such as `Toah_Nipi_Booking_Software` → **Booking Software**. Folders without an alias are shown with underscores turned into spaces (`My_Project` → `My Project`). See [Configuration](#configuration).
- **Several workers in one project** are numbered: `Booking Software #1`, `#2`, `#3`. Numbers follow the order the sessions first appeared, and a worker keeps its number while it's on the board, so rows never swap identities when states change. If only one worker exists for a project, there's no `#1`.
- **Ordering** (Settings → *Worker ordering*):
  - **Urgency** (default): NEEDS YOU, then ERROR, then WORKING, then DONE; within each, the most recent state change first.
  - **Most recent**: the most recently updated worker first.
- **Optional summary line** (Settings → *Show worker summary*, off by default): `1 needs you · 2 working · 2 done` under the heading. When it's off, no space is reserved for it.
- **Size.** About 276 px wide (scaled for your DPI). The window grows and shrinks with the number of rows. It grows downward until it reaches the bottom of the monitor's working area (never under the taskbar), and then only the list scrolls (mouse wheel or the thin scrollbar) while the header stays visible. If you park the window so low that fewer than four rows would fit, it moves up just enough to show them, without changing your saved position.
- **Empty board:** a single line, *○ No Claude workers*.

### Switching views

| To… | Do this |
|---|---|
| Switch to **Compact** | Click the small pill icon (▭) in the Worker Board header |
| Switch to **Worker Board** | Hover over Compact and click the small list icon (☰) that appears next to the × |
| Either, from the menu | Right-click the window → **View** → *Worker Board* / *Compact* |
| Choose the view at startup | Settings (⚙) → *Startup view* |

Every icon has a tooltip. Switching views keeps the window anchored at its saved position and never loses any session state.

### Compact

**Compact** is the original Claude Status pill: 124 × 38 px, the same layout, colors, sounds and notifications as before. With several sessions it shows *Claude (N sessions)* and surfaces **one** session, chosen by the same *Worker ordering* setting: with **Urgency** it shows the session that most needs attention (e.g. `NEEDS YOU` even if another session updated more recently), with **Most recent** the latest one. The view toggle and the ⚙ only appear while the mouse is over it, so it stays as small and quiet as before.

### Settings

The ⚙ button (in the Worker Board header, or in Compact while hovering) opens a small settings window. Changes apply immediately and are saved to `config.json`:

- **Worker ordering:** Urgency / Most recent
- **Show worker summary:** on / off
- **Startup view:** Worker Board / Compact (used the next time Claude Status starts)
- **Phone dashboard (private, via Tailscale):** on / off (see [Phone dashboard](#phone-dashboard-optional))
- **Edit project aliases (config.json)…** opens `config.json` in Notepad. Saved alias changes apply as soon as you save the file.

### Clicking a worker

Clicking a row tries to bring **that specific session's** window to the front. Precision matters more than always succeeding: if Claude Status can't be sure which window belongs to the worker, it **does not guess**. It shows a short note instead (for example *Couldn't identify that worker's window*) and leaves focus where it was. It never focuses a window just because its folder name matches.

How a session is matched to a window:

1. Each hook call records the Claude Code process that owns the session: its process ID (`CLAUDE_PID`, which Claude Code sets for hooks) **plus that process's start time**. Together these identify one specific process; if Windows later reuses the same process ID for another program, the start time no longer matches and the row is treated as ended.
2. When you click, Claude Status checks that this exact process is still running. If it isn't (the session was closed), it says so and does nothing else.
3. **Terminal sessions** (Windows Terminal, classic console, other console hosts): Claude Status asks Windows which console window that process is attached to. For Windows Terminal this gives the **exact Windows Terminal window** hosting the session, even when several Windows Terminal windows are open.
4. **Otherwise** (e.g. the Claude Code **VS Code extension**, or `claude` in VS Code's integrated terminal): it walks up the process tree from the Claude process to the nearest program that owns windows on screen. Each step is verified (a parent must have started before its child) so a recycled process ID can't redirect it. If that program has **exactly one** window, that's the target.
5. **VS Code with several windows open.** All VS Code windows belong to one `Code.exe` process, so the process tree alone can't say which window hosts the session. When you send a session a prompt, Claude Status (if running) notes which window was in the foreground at that moment, but **only if that window belongs to the same VS Code process** as the session. Clicking that row later focuses that window, after re-checking the window still belongs to the same (not reused) process. Until a session has had a prompt sent while Claude Status was running, clicking it shows *Couldn't identify that worker's window* instead of guessing.

What this can't do:

- **Two Claude sessions in the same VS Code window** (or two tabs in one Windows Terminal window): the correct window comes to the front, but Windows offers no supported way to select a specific Claude tab or terminal tab inside it. The Claude Code extension has a `vscode://anthropic.claude-code/open?session=…` link, but VS Code delivers such links to whichever VS Code window was focused last and the extension *opens* (resumes) the session there, which could open it in the wrong window, so Claude Status deliberately doesn't use it.
- Sessions recorded by the previous version of Claude Status (before upgrading) have no process identity yet; they become clickable after their next prompt.
- Clicking a worker is the **only** thing that ever moves keyboard focus. State changes, sounds, notifications, view switching and resizing never take focus.

## Phone dashboard (optional)

See the same Worker Board on your phone, for example on Android while you're away from the desk, and dismiss finished workers from there. It is **off by default** and stays **private to your own Tailscale network**: nothing is published to the internet.

```text
Android phone (browser, Tailscale on)
    │  http://<laptop>.<tailnet>.ts.net/        (WireGuard-encrypted, your tailnet only)
    ▼
tailscale serve on the laptop  ── forwards to ──►  127.0.0.1:48521
                                                     │
                                  Claude Status itself (the desktop app's process)
                                  serves the board it is showing, nothing else
```

- **Same board, not a copy.** The phone shows exactly what the desktop Worker Board shows: friendly names, `#1/#2` numbering, ordering setting, summary line, status text and colors, short error types, timers and *No Claude workers*. It comes straight from the running desktop app, so the desktop's own cleanup (closed or crashed sessions) and dismissals apply automatically.
- **Live.** Changes appear within about a second without refreshing; timers tick on the phone. Opening or reopening the page always fetches the current board first.
- **Dismiss DONE rows from either screen.** Tap the **×** on a DONE row on the phone and it disappears from both screens; dismiss it on the desktop and it disappears from the phone. As on the desktop, this only removes Claude Status's row: Claude isn't stopped or messaged and no session or file is touched, and a later prompt in that session brings the worker back. WORKING, NEEDS YOU and ERROR rows can't be dismissed.
- **Honest connection state** (top right): **LIVE**, **CONNECTING…**, **RECONNECTING…** or **OFFLINE**. When the connection is lost, the page keeps the last board but dims it, freezes its timers, shows *Last update 11:55:07 (3s ago). The board below may be out of date.* and says why (laptop unreachable, or Claude Status not answering). Dismissing is disabled until it's LIVE again, and a dismiss is never queued for later. It reconnects by itself and refreshes as soon as the laptop answers again, including when you come back to the page after Android put it in the background.
- **No phone notifications**: no sounds, vibration, notification prompts or background activity. (Desktop sounds and notifications are unchanged.)
- **Two views: List or Creatures** (switch at the top). See below.

### Creatures view

Tap **Creatures** at the top of the page for a grid of little pixel creatures, one per worker, instead of rows. **List** is the default; the phone's browser remembers your choice (only that choice is stored, and only on the phone). Switching views keeps the same live connection and board.

- **Same board.** Same workers, names, `#n` numbers, order, status text, timers, cleanup and **×** dismissal as the list. Each card shows the name (long names wrap) and the real status line, e.g. *WORKING · 04:32*. No made-up numbers: there are no context or progress percentages.
- **Each worker has its own creature.** A worker keeps the same body colour and hat (party hat, beanie, top hat, propeller cap, crown, cowboy hat, headphones, chef hat, bow, antennae, wizard hat, cap or flower) for as long as it's on the board, on every phone and in both views. Up to 13 workers all wear different hats. Restarting Claude Status deals out new creatures.
- **Creatures act out their status** (the status text and card border colour always say it too):

  | Status | Creature |
  |---|---|
  | WORKING | small busy bounce, stepping feet, the occasional blink |
  | NEEDS YOU | waves a hand, with a red **!** |
  | DONE | happy face; hops with sparkles when it finishes while you're watching |
  | ERROR | worried face, sweat drop, striped red card; a small shake when it fails while you're watching |
  | (no workers) | one sleeping creature, *No Claude workers* |

  Motion is small and stops while the page is in the background or the board isn't live. With **Reduce motion** / **Remove animations** turned on in Android's accessibility settings there's no animation at all.

**Tap a creature to bring up its window on the laptop.** It hops right away, and the laptop then does exactly what clicking that worker on the desktop board does (see [Clicking a worker](#clicking-a-worker)): it finds **that session's** window, telling apart two workers in the same project, and brings it to the front, or refuses rather than guess. Nothing is opened or launched on the phone or the laptop; it's the existing window or nothing. The phone then says what the laptop reported:

- *Brought up "Portal" on the laptop.*: Windows confirmed that window is now in front.
- *The laptop is locked. Unlock it to switch windows.*: nothing was attempted (see below).
- *Windows didn't allow bringing "Portal" forward.*: Windows refused the switch.
- *Couldn't identify that worker's window…*, *That Claude session is no longer running.*, *That worker is no longer on the board.*: as on the desktop, nothing was focused.

Repeated taps send one request. A tap while the page isn't LIVE says so and sends nothing. Tapping a creature never dismisses it; only the **×** on a DONE card dismisses, and that never brings up a window.

**What to expect from the laptop**

- **Locked:** Windows doesn't let any app switch windows on the lock screen, and Claude Status doesn't try to get around that or unlock anything. Tapping while it's locked just tells you it's locked.
- **Unlocked but not in use:** the window comes forward on the laptop's screen. Nobody needs to be at the laptop, but you'll only see the result when you're back at it.
- **Windows may refuse.** Windows limits which programs may bring a window to the front. Claude Status is allowed when you click it on the desktop; a request from the phone isn't a click, so Windows can occasionally refuse (for example while a full-screen app or a system prompt is in front). The phone then says so instead of claiming success.

### Requirements

- The Claude Status desktop app, running on the laptop (the phone dashboard runs inside it).
- [Tailscale](https://tailscale.com/) on the laptop **and** the phone, signed in to the same tailnet, with MagicDNS on (the default). The phone and laptop can be on different networks (e.g. mobile data). HTTPS certificates are **not** needed.
- Any mobile browser (tested with Chrome's Android emulation). Nothing else to install.

### Turn it on

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\phone-setup.ps1"
```

This:

1. sets `"phoneDashboard": true` in `config.json` (or tick **Settings → Phone dashboard**). Claude Status then starts its dashboard on `127.0.0.1:48521` within a few seconds; the script starts Claude Status if it isn't running;
2. runs `tailscale serve --bg --http=80 http://127.0.0.1:48521`, which makes it reachable **only from devices in your tailnet** (never Tailscale Funnel, never router port forwarding). If tailnet port 80 is already used by another `tailscale serve` entry, the script stops and changes nothing there; use `-HttpPort 8080` instead;
3. prints the address to open on the phone, e.g. `http://thund3rhead.tail33ffb4.ts.net/`.

If the `tailscale serve` step fails (for example because Tailscale wants an administrator prompt), run it yourself:

```powershell
tailscale serve --bg --http=80 http://127.0.0.1:48521
```

Expected result: `tailscale serve status` lists `http://<laptop>.<tailnet>.ts.net/` → `proxy http://127.0.0.1:48521`.

### Open it on the phone

1. Make sure the Tailscale app on the phone is connected.
2. Open the address printed by `phone-setup.ps1` (find it again any time with `phone-setup.ps1 -Status`). It's `http://` + the laptop's MagicDNS name; on the phone it's also listed in the Tailscale app under the laptop's name.
3. Optional: browser menu → **Add to Home screen** for a one-tap shortcut. (It's a normal web page: no offline mode or caching, so it never shows old data as current.)

### Lifecycle

- The dashboard is part of the desktop app: it starts when Claude Status starts (if enabled) and stops when you exit it. There's no separate service or background process, and only one can run (one Claude Status per user; the port is opened exclusively).
- If the dashboard can't start (e.g. another program uses port 48521), the desktop app works normally and its right-click menu shows *Phone dashboard: not running: port 48521 is already in use*; set another `"phonePort"` in `config.json` and run `phone-setup.ps1` again. Its right-click menu always shows the dashboard's state.
- The hooks and the desktop board never depend on the phone side.
- `tailscale serve --bg` persists across restarts. While Claude Status isn't running, the phone shows **OFFLINE** with *Claude Status isn't answering*.

### Locking, sleep and disconnection

- **Locked laptop: works.** Claude Status, Claude Code and Tailscale keep running while Windows is locked. Only *bringing up a window* (tapping a creature) waits until it's unlocked.
- **Sleeping / hibernating laptop: doesn't.** Nothing runs while it sleeps; the phone shows **OFFLINE** with the time of the last update, and reconnects on its own once the laptop wakes. To use it away from the desk, keep the laptop plugged in and set *Sleep* to *Never* (when plugged in) in Windows power settings.
- **Laptop or phone off Tailscale / no network:** OFFLINE with the last update time; reconnects automatically when the connection returns.

### How synced dismissal stays safe

Each DONE row on the phone carries an opaque token for that particular DONE occurrence (that session, that DONE, that last update). When you tap **×**, Claude Status re-reads the session's state file **while holding it locked against the hook's writes**, checks that it is still that exact DONE, and only then deletes it through the same lock. So if the session got a new prompt in the meantime (even a moment before your tap arrived), the dismiss is refused (*Not dismissed: that worker has changed*) and the worker stays. Repeating a dismiss is harmless. The desktop **×** uses the same check.

### Security and privacy

- The dashboard listens on `127.0.0.1` only; it's reachable from the phone solely through `tailscale serve`, i.e. by devices signed in to your tailnet. It is not exposed on your Wi-Fi/LAN.
- Requests must be addressed to `127.0.0.1`/`localhost` or this laptop's own Tailscale name/IP (protects against DNS-rebinding pages). The two actions (dismiss, and bring up a worker's window) are POST-only and each requires a same-origin `Origin`, a JSON body and its own custom header, so other websites can't trigger them; there's no CORS. The page has a strict Content-Security-Policy and isn't cached.
- The only things the phone can make happen are dismissing a current DONE row and bringing up a current worker's existing window. Both name the worker by a random token from the current board; the laptop looks up the worker and picks the window itself. The browser can't name files, paths, programs, commands, process IDs or URLs; extra fields are ignored.
- The phone receives only what it displays: friendly name, state, when the state began, the short error type, a creature number (0–64), and random-looking tokens. No session IDs, process IDs, folder paths, window data, prompts or Claude output. Nothing is logged.
- No accounts, cloud hosting, database or paid services.

### Turn it off / remove it

```powershell
# turn off (removes the tailscale serve entry phone-setup.ps1 created, sets phoneDashboard=false)
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\phone-setup.ps1" -Disable
```

Or untick **Settings → Phone dashboard** (stops it immediately; the `tailscale serve` entry then just answers "not available" until you remove it with `tailscale serve --http=80 off`). `uninstall.ps1` also removes the serve entry created by `phone-setup.ps1`.

### Troubleshooting

| What you see | What to do |
|---|---|
| Page doesn't load at all | Tailscale connected on the phone? Laptop awake? Check `phone-setup.ps1 -Status` on the laptop: it shows whether the dashboard answers locally and the `tailscale serve` entry. |
| **OFFLINE** *Claude Status isn't answering* | The laptop is reachable but Claude Status isn't running, or the dashboard is off. Start Claude Status; right-click its window and check *Phone dashboard: …*. |
| **OFFLINE** *Can't reach the laptop* | Laptop asleep, offline, or Tailscale disconnected on either device. The board shown is the last known one; it refreshes by itself once reachable. |
| **RECONNECTING…** with a *Last update* time | Brief interruption; rows are dimmed and not current until it says LIVE again. |
| *This address isn't one Claude Status accepts* | Use the MagicDNS address from `phone-setup.ps1 -Status`; other names pointing at the laptop are refused on purpose. |
| Tapping a creature says *The laptop is locked* | Expected: Windows won't switch windows on the lock screen. Unlock the laptop, or wait until you're back. |
| Tapping a creature says *Couldn't identify that worker's window* | Same as on the desktop: send that worker a prompt from its window once (VS Code with several windows open), then it works. |
| Tapping a creature says *Windows didn't allow…* | Windows refused to switch windows from a background request. Try again, or click the worker on the desktop board. |
| Right-click menu says *port 48521 is already in use* | Set `"phonePort": 48522` (any free port above 1024) in `config.json`, then `phone-setup.ps1 -Disable` and `phone-setup.ps1` again so `tailscale serve` points at the new port. |

## How it works

Claude Status uses Claude Code's official **lifecycle hooks**. It does **not**:

- scrape the screen, read window titles or watch VS Code pixels;
- poll Anthropic or make any outgoing network requests (the optional [phone dashboard](#phone-dashboard-optional) only *listens*, on 127.0.0.1);
- store your prompts or Claude's responses. (Claude Code hands each hook its event data, which for a new prompt includes the prompt text. `hook.ps1` only extracts a few fields from it and never saves the prompt.)

```text
Claude Code
    │
    │ lifecycle hooks (UserPromptSubmit, Stop, StopFailure, Notification, ...)
    ▼
hook.ps1
    │ writes a tiny JSON file (state, folder name, timestamps, Claude process ID)
    ▼
state\sessions\<session-id>.json
    │
    │ file-system watcher (instant, no polling)
    ▼
Claude Status (Worker Board / Compact)
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
| `SessionEnd` | a **DONE** session is kept (until you dismiss it); a WORKING / NEEDS YOU / ERROR session's file is removed |

Ordinary notifications (such as the "Claude is idle" reminder) are ignored. The hooks that don't need to block (`PermissionRequest`, `PreToolUse`, `PostToolUse*`) run asynchronously so they never slow Claude down. The hook configuration itself is unchanged from earlier versions.

### Session lifecycle in detail

| Situation | What happens |
|---|---|
| WORKING → SessionEnd | row removed |
| NEEDS YOU → SessionEnd | row removed |
| ERROR → SessionEnd | row removed |
| DONE → SessionEnd | row **kept** until you dismiss it |
| DONE dismissed → SessionEnd | nothing (already gone) |
| DONE dismissed → new prompt in the same session | the session reappears as WORKING |
| Claude process crashed / terminal killed while WORKING, NEEDS YOU or ERROR | row removed within seconds (its recorded Claude process is gone) |
| No update for `staleHours` while WORKING, NEEDS YOU or ERROR | row removed |
| DONE for any length of time | kept; `staleHours` never removes DONE rows |

### Privacy: what is stored

Each session file contains only:

```json
{"v":2,"session_id":"…","state":"WORKING","event":"UserPromptSubmit","detail":"","project":"my-project",
 "updated":"2026-10-25T21:00:00Z","state_since":"2026-10-25T20:55:28Z","first_seen":"2026-10-25T20:40:02Z",
 "claude_pid":12345,"claude_started":"2026-10-25T20:39:58.1234567Z"}
```

| Field | Meaning |
|---|---|
| `project` | Just the name of the folder Claude is working in (not the full path). |
| `detail` | Only the notification type, the name of the tool awaiting approval (e.g. `Bash`), or the API error type. Stored only as a short identifier (letters, digits, `_ . : -`). |
| `updated` | When the last hook event arrived. |
| `state_since` | When the current state began (drives the timers). New in v2. |
| `first_seen` | When the session first appeared (keeps `#1/#2` numbering stable). New in v2. |
| `claude_pid`, `claude_started` | The process ID and start time of the Claude Code process for this session, used only to find its window and to notice when it has exited. New in v2. |

**Prompts, Claude's messages, tool inputs, Bash commands, tool output, file contents, transcript paths and window titles are never written anywhere.**

The other files Claude Status writes, all on your computer only:

- `state\windows.json`: for sessions in an app with several windows (e.g. two VS Code windows), the window that was in front when you last prompted that session: `{"<session-id>": {"hwnd": 67076, "pid": 21748, "started": "…"}}`. That's a window handle number plus the owning process ID and start time, with no window title or content. Entries are removed when their session disappears from the board.
- `position.txt` (window position), `config.json` (your settings), the `settings.json` backups the installer/uninstaller make next to your settings, and, only if you turn on debugging, `debug.log`.

Nothing is sent anywhere. Older v1 state files from previous versions are read as-is (their timers start from their last update) and turn into v2 files the next time their session fires a hook; you never need to delete them.

## Features

- **Worker Board** (default): one row per Claude session with friendly names, live timers, urgency or recency ordering, DONE rows that stay until dismissed, and click-to-focus
- **Compact**: the original tiny pill (124 × 38 px), still available with one click
- True always-on-top windows (native `WS_EX_TOPMOST`, no polling) that **never steal keyboard focus** when anything changes
- Auto-sizing to the number of workers; scrolls only the list once it reaches the bottom of the screen; DPI-aware (tested at 100/125/150/200 %)
- Draggable anywhere; remembers its position
- Subtle sound when Claude is DONE; Windows notifications for NEEDS YOU and ERROR (all configurable)
- System-tray icon whose color matches the most important state; the **–** button minimizes the window to it (monitoring keeps running), a click on the icon brings it back
- Uses real Claude Code lifecycle hooks, works **globally across all your projects**
- User-level install, no administrator rights
- No Node.js, Vite, Python, or other runtime needed; it uses only what ships with Windows
- No network connection required; negligible CPU while idle
- Optional private [phone dashboard](#phone-dashboard-optional) over Tailscale: the same board on your phone, with synced DONE dismissal

## Requirements

| Requirement | Notes |
|---|---|
| **Windows 10 or 11** | Uses WinForms, Windows notifications, and the Startup folder. |
| **Windows PowerShell 5.1** | Built into Windows (`powershell.exe`). PowerShell 7 is *not* required; if you run the installer from PowerShell 7 it automatically hands off to 5.1. |
| **.NET Framework 4.5+** | Built into Windows 10/11. The indicator's small C# source is compiled on the fly by PowerShell's `Add-Type`; no Visual Studio or SDK needed. |
| **Claude Code** with hooks support | The CLI and/or the Claude Code VS Code extension. Developed and tested with Claude Code **2.1.308–2.1.310**. Needs a version that supports the `StopFailure` and `PermissionRequest` hook events. Clicking a worker to focus its window uses the `CLAUDE_PID` variable Claude Code sets for hooks. |

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

The Worker Board appears near the bottom-right of your screen. Send Claude a prompt and a 🟡 row appears.

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
- your `config.json` choices are kept (new options are added with their defaults: when updating from a version without Worker Board, you get `startupView`, `workerSort`, `showWorkerSummary` and `projectAliases`, so **Worker Board becomes your view**; set *Startup view* to Compact in Settings if you prefer the old look);
- your saved window position, autostart shortcut and existing session files are kept;
- Claude Status hooks are never duplicated, and old entries are replaced;
- if nothing changed, `settings.json` isn't touched (and no backup is made). Updating to the Worker Board version doesn't change the hooks;
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
| **Stop** | Click the **×** in the header (top-right), right-click it → **Exit**, or right-click the tray icon → **Quit** |
| **Minimize to tray** | Click the **–** next to the × (in Compact it appears on hover). Everything keeps updating; the tray icon's color and notifications still work |
| **Move** | Drag the header (Worker Board) or anywhere (Compact) |
| **Switch view** | The small icon next to ⚙, or right-click → **View** |
| **Bring a worker's window forward** | Click its row |
| **Dismiss a finished worker** | Click the **×** at the end of its DONE row |
| **Settings** | ⚙, or right-click → **Settings…** |
| **Reset position** | Right-click → **Reset position** |
| **See all sessions** | The Worker Board, or right-click the indicator |
| **Bring it back if hidden** | Left-click the tray icon, or right-click it → **Open** (same window, same view and position) |

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

`%USERPROFILE%\.claude\status-indicator\config.json`. The view, ordering and summary options can be changed from the ⚙ Settings window; everything else is edited here. The indicator picks up saved changes automatically.

```json
{
  "soundOnDone": true,
  "toastOnDone": false,
  "soundOnNeedsYou": true,
  "toastOnNeedsYou": true,
  "soundOnError": true,
  "toastOnError": true,
  "staleHours": 24,
  "startupView": "workerBoard",
  "workerSort": "urgency",
  "showWorkerSummary": false,
  "phoneDashboard": false,
  "phonePort": 48521,
  "projectAliases": {
    "Toah_Nipi_Booking_Software": "Booking Software",
    "Toah_Nipi_Website": "Website",
    "Toah_Nipi_Portal": "Portal",
    "Guest_Booking_Form": "Guest Form"
  }
}
```

| Option | Meaning |
|---|---|
| `soundOn…` | Play a Windows system sound on that state. |
| `toastOn…` | Show a Windows notification on that state. |
| `staleHours` | Forget WORKING / NEEDS YOU / ERROR sessions that haven't changed for this long (e.g. a crashed session that never sent `SessionEnd`). **DONE sessions are never removed automatically**; dismiss them with their ×. |
| `startupView` | `"workerBoard"` (default) or `"compact"`: the view Claude Status opens in. |
| `workerSort` | `"urgency"` (default: NEEDS YOU, ERROR, WORKING, DONE) or `"recent"` (most recently updated first). Used by both views. |
| `showWorkerSummary` | `true` shows a one-line summary under the Worker Board heading. Default `false`. |
| `phoneDashboard` | `true` runs the [phone dashboard](#phone-dashboard-optional) on 127.0.0.1. Default `false`. Also in Settings. |
| `phonePort` | Its local port (default `48521`). |
| `projectAliases` | Friendly names: `"Folder_Name": "Friendly Name"`. The folder name is matched case-insensitively against the name of the folder Claude is working in. Folders without an alias get their underscores replaced with spaces. Several folders may share one alias; their workers are then numbered together. |

Sounds and notifications are the same as before: one per state change, never duplicated per row. Notifications follow Windows' Do Not Disturb / Focus settings and appear as coming from "Windows PowerShell".

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\uninstall.ps1"
```

Or, from your cloned repository (it does the same thing):

```powershell
powershell -ExecutionPolicy Bypass -File .\src\uninstall.ps1
```

The uninstaller:

1. stops the indicator (and with it the phone dashboard), and removes the `tailscale serve` entry `phone-setup.ps1` created, if any;
2. removes the autostart shortcut, if it points to this install;
3. backs up `settings.json`, then removes **only** Claude Status's hook entries;
4. deletes `%USERPROFILE%\.claude\status-indicator\` (including session files, `windows.json` and settings).

It does **not** delete your cloned repository, Claude Code, the `.claude` folder, your other settings, or your other hooks. It's safe to run even if parts of Claude Status are already missing.

## Works with

- ✅ **Claude Code CLI** (any terminal: Windows Terminal, classic console, VS Code's integrated terminal)
- ✅ **Claude Code VS Code extension**: hooks from `~/.claude/settings.json` apply there too.
- Works for every project and repository, because the hooks are installed at user level.

## Limitations

- **Interrupting Claude (Esc)** doesn't fire a hook, so the worker stays 🟡 WORKING until your next prompt (or until the session is closed).
- **Permission prompts inside the VS Code extension**: the extension handles approvals in its own UI, and the docs don't guarantee the `PermissionRequest` / `Notification` hooks fire there. If NEEDS YOU never appears for approvals in VS Code, that's why. WORKING / DONE / ERROR work normally.
- In **auto / bypass permission modes**, NEEDS YOU only appears when Claude actually shows you a prompt.
- Notifications and sounds only happen while the indicator is running.
- **Clicking a worker** focuses its top-level window, not a specific VS Code editor tab or Windows Terminal tab. With several VS Code windows open, a session becomes clickable once you've sent it a prompt while Claude Status was running (see [Clicking a worker](#clicking-a-worker)). Sessions that have ended can't be focused.
- A DONE worker stays on the Worker Board until you dismiss it, even after its session has closed; with Compact as your only view, dismiss old ones occasionally from the Worker Board (Compact counts them in *(N sessions)*).
- If a session ends abruptly without its Claude process exiting, its last WORKING / NEEDS YOU / ERROR state lingers until `staleHours` passes.
- Each hook call starts a short-lived `powershell.exe` (about 0.3–0.5 s). The blocking ones (`UserPromptSubmit`, `Stop`, `StopFailure`, `Notification`, `SessionEnd`) add that small delay; the rest run in the background.

## Troubleshooting

**The indicator never changes state.**
Run `/hooks` in Claude Code and check that the Claude Status hooks are listed under `UserPromptSubmit`, `Stop`, etc. Open sessions need `/hooks` or a restart after installing.

**Clicking a worker says "Couldn't identify that worker's window".**
Its app (usually VS Code) has several windows and Claude Status hasn't yet seen which one hosts that session. Send that session a prompt from its window (with Claude Status running) and try again. "That Claude session is no longer running" means the session was closed; dismiss its row if it's DONE.

**I want to see whether hooks fire.**
Create an empty file named `debug.on` in `%USERPROFILE%\.claude\status-indicator\`. Each hook call is then logged (time, event, state, session ID only) to `debug.log` in that folder. Delete `debug.on` when done.

**The window disappeared off-screen.**
Left-click the tray icon (it moves the window back onto the screen if needed), then right-click the window → **Reset position** if you want its default spot.

**"Running scripts is disabled on this system".**
Use the commands exactly as shown above; they include `-ExecutionPolicy Bypass`, which applies only to that one command.

## Repository layout

```text
install.ps1              installer / updater (run from the repo)
src/
  hook.ps1               called by Claude Code hooks; writes state files
  indicator.cs           the Worker Board / Compact window (C#, WinForms)
  phone.cs               optional phone dashboard server (127.0.0.1 only) + race-safe DONE dismissal
  phone.html/.css/.js    the phone page (List and Creatures views; the creature pixel art is drawn by phone.js)
  phone-setup.ps1        turn the phone dashboard on/off and set up `tailscale serve`
  indicator.ps1          compiles indicator.cs with Add-Type and runs it
  start-indicator.vbs    launches the indicator with no console window
  stop-indicator.ps1     stops the indicator
  autostart.ps1          enable/disable start with Windows
  uninstall.ps1          complete, safe uninstall
  config.json            default settings
tests/
  run-tests.ps1          automated tests (temporary folders only; never touches your real install)
  live-tests.ps1         window/focus tests, started hidden by run-tests.ps1 -Live
  common.ps1             shared test helpers
  phone-browser.mjs      phone page checks in headless Chrome/Edge (run-tests.ps1 -Browser)
```

Runtime files (`state/`, `position.txt`, `debug.log`) are created only in the install folder and are git-ignored.

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1          # ~1 minute
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -Live    # + opens test windows and moves focus (~30 s; don't type meanwhile)
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -E2E     # + one real `claude -p` prompt against a temporary install
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -Browser # + phone page in headless Chrome at Android sizes (needs Node.js 22+)
```

They cover the hook lifecycle and privacy (run as real child processes with secret-laden inputs), aliases, numbering, both orderings, timers, v1 compatibility, DONE retention and dismissal, layout at 100–200 % DPI with 1/6/90 workers, worker → window identification (terminals, multi-window apps, closed windows, reused process IDs), focus stealing, and install → update → re-install → uninstall against a temporary Claude config folder. The phone dashboard tests cover desktop/phone parity, dismissal in both directions, the delayed-dismiss race and 400 randomized hook-write/dismiss interleavings, malformed and cross-site requests, Host/Origin checks, start/stop/port conflicts, and that phone.js formats timers exactly like the desktop. For the Creatures view they check that a tap reaches the desktop's own click-on-a-worker routine for exactly that worker (and that a desktop click uses the same routine), with window lookup and activation **mocked** so the tests never move your focus. They also check that rapid taps make one request, that taps and **×** never trigger each other, stale and malformed references, the action's request protections, and stable, distinct creature looks. `-Browser` additionally drives the real page at 320/360/412 px widths in both views through disconnects, a 502 from `tailscale serve`, resume and recovery, reduced motion, keyboard use and background pausing, saving `phone-*.png` screenshots. These are emulated phones; Tailscale itself and a physical Android device are not part of the automated tests. `-RenderDir <folder>` saves PNG renders of the board for a visual check.

## Ideas for later

Worker Board already covers what this section used to list as future work (a row per session, most-urgent-first ordering, dismissing DONE rows, per-session timers). Possible next steps:

- an in-app alias editor (aliases are edited in `config.json` for now);
- focusing the exact Claude tab inside a VS Code window, if Claude Code ever offers a supported way to do that without resuming the session.
