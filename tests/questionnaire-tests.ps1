# Questionnaires (questionnaire.cs + the phone's api/qn): the v1 format, answered files, drafts, replacement and submission.
# Temporary folders only. Dot-sourced by run-tests.ps1, or run alone:
#   powershell -ExecutionPolicy Bypass -File .\tests\questionnaire-tests.ps1 [-Browser]
param([switch]$Browser, [string]$RenderDir)
if (-not (Get-Command Check -ErrorAction SilentlyContinue)) {
    if ($PSVersionTable.PSEdition -eq 'Core') { & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args; exit $LASTEXITCODE }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-qn-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    . (Join-Path $PSScriptRoot 'common.ps1')
    $qnStandalone = $true
}
if (-not $RenderDir) { $RenderDir = Join-Path $tmp 'renders' }
$null = New-Item -ItemType Directory -Force $RenderDir
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
'@
}

Section 'Questionnaires: format v1, answered files, drafts, replacement, submission'

$QF = [ClaudeStatus.QnFormat]
$qser = New-Object Web.Script.Serialization.JavaScriptSerializer; $qser.MaxJsonLength = [int]::MaxValue
function J([string]$json) { $qser.DeserializeObject($json) }
function P([string]$text) { $QF::Parse($text) }
function Errs($d) { $d.Errors -join ' | ' }
$exPath = Join-Path $repo 'questionnaires\example.questionnaire.md'
$guidePath = Join-Path $repo 'questionnaires\QUESTIONNAIRE_GUIDE.md'
$ex = P ([IO.File]::ReadAllText($exPath))

# --- parsing every type ---
Check 'example parses: title, project, 5 questions of all four types' ($ex.Ok -and $ex.Title -eq 'Guest portal: notification settings' -and $ex.Project -eq 'Toah Nipi Portal' -and $ex.Questions.Count -eq 5 -and
    (($ex.Questions | ForEach-Object { $_.Type }) -join ',') -eq 'multiple,single,short,long,single') (Errs $ex)
$q1 = $ex.Find('channels'); $q2 = $ex.Find('default-state')
Check 'options keep stable ids and labels; recommendations and required flags read' ((($q1.Options | ForEach-Object { $_.Id + '=' + $_.Label }) -join ';') -eq 'email=Email;sms=SMS text messages;push=Browser push notifications' -and
    ($q1.Recommended -join ',') -eq 'email,push' -and $q1.RecWhy -match '^Email works today' -and $q1.Required -and -not $ex.Find('sender-name').Required -and $ex.Find('launch').Recommended.Count -eq 0)
Check 'explanation, context (with lists, links, code) and intro kept as Markdown' ($q1.Explanation -eq 'Pick every channel the first version should support.' -and $q1.Context -match '(?m)^- Email goes' -and $q1.Context -match '\[mailer README\]' -and $ex.Intro -match '\*\*notification settings\*\*')
Check 'directives and headings inside code blocks are text, not questions' ($ex.Find('not-a-question') -eq $null -and $q2.Context -match '<!-- question id="not-a-question"' -and $q2.Context -match '(?m)^```sql' -and $q2.Context -match '(?m)^~~~$')
Check 'type summary for the import preview' ($ex.TypeSummary() -eq '2 single choice, 1 multiple choice, 1 short text, 1 long text')
$g = [IO.File]::ReadAllText($guidePath).Replace("`r`n", "`n"); $gi = $g.IndexOf('````markdown'); $gj = $g.IndexOf("`n" + ([string][char]96 * 4), $gi + 5)
$gEx = P ($g.Substring($gi + 12, $gj - $gi - 12))
Check 'the guide''s own valid example parses (guide and parser agree)' ($gEx.Ok -and $gEx.Questions.Count -eq 4 -and $gEx.Find('export-format').Context -match 'writeXlsx' -and ($gEx.Find('columns').Recommended -join ',') -eq 'payment,source') (Errs $gEx)
$crlf = P ([IO.File]::ReadAllText($exPath).Replace("`r`n", "`n").Replace("`n", "`r`n"))
Check 'CRLF line endings parse the same' ($crlf.Ok -and $crlf.Questions.Count -eq 5 -and $crlf.Find('default-state').Context -eq $q2.Context)
$bom = $QF::ParseBytes([byte[]](0xEF, 0xBB, 0xBF) + [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($exPath)))
Check 'a UTF-8 BOM is fine' ($bom.Ok -and $bom.Questions.Count -eq 5)

