# Phone Commit & Push (git.cs) against temporary repositories and a local bare remote: never your real projects.
# Dot-sourced by run-tests.ps1 (after the phone dashboard section, whose HTTP helpers it uses), or run alone:
#   powershell -ExecutionPolicy Bypass -File .\tests\git-tests.ps1
if (-not (Get-Command Check -ErrorAction SilentlyContinue)) {
    if ($PSVersionTable.PSEdition -eq 'Core') { & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath; exit $LASTEXITCODE }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-git-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    . (Join-Path $PSScriptRoot 'common.ps1')
    $gitStandalone = $true
}
if (-not ('THttp' -as [type])) {
    Add-Type @'
using System; using System.IO; using System.Text; using System.Threading.Tasks; using System.Net.Sockets;
public static class THttp {
    public static string Send(int port, string raw, int timeoutMs) {
        using (var c = new TcpClient()) {
            c.ReceiveTimeout = timeoutMs; c.Connect("127.0.0.1", port);
            var s = c.GetStream(); var b = Encoding.UTF8.GetBytes(raw); s.Write(b, 0, b.Length);
            using (var sr = new StreamReader(s, Encoding.UTF8)) return sr.ReadToEnd();
        }
    }
    public static Task<string> SendAsync(int port, string raw, int timeoutMs) { return Task.Run(() => Send(port, raw, timeoutMs)); }
    public static int Status(string r) { var p = r.Split(' '); int c; return p.Length > 1 && int.TryParse(p[1], out c) ? c : 0; }
    public static string Body(string r) { int i = r.IndexOf("\r\n\r\n"); return i < 0 ? "" : r.Substring(i + 4); }
    public static int FreePort() { var l = new TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
}
public static class TFake { public static Func<string[]> Names(string[] n) { return () => n; } }
'@
}
if (-not ('TCwd' -as [type])) { Add-Type @'
public class TCwd {
    public System.Collections.Generic.Dictionary<string, string> Map = new System.Collections.Generic.Dictionary<string, string>();
    public string Get(string id) { string v; return Map.TryGetValue(id, out v) ? v : null; }
}
'@ }

Section 'Phone Commit & Push (temporary repositories + a local bare remote)'

$gt = Join-Path $tmp 'git'; $null = New-Item -ItemType Directory -Force $gt
$ser = New-Object Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
# git without PowerShell turning its stderr into errors; returns trimmed stdout+stderr text.
function G([string]$dir) {
    $ea = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & git -C $dir @args 2>&1 | ForEach-Object { "$_" }; $script:gcode = $LASTEXITCODE; return (($o -join "`n").Trim()) }
    finally { $ErrorActionPreference = $ea }
}
function W([string]$path, [string]$text) { $d = Split-Path $path; if (-not (Test-Path $d)) { $null = New-Item -ItemType Directory -Force $d }; [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false)) }
function New-Repo([string]$name, [string]$remote) {
    $d = Join-Path $gt $name
    $null = G $gt clone -q $remote $d
    $null = G $d config user.name 'Test User'; $null = G $d config user.email 'test@example.invalid'
    $null = G $d config commit.gpgsign false; $null = G $d config core.autocrlf false
    $null = G $d config core.hooksPath ((Join-Path $d '.git\hooks') -replace '\\', '/')   # your global hooksPath must not apply here
    return $d
}
function Hook([string]$dir, [string]$name, [string]$body) { W (Join-Path $dir $name) ("#!/bin/sh`n" + $body + "`n") }

# --- the remote and the working repositories ---
$remote = Join-Path $gt 'remote.git'
$null = G $gt init -q --bare -b main $remote
$null = G $remote config core.hooksPath ((Join-Path $remote 'hooks') -replace '\\', '/')
$work = New-Repo 'work' $remote
$null = G $work checkout -q -b main
W "$work\a.txt" "line 1`nline 2`n"; W "$work\b.txt" "b`n"; W "$work\del.txt" "delete me`n"; W "$work\keep.txt" "keep`n"
[IO.File]::WriteAllBytes("$work\bin.dat", [byte[]](0, 1, 2, 3, 0, 255))
W "$work\sub\deep\readme.md" "nested`n"
$null = G $work add -A; $null = G $work commit -q -m 'initial'
$null = G $work push -q -u origin main
# a linked worktree on its own branch, with its own upstream
$wt = Join-Path $gt 'wt'
$null = G $work worktree add -q -b feature $wt
$null = G $wt push -q -u origin feature
Check 'setup: repository, bare remote and worktree ready' ((G $remote rev-parse main) -eq (G $work rev-parse HEAD) -and (G $remote rev-parse feature) -eq (G $wt rev-parse HEAD)) (G $work log --oneline -1)
$busyRepo = New-Repo 'busy' $remote
$plain = Join-Path $gt 'plain'; $null = G $gt init -q -b main $plain   # no upstream
$null = G $plain config user.name T; $null = G $plain config user.email t@example.invalid; $null = G $plain config commit.gpgsign false
W "$plain\x.txt" "x`n"; $null = G $plain add x.txt; $null = G $plain commit -q -m x; W "$plain\x.txt" "y`n"

