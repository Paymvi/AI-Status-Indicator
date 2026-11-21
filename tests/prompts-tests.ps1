# Prompts (prompts.cs + the phone's api/prompts): saving, editing, deleting, search, persistence, the desktop tab
# (collapse/expand, Copy) and the phone's read-only view. Temporary folders only. Dot-sourced by run-tests.ps1, or alone:
#   powershell -ExecutionPolicy Bypass -File .\tests\prompts-tests.ps1 [-Browser]
param([switch]$Browser, [string]$RenderDir)
if (-not (Get-Command Check -ErrorAction SilentlyContinue)) {
    if ($PSVersionTable.PSEdition -eq 'Core') { & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args; exit $LASTEXITCODE }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-pr-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    . (Join-Path $PSScriptRoot 'common.ps1')
    $prStandalone = $true
}
if (-not $RenderDir) { $RenderDir = Join-Path $tmp 'renders' }
$null = New-Item -ItemType Directory -Force $RenderDir
if (-not ('THttp' -as [type])) {
    Add-Type @'
using System; using System.IO; using System.Text; using System.Net.Sockets;
public static class THttp {
    public static string Send(int port, string raw, int timeoutMs) {
        using (var c = new TcpClient()) {
            c.ReceiveTimeout = timeoutMs; c.Connect("127.0.0.1", port);
            var s = c.GetStream(); var b = Encoding.UTF8.GetBytes(raw); s.Write(b, 0, b.Length);
            using (var sr = new StreamReader(s, Encoding.UTF8)) return sr.ReadToEnd();
        }
    }
    public static int Status(string r) { var p = r.Split(' '); int c; return p.Length > 1 && int.TryParse(p[1], out c) ? c : 0; }
    public static string Body(string r) { int i = r.IndexOf("\r\n\r\n"); return i < 0 ? "" : r.Substring(i + 4); }
    public static int FreePort() { var l = new TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
}
'@
}

Section 'Prompts: save, edit, delete, search, persistence, desktop tab, phone view'

$PS = [ClaudeStatus.PromptStore]
$pfile = Join-Path $tmp 'pr-store\state\prompts.json'
$store = New-Object ClaudeStatus.PromptStore($pfile)
$script:clock = [DateTime]::new(2026, 11, 20, 9, 0, 0, [DateTimeKind]::Utc)
$store.Now = [Func[DateTime]] { $script:clock = $script:clock.AddMinutes(1); $script:clock }
$crText = "Review this diff.`r`n`r`n  - keep the indentation`r`n  - and the blank line`t(tab) ""quotes"" {braces} & <html> `r`n"

# --- required fields ---
$errs = @()
foreach ($c in @(@('', 'x'), @('   ', 'x'), @('t', ''), @('t', " `r`n "))) { try { $null = $store.Save($null, $c[0], $c[1]); $errs += 'saved' } catch { $errs += $_.Exception.InnerException.Message } }
Check 'a title and prompt text are both required (blank or whitespace-only is refused, nothing is written)' (($errs -join '|') -eq 'Give the prompt a title.|Give the prompt a title.|Write the prompt text.|Write the prompt text.' -and -not (Test-Path $pfile)) ($errs -join '|')

# --- create, order, exact text ---
$a = $store.Save($null, '  Code review  ', $crText)
$b = $store.Save($null, 'Write tests', "Add tests for the change.")
$c = $store.Save($null, 'Explain', "Explain this code like I'm new to the project.")
$all = $store.All()
Check 'new prompts are saved; newest first; the title is trimmed, the text kept exactly (line breaks, spaces, tabs, quotes)' (($all | ForEach-Object { $_.Title }) -join ',' -eq 'Explain,Write tests,Code review' -and $all[2].Text -ceq $crText) (($all | ForEach-Object { $_.Title }) -join ',')
Check 'the file is written (atomically, no temp files left)' ((Test-Path $pfile) -and @(Get-ChildItem (Split-Path $pfile) -Filter '*.tmp').Count -eq 0)

# --- edit one: only that one changes, and it moves to the top ---
$before = @{}; foreach ($p in $store.All()) { $before[$p.Id] = $p.Title + '|' + $p.Text + '|' + $p.UpdatedUtc.Ticks }
$null = $store.Save($b, 'Write unit tests', "Add unit tests.`r`nCover the edge cases.")
$all = $store.All()
$eb = $all | Where-Object { $_.Id -eq $b }
Check 'editing a prompt changes only that prompt; recently updated goes first' ($all[0].Id -eq $b -and $eb.Title -eq 'Write unit tests' -and $eb.Text -eq "Add unit tests.`r`nCover the edge cases." -and
    (($all | Where-Object { $_.Id -ne $b }) | Where-Object { $before[$_.Id] -ne ($_.Title + '|' + $_.Text + '|' + $_.UpdatedUtc.Ticks) }).Count -eq 0 -and $all.Count -eq 3)
$gone = try { $null = $store.Save('nope', 't', 'x'); 'saved' } catch { $_.Exception.InnerException.Message }
Check 'saving an edit to a prompt that no longer exists is refused (it is not re-created)' ($gone -eq 'This prompt was deleted.' -and $store.All().Count -eq 3)

# --- search ---
$m = { param($q) (($store.All() | Where-Object { $PS::Matches($_, $q) }) | ForEach-Object { $_.Title }) -join ',' }
Check 'search covers titles and text, ignores case and outer spaces; blank shows everything' ((& $m 'REVIEW') -eq 'Code review' -and (& $m 'edge cases') -eq 'Write unit tests' -and (& $m '  new to ') -eq 'Explain' -and (& $m 'zzz') -eq '' -and (& $m '') -eq 'Write unit tests,Explain,Code review')

# --- persistence: a fresh store (an app restart) reads the same prompts ---
$re = New-Object ClaudeStatus.PromptStore($pfile)
$r = $re.All()
Check 'prompts survive a restart: same ids, titles, exact text, times and order' ($r.Count -eq 3 -and (($r | ForEach-Object { $_.Id + $_.Title + $_.Text + $_.UpdatedUtc.ToUniversalTime().Ticks }) -join '|') -ceq (($store.All() | ForEach-Object { $_.Id + $_.Title + $_.Text + $_.UpdatedUtc.ToUniversalTime().Ticks }) -join '|') -and $re.Notice -eq '')

# --- your status marker ---
$orderBefore = ($store.All() | ForEach-Object { $_.Id + '@' + $_.UpdatedUtc.Ticks }) -join ','
$store.SetStatus($a, 'red')
Check 'setting a status saves it without moving the prompt or changing its update time' (($store.All() | Where-Object { $_.Id -eq $a }).Status -eq 'red' -and (($store.All() | ForEach-Object { $_.Id + '@' + $_.UpdatedUtc.Ticks }) -join ',') -eq $orderBefore)
$bogus = try { $store.SetStatus($a, 'purple'); 'set' } catch { $_.Exception.InnerException.Message }
$ghost = try { $store.SetStatus('nope', 'green'); 'set' } catch { $_.Exception.InnerException.Message }
Check 'only none / green / yellow / red are accepted; a deleted prompt''s status can''t be set' ($bogus -match 'Unknown status' -and $ghost -eq 'This prompt was deleted.' -and ($store.All() | Where-Object { $_.Id -eq $a }).Status -eq 'red')
$null = $store.Save($a, 'Code review', $crText)
Check 'editing the title or text keeps the status; the status survives a restart' (($store.All() | Where-Object { $_.Id -eq $a }).Status -eq 'red' -and ((New-Object ClaudeStatus.PromptStore($pfile)).All() | Where-Object { $_.Id -eq $a }).Status -eq 'red')
$oldf = Join-Path $tmp 'pr-old\prompts.json'; $null = New-Item -ItemType Directory -Force (Split-Path $oldf)
[IO.File]::WriteAllText($oldf, '[{"Id":"o1","Title":"Old","Text":"From before statuses","UpdatedUtc":"\/Date(1700000000000)\/"},{"Id":"o2","Title":"Odd","Text":"x","Status":"blue","UpdatedUtc":"\/Date(1700000000000)\/"}]')
$olds = (New-Object ClaudeStatus.PromptStore($oldf)).All()
Check 'prompts saved before statuses existed (or with an unknown one) load with no status' ($olds.Count -eq 2 -and @($olds | Where-Object { $_.Status -ne '' }).Count -eq 0)

# --- delete one ---
Check 'deleting a prompt removes only that one, on disk too; deleting it again is a no-op' ($store.Delete($c) -and -not $store.Delete($c) -and
    ((New-Object ClaudeStatus.PromptStore($pfile)).All() | ForEach-Object { $_.Id }) -join ',' -eq "$a,$b")   # (a was edited last, above)

# --- an unreadable file is never overwritten ---
$bad = Join-Path $tmp 'pr-bad\prompts.json'; $null = New-Item -ItemType Directory -Force (Split-Path $bad); [IO.File]::WriteAllText($bad, '[{"Id":"x","Title":"Half')
$bs = New-Object ClaudeStatus.PromptStore($bad)
$kept = @(Get-ChildItem (Split-Path $bad) -Filter 'prompts.json.unreadable-*.json')
Check 'an unreadable prompts file is kept aside (copy) and the tab says so' ($kept.Count -eq 1 -and [IO.File]::ReadAllText($kept[0].FullName) -eq '[{"Id":"x","Title":"Half' -and $bs.Notice -match 'couldn''t be read' -and $bs.All().Count -eq 0)

# --- the desktop tab ---
$vfile = Join-Path $tmp 'pr-view\prompts.json'
$vs = New-Object ClaudeStatus.PromptStore($vfile)
$vs.Now = [Func[DateTime]] { $script:clock = $script:clock.AddMinutes(1); $script:clock }   # distinct times: the real clock ticks only every ~15 ms
$qe = New-Object ClaudeStatus.QueueEngine((Join-Path $tmp 'pr-queue'))
$vcal = New-Object ClaudeStatus.CalendarSource; $vcal.Log = New-Object ClaudeStatus.ActivityLog((Join-Path $tmp 'pr-act'))
$qdesk = New-Object ClaudeStatus.QuestionnaireDesk((Join-Path $tmp 'pr-qn'), (Join-Path $tmp 'pr-qn-out'))
function Walk($c, $acc) { foreach ($k in $c.Controls) { [void]$acc.Add($k); Walk $k $acc } }
function Ctl($v) { $acc = New-Object System.Collections.ArrayList; Walk $v $acc; $acc }
function Toggles($v) { @(Ctl $v | Where-Object { $_ -is [Windows.Forms.Button] -and $_.AccessibleName -match '\((collapsed|expanded)\)$' } | Sort-Object { $_.Parent.Parent.Top }) }   # as shown, top to bottom
function Shot($qv, $w, $h, $name) { $bmp = New-Object Drawing.Bitmap($w, $h); $qv.DrawToBitmap($bmp, (New-Object Drawing.Rectangle(0, 0, $w, $h))); $bmp.Save((Join-Path $RenderDir $name)); $bmp.Dispose() }

$qv = New-Object ClaudeStatus.QueueView($qe, 1.0, $vcal, $qdesk, $vs)
$qv.Size = New-Object Drawing.Size(900, 600); $null = $qv.Handle; $qv.CreateControl()
$qv.ShowTab(4)
$pv = $qv.Prompts
$tabTexts = (Ctl $qv | Where-Object { $_ -is [Windows.Forms.Button] } | ForEach-Object { $_.AccessibleName }) -join ','
Check 'Queue window: a Prompts tab beside Tasks / Batches / Calendar / Questionnaires, shown when picked' ($pv -ne $null -and $pv.Visible -and $tabTexts -match 'Prompts \(shown\)' -and $tabTexts -match 'Questionnaires,' -and -not $qv.Calendar.Visible) $tabTexts
Check 'empty state when nothing is saved yet' ($pv.EmptyText -match '^No saved prompts yet' -and $pv.EmptyText -match '\+ New prompt')
Shot $qv 900 600 'prompts-desktop-empty.png'

$long = 'A long line that has to wrap: ' + ('wrap me please ' * 40) + "`r`n" + 'Averyveryverylongwordwithoutanyspaces' * 6 + "`r`n`r`n    indented line`r`nlast line"
$i1 = $vs.Save($null, 'Code review', $crText)
$i2 = $vs.Save($null, 'Long one with a very long title that keeps going and going so it has to be cut short in the header row', $long)
$i3 = $vs.Save($null, 'Explain', 'Explain this code.')
$pv.RefreshView()
$tg = Toggles $pv
Check 'every card starts collapsed and shows its title; newest first' ($tg.Count -eq 3 -and ($tg | Where-Object { $_.AccessibleName -match 'expanded' }).Count -eq 0 -and $tg[0].AccessibleName -eq 'Explain (collapsed)' -and $tg[2].AccessibleName -eq 'Code review (collapsed)') (($tg | ForEach-Object { $_.AccessibleName }) -join ' | ')
$bodyOf = { param($t) $t.Parent.Parent.Controls | Where-Object { $_ -is [Windows.Forms.Label] } | Select-Object -First 1 }
$cardH0 = $tg[2].Parent.Parent.Height
$tg[2].PerformClick(); $tg[1].PerformClick()
$tg = Toggles $pv
$b2 = & $bodyOf $tg[2]
Check 'clicking a header expands it (full text, line breaks kept); several stay open at once' ($tg[2].AccessibleName -eq 'Code review (expanded)' -and $tg[1].AccessibleName -match '\(expanded\)$' -and $tg[0].AccessibleName -match 'collapsed' -and
    $b2.Visible -and $b2.Text -ceq $crText -and -not $b2.UseMnemonic -and $tg[2].Parent.Parent.Height -gt $cardH0 + 20) ("h0=$cardH0 h=" + $tg[2].Parent.Parent.Height)
$b1 = & $bodyOf $tg[1]
Check 'long text wraps inside the card (no sideways scrolling): taller than one line, no wider than the list' ($b1.Height -gt 120 -and $b1.Right -le $pv.Width -and -not $pv.Controls[0].HorizontalScroll.Visible) ("h=" + $b1.Height)
Shot $qv 900 600 'prompts-desktop-100.png'
$lp = $tg[0].Parent.Parent.Parent
$wide = @((Toggles $pv) | ForEach-Object { $_.Parent.Parent } | Where-Object { $_.Right -gt $lp.ClientSize.Width })
$wide += @(Ctl $pv | Where-Object { $_.AccessibleName -match '^Delete ' } | Where-Object { $lp.PointToClient($_.PointToScreen([Drawing.Point]::Empty)).X + $_.Width -gt $lp.ClientSize.Width })
Check 'a very long title is cut with … and never widens its card past the list (Copy / Edit / Delete stay in view)' ($wide.Count -eq 0 -and -not $lp.HorizontalScroll.Visible -and $tg[1].AutoEllipsis) ("list=" + $lp.ClientSize.Width + " cards=" + ((Toggles $pv | ForEach-Object { $_.Parent.Parent.Right }) -join ','))
$sb = Ctl $pv | Where-Object { $_ -is [Windows.Forms.TextBox] } | Select-Object -First 1
Check 'the search box fills the toolbar beside + New prompt' ($sb.AccessibleName -match 'Search' -and $sb.Width -gt 400 -and $sb.Visible) ("w=" + $sb.Width)
$tg[1].PerformClick()
Check 'clicking it again collapses it' (((Toggles $pv)[1]).AccessibleName -match '\(collapsed\)$' -and -not (& $bodyOf ((Toggles $pv)[1])).Visible)

# Copy: works collapsed and expanded, puts the exact text on the clipboard, shows Copied!, doesn't toggle the card
$copyOf = { param($t) Ctl $t.Parent | Where-Object { $_ -is [Windows.Forms.Button] -and $_.Text -match '^Cop' } | Select-Object -First 1 }
[Windows.Forms.Clipboard]::SetText('before')
$tg = Toggles $pv
$cp = & $copyOf $tg[0]; $cp.PerformClick()
$got0 = [Windows.Forms.Clipboard]::GetText()
Check 'Copy on a collapsed card copies the exact full text, says Copied!, and leaves the card collapsed' ($got0 -ceq 'Explain this code.' -and $cp.Text -eq 'Copied!' -and ((Toggles $pv)[0]).AccessibleName -match 'collapsed') $got0
$cp2 = & $copyOf ((Toggles $pv)[2]); $cp2.PerformClick()
$got2 = [Windows.Forms.Clipboard]::GetText()
Check 'Copy on an expanded card copies the exact text (line breaks, indentation, tabs) and it stays expanded' ($got2 -ceq $crText -and ((Toggles $pv)[2]).AccessibleName -match 'expanded') ($got2.Replace("`r", '\r').Replace("`n", '\n'))
Check 'Copy, Edit and Delete name their prompt for screen readers; headers are keyboard-focusable buttons' ($cp.AccessibleName -eq 'Copy Explain' -and @(Ctl $pv | Where-Object { $_.AccessibleName -eq 'Delete Explain' }).Count -eq 1 -and ((Toggles $pv) | Where-Object { -not $_.TabStop }).Count -eq 0)

# the status dot: click cycles none → green → yellow, saved at once, the card keeps its place and stays open
$dotOf = { param($title) Ctl $pv | Where-Object { $_ -is [Windows.Forms.Button] -and $_.AccessibleName -like "Status of ${title}: *" } | Select-Object -First 1 }
$d0 = & $dotOf 'Code review'
$n0 = $d0.AccessibleName
$d0.PerformClick(); $n1 = (& $dotOf 'Code review').AccessibleName
(& $dotOf 'Code review').PerformClick(); $n2 = (& $dotOf 'Code review').AccessibleName
$tg = Toggles $pv
Check 'status dot: none → Green → Yellow on click, saved at once; the list order and the open card don''t change' ($n0 -eq 'Status of Code review: none' -and $n1 -eq 'Status of Code review: Green' -and $n2 -eq 'Status of Code review: Yellow' -and
    ($vs.All() | Where-Object { $_.Id -eq $i1 }).Status -eq 'yellow' -and $tg[2].AccessibleName -eq 'Code review (expanded)' -and $tg[0].AccessibleName -match '^Explain ') ("$n0 / $n1 / $n2")
Check 'status dot comes first in the card''s tab order (then title, Copy, Edit, Delete)' ($d0.TabStop -and $d0.TabIndex -lt $tg[2].TabIndex)

# search in the tab
$pv.SearchText = 'INDENTATION'
$tg = Toggles $pv
Check 'search filters the cards by title or text; what was open stays open' ($tg.Count -eq 1 -and $tg[0].AccessibleName -eq 'Code review (expanded)') (($tg | ForEach-Object { $_.AccessibleName }) -join ' | ')
$pv.SearchText = 'no such words'
Check 'no matches: a helpful empty state naming the search' ((Toggles $pv).Count -eq 0 -and $pv.EmptyText -match 'No prompts match .no such words.' -and $pv.EmptyText -match 'Esc')
Shot $qv 900 600 'prompts-desktop-nomatch.png'
$pv.SearchText = ''

# edit / delete from the store show up in the tab without touching the others
$null = $vs.Save($i3, 'Explain simply', 'Explain this code simply.')
$null = $vs.Delete($i2)
$pv.RefreshView()
$tg = Toggles $pv
Check 'after an edit and a delete: the edited prompt on top, the deleted one gone, the other untouched (and still open)' ($tg.Count -eq 2 -and $tg[0].AccessibleName -eq 'Explain simply (collapsed)' -and $tg[1].AccessibleName -eq 'Code review (expanded)' -and (& $bodyOf $tg[1]).Text -ceq $crText)

# window sizes: smallest Queue window and 150%
$qv.Size = New-Object Drawing.Size(560, 380); $qv.PerformLayout()
$tg = Toggles $pv; $cpm = & $copyOf $tg[0]
Check 'at the smallest Queue window (560x380) the header buttons still fit: Copy is fully visible' ($cpm.Right -le $cpm.Parent.ClientSize.Width -and $tg[0].Width -gt 60 -and ($qv.PointToClient($cpm.PointToScreen([Drawing.Point]::Empty))).X + $cpm.Width -le 560) ("copy right=" + $cpm.Right + " head=" + $cpm.Parent.ClientSize.Width + " title=" + $tg[0].Width)
Shot $qv 560 380 'prompts-desktop-min.png'
# The toolbar at the smallest window, on this screen's own scale (as the app sizes it: QueueMinW 560 x the DPI scale).
$dpi = [single]1; $g = $qv.CreateGraphics(); $dpi = $g.DpiX / 96; $g.Dispose()
$tv = New-Object ClaudeStatus.QueueView($qe, $dpi, $vcal, $qdesk, $vs); $null = $tv.Handle; $tv.CreateControl()
$tw = [int](550 * $dpi); $tv.Size = New-Object Drawing.Size($tw, [int](370 * $dpi)); $tv.PerformLayout()
$tb = { param($rx) @(Ctl $tv | Where-Object { $_ -is [Windows.Forms.Button] -and $_.Text -match $rx }) }
$cut = @(& $tb '^(Tasks|Batches|Calendar|Questionnaires|Prompts|\+  New task|. +Pause queue)$' | Where-Object { $tv.PointToClient($_.PointToScreen([Drawing.Point]::Empty)).X + $_.Width -gt $tw } | ForEach-Object { $_.Text })
$ys = @{}; foreach ($k in (& $tb '^(Tasks|Prompts|\+  New task)$')) { $ys[$k.Text] = $tv.PointToClient($k.PointToScreen([Drawing.Point]::Empty)).Y }
Check 'smallest Queue window: every tab and New task / Pause stay in view (the actions wrap to a second row)' ($cut.Count -eq 0 -and $ys.Count -eq 3 -and $ys['Tasks'] -eq $ys['Prompts'] -and $ys['+  New task'] -gt $ys['Tasks']) (($cut -join '|') + ' / ' + (($ys.Keys | ForEach-Object { "$_@" + $ys[$_] }) -join ' ') + " scale=$dpi")
Shot $tv $tw ([int](370 * $dpi)) 'queue-toolbar-min.png'
$tv.Size = New-Object Drawing.Size([int](810 * $dpi), [int](550 * $dpi)); $tv.PerformLayout()
$ys = @{}; foreach ($k in (& $tb '^(Tasks|\+  New task)$')) { $ys[$k.Text] = $tv.PointToClient($k.PointToScreen([Drawing.Point]::Empty)).Y }
Check 'the default Queue window size keeps the tabs and New task on one row' ($ys['Tasks'] -eq $ys['+  New task']) (($ys.Keys | ForEach-Object { "$_@" + $ys[$_] }) -join ' ')
$tv.Dispose()
$qv.Dispose()
$qv = New-Object ClaudeStatus.QueueView($qe, 1.5, $vcal, $qdesk, $vs)
$qv.Size = New-Object Drawing.Size(1350, 900); $null = $qv.Handle; $qv.CreateControl(); $qv.ShowTab(4)
((Toggles $qv.Prompts)[1]).PerformClick()
Shot $qv 1350 900 'prompts-desktop-150.png'
$qv.ShowTab(0)
Check 'switching back to Tasks hides the Prompts tab' (-not $qv.Prompts.Visible)
$qv.Dispose()
$plain = New-Object ClaudeStatus.QueueView($qe, 1.0, $vcal, $qdesk)
Check 'the Queue view without a prompt store (older callers) has no Prompts tab' ($plain.Prompts -eq $null -and @(Ctl $plain | Where-Object { $_ -is [Windows.Forms.Button] -and $_.Text -eq 'Prompts' }).Count -eq 0)
$plain.Dispose()
Info "Prompts tab renders: $RenderDir\prompts-desktop-*.png"

# --- phone API: read-only, same protections as the other phone reads ---
$pp = [THttp]::FreePort()
$proot = Join-Path $tmp 'pr-phone'; $null = New-Item -ItemType Directory -Force $proot
foreach ($fn in 'phone.html', 'phone.css', 'phone.js', 'sw.js', 'workers.png', 'favicon.ico', 'worker-192.png', 'apple-touch-icon.png') { Copy-Item (Join-Path $src $fn) $proot }
$srv = New-Object ClaudeStatus.PhoneServer($proot, $pp, $null, $null, [Func[string[]]] { @() })
$null = $srv.Start(); $srv.Publish((New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'), $false, '')
function PReq([string]$method, [string]$path, [hashtable]$h = @{}, [string]$body = $null) {
    $lines = @("$method $path HTTP/1.1")
    if (-not $h.ContainsKey('Host')) { $lines += "Host: 127.0.0.1:$pp" }
    foreach ($k in $h.Keys) { if ($null -ne $h[$k]) { $lines += "${k}: $($h[$k])" } }
    if ($null -ne $body) { $lines += "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($body))" }
    $raw = [THttp]::Send($pp, (($lines -join "`r`n") + "`r`n`r`n" + $body), 30000)
    return @{ Code = [THttp]::Status($raw); Body = [THttp]::Body($raw) }
}
try {
    $u0 = PReq 'GET' '/api/prompts' @{ 'X-Claude-Status' = 'prompts' }
    Check 'api/prompts without a store (older setup): 503' ($u0.Code -eq 503)
    $srv.Prompts = $vs
    $g0 = PReq 'GET' '/api/prompts'
    $g1 = PReq 'GET' '/api/prompts' @{ 'X-Claude-Status' = 'prompts' }
    $g2 = PReq 'GET' '/api/prompts' @{ 'X-Claude-Status' = 'prompts'; 'Sec-Fetch-Site' = 'cross-site' }
    $g3 = PReq 'GET' '/api/prompts' @{ 'X-Claude-Status' = 'qn' }
    $g4 = PReq 'GET' '/api/prompts' @{ Host = 'evil.example'; 'X-Claude-Status' = 'prompts' }
    Check 'api/prompts needs the page''s own header and a same-origin fetch; a foreign Host is refused' ($g0.Code -eq 403 -and $g2.Code -eq 403 -and $g3.Code -eq 403 -and $g4.Code -eq 421 -and $g1.Code -eq 200)
    $pj = ($g1.Body | ConvertFrom-Json).prompts
    Check 'api/prompts: titles and exact text, newest change first, opaque keys, no file paths or ids' ($pj.Count -eq 2 -and $pj[0].t -eq 'Explain simply' -and $pj[1].x -ceq $crText -and $pj[0].k -match '^[A-Za-z0-9_-]{22}$' -and
        $g1.Body -notmatch [regex]::Escape($i1) -and $g1.Body -notmatch [regex]::Escape($tmp.Replace('\', '\\')))
    Check 'api/prompts carries each prompt''s status (empty when none)' ($pj[1].s -eq 'yellow' -and $pj[0].s -eq '')
    $p1 = PReq 'POST' '/api/prompts' @{ Origin = "http://127.0.0.1:$pp"; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'prompts' } '{}'
    Check 'api/prompts is read-only: POST answers 405 and changes nothing' ($p1.Code -eq 405 -and $vs.All().Count -eq 2)
} finally { $srv.Stop() }

# --- the phone page in a real browser ---
$nodeP = Get-Command node -ErrorAction SilentlyContinue
$pChrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Browser) { Skip 'prompts on the phone page in a browser' 'run with -Browser' }
elseif (-not $pChrome -or -not $nodeP) { Skip 'prompts on the phone page in a browser' 'needs Chrome or Edge, and Node.js 22+' }
else {
    $bp = [THttp]::FreePort()
    $script:psrv = New-Object ClaudeStatus.PhoneServer($proot, $bp, $null, $null, [Func[string[]]] { @() }); $script:psrv.Prompts = $vs
    $null = $vs.Save($null, 'Second line test', "Line one`r`nLine two")
    $null = $script:psrv.Start(); $script:psrv.Publish((New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'), $false, '')
    $bprof = Join-Path $tmp 'chrome-pr'
    $bc = Start-Process $pChrome -ArgumentList '--headless=new', '--remote-debugging-port=0', "--user-data-dir=`"$bprof`"", '--no-first-run', '--no-default-browser-check', '--disable-extensions', 'about:blank' -PassThru
    try {
        $pf = Join-Path $bprof 'DevToolsActivePort'
        for ($i = 0; $i -lt 100 -and -not (Test-Path $pf); $i++) { Start-Sleep -Milliseconds 100 }
        $dbgPort = if (Test-Path $pf) { (Get-Content $pf)[0] } else { '' }
        if (-not $dbgPort) { Check 'prompts browser: headless browser started' $false }
        else {
            $lines = & $nodeP.Source (Join-Path $PSScriptRoot 'prompts-browser.mjs') $dbgPort $bp $RenderDir 2>&1
            if (-not @($lines | Where-Object { $_ -match "^(PASS|FAIL)`t" })) { Check 'prompts browser: the script ran' $false ($lines | Out-String) }
            foreach ($l in $lines) { $p = "$l" -split "`t"; if ($p[0] -eq 'PASS' -or $p[0] -eq 'FAIL') { Check ('phone prompts: ' + $p[1]) ($p[0] -eq 'PASS') $p[2] } }
            Info "Phone prompts screenshots: $RenderDir\phone-pr-*.png"
        }
    } finally { try { Stop-Process -Id $bc.Id -Force -ErrorAction SilentlyContinue } catch {}; if ($script:psrv) { $script:psrv.Stop() } }
}

if ($prStandalone) {
    Write-Host ''
    Write-Host ("RESULT: {0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
    if ($script:fail) { $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
    try { Get-ChildItem $tmp -Exclude 'renders' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    exit $(if ($script:fail) { 1 } else { 0 })
}