# --- invalid files: clear errors naming the question, never a silent guess ---
$head = "<!-- questionnaire v1 -->`n# T`n`n"
function Q([string]$attrs, [string]$body = '') { "<!-- question $attrs -->`n## Question text?`n$body`n" }
$opts = "<!-- options -->`n- ``a`` Alpha`n- ``b`` Beta`n"
$cases = @(
    @('ordinary Markdown is refused, with the reason', "# Notes`n`n- What DB?`n- Which colour?`n", 'first line must be <!-- questionnaire v1 -->'),
    @('another format version is refused', "<!-- questionnaire v2 -->`n# T`n", 'reads v1'),
    @('duplicate question ids', ($head + (Q 'id="x" type="single"' $opts) + (Q 'id="x" type="short"')), 'Question 2 \(id "x", line \d+\): the id "x" is already used'),
    @('duplicate option ids', ($head + (Q 'id="x" type="single"' "<!-- options -->`n- ``a`` A`n- ``a`` B`n")), 'Question 1 \(id "x", line 4\), line \d+: option id "a" is used twice'),
    @('recommendation of a missing option', ($head + (Q 'id="x" type="single"' ($opts + "<!-- recommended: c -->`n"))), 'recommended option "c" isn''t one of'),
    @('two recommendations on a single-choice question', ($head + (Q 'id="x" type="single"' ($opts + "<!-- recommended: a, b -->`n"))), 'can recommend only one'),
    @('recommendation on a text question', ($head + (Q 'id="x" type="long"' "<!-- recommended: a -->`n")), 'only for single and multiple'),
    @('options on a text question', ($head + (Q 'id="x" type="short"' $opts)), 'can''t have options'),
    @('a choice question without options', ($head + (Q 'id="x" type="multiple"')), 'needs an <!-- options --> section with at least 2'),
    @('unknown type', ($head + (Q 'id="x" type="dropdown"' $opts)), 'type must be single, multiple, short or long \(not "dropdown"\)'),
    @('unknown attribute (typo) is not ignored', ($head + (Q 'id="x" type="short" requred="false"')), 'unknown attribute requred'),
    @('missing id', ($head + (Q 'type="short"')), 'has no id'),
    @('invalid id characters', ($head + (Q 'id="Bad Id" type="short"')), 'may only use lowercase'),
    @('a line in options that isn''t an option', ($head + (Q 'id="x" type="single"' "<!-- options -->`n- ``a`` A`n- b without id`n- ``c`` C`n")), 'line 8: expected an option line'),
    @('no question heading', ($head + "<!-- question id=`"x`" type=`"short`" -->`nJust text`n"), 'must be a ''## '' heading'),
    @('an unclosed code block (it would swallow questions)', ($head + (Q 'id="x" type="short"' "<!-- context -->`n``````js`nlet a`n") + (Q 'id="y" type="short"')), 'never closed'),
    @('no title', ("<!-- questionnaire v1 -->`n" + (Q 'id="x" type="short"')), 'There is no title'),
    @('no questions', ($head + "Only an intro.`n"), 'There are no questions'),
    @('a section before any question', ($head + $opts), 'must be inside a question'),
    @('a second context section', ($head + (Q 'id="x" type="short"' "<!-- context -->`nA`n<!-- context -->`nB`n")), 'a second <!-- context --> section')
)
foreach ($c in $cases) { $d = P $c[1]; Check ("invalid: " + $c[0]) (-not $d.Ok -and (Errs $d) -match $c[2]) (Errs $d) }
Check 'invalid: larger than 512 KB' (-not ($QF::ParseBytes((New-Object byte[] (513 * 1024)))).Ok)
Check 'invalid: not UTF-8' ((Errs ($QF::ParseBytes([byte[]](0x3C, 0x21, 0xFF, 0xFE, 0x41)))) -match 'isn''t UTF-8')
$okCase = P ($head + (Q 'id="x" type="single" required="false"' ($opts + "<!-- context -->`n````````md`n``````js`n<!-- options -->`n``````" + "`n````````" + "`nAfter.`n")) + (Q 'id="y" type="short"'))
Check 'a longer fence holding a shorter one, with a directive inside, stays context' ($okCase.Ok -and $okCase.Questions.Count -eq 2 -and $okCase.Find('x').Options.Count -eq 2 -and $okCase.Find('x').Context -match 'After\.$' -and $okCase.Find('x').Context -match '<!-- options -->') (Errs $okCase)
Check 'an ordinary HTML comment is plain text, not structure' ((P ($head + (Q 'id="x" type="short"' "<!-- note to self -->`n"))).Find('x').Explanation -eq '<!-- note to self -->')

# --- answers: validation and status rules ---
$desk0 = New-Object ClaudeStatus.QuestionnaireDesk((Join-Path $tmp 'qn-unused'), (Join-Path $tmp 'qn-unused-out'))
function A($q, [string]$json) { $e = $null; $a = [ClaudeStatus.QnAnswer]::From((J $json), $q, [ref]$e); return @{ A = $a; E = $e } }
Check 'answer: unknown option refused' ((A $q1 '{"s":["fax"]}').A -eq $null)
Check 'answer: two choices on a single-choice question refused (also option + Other)' ((A $q2 '{"s":["on","off"]}').A -eq $null -and (A $q2 '{"s":["on"],"o":true,"ot":"x"}').A -eq $null)
Check 'answer: text on a choice question / options on a text question refused' ((A $q1 '{"t":"hi"}').A -eq $null -and (A ($ex.Find('wording')) '{"s":["email"]}').A -eq $null)
Check 'answer: unknown mode and over-long text refused' ((A $q1 '{"m":"approve"}').A -eq $null -and (A ($ex.Find('wording')) ('{"t":"' + ('x' * 20001) + '"}')).A -eq $null)
function St($q, [string]$json) { [ClaudeStatus.QnAnswer]::StatusOf($q, (A $q $json).A) }
Check 'status: Other with empty text is unanswered; with text answered' ((St $q2 '{"o":true,"ot":"  "}') -eq 'unanswered' -and (St $q2 '{"o":true,"ot":"Only staff"}') -eq 'answered')
Check 'status: Let the AI decide and Skip win over a kept selection (the selection is remembered, not exported)' ((St $q1 '{"m":"delegate","s":["sms"]}') -eq 'delegated' -and (St $q1 '{"m":"skip","s":["sms"]}') -eq 'skipped' -and ((A $q1 '{"m":"delegate","s":["sms"]}').A.Sel -join ',') -eq 'sms')
Check 'recommendation: used vs. edited afterwards vs. not used' ([ClaudeStatus.QnAnswer]::RecOf($q1, (A $q1 '{"s":["push","email"],"r":true}').A) -eq 'used' -and
    [ClaudeStatus.QnAnswer]::RecOf($q1, (A $q1 '{"s":["email"],"r":true}').A) -eq 'edited' -and [ClaudeStatus.QnAnswer]::RecOf($q1, (A $q1 '{"s":["email","push"]}').A) -eq 'no')

# --- export: structure-safe, literal, re-readable ---
$evil = "First line`n## Heading-looking line`n<!-- question id=`"forged`" type=`"short`" -->`n<!-- /answer -->`n<!-- answer id=`"channels`" -->`n``````js`ncode`n``````" + "`n````````" + " four backticks`n---`n   indented`n" + [char]0x00E9 + " Unicode " + [char]0x2713
$ansJson = $qser.Serialize(@{
    'channels' = @{ s = @('email', 'push'); r = $true; c = $evil; o = $true; ot = "Pager`nsecond line" }
    'default-state' = @{ m = 'delegate'; c = 'Prefer opt-in if unsure.' }
    'sender-name' = @{ m = 'skip' }
    'wording' = @{ t = $evil }
})
$answers = New-Object 'System.Collections.Generic.Dictionary[string,ClaudeStatus.QnAnswer]'
foreach ($kv in (J $ansJson).GetEnumerator()) { $answers[$kv.Key] = (A ($ex.Find($kv.Key)) ($qser.Serialize($kv.Value))).A }
$meta = New-Object 'System.Collections.Generic.Dictionary[string,string]'; $meta['questionnaire'] = 'QID'; $meta['submission'] = 'SID'
$out = $QF::Export($ex, $answers, $meta, $false)
$re = P $out
Check 'export re-reads as the same questionnaire: same ids, types, options, context (answer text never forms a question)' ($re.Ok -and (($re.Questions | ForEach-Object { $_.Id }) -join ',') -eq (($ex.Questions | ForEach-Object { $_.Id }) -join ',') -and $re.Find('forged') -eq $null -and
    $re.Find('default-state').Context -eq $q2.Context -and $re.Find('channels').RecWhy -eq $q1.RecWhy) (Errs $re)
$nl = "`n"; $bt = [string][char]96
Check 'multiline answers and comments appear byte for byte inside fences longer than any backtick run' ($out.Contains(($bt * 5) + 'text' + $nl + $evil + $nl + ($bt * 5)) -and $out.Contains(($bt * 3) + 'text' + $nl + 'Pager' + $nl + 'second line' + $nl + ($bt * 3)))
Check 'answer blocks: ids, statuses, recommendation use, labels, Let the AI decide, Skipped, Unanswered, comments' ($out -match '<!-- answer id="channels" status="answered" recommendation="edited" -->' -and $out -match '(?m)^- `email` Email$' -and $out -match '(?m)^- `push` Browser push notifications$' -and
    $out -match 'started from the recommendation, then changed it' -and $out -match '- Other \(my own answer\):' -and $out -match '<!-- answer id="default-state" status="delegated"' -and $out -match 'The AI decides: use your judgment' -and $out -match 'Prefer opt-in if unsure' -and
    $out -match '<!-- answer id="sender-name" status="skipped"' -and $out -match '<!-- answer id="launch" status="unanswered"' -and $out -match "Don't treat this as approval")
Check 'header: format version, source identity, summary counts' ($out -match '<!-- answers format="1" questionnaire="QID" submission="SID" -->' -and $out -match '5 questions: 2 answered, 1 Let the AI decide, 1 skipped, 1 unanswered')
Check 'the original text (title, intro, questions, context, options, order) is all there, unchanged' (@(([IO.File]::ReadAllText($exPath).Replace("`r`n", "`n").TrimEnd("`n").Split("`n")) | Where-Object { -not $out.Contains($_) }).Count -eq 0)
$empty = New-Object 'System.Collections.Generic.Dictionary[string,ClaudeStatus.QnAnswer]'
Check 'exporting an answered file again replaces its old answer blocks instead of stacking them' ($QF::Export($re, $empty, $meta, $false) -eq $QF::Export($ex, $empty, $meta, $false))
$exCrlf = $QF::Export($crlf, $answers, $meta, $false)
Check 'a CRLF source exports with CRLF throughout' ($exCrlf -notmatch "[^`r]`n" -and $exCrlf.Contains("Pager`r`nsecond line"))
Check 'output names come from the imported file name only, made safe' ($QF::SafeBase('my questions.md') -eq 'my questions' -and $QF::SafeBase('CON.md') -eq '_CON' -and $QF::SafeBase('a|b?.md') -eq 'a_b_' -and $QF::SafeBase('...md') -eq 'questionnaire')

# --- the desk: import, drafts, persistence, conflicts, replacement, submission ---
$qdir = Join-Path $tmp 'qn-state'; $qout = Join-Path $tmp 'qn-out'
$srcDir = Join-Path $tmp 'qn-project'; $null = New-Item -ItemType Directory -Force $srcDir
$srcFile = Join-Path $srcDir 'questions.md'; Copy-Item $exPath $srcFile
$srcHash = (Get-FileHash $srcFile).Hash
$desk = New-Object ClaudeStatus.QuestionnaireDesk($qdir, $qout)
$bytes = $null; $pv = [ClaudeStatus.QuestionnaireDesk]::Preview($srcFile, [ref]$bytes)
Check 'preview of a valid file: summary with title, project, count and types' ($pv.Ok -and ($desk.ImportSummary($pv)) -match 'Title: Guest portal' -and ($desk.ImportSummary($pv)) -match 'Project: Toah Nipi Portal' -and ($desk.ImportSummary($pv)) -match '5 questions: 2 single choice, 1 multiple choice, 1 short text, 1 long text \(3 required\)')
$txt = Join-Path $srcDir 'notes.txt'; Set-Content $txt 'x'
$big = Join-Path $srcDir 'big.md'; [IO.File]::WriteAllBytes($big, (New-Object byte[] (600 * 1024)))
Check 'preview refuses other file types and oversized files' ((-not ([ClaudeStatus.QuestionnaireDesk]::Preview($txt, [ref]$bytes)).Ok) -and (Errs ([ClaudeStatus.QuestionnaireDesk]::Preview($big, [ref]$bytes))) -match 'larger than 512 KB')
$bytes = $null; $pv = [ClaudeStatus.QuestionnaireDesk]::Preview($srcFile, [ref]$bytes)
$changed = @{ n = 0 }; $desk.add_Changed([Action]({ $changed.n++ }.GetNewClosure()))
$note = $desk.Import($srcFile, $bytes, $pv)
$qid = $desk.Id
Check 'import: active, token-shaped id, nothing replaced, change announced' ($desk.Active -and $qid -match '^[A-Za-z0-9_-]{22}$' -and $note -eq '' -and $changed.n -ge 1)
Check 'import keeps the original bytes in the app''s folder and never touches the source file' ((Get-FileHash $desk.SourcePath).Hash -eq $srcHash -and (Get-FileHash $srcFile).Hash -eq $srcHash -and $desk.Original() -eq [IO.File]::ReadAllText($exPath))
$pd = $desk.PhoneData()
Check 'phone data: questions with types, required, options, recommendations, context; revision 0, no answers' ($pd['q']['id'] -eq $qid -and $pd['q']['qs'].Count -eq 5 -and $pd['q']['qs'][0]['t'] -eq 'multiple' -and $pd['q']['qs'][0]['rc'].Count -eq 2 -and $pd['rev'] -eq 0 -and $pd['answers'].Count -eq 0 -and $pd['q']['qs'][1]['c'] -match 'ALTER TABLE')
function Save([string]$q, [int]$base, [string]$json, [bool]$force = $false) { $desk.Save($q, $base, (J $json), $force) }
$r1 = Save $qid 0 '{"channels":{"s":["email"]}}'
Check 'save: accepted, revision 1' ($r1['result'] -eq 'saved' -and $r1['rev'] -eq 1 -and $desk.Revision -eq 1)
$r1b = Save $qid 0 '{"channels":{"s":["email"]}}'
Check 'save: the same answers again (a retry) change nothing' ($r1b['result'] -eq 'saved' -and $r1b['rev'] -eq 1)
$r2 = Save $qid 1 '{"channels":{"s":["email","push"]},"wording":{"t":"Hi"}}'
$stale = Save $qid 1 '{"channels":{"s":["sms"]}}'
Check 'an older draft never overwrites a newer one: conflict, with the newer answers returned' ($r2['rev'] -eq 2 -and $stale['result'] -eq 'conflict' -and $stale['rev'] -eq 2 -and ($stale['answers']['channels']['s'] -join ',') -eq 'email,push' -and $desk.Revision -eq 2)
$forced = Save $qid 1 '{"channels":{"s":["sms"]}}' $true
Check 'keep this phone''s version (explicit choice): saved as revision 3' ($forced['result'] -eq 'saved' -and $forced['rev'] -eq 3)
$bad = Save $qid 3 '{"channels":{"s":["fax"]}}'
$badQ = Save $qid 3 '{"nope":{"t":"x"}}'
Check 'invalid answers are refused and change nothing' ($bad['result'] -eq 'bad_request' -and $badQ['result'] -eq 'bad_request' -and $desk.Revision -eq 3)
$r4 = Save $qid 3 $ansJson
$desk2 = New-Object ClaudeStatus.QuestionnaireDesk($qdir, $qout)
$pd2 = $desk2.PhoneData()
Check 'drafts survive a laptop restart: same questionnaire, revision and answers' ($desk2.Id -eq $qid -and $desk2.Revision -eq 4 -and $pd2['answers']['wording']['t'] -eq $evil -and $pd2['answers']['default-state']['m'] -eq 'delegate')
$desk = $desk2
$sid1 = 'S1aaaaaaaaaaaaaaaaaaaa'
$need = $desk.Submit($qid, $sid1, 4, (J '{"channels":{"s":["email"]}}'), $false)
Check 'submit with required questions unanswered is refused unless explicitly allowed; nothing written' ($need['result'] -eq 'gaps' -and (@($need['ids']) -join ',') -eq 'default-state,launch' -and -not (Test-Path $qout))
$stSub = $desk.Submit($qid, $sid1, 2, (J '{"channels":{"s":["email"]}}'), $true)
Check 'submit based on an old revision with different answers: conflict, nothing written' ($stSub['result'] -eq 'conflict' -and -not (Test-Path $qout))
$d1 = $desk.Submit($qid, $sid1, 4, (J $ansJson), $true)
$f1 = Join-Path $qout 'questions.answered.md'
$t1 = if (Test-Path $f1) { [IO.File]::ReadAllText($f1) } else { '' }
Check 'delivered only with the complete answered file already on disk (name.answered.md in the output folder)' ($d1['result'] -eq 'delivered' -and $d1['file'] -eq 'questions.answered.md' -and $t1.Contains('submission="' + $sid1 + '"') -and $t1.Replace("`r`n", "`n").Contains($evil) -and $t1 -match 'recommendation="edited"')
Check 'no temporary files left behind' (@(Get-ChildItem $qout -Filter '*.tmp').Count -eq 0)
$again = $desk.Submit($qid, $sid1, 4, (J $ansJson), $true)
$restart = (New-Object ClaudeStatus.QuestionnaireDesk($qdir, $qout)).Submit($qid, $sid1, 4, (J $ansJson), $true)
Check 'retrying the same submission (double tap, lost reply, laptop restart) returns the same file: no duplicate' ($again['result'] -eq 'delivered' -and $again['file'] -eq 'questions.answered.md' -and $restart['file'] -eq 'questions.answered.md' -and @(Get-ChildItem $qout).Count -eq 1)
$d2 = $desk.Submit($qid, 'S2aaaaaaaaaaaaaaaaaaaa', $desk.Revision, (J $ansJson), $true)
Set-Content (Join-Path $qout 'questions.answered-3.md') 'someone else''s file'
$d3 = $desk.Submit($qid, 'S3aaaaaaaaaaaaaaaaaaaa', $desk.Revision, (J $ansJson), $true)
Check 'a new submission gets a new, collision-safe name; existing files are never overwritten' ($d2['file'] -eq 'questions.answered-2.md' -and $d3['file'] -eq 'questions.answered-4.md' -and (Get-Content (Join-Path $qout 'questions.answered-3.md') -Raw) -match 'someone else' -and (Get-FileHash $srcFile).Hash -eq $srcHash)
Check 'the desktop sees the submissions with their paths (and the phone gets file names only)' ($desk.Submissions.Count -eq 3 -and $desk.Submissions[2].Path -eq (Join-Path $qout 'questions.answered-4.md') -and ($qser.Serialize($desk.PhoneData())) -notmatch [regex]::Escape($qout.Replace('\', '\\')))
# the output folder can't be written: not delivered, nothing claimed; fixing it and retrying the SAME submission works once
$blocker = Join-Path $tmp 'qn-blocked'; Set-Content $blocker 'a file where the folder should be'
$desk.SetOutputDir($blocker)
$wfail = $desk.Submit($qid, 'S4aaaaaaaaaaaaaaaaaaaa', $desk.Revision, (J $ansJson), $true)
Check 'a write failure is reported, never acknowledged as delivered' ($wfail['result'] -eq 'error' -and $desk.Submissions.Count -eq 3)
$desk.SetOutputDir((Join-Path $tmp 'qn-out2'))
$ok4 = $desk.Submit($qid, 'S4aaaaaaaaaaaaaaaaaaaa', $desk.Revision, (J $ansJson), $true)
Check 'the retry after the failure delivers once, into the folder chosen on the desktop' ($ok4['result'] -eq 'delivered' -and (Test-Path (Join-Path $tmp 'qn-out2\questions.answered.md')) -and @(Get-ChildItem (Join-Path $tmp 'qn-out2')).Count -eq 1 -and $desk.Submissions.Count -eq 4)
$desk.SetOutputDir('')

# replacement: the old questionnaire's work is kept; stale phone data can't attach to the new one
$src2 = Join-Path $srcDir 'second.md'; [IO.File]::WriteAllText($src2, ($head + (Q 'id="channels" type="short"')))
$b2 = $null; $pv2 = [ClaudeStatus.QuestionnaireDesk]::Preview($src2, [ref]$b2)
Check 'the import preview says it replaces the active one and that its answers are kept' (($desk.ImportSummary($pv2)) -match 'REPLACES the active questionnaire "Guest portal: notification settings" \(\d+ answers saved, submitted 4×\)' -and ($desk.ImportSummary($pv2)) -match 'kept in the history folder')
$note2 = $desk.Import($src2, $b2, $pv2)
$hist = @(Get-ChildItem $desk.HistoryDir -Directory)
$draft = if ($hist.Count) { Join-Path $hist[0].FullName 'questions.draft-answers.md' } else { '' }
Check 'replacing moves the old one to history with its source, state and a draft answered file' ($hist.Count -eq 1 -and (Test-Path (Join-Path $hist[0].FullName 'source.md')) -and (Test-Path (Join-Path $hist[0].FullName 'state.json')) -and (Test-Path $draft) -and
    (Get-Content $draft -Raw) -match 'Draft answers, NOT submitted' -and $note2 -match 'history' -and $desk.Id -ne $qid -and $desk.Revision -eq 0 -and (Test-Path $f1))
$sameId = 'channels'
$staleSave = Save $qid 0 '{"channels":{"s":["email"]}}'
$staleSub = $desk.Submit($qid, 'S5aaaaaaaaaaaaaaaaaaaa', 0, (J '{}'), $true)
Check 'stale draft or submission for the replaced questionnaire is refused (even with a matching question id)' ($staleSave['result'] -eq 'replaced' -and $staleSub['result'] -eq 'replaced' -and $desk.PhoneData()['answers'].Count -eq 0)
Check 'answered files of the old questionnaire stay where they were' ((Test-Path $f1) -and (Test-Path (Join-Path $qout 'questions.answered-2.md')))
$arch = $desk.Archive()
Check 'remove: the questionnaire leaves the phone and goes to history' (-not $desk.Active -and $desk.PhoneData()['q'] -eq $null -and @(Get-ChildItem $desk.HistoryDir -Directory).Count -eq 2 -and $desk.Marker -eq '')

# --- phone API: same protections as the other phone actions ---
$qp = [THttp]::FreePort()
$qroot = Join-Path $tmp 'qn-phone'; $null = New-Item -ItemType Directory -Force $qroot
foreach ($fn in 'phone.html', 'phone.css', 'phone.js', 'sw.js', 'workers.png', 'favicon.ico', 'worker-192.png', 'apple-touch-icon.png') { Copy-Item (Join-Path $src $fn) $qroot }
$pdesk = New-Object ClaudeStatus.QuestionnaireDesk((Join-Path $qroot 'state'), (Join-Path $qroot 'answered'))
$bx = $null; $pvx = [ClaudeStatus.QuestionnaireDesk]::Preview($srcFile, [ref]$bx); $null = $pdesk.Import($srcFile, $bx, $pvx)
$srv = New-Object ClaudeStatus.PhoneServer($qroot, $qp, $null, $null, [Func[string[]]] { @() })
$srv.Questionnaires = $pdesk
$pdesk.add_Changed([Delegate]::CreateDelegate([Action], $srv, 'Bump'))   # a real delegate: it runs on the server's threads
$null = $srv.Start(); $srv.Publish((New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'), $false, '')
function QReq([string]$method, [string]$path, [hashtable]$h = @{}, [string]$body = $null) {
    $lines = @("$method $path HTTP/1.1")
    if (-not $h.ContainsKey('Host')) { $lines += "Host: 127.0.0.1:$qp" }
    foreach ($k in $h.Keys) { if ($null -ne $h[$k]) { $lines += "${k}: $($h[$k])" } }
    if ($null -ne $body) { $lines += "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($body))" }
    $raw = [THttp]::Send($qp, (($lines -join "`r`n") + "`r`n`r`n" + $body), 30000)
    return @{ Code = [THttp]::Status($raw); Body = [THttp]::Body($raw) }
}
$qh = @{ Origin = "http://127.0.0.1:$qp"; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'qn'; 'Sec-Fetch-Site' = 'same-origin' }
function QH([hashtable]$o = @{}) { $h = $qh.Clone(); foreach ($k in $o.Keys) { $h[$k] = $o[$k] }; return $h }
$pqid = $pdesk.Id
$g0 = QReq 'GET' '/api/qn'
$g1 = QReq 'GET' '/api/qn' @{ 'X-Claude-Status' = 'qn' }
$g2 = QReq 'GET' '/api/qn' @{ 'X-Claude-Status' = 'qn'; 'Sec-Fetch-Site' = 'cross-site' }
Check 'api/qn read needs the page''s own header (and a same-origin fetch)' ($g0.Code -eq 403 -and $g1.Code -eq 200 -and $g2.Code -eq 403 -and ($g1.Body | ConvertFrom-Json).q.id -eq $pqid)
Check 'api/qn read never shows laptop paths' ($g1.Body -notmatch [regex]::Escape($srcDir.Replace('\', '\\')) -and $g1.Body -notmatch 'qn-phone')
$saveBody = '{"op":"save","qid":"' + $pqid + '","base":0,"answers":{"channels":{"s":["email"]}}}'
Check 'save without Origin / with another site''s Origin / wrong action header / no JSON type: refused' ((QReq 'POST' '/api/qn' (QH @{ Origin = $null }) $saveBody).Code -eq 403 -and (QReq 'POST' '/api/qn' (QH @{ Origin = 'http://evil.example' }) $saveBody).Code -eq 403 -and
    (QReq 'POST' '/api/qn' (QH @{ 'X-Claude-Status' = 'queue' }) $saveBody).Code -eq 403 -and (QReq 'POST' '/api/qn' (QH @{ 'Content-Type' = 'text/plain' }) $saveBody).Code -eq 415 -and $pdesk.Revision -eq 0)
Check 'api/qn: a foreign Host is refused; GET-only and POST-only paths answer 405 for the other method' ((QReq 'GET' '/api/qn' @{ Host = 'evil.example'; 'X-Claude-Status' = 'qn' }).Code -eq 421 -and (QReq 'PUT' '/api/qn' (QH) '{}').Code -eq 405)
$v0 = ((QReq 'GET' '/api/board').Body | ConvertFrom-Json)
$sv = QReq 'POST' '/api/qn' (QH) $saveBody
$v1 = ((QReq 'GET' '/api/board').Body | ConvertFrom-Json)
Check 'save over HTTP: 200 with the new revision; the board''s marker and version move so phones refetch' ($sv.Code -eq 200 -and ($sv.Body | ConvertFrom-Json).rev -eq 1 -and $v1.qn -eq ($pqid + '.1.0') -and $v1.v -gt $v0.v)
Check 'malformed requests: bad JSON, bad id, missing base, unknown op, a path instead of answers' ((QReq 'POST' '/api/qn' (QH) '{oops').Code -eq 400 -and (QReq 'POST' '/api/qn' (QH) ('{"op":"save","qid":"../x","base":0,"answers":{}}')).Code -eq 400 -and
    (QReq 'POST' '/api/qn' (QH) ('{"op":"save","qid":"' + $pqid + '","answers":{}}')).Code -eq 400 -and (QReq 'POST' '/api/qn' (QH) ('{"op":"write","qid":"' + $pqid + '","base":0,"answers":{}}')).Code -eq 400 -and
    (QReq 'POST' '/api/qn' (QH) ('{"op":"save","qid":"' + $pqid + '","base":0,"answers":"C:\\Windows"}')).Code -eq 400)
Check 'stale revision over HTTP: 409 conflict; replaced id: 409 replaced' ((QReq 'POST' '/api/qn' (QH) ('{"op":"save","qid":"' + $pqid + '","base":0,"answers":{"channels":{"s":["sms"]}}}')).Code -eq 409 -and
    ((QReq 'POST' '/api/qn' (QH) ('{"op":"save","qid":"AAAAAAAAAAAAAAAAAAAAAA","base":0,"answers":{}}')).Body | ConvertFrom-Json).result -eq 'replaced')
$bigRaw = [THttp]::Send($qp, ("POST /api/qn HTTP/1.1`r`nHost: 127.0.0.1:$qp`r`nContent-Type: application/json`r`nContent-Length: " + (4 * 1024 * 1024 + 1) + "`r`n`r`n{"), 10000)
Check 'oversized request body refused (413) before reading it' ([THttp]::Status($bigRaw) -eq 413)
$subBody = '{"op":"submit","qid":"' + $pqid + '","sid":"Phoneaaaaaaaaaaaaaaaaa","base":1,"force":true,"answers":{"channels":{"s":["email"]},"output":{"t":"C:\\Windows\\x.md"}}}'
Check 'a submission naming extra fields or questions can''t steer anything: refused' ((QReq 'POST' '/api/qn' (QH) $subBody).Code -eq 400 -and -not (Test-Path (Join-Path $qroot 'answered')))
$subBody = '{"op":"submit","qid":"' + $pqid + '","sid":"Phoneaaaaaaaaaaaaaaaaa","base":1,"force":true,"answers":{"channels":{"s":["email"]}},"path":"C:\\Windows\\evil.md","file":"..\\..\\evil.md"}'
$tasks = 1..4 | ForEach-Object { [THttp]::SendAsync($qp, ("POST /api/qn HTTP/1.1`r`nHost: 127.0.0.1:$qp`r`nOrigin: http://127.0.0.1:$qp`r`nContent-Type: application/json`r`nX-Claude-Status: qn`r`nSec-Fetch-Site: same-origin`r`nContent-Length: " + [Text.Encoding]::UTF8.GetByteCount($subBody) + "`r`n`r`n" + $subBody), 30000) }
[Threading.Tasks.Task]::WaitAll($tasks)
$res = @($tasks | ForEach-Object { [THttp]::Body($_.Result) | ConvertFrom-Json })
Check 'four simultaneous identical submits: all "delivered", one file, in the app''s folder (path fields ignored)' (@($res | Where-Object { $_.result -eq 'delivered' -and $_.file -eq 'questions.answered.md' }).Count -eq 4 -and @(Get-ChildItem (Join-Path $qroot 'answered')).Count -eq 1 -and -not (Test-Path 'C:\Windows\evil.md'))
$srv.Stop()

# --- the phone's status rules = the laptop's (same answers, same statuses) ---
$nodeQ = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeQ) { Skip 'phone answer status matches the laptop' 'node not found' }
else {
    $samples = @('{}', '{"s":["email"]}', '{"o":true,"ot":"  "}', '{"o":true,"ot":"x"}', '{"m":"delegate","s":["sms"]}', '{"m":"skip"}', '{"s":["push","email"],"r":true}', '{"s":["email"],"r":true}')
    $sf = Join-Path $tmp 'qn-samples.json'; [IO.File]::WriteAllText($sf, ('[' + ($samples -join ',') + ']'))
    $js = "const m=require(process.argv[1]);const q={t:'multiple',r:true,rc:['email','push']};const s=require(process.argv[2]);console.log(JSON.stringify(s.map(a=>m.qnStatus(q,Object.assign({s:[],o:false,ot:'',t:'',c:'',m:''},a))+'/'+m.qnRec(q,Object.assign({s:[]},a)))))"
    $phoneSt = (& $nodeQ.Source -e $js (Join-Path $src 'phone.js') $sf) | ConvertFrom-Json
    $deskSt = $samples | ForEach-Object { $a = (A $q1 $_).A; [ClaudeStatus.QnAnswer]::StatusOf($q1, $a) + '/' + [ClaudeStatus.QnAnswer]::RecOf($q1, $a) }
    Check 'phone answer status and recommendation use match the laptop''s for the same answers' (($phoneSt -join ',') -eq ($deskSt -join ',')) (($phoneSt -join ',') + ' vs ' + ($deskSt -join ','))
    $mdJs = "const m=require(process.argv[1]);const b=m.mdBlocks('Text <script>alert(1)</script> [x](javascript:alert(1)) ![pic](http:/' + '/e.x/a.png)\n\n~~~\n<!-- question -->\n~~~');const i=m.mdInline(b[0].v);console.log(JSON.stringify({kinds:b.map(x=>x.k),pieces:i.map(x=>x.k),link:i.filter(x=>x.k==='a').length,img:i.filter(x=>x.k==='img').length,code:b[1].v}))"
    $mdr = (& $nodeQ.Source -e $mdJs (Join-Path $src 'phone.js')) | ConvertFrom-Json
    Check 'phone Markdown: HTML stays text, javascript: links aren''t links, images become placeholders, code is literal' ((($mdr.kinds) -join ',') -eq 'p,code' -and $mdr.link -eq 0 -and $mdr.img -eq 1 -and $mdr.code -eq '<!-- question -->') ($mdr | ConvertTo-Json -Compress)
}

# --- the desktop tab ---
$vdesk = New-Object ClaudeStatus.QuestionnaireDesk((Join-Path $tmp 'qn-view'), (Join-Path $tmp 'qn-view-out'))
$bv = $null; $pvv = [ClaudeStatus.QuestionnaireDesk]::Preview($srcFile, [ref]$bv); $null = $vdesk.Import($srcFile, $bv, $pvv)
$null = $vdesk.Save($vdesk.Id, 0, (J $ansJson), $false)
$null = $vdesk.Submit($vdesk.Id, 'Viewaaaaaaaaaaaaaaaaaa', 1, (J $ansJson), $true)
$qe = New-Object ClaudeStatus.QueueEngine((Join-Path $tmp 'qn-queue'))
$vcal = New-Object ClaudeStatus.CalendarSource; $vcal.Log = New-Object ClaudeStatus.ActivityLog((Join-Path $tmp 'qn-act'))
foreach ($sc in 1.0, 1.5) {
    $qv = New-Object ClaudeStatus.QueueView($qe, $sc, $vcal, $vdesk)
    $sz = New-Object Drawing.Size([int](900 * $sc), [int](600 * $sc)); $qv.Size = $sz; $null = $qv.Handle; $qv.CreateControl()
    $qv.ShowTab(2)
    $bmp = New-Object Drawing.Bitmap($sz.Width, $sz.Height); $qv.DrawToBitmap($bmp, (New-Object Drawing.Rectangle(0, 0, $sz.Width, $sz.Height)))
    $bmp.Save((Join-Path $RenderDir ("questionnaires-desktop-{0}.png" -f [int]($sc * 100)))); $bmp.Dispose()
    if ($sc -eq 1.0) {
        $view = $qv.Questionnaires
        $texts = New-Object System.Collections.ArrayList
        function Walk($c) { foreach ($k in $c.Controls) { [void]$texts.Add($k.Text); Walk $k } }
        Walk $view
        $all = $texts -join "`n"
        Check 'desktop tab: Tasks / Calendar / Questionnaires; the Questionnaires tab shows the questionnaire, progress and the saved path' ($view.Visible -and $all -match 'Guest portal: notification settings' -and $all -match 'On the phone: 2 answered, 1 Let the AI decide, 1 skipped, 1 unanswered' -and
            $all -match [regex]::Escape((Join-Path $tmp 'qn-view-out\questions.answered.md')) -and $all -match 'Copy answered Markdown' -and $all -match 'Open containing folder' -and $all -match 'Import questionnaire')
        $qv.ShowTab(0)
        Check 'desktop tab: switching back to Tasks hides it again' (-not $view.Visible)
    }
    $qv.Dispose()
}
Info "Questionnaires tab renders: $RenderDir\questionnaires-desktop-*.png"

# --- the phone page in a real browser: the whole flow ---
$qChrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Browser) { Skip 'questionnaire on the phone page in a browser' 'run with -Browser' }
elseif (-not $qChrome -or -not $nodeQ) { Skip 'questionnaire on the phone page in a browser' 'needs Chrome or Edge, and Node.js 22+' }
else {
    $bp = [THttp]::FreePort()
    $broot = Join-Path $tmp 'qn-browser'; $null = New-Item -ItemType Directory -Force $broot
    foreach ($fn in 'phone.html', 'phone.css', 'phone.js', 'sw.js', 'workers.png', 'favicon.ico', 'worker-192.png', 'apple-touch-icon.png') { Copy-Item (Join-Path $src $fn) $broot }
    $bdesk = New-Object ClaudeStatus.QuestionnaireDesk((Join-Path $broot 'state'), (Join-Path $broot 'answered'))
    $bb = $null; $bpv = [ClaudeStatus.QuestionnaireDesk]::Preview($srcFile, [ref]$bb); $null = $bdesk.Import($srcFile, $bb, $bpv)
    $script:bsrv = New-Object ClaudeStatus.PhoneServer($broot, $bp, $null, $null, [Func[string[]]] { @() }); $script:bsrv.Questionnaires = $bdesk
    $bdesk.add_Changed([Delegate]::CreateDelegate([Action], $script:bsrv, 'Bump'))
    $null = $script:bsrv.Start(); $script:bsrv.Publish((New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'), $false, '')
    $bprof = Join-Path $tmp 'chrome-qn'
    $bc = Start-Process $qChrome -ArgumentList '--headless=new', '--remote-debugging-port=0', "--user-data-dir=`"$bprof`"", '--no-first-run', '--no-default-browser-check', '--disable-extensions', 'about:blank' -PassThru
    try {
        $pf = Join-Path $bprof 'DevToolsActivePort'
        for ($i = 0; $i -lt 100 -and -not (Test-Path $pf); $i++) { Start-Sleep -Milliseconds 100 }
        $dbgPort = if (Test-Path $pf) { (Get-Content $pf)[0] } else { '' }
        if (-not $dbgPort) { Check 'questionnaire browser: headless browser started' $false }
        else {
            $qdirB = Join-Path $tmp 'qn-browser-ctl'; $null = New-Item -ItemType Directory -Force $qdirB
            $outF = Join-Path $tmp 'qn-browser.txt'
            $np = Start-Process $nodeQ.Source -ArgumentList "`"$(Join-Path $PSScriptRoot 'questionnaire-browser.mjs')`"", $dbgPort, $bp, "`"$RenderDir`"", "`"$qdirB`"" -PassThru -NoNewWindow -RedirectStandardOutput $outF -RedirectStandardError (Join-Path $tmp 'qn-browser-err.txt')
            $handled = @{}
            $deadline = (Get-Date).AddMinutes(4)
            while (-not $np.HasExited -and (Get-Date) -lt $deadline) {
                foreach ($req in @(Get-ChildItem $qdirB -Filter 'req-*' -ErrorAction SilentlyContinue)) {
                    $name = $req.Name.Substring(4)
                    if ($handled[$name]) { continue }
                    $handled[$name] = $true
                    $reply = 'ok'
                    switch -Wildcard ($name) {
                        'state*'      { $reply = $qser.Serialize(@{ rev = $bdesk.Revision; answers = $bdesk.PhoneData()['answers']; subs = $bdesk.Submissions.Count; files = @(Get-ChildItem (Join-Path $broot 'answered') -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) }) }
                        'other-device*' { $null = $bdesk.Save($bdesk.Id, $bdesk.Revision, (J '{"sender-name":{"t":"Set on the laptop tab"}}'), $true) }
                        'replace*'    { $r2 = Join-Path $srcDir 'replacement.md'; [IO.File]::WriteAllText($r2, ($head + (Q 'id="channels" type="short"'))); $rb = $null; $rpv = [ClaudeStatus.QuestionnaireDesk]::Preview($r2, [ref]$rb); $null = $bdesk.Import($r2, $rb, $rpv) }
                    }
                    [IO.File]::WriteAllText((Join-Path $qdirB "done-$name"), $reply)
                }
                Start-Sleep -Milliseconds 50
            }
            if (-not $np.HasExited) { $np.Kill(); Check 'questionnaire browser: finished in time' $false }
            $lines = @(Get-Content $outF -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(PASS|FAIL)\t' })
            if (-not $lines.Count) { Check 'questionnaire browser: produced results' $false ((Get-Content (Join-Path $tmp 'qn-browser-err.txt') -Raw -ErrorAction SilentlyContinue) -as [string]) }
            foreach ($l in $lines) { $p = $l -split "`t"; Check ('phone questions: ' + $p[1]) ($p[0] -eq 'PASS') $p[2] }
            $afile = Get-ChildItem (Join-Path $broot 'answered') -Filter '*.answered*.md' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($afile) {
                $at = [IO.File]::ReadAllText($afile.FullName)
                Check 'phone flow: the answered file on the laptop has the phone''s answers (choice, recommendation, Other, comment, dictated text, Let the AI decide, skip)' ($at -match 'status="answered" recommendation="used"' -and $at -match 'Comment:' -and $at -match 'Only if they opted in' -and
                    $at -match '<!-- answer id="default-state" status="delegated"' -and $at -match '<!-- answer id="sender-name" status="skipped"' -and $at -match '## Not a heading' -and ($QF::Parse($at)).Questions.Count -eq 5)
                Info "Answered file from the browser run: $($afile.FullName)"
            }
            Info "Phone questionnaire screenshots: $RenderDir\phone-qn-*.png"
        }
    } finally { try { Stop-Process -Id $bc.Id -Force -ErrorAction SilentlyContinue } catch {}; if ($script:bsrv) { $script:bsrv.Stop() } }
}

if ($qnStandalone) {
    Write-Host ''
    Write-Host ("RESULT: {0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
    if ($script:fail) { $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
    try { Get-ChildItem $tmp -Exclude 'renders' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    exit $(if ($script:fail) { 1 } else { 0 })
}