# --- the phone server with workers in those folders ---
$gport = [THttp]::FreePort()
$groot = New-Root @{ phoneDashboard = $true; phonePort = $gport }
foreach ($fn in 'phone.html', 'phone.css', 'phone.js') { Copy-Item (Join-Path $src $fn) $groot }
$gsrv = New-Object ClaudeStatus.PhoneServer($groot, $gport, $null, [TFake]::Names(@()))
$cwd = New-Object TCwd
$cwd.Map['s-work'] = "$work\sub\deep"; $cwd.Map['s-wt'] = $wt; $cwd.Map['s-busy'] = $busyRepo; $cwd.Map['s-plain'] = $plain
$gsrv.GitOps.CwdOf = [Delegate]::CreateDelegate([Func[string, string]], $cwd, 'Get')
$null = $gsrv.Start()
function GSess([string]$id, [string]$state, [string]$name) {
    $now = [DateTime]::UtcNow
    New-Object ClaudeStatus.SessionInfo -Property @{ Id = $id; State = $state; Name = $name; StateSince = $now.AddMinutes(-2); Updated = $now.AddMinutes(-2); FirstSeen = $now.AddMinutes(-9) }
}
function Board-Publish($list) { $l = New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'; foreach ($s in $list) { $l.Add($s) }; $gsrv.Publish($l, $false, '') }
Board-Publish @((GSess 's-work' 'DONE' 'Work Proj'), (GSess 's-wt' 'DONE' 'Feature Tree'), (GSess 's-busy' 'WORKING' 'Busy Proj'), (GSess 's-plain' 'DONE' 'Plain'))

function GH() { @{ 'X-Claude-Status' = 'git'; 'Sec-Fetch-Site' = 'same-origin' } }
function PH() { @{ Origin = "http://127.0.0.1:$gport"; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'git'; 'Sec-Fetch-Site' = 'same-origin' } }
function GReq([string]$method, [string]$path, [hashtable]$h, [string]$body = $null) {
    $lines = @("$method $path HTTP/1.1", "Host: 127.0.0.1:$gport")
    foreach ($k in $h.Keys) { $lines += "${k}: $($h[$k])" }
    if ($null -ne $body) { $lines += "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($body))" }
    $raw = [THttp]::Send($gport, (($lines -join "`r`n") + "`r`n`r`n" + $body), 60000)
    return @{ Code = [THttp]::Status($raw); Body = [THttp]::Body($raw) }
}
function GGet([string]$q) { $r = GReq 'GET' "/api/git?$q" (GH); return $ser.DeserializeObject($r.Body) }
# JSON of a hashtable; each value goes straight to the serializer (PowerShell-wrapped values can't be serialized).
function J([hashtable]$h) { '{' + (($h.Keys | ForEach-Object { $ser.Serialize([string]$_) + ':' + $ser.Serialize($h[$_]) }) -join ',') + '}' }
function GPost([hashtable]$obj) { $r = GReq 'POST' '/api/git' (PH) (J $obj); return $ser.DeserializeObject($r.Body) }
function RepoOf([string]$path) { (GGet 'op=repos&fresh=1')['repos'] | Where-Object { [ClaudeStatus.Git]::Norm($_['path']) -eq [ClaudeStatus.Git]::Norm($path) } | Select-Object -First 1 }
function Review([string]$r) { GGet "op=review&r=$r" }
# The commands exactly as the phone page builds them (phone.js gitCommands), from the review it was shown.
$phoneJs = Join-Path $src 'phone.js'
# Picked files in review order, as plain .NET objects (the serializer can't take PowerShell-wrapped ones).
function Pick($review, [string[]]$paths, [switch]$Refs) {
    $l = New-Object Collections.ArrayList
    foreach ($x in $review['files']) { if ($paths -contains $x['p']) { if ($Refs) { [void]$l.Add(@{ p = $x['p']; h = $x['h'] }) } else { [void]$l.Add($x) } } }
    return , $l
}
function PhoneCmds($review, [string[]]$paths) {
    $picked = Pick $review $paths
    $f = Join-Path $gt 'pick.json'
    [IO.File]::WriteAllText($f, (J @{ review = $review; picked = $picked }), (New-Object Text.UTF8Encoding $false))
    $out = & node -e "const p=require(process.argv[1]);const d=JSON.parse(require('fs').readFileSync(process.argv[2],'utf8'));process.stdout.write(JSON.stringify({c:p.gitCommands(d.review,d.picked),why:p.gitProblem(d.review,d.picked,'msg')}))" $phoneJs $f
    return $ser.DeserializeObject(($out -join ''))
}
function NewId() { $b = New-Object byte[] 16; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
function Send-Commit($review, [string[]]$paths, [string]$msg, [string]$id = (NewId), $cmds = $null) {
    $picked = Pick $review $paths -Refs
    if ($null -eq $cmds) { $cmds = (PhoneCmds $review $paths)['c'] }
    GPost @{ op = 'commit'; id = $id; r = $review['r']; fp = $review['fp']; files = $picked; msg = $msg; cmds = $cmds }
}
function Wait-GitJob([string]$id) {
    $end = (Get-Date).AddSeconds(90)
    do { $j = GGet "op=job&id=$id"; if ($j['state'] -ne 'running') { return $j }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $end)
    return $j
}
function Count([string]$dir) { [int](G $dir rev-list --count HEAD) }
function Out($j) { (@($j['steps']) | ForEach-Object { $_['out'] }) -join ' | ' }

# --- request protection: looking needs the page's header; acting needs the full action checks ---
Check 'GET without the page''s header is refused (403)' ((GReq 'GET' '/api/git?op=repos' @{}).Code -eq 403)
Check 'GET from another site is refused (403)' ((GReq 'GET' '/api/git?op=repos' @{ 'X-Claude-Status' = 'git'; 'Sec-Fetch-Site' = 'cross-site' }).Code -eq 403)
$h = PH; $h.Remove('Origin')
Check 'POST without Origin is refused (403)' ((GReq 'POST' '/api/git' $h '{"op":"push","id":"AAAAAAAAAAAAAAAAAAAAAA","n":0}').Code -eq 403)
$h = PH; $h['X-Claude-Status'] = 'dismiss'
Check 'POST with another action''s header is refused (403)' ((GReq 'POST' '/api/git' $h '{"op":"push","id":"AAAAAAAAAAAAAAAAAAAAAA","n":0}').Code -eq 403)
Check 'no action through GET (op=commit is a bad request)' ((GReq 'GET' '/api/git?op=commit&r=AAAAAAAAAAAAAAAAAAAAAA' (GH)).Code -eq 400)
Check 'unknown POST op is a bad request' ((GReq 'POST' '/api/git' (PH) '{"op":"exec","id":"AAAAAAAAAAAAAAAAAAAAAA","cmd":"whoami"}').Code -eq 400)
$big = try { (GReq 'POST' '/api/git' (PH) ('{"op":"commit","msg":"' + ('x' * 600000) + '"}')).Code } catch { 'reset' }   # refused before the body is read: 413, or the connection closes mid-upload
Check 'bodies over 512 KB are refused (413 / closed)' ($big -eq 413 -or $big -eq 'reset') "$big"

# --- repositories: nested folder and worktree resolve to their own roots ---
$repos = (GGet 'op=repos&fresh=1')['repos']
$rw = RepoOf $work; $rwt = RepoOf $wt; $rb = RepoOf $busyRepo
Check 'a worker in a nested folder resolves to its repository root' ($rw -and $rw['name'] -eq 'work' -and $rw['n'] -eq 0)
Check 'a linked worktree is its own repository (separate token and path)' ($rwt -and $rwt['r'] -ne $rw['r'] -and [ClaudeStatus.Git]::Norm($rwt['path']) -eq [ClaudeStatus.Git]::Norm($wt))
Check 'a repository with a WORKING worker is marked busy' ($rb -and $rb['busy'] -eq $true -and -not $rw['busy'])
Check 'repository tokens are opaque (no path in the token)' ($rw['r'] -match '^[A-Za-z0-9_-]{22}$')

# --- changes to review ---
W "$work\a.txt" "line 1 changed`nline 2`n"
$odd = "odd name [1] {x} `$HOME 'q' & ;.txt"
W (Join-Path $work $odd) "special file`n"
Remove-Item "$work\del.txt"
[IO.File]::WriteAllBytes("$work\bin.dat", [byte[]](0, 9, 9, 9, 0, 255, 7))
W "$work\sub\deep\new.txt" "new nested`n"
$idxBefore = (Get-Item "$work\.git\index").LastWriteTimeUtc
$headBefore = G $work rev-parse HEAD
$rv = Review $rw['r']
$byPath = @{}; foreach ($f in $rv['files']) { $byPath[$f['p']] = $f }
Check 'review: branch, remote and upstream branch' ($rv['branch'] -eq 'main' -and $rv['remote'] -eq 'origin' -and $rv['merge'] -eq 'refs/heads/main' -and $rv['dest'] -eq 'main' -and @($rv['block']).Count -eq 0) ($ser.Serialize($rv['block']))
Check 'review: path of the repository root' ([ClaudeStatus.Git]::Norm($rv['path']) -eq [ClaudeStatus.Git]::Norm($work))
Check 'review: statuses (modified / untracked / deleted)' ($byPath['a.txt']['kind'] -eq 'modified' -and $byPath[$odd]['kind'] -eq 'untracked' -and $byPath['del.txt']['kind'] -eq 'deleted' -and $byPath['bin.dat']['kind'] -eq 'modified' -and $byPath['sub/deep/new.txt']['kind'] -eq 'untracked') (($rv['files'] | ForEach-Object { $_['p'] + '=' + $_['kind'] }) -join '; ')
$d = GGet ("op=diff&r=" + $rv['r'] + "&p=" + [Uri]::EscapeDataString('a.txt'))
Check 'diff: unstaged change of a modified file' ($d['result'] -eq 'ok' -and $d['parts'][0]['t'] -eq 'unstaged' -and $d['parts'][0]['text'] -match '\+line 1 changed' -and $d['parts'][0]['text'] -match '-line 1')
$d = GGet ("op=diff&r=" + $rv['r'] + "&p=" + [Uri]::EscapeDataString($odd))
Check 'diff: a new file shows its content (odd file name intact)' ($d['parts'][0]['t'] -eq 'new' -and $d['parts'][0]['text'] -eq "special file`n" -and -not $d['parts'][0]['bin'])
$d = GGet ("op=diff&r=" + $rv['r'] + "&p=bin.dat")
Check 'diff: binary file is labelled binary' ($d['parts'][0]['bin'] -eq $true)
$d = GGet ("op=diff&r=" + $rv['r'] + "&p=" + [Uri]::EscapeDataString('keep.txt'))
$d2 = GGet ("op=diff&r=" + $rv['r'] + "&p=" + [Uri]::EscapeDataString('..\..\..\Windows\win.ini'))
Check 'diff: only files Git lists as changed (unchanged or outside paths are refused)' ($d['result'] -eq 'gone' -and $d2['result'] -eq 'gone')
Check 'looking (repos / review / diff) never touched the index, HEAD or staging' ((Get-Item "$work\.git\index").LastWriteTimeUtc -eq $idxBefore -and (G $work rev-parse HEAD) -eq $headBefore -and (G $work diff --cached --name-only) -eq '')

# --- preview == execution: the phone's command list, and the laptop refuses any other ---
$pc = PhoneCmds $rv @('a.txt', $odd, 'del.txt')
$expect = $ser.Serialize(@(, @('git', '--literal-pathspecs', 'add', '--', 'a.txt', 'del.txt', $odd)) + @(, @('git', 'commit', '--cleanup=verbatim', '--file=-')) + @(, @('git', 'push', 'origin', 'refs/heads/main:refs/heads/main')))
$expect2 = $ser.Serialize(@(, @('git', '--literal-pathspecs', 'add', '--', 'a.txt', $odd, 'del.txt')) + @(, @('git', 'commit', '--cleanup=verbatim', '--file=-')) + @(, @('git', 'push', 'origin', 'refs/heads/main:refs/heads/main')))
$order = @($rv['files'] | ForEach-Object { $_['p'] }) -join ','
Check 'phone preview: selected-file add, commit with stdin message, push to the shown destination' ($ser.Serialize($pc['c']) -eq $expect -or $ser.Serialize($pc['c']) -eq $expect2) ($ser.Serialize($pc['c']) + ' order=' + $order)
$bad = @(@('git', 'add', '.'), @('git', 'commit', '--cleanup=verbatim', '--file=-'), @('git', 'push', 'origin', 'refs/heads/main:refs/heads/main'))
$j = Wait-GitJob (Send-Commit $rv @('a.txt') 'x' -cmds $bad)['id']
Check 'commands that differ from the preview are refused; nothing runs' ($j['outcome'] -eq 'stale' -and (G $work rev-parse HEAD) -eq $headBefore -and (G $work diff --cached --name-only) -eq '') $j['msg']

# --- selected files only; message preserved exactly ---
$msg = "Fix: `"quotes`" & `$HOME ``tick`` ; rm -rf / | %PATH%`n`n# not a comment, kept`n  indented line`ttab`nünïcødé 🚀 <b>&amp;</b>`n"
$id = NewId
# A UTF-8 console code page (e.g. Windows' "Use Unicode UTF-8" option) must not put a byte-order mark in the message.
$oldIn = [Console]::InputEncoding
try { [Console]::InputEncoding = New-Object Text.UTF8Encoding $true } catch { }
try {
    $first = Send-Commit $rv @('a.txt', $odd, 'del.txt') $msg $id
    $again = Send-Commit $rv @('a.txt', $odd, 'del.txt') $msg $id        # double tap / resend with the same id
    $j = Wait-GitJob $id
} finally { try { [Console]::InputEncoding = $oldIn } catch { } }
$head1 = G $work rev-parse HEAD
Check 'Send: commit and push succeeded, with the commit hash' ($j['outcome'] -eq 'pushed' -and $j['commit'] -eq $head1 -and $head1 -ne $headBefore) ("$($j['outcome']): $($j['msg']) :: " + (Out $j))
$names = ((G $work show --name-status --format= HEAD) -split "`n" | Sort-Object) -join ','
Check 'the commit holds exactly the selected files' ($names -eq ((@("D`tdel.txt", "M`ta.txt", "A`t$odd") | Sort-Object) -join ',')) $names
$body = (G $work log -1 --format=%B)
$catBytes = [Text.Encoding]::UTF8.GetString((& { $p = New-Object Diagnostics.Process; $p.StartInfo = New-Object Diagnostics.ProcessStartInfo('git', "-C `"$work`" cat-file commit HEAD") -Property @{ UseShellExecute = $false; RedirectStandardOutput = $true }; $null = $p.Start(); $ms = New-Object IO.MemoryStream; $p.StandardOutput.BaseStream.CopyTo($ms); $p.WaitForExit(); , $ms.ToArray() }))
Check 'commit message preserved exactly, byte for byte (multi-line, quotes, $, backticks, #, tabs, Unicode)' ($catBytes.Substring($catBytes.IndexOf("`n`n") + 2) -eq $msg) ($catBytes.Substring($catBytes.IndexOf("`n`n") + 2))
Check 'pushed to the shown destination (remote main = the new commit)' ((G $remote rev-parse main) -eq $head1)
Check 'unselected changes untouched and unstaged (bin.dat, new.txt)' ((G $work status --porcelain) -match 'M bin\.dat' -and (G $work status --porcelain) -match '\?\? sub/deep/new\.txt' -and (G $work diff --cached --name-only) -eq '') (G $work status --porcelain)
Check 'double tap: the same id returns the same operation; one commit only' ($first['id'] -eq $id -and $again['id'] -eq $id -and (Count $work) -eq 2 -and @($j['steps'] | Where-Object { $_['c'].Count -gt 1 -and $_['c'][1] -eq 'commit' }).Count -eq 1)
$j2 = Wait-GitJob (Send-Commit $rv @('a.txt', $odd, 'del.txt') $msg)['id']
Check 'resending the old review with a new id is refused as stale (no duplicate commit)' ($j2['outcome'] -eq 'stale' -and (Count $work) -eq 2) $j2['msg']

# --- concurrent duplicate requests with one id ---
$rv = Review $rw['r']
$id = NewId
$picked = Pick $rv @('sub/deep/new.txt') -Refs
$bodyJson = J (@{ op = 'commit'; id = $id; r = $rv['r']; fp = $rv['fp']; files = $picked; msg = 'nested file'; cmds = (PhoneCmds $rv @('sub/deep/new.txt'))['c'] })
$hdr = PH; $lines = @('POST /api/git HTTP/1.1', "Host: 127.0.0.1:$gport"); foreach ($k in $hdr.Keys) { $lines += "${k}: $($hdr[$k])" }; $lines += "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($bodyJson))"
$rawReq = ($lines -join "`r`n") + "`r`n`r`n" + $bodyJson
$tasks = 1..4 | ForEach-Object { [THttp]::SendAsync($gport, $rawReq, 30000) }
[Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$tasks)
$ids = @($tasks | ForEach-Object { $ser.DeserializeObject([THttp]::Body($_.Result))['id'] } | Sort-Object -Unique)
$j = Wait-GitJob $id
Check 'four simultaneous identical Sends: one operation, one commit' ($ids.Count -eq 1 -and $ids[0] -eq $id -and $j['outcome'] -eq 'pushed' -and (Count $work) -eq 3) ("ids=$($ids -join ',') " + $j['msg'])

# --- stale review: a selected file changed after the review ---
$rv = Review $rw['r']
W "$work\a.txt" "changed again`n"
$rv2 = Review $rw['r']; W "$work\a.txt" "changed a third time`n"
$j = Wait-GitJob (Send-Commit $rv2 @('a.txt') 'stale')['id']
Check 'a selected file changed after the review: refused, nothing staged or committed' ($j['outcome'] -eq 'stale' -and (Count $work) -eq 3 -and (G $work diff --cached --name-only) -eq '') $j['msg']
$rv3 = Review $rw['r']
W "$work\keep.txt" "keep 2`n"; $null = G $work add -- keep.txt
$j = Wait-GitJob (Send-Commit $rv3 @('a.txt') 'stale 2')['id']
Check 'staging changed after the review: refused as stale' ($j['outcome'] -eq 'stale' -and (Count $work) -eq 3) $j['msg']

# --- pre-existing staged changes are visible and never silently included or unstaged ---
$rv = Review $rw['r']
$k = $rv['files'] | Where-Object { $_['p'] -eq 'keep.txt' }
Check 'review shows the pre-existing staged file as staged' ($k -and $k['st'] -eq $true -and $k['un'] -eq $false)
$pc = PhoneCmds $rv @('a.txt')
Check 'phone explains the staging conflict before Send (staged but not selected)' ($pc['why'] -match 'Already staged on the laptop but not selected: keep\.txt') $pc['why']
$j = Wait-GitJob (Send-Commit $rv @('a.txt') 'without keep')['id']
Check 'laptop refuses it too: nothing committed, keep.txt still staged, a.txt not staged' ($j['outcome'] -eq 'blocked' -and (Count $work) -eq 3 -and (G $work diff --cached --name-only) -eq 'keep.txt') ("$($j['msg']) staged=" + (G $work diff --cached --name-only))
$j = Wait-GitJob (Send-Commit $rv @('a.txt', 'keep.txt') 'with keep')['id']
Check 'selecting the staged file too: both committed and pushed' ($j['outcome'] -eq 'pushed' -and (((G $work show --name-only --format= HEAD) -split "`n" | Sort-Object) -join ',') -eq 'a.txt,keep.txt' -and (G $remote rev-parse main) -eq (G $work rev-parse HEAD)) ("$($j['msg']) " + (Out $j))

# --- commit failure (a pre-commit hook says no): no commit, nothing discarded ---
Hook "$work\.git\hooks" 'pre-commit' "echo HOOK-SAYS-NO >&2`nexit 1"
$rv = Review $rw['r']
$c0 = Count $work
$j = Wait-GitJob (Send-Commit $rv @('bin.dat') 'should fail')['id']
Check 'commit failed: reported with Git''s output, HEAD unchanged, push skipped' ($j['outcome'] -eq 'commit_failed' -and (Out $j) -match 'HOOK-SAYS-NO' -and (Count $work) -eq $c0 -and $j['steps'][-1]['st'] -eq 'skipped' -and -not $j['retry']) ("$($j['outcome']) $($j['msg'])")
Check 'after the failed commit the file is still there and staged (nothing discarded)' ((G $work diff --cached --name-only) -eq 'bin.dat' -and (Test-Path "$work\bin.dat"))
Remove-Item "$work\.git\hooks\pre-commit"

# --- commit succeeds, push fails (the remote rejects): commit kept, Retry Push pushes it, no new commit ---
Hook "$remote\hooks" 'pre-receive' "echo REMOTE-SAYS-NO >&2`nexit 1"
$rv = Review $rw['r']
$j = Wait-GitJob (Send-Commit $rv @('bin.dat') 'push will fail')['id']
$headC = G $work rev-parse HEAD
Check 'commit ok + push failed: commit hash reported and kept, Retry Push offered' ($j['outcome'] -eq 'push_failed' -and $j['commit'] -eq $headC -and $j['retry'] -eq $true -and (Out $j) -match 'REMOTE-SAYS-NO' -and (G $remote rev-parse main) -ne $headC) ("$($j['outcome']) $($j['msg'])")
$c1 = Count $work
Remove-Item "$remote\hooks\pre-receive"
$r1 = GPost @{ op = 'push'; id = $j['id']; n = 0 }
$r2 = GPost @{ op = 'push'; id = $j['id']; n = 0 }   # double tap on Retry Push
$j = Wait-GitJob $j['id']
Check 'Retry Push: pushed the same commit, no new commit' ($j['outcome'] -eq 'pushed' -and (G $remote rev-parse main) -eq $headC -and (Count $work) -eq $c1 -and $j['n'] -eq 1) ("$($j['outcome']) $($j['msg'])")
Check 'Retry Push double tap ran one push' (@($j['steps'] | Where-Object { $_['c'].Count -gt 1 -and $_['c'][1] -eq 'push' -and $_['st'] -ne 'skipped' }).Count -eq 1)
$r3 = GPost @{ op = 'push'; id = $j['id']; n = 1 }
Check 'no Retry Push after success' ($r3['retry'] -eq $false -and $r3['n'] -eq 1)

# --- push needing attention (credentials) is reported as such, commit kept ---
$null = G $work remote set-url --push origin 'https://user:SECRET-TOKEN@127.0.0.1:9/nowhere.git'
$rv = Review $rw['r']
Check 'credentials in the remote URL never reach the phone' ($rv['url'] -match '\*\*\*@127\.0\.0\.1' -and $ser.Serialize($rv) -notmatch 'SECRET-TOKEN') $rv['url']
W "$work\b.txt" "b2`n"
$rv = Review $rw['r']
$j = Wait-GitJob (Send-Commit $rv @('b.txt') 'push unreachable')['id']
Check 'push to an unreachable remote: commit kept, push reported failed or needing attention, no secret in output' (($j['outcome'] -eq 'push_failed' -or $j['outcome'] -eq 'attention') -and $j['commit'] -ne '' -and $ser.Serialize($j) -notmatch 'SECRET-TOKEN') ("$($j['outcome']) $($j['msg']) " + (Out $j))
$kept = $j['commit']
$null = G $work remote set-url --push origin $remote
$j = Wait-GitJob (GPost @{ op = 'push'; id = $j['id']; n = 0 })['id']
Check 'Retry Push after the destination changed on the laptop: refused (needs a fresh review), commit kept' ($j['outcome'] -eq 'push_failed' -and -not $j['retry'] -and $j['commit'] -eq $kept -and (G $work rev-parse HEAD) -eq $kept -and (G $remote rev-parse main) -ne $kept) ("$($j['outcome']) $($j['msg'])")
W "$work\b.txt" "b3`n"
$rv = Review $rw['r']
Check 'the fresh review says the earlier unpushed commit will be pushed too' ((@($rv['warn']) -join ' ') -match '1 earlier commit') ($ser.Serialize($rv['warn']))
$j = Wait-GitJob (Send-Commit $rv @('b.txt') 'after fixing the remote')['id']
Check '... and the next Send pushes both commits' ($j['outcome'] -eq 'pushed' -and (G $remote rev-parse main) -eq (G $work rev-parse HEAD) -and (G $work rev-parse HEAD~1) -eq $kept)

# --- the worktree: isolated from the main checkout ---
W "$wt\feature.txt" "feature work`n"
$rvt = Review $rwt['r']
$mainBefore = G $work rev-parse HEAD
$j = Wait-GitJob (Send-Commit $rvt @('feature.txt') 'feature commit')['id']
Check 'worktree: commits on its own branch and pushes to its own upstream' ($j['outcome'] -eq 'pushed' -and $rvt['branch'] -eq 'feature' -and (G $remote rev-parse feature) -eq (G $wt rev-parse HEAD) -and (G $work rev-parse HEAD) -eq $mainBefore -and (G $remote rev-parse main) -eq $mainBefore) ("$($j['msg']) " + (Out $j))

# --- blocked situations: busy worker, no upstream, detached HEAD, conflicts, behind ---
W "$busyRepo\z.txt" "z`n"
$rvb = Review $rb['r']
Check 'a worker is working in the repository: Send blocked with the reason' ((@($rvb['block']) -join ' ') -match 'Busy Proj.*working')
$j = Wait-GitJob (Send-Commit $rvb @('z.txt') 'busy')['id']
Check '... and the laptop refuses it too' ($j['outcome'] -eq 'blocked' -and (Count $busyRepo) -eq 1)
Board-Publish @((GSess 's-work' 'DONE' 'Work Proj'), (GSess 's-wt' 'DONE' 'Feature Tree'), (GSess 's-busy' 'DONE' 'Busy Proj'), (GSess 's-plain' 'DONE' 'Plain'))
$rvb = Review $rb['r']
Check 'once the worker is done, a refresh unblocks it' (@($rvb['block']).Count -eq 0) ($ser.Serialize($rvb['block']))
$rp = RepoOf $plain; $rvp = Review $rp['r']
Check 'no upstream: blocked with an explanation' ((@($rvp['block']) -join ' ') -match 'no upstream')
$null = G $plain checkout -q --detach
$rvp = Review $rp['r']
Check 'detached HEAD: blocked with an explanation' ((@($rvp['block']) -join ' ') -match 'detached')
$null = G $plain checkout -q main; $null = G $plain checkout -q -- x.txt
$null = G $plain checkout -q -b other; W "$plain\x.txt" "other`n"; $null = G $plain commit -qam other
$null = G $plain checkout -q main; W "$plain\x.txt" "main`n"; $null = G $plain commit -qam main
$null = G $plain merge other
$rvp = Review $rp['r']
Check 'merge conflict: blocked (conflicts and merge in progress)' ((@($rvp['block']) -join ' ') -match 'conflicts in x\.txt' -and (@($rvp['block']) -join ' ') -match 'merge is in progress')
$null = G $plain merge --abort
# behind its upstream (as of the last fetch): a push would be rejected, so it's blocked before any commit
$other = New-Repo 'other' $remote
W "$other\o.txt" "o`n"; $null = G $other add o.txt; $null = G $other commit -qm o; $null = G $other push -q origin main
$null = G $busyRepo fetch -q
$rvb = Review $rb['r']
Check 'behind its upstream: blocked before committing' ((@($rvb['block']) -join ' ') -match 'behind origin/main')

# --- static guarantees ---
$gcs = Get-Content (Join-Path $src 'git.cs') -Raw
Check 'git.cs never builds forbidden Git commands or uses a shell' ($gcs -notmatch '"(reset|clean|stash|pull|rebase|--force|--force-with-lease|--amend|--no-verify|-A|--all|checkout|restore)"' -and $gcs -notmatch '(?i)cmd\.exe|powershell|bash|UseShellExecute = true|"/c"')
Check 'Git.Quote round-trips awkward arguments' ([ClaudeStatus.Git]::Quote('a b') -eq '"a b"' -and [ClaudeStatus.Git]::Quote('x"y') -eq '"x\"y"' -and [ClaudeStatus.Git]::Quote('d\ e\') -eq '"d\ e\\"' -and [ClaudeStatus.Git]::Quote('') -eq '""' -and [ClaudeStatus.Git]::Quote('plain-arg') -eq 'plain-arg')

$gsrv.Stop()
if ($gitStandalone) {
    Write-Host ''
    Write-Host ("RESULT: {0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
    if ($script:fail) { $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
    try { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    exit $(if ($script:fail) { 1 } else { 0 })
}
