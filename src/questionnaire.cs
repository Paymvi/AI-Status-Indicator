// Claude Status - Questionnaires: clarification questions Claude wrote as a Markdown file (questionnaires\QUESTIONNAIRE_GUIDE.md,
// format v1), imported on the laptop, answered on the phone, and written back as a NEW answered Markdown file.
// C# 5 (Windows PowerShell's Add-Type). No AI calls, no network: the phone talks to phone.cs (/api/qn), which calls QuestionnaireDesk.
//
//   QnFormat         the v1 parser (structure = standalone HTML-comment directive lines outside fenced code) and the exporter
//   QuestionnaireDesk the one active questionnaire: its preserved source, the phone's draft (revisioned), submissions (idempotent,
//                    written durably before they are acknowledged), and replaced questionnaires kept in history
//   QuestionnaireView the desktop tab (Queue window -> Questionnaires): import (picker or drop), preview, original Markdown, results
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeStatus
{
    public class QnOption { public string Id = "", Label = ""; }

    public class QnQuestion
    {
        public string Id = "", Type = "", Text = "", Explanation = "", Context = "", RecWhy = "";
        public bool Required = true;
        public List<QnOption> Options = new List<QnOption>();
        public List<string> Recommended = new List<string>();
        public int Line;            // 1-based line of its directive
        internal int End;           // index of the first line after it
        public bool Choice { get { return Type == "single" || Type == "multiple"; } }
        public QnOption Option(string id) { foreach (var o in Options) if (o.Id == id) return o; return null; }
    }

    public class QnDoc
    {
        public string Title = "", Project = "", Intro = "";
        public List<QnQuestion> Questions = new List<QnQuestion>();
        public List<string> Errors = new List<string>();
        internal string[] Lines = new string[0];
        internal bool[] Skip = new bool[0];   // lines of answer blocks (an answered file imported again): never exported twice
        internal int Marker;
        internal string Newline = "\n";
        public bool Ok { get { return Errors.Count == 0; } }
        public QnQuestion Find(string id) { foreach (var q in Questions) if (q.Id == id) return q; return null; }

        public string TypeSummary()
        {
            var names = new[] { "single", "single choice", "multiple", "multiple choice", "short", "short text", "long", "long text" };
            var parts = new List<string>();
            for (int i = 0; i < names.Length; i += 2)
            {
                int n = Questions.FindAll(q => q.Type == names[i]).Count;
                if (n > 0) parts.Add(n + " " + names[i + 1]);
            }
            return string.Join(", ", parts);
        }
    }

    // One question's answer as the phone keeps it. Mode "" = answering (answered once it has content), "delegate" = Let the AI
    // decide, "skip" = skipped. Selections, Other text and comments survive mode switches; only the export decides what counts.
    public class QnAnswer
    {
        public string Mode = "", OtherText = "", Text = "", Comment = "";
        public List<string> Sel = new List<string>();
        public bool Other, Rec;

        public const string Answered = "answered", Delegated = "delegated", Skipped = "skipped", Unanswered = "unanswered";

        public static string StatusOf(QnQuestion q, QnAnswer a)
        {
            if (a == null) return Unanswered;
            if (a.Mode == "delegate") return Delegated;
            if (a.Mode == "skip") return Skipped;
            if (q.Choice) return a.Sel.Count > 0 || (a.Other && a.OtherText.Trim() != "") ? Answered : Unanswered;
            return a.Text.Trim() != "" ? Answered : Unanswered;
        }

        // "used" = the selection is exactly the recommendation the user applied; "edited" = applied, then changed.
        public static string RecOf(QnQuestion q, QnAnswer a)
        {
            if (a == null || !a.Rec || q.Recommended.Count == 0) return "no";
            var s = new HashSet<string>(a.Sel);
            return !a.Other && s.SetEquals(q.Recommended) ? "used" : "edited";
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> { { "m", Mode }, { "s", new List<string>(Sel) }, { "o", Other }, { "ot", OtherText }, { "t", Text }, { "c", Comment }, { "r", Rec } };
        }

        // Validates one answer from the phone (or the saved state) against its question. Null + error = refused.
        public static QnAnswer From(object raw, QnQuestion q, out string error)
        {
            error = null;
            var d = raw as Dictionary<string, object>;
            if (d == null) { error = "not an object"; return null; }
            var a = new QnAnswer();
            object v;
            a.Mode = d.TryGetValue("m", out v) && v is string ? (string)v : "";
            if (a.Mode != "" && a.Mode != "delegate" && a.Mode != "skip") { error = "unknown answer mode"; return null; }
            a.OtherText = Str(d, "ot"); a.Text = Str(d, "t"); a.Comment = Str(d, "c");
            if (a.OtherText == null || a.Text == null || a.Comment == null) { error = "a text field isn't text"; return null; }
            a.Other = d.TryGetValue("o", out v) && v is bool && (bool)v;
            a.Rec = d.TryGetValue("r", out v) && v is bool && (bool)v;
            if (d.TryGetValue("s", out v) && v != null)
            {
                var list = v as System.Collections.IList;
                if (list == null) { error = "selection isn't a list"; return null; }
                foreach (var x in list)
                {
                    var id = x as string;
                    if (id == null || q.Option(id) == null) { error = "unknown option"; return null; }
                    if (!a.Sel.Contains(id)) a.Sel.Add(id);
                }
            }
            if (a.OtherText.Length > QnFormat.MaxText || a.Text.Length > QnFormat.MaxText || a.Comment.Length > QnFormat.MaxText) { error = "text too long"; return null; }
            if ((a.OtherText + a.Text + a.Comment).IndexOf('\0') >= 0) { error = "NUL character"; return null; }
            if (!q.Choice && (a.Sel.Count > 0 || a.Other || a.OtherText != "")) { error = "options on a text question"; return null; }
            if (q.Choice && a.Text != "") { error = "text on a choice question"; return null; }
            if (q.Type == "single" && a.Sel.Count + (a.Other ? 1 : 0) > 1) { error = "more than one choice on a single-choice question"; return null; }
            if (q.Type == "short") { if (a.Text.Length > QnFormat.MaxShort) { error = "text too long"; return null; } a.Text = a.Text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '); }
            if (a.Rec && q.Recommended.Count == 0) a.Rec = false;
            // keep the selection in the questionnaire's option order
            a.Sel.Sort((x, y) => q.Options.FindIndex(o => o.Id == x).CompareTo(q.Options.FindIndex(o => o.Id == y)));
            return a;
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return !d.TryGetValue(k, out v) || v == null ? "" : v as string; }
    }

    public static class QnFormat
    {
        public const int Version = 1, MaxBytes = 512 * 1024, MaxQuestions = 200, MaxOptions = 50, MaxText = 20000, MaxShort = 2000, MaxErrors = 40;
        public const string Rejected = "This file doesn't follow QUESTIONNAIRE_GUIDE.md (format v1), so nothing was imported. Ask your AI assistant to regenerate it by following QUESTIONNAIRE_GUIDE.md.";
        static readonly Regex IdRx = new Regex("^[a-z0-9][a-z0-9_-]{0,63}$");
        static readonly Regex DirRx = new Regex("^ {0,3}<!--(.*)-->\\s*$");
        static readonly Regex KwRx = new Regex("^\\s*(/?[a-z]+)(:|\\s|$)(.*)$", RegexOptions.Singleline);
        static readonly Regex AttrRx = new Regex("\\G\\s*([A-Za-z][A-Za-z-]*)=\"([^\"]*)\"");
        static readonly Regex OptRx = new Regex("^ {0,3}[-*] +`([^`]+)` +(\\S.*?)\\s*$");
        static readonly Regex OpenFenceRx = new Regex("^ {0,3}(`{3,}|~{3,})(.*)$");
        static readonly HashSet<string> Keywords = new HashSet<string> { "questionnaire", "project", "question", "context", "options", "recommended", "answers", "/answers", "answer", "/answer" };

        // Decodes a file's bytes (UTF-8, BOM optional) and parses it. Errors say which line / question is affected.
        public static QnDoc ParseBytes(byte[] bytes)
        {
            if (bytes.Length > MaxBytes) return Failed("The file is larger than " + (MaxBytes / 1024) + " KB.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Failed("The file isn't UTF-8 text."); }
            return Parse(text);
        }

        static QnDoc Failed(string why) { var d = new QnDoc(); d.Errors.Add(why); return d; }

        public static QnDoc Parse(string text)
        {
            var doc = new QnDoc();
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            doc.Newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int n = lines.Length;
            doc.Lines = lines; doc.Skip = new bool[n];
            if (text.IndexOf('\0') >= 0) { doc.Errors.Add("The file contains NUL characters; it isn't a text file."); return doc; }

            // 1. Classify: fenced code (never structure), directives, content. Answer blocks are skipped whole.
            var code = new bool[n];
            var kw = new string[n]; var arg = new string[n];
            char fc = '\0'; int fl = 0, fenceLine = 0, skipFrom = -1;
            string skipEnd = null;
            for (int i = 0; i < n; i++)
            {
                string l = lines[i];
                if (fc != '\0')
                {
                    code[i] = true;
                    string t = l.TrimStart(' ');
                    if (l.Length - t.Length <= 3 && t.Length >= fl && t.TrimEnd().Trim(fc) == "" && t.TrimEnd().Length >= fl) fc = '\0';
                }
                else
                {
                    var f = OpenFenceRx.Match(l);
                    if (f.Success && !(f.Groups[1].Value[0] == '`' && f.Groups[2].Value.Contains("`")))
                    {
                        code[i] = true; fc = f.Groups[1].Value[0]; fl = f.Groups[1].Value.Length; fenceLine = i + 1;
                    }
                    else
                    {
                        var m = DirRx.Match(l);
                        var k = m.Success && !m.Groups[1].Value.Contains("-->") ? KwRx.Match(m.Groups[1].Value) : null;
                        if (k != null && k.Success && Keywords.Contains(k.Groups[1].Value))
                        {
                            kw[i] = k.Groups[1].Value;
                            arg[i] = (k.Groups[2].Value == ":" ? ":" : "") + k.Groups[3].Value.Trim();
                        }
                    }
                }
                if (skipEnd == null && (kw[i] == "answers" || kw[i] == "answer")) { skipEnd = "/" + kw[i]; skipFrom = i; }
                if (skipEnd != null) { doc.Skip[i] = true; if (kw[i] == skipEnd) skipEnd = null; kw[i] = null; }
                else if (kw[i] == "/answers" || kw[i] == "/answer") { Err(doc, "Line " + (i + 1) + ": <!-- " + kw[i] + " --> without its opening <!-- " + kw[i].Substring(1) + " … -->."); kw[i] = null; }
            }
            if (fc != '\0') { Err(doc, "Line " + fenceLine + ": this fenced code block is never closed, so everything after it would be swallowed. Close it with " + new string(fc, fl) + "."); return doc; }
            if (skipEnd != null) { Err(doc, "Line " + (skipFrom + 1) + ": this answer block is never closed with <!-- " + skipEnd + " -->."); return doc; }

            // 2. Header: the version marker, title, project and introduction.
            int first = 0;
            while (first < n && (doc.Skip[first] || lines[first].Trim() == "")) first++;
            if (first >= n || kw[first] != "questionnaire")
            {
                Err(doc, "Line " + Math.Min(first + 1, n) + ": the first line must be <!-- questionnaire v1 -->. This looks like ordinary Markdown, not a questionnaire file.");
                return doc;
            }
            if (arg[first] != "v1") { Err(doc, "Line " + (first + 1) + ": this is questionnaire format \"" + arg[first] + "\"; Claude Status reads v1 (<!-- questionnaire v1 -->)."); return doc; }
            doc.Marker = first;

            var intro = new List<string>();
            int i2 = first + 1;
            for (; i2 < n && kw[i2] != "question"; i2++)
            {
                if (doc.Skip[i2]) continue;
                string l = lines[i2];
                if (kw[i2] == "project")
                {
                    if (doc.Project != "") Err(doc, "Line " + (i2 + 1) + ": a second <!-- project: … --> line.");
                    else if (!arg[i2].StartsWith(":") || arg[i2].Substring(1).Trim() == "") Err(doc, "Line " + (i2 + 1) + ": write the project as <!-- project: Name -->.");
                    else doc.Project = arg[i2].Substring(1).Trim();
                    continue;
                }
                if (kw[i2] != null) { Err(doc, "Line " + (i2 + 1) + ": <!-- " + kw[i2] + " --> must be inside a question (after a <!-- question … --> line)."); continue; }
                if (doc.Title == "" && !code[i2] && l.StartsWith("# ") && l.Substring(2).Trim() != "") { doc.Title = l.Substring(2).Trim(); continue; }
                intro.Add(l);
            }
            doc.Intro = Block(intro);
            if (doc.Title == "") Err(doc, "There is no title: add a '# Title' line after <!-- questionnaire v1 -->, before the first question.");

            // 3. Questions.
            var ids = new HashSet<string>();
            while (i2 < n)
            {
                var q = new QnQuestion { Line = i2 + 1 };
                int num = doc.Questions.Count + 1;
                string where = Attrs(doc, q, arg[i2], num);
                if (q.Id != "" && !ids.Add(q.Id)) Err(doc, where + ": the id \"" + q.Id + "\" is already used by another question.");
                int j = i2 + 1;
                while (j < n && (doc.Skip[j] || (lines[j].Trim() == "" && kw[j] == null))) j++;
                if (j < n && kw[j] == null && !code[j] && lines[j].StartsWith("## ") && lines[j].Substring(3).Trim() != "") { q.Text = lines[j].Substring(3).Trim(); j++; }
                else Err(doc, where + ": the line after <!-- question … --> must be a '## ' heading with the question text.");

                var sec = new Dictionary<string, List<string>> { { "", new List<string>() } };
                string cur = "", recArg = null;
                int optLine = 0;
                for (; j < n && kw[j] != "question"; j++)
                {
                    if (doc.Skip[j]) continue;
                    if (kw[j] == "context" || kw[j] == "options" || kw[j] == "recommended")
                    {
                        if (sec.ContainsKey(kw[j])) Err(doc, where + ", line " + (j + 1) + ": a second <!-- " + kw[j] + " --> section in this question.");
                        cur = kw[j]; sec[cur] = new List<string>();
                        if (cur == "options") optLine = j + 1;
                        if (cur == "recommended") recArg = arg[j];
                        if (cur != "recommended" && arg[j] != "") Err(doc, where + ", line " + (j + 1) + ": <!-- " + cur + " --> takes nothing after it.");
                        continue;
                    }
                    if (kw[j] != null) { Err(doc, where + ", line " + (j + 1) + ": <!-- " + kw[j] + " … --> can't be inside a question."); continue; }
                    if (cur == "options")
                    {
                        if (lines[j].Trim() == "") continue;
                        var m = code[j] ? Match.Empty : OptRx.Match(lines[j]);
                        if (!m.Success) { Err(doc, where + ", line " + (j + 1) + ": expected an option line like - `option-id` Label (every line in <!-- options --> must be one)."); continue; }
                        string oid = m.Groups[1].Value;
                        if (!IdRx.IsMatch(oid)) Err(doc, where + ", line " + (j + 1) + ": option id \"" + oid + "\" may only use lowercase letters, digits, - and _.");
                        else if (q.Option(oid) != null) Err(doc, where + ", line " + (j + 1) + ": option id \"" + oid + "\" is used twice in this question.");
                        q.Options.Add(new QnOption { Id = oid, Label = m.Groups[2].Value });
                        continue;
                    }
                    sec[cur].Add(lines[j]);
                }
                q.End = j;
                q.Explanation = Block(sec[""]);
                if (sec.ContainsKey("context")) q.Context = Block(sec["context"]);
                if (sec.ContainsKey("recommended")) q.RecWhy = Block(sec["recommended"]);

                if (q.Choice)
                {
                    if (q.Options.Count < 2) Err(doc, where + ": a " + q.Type + "-choice question needs an <!-- options --> section with at least 2 options.");
                    if (q.Options.Count > MaxOptions) Err(doc, where + ": more than " + MaxOptions + " options.");
                }
                else if (q.Type != "")
                {
                    if (optLine > 0) Err(doc, where + ": a " + q.Type + "-text question can't have options (line " + optLine + ").");
                    if (recArg != null) Err(doc, where + ": recommendations are only for single and multiple choice questions.");
                }
                if (recArg != null && q.Choice)
                {
                    string list = recArg.StartsWith(":") ? recArg.Substring(1) : "";
                    foreach (var part in list.Split(','))
                    {
                        string r = part.Trim();
                        if (r == "") continue;
                        if (q.Option(r) == null) Err(doc, where + ": the recommended option \"" + r + "\" isn't one of this question's options.");
                        else if (q.Recommended.Contains(r)) Err(doc, where + ": the option \"" + r + "\" is recommended twice.");
                        else q.Recommended.Add(r);
                    }
                    if (list.Trim() == "") Err(doc, where + ": write the recommendation as <!-- recommended: option-id -->.");
                    else if (q.Type == "single" && q.Recommended.Count > 1) Err(doc, where + ": a single-choice question can recommend only one option.");
                }
                doc.Questions.Add(q);
                i2 = j;
            }
            if (doc.Questions.Count == 0) Err(doc, "There are no questions: each one starts with a <!-- question id=\"…\" type=\"…\" --> line.");
            if (doc.Questions.Count > MaxQuestions) Err(doc, "More than " + MaxQuestions + " questions.");
            return doc;
        }

        // Reads id / type / required; returns how errors name this question: Question 3 (id "db", line 40).
        static string Attrs(QnDoc doc, QnQuestion q, string a, int num)
        {
            int pos = 0;
            var seen = new HashSet<string>();
            var errs = new List<string>();
            for (Match m = AttrRx.Match(a, 0); m.Success && m.Index == pos; m = AttrRx.Match(a, pos))
            {
                pos = m.Index + m.Length;
                string k = m.Groups[1].Value, v = m.Groups[2].Value;
                if (!seen.Add(k)) { errs.Add("the attribute " + k + " is given twice."); continue; }
                if (k == "id") q.Id = v;
                else if (k == "type") q.Type = v;
                else if (k == "required") { if (v == "true") q.Required = true; else if (v == "false") q.Required = false; else errs.Add("required must be \"true\" or \"false\"."); }
                else errs.Add("unknown attribute " + k + " (use id, type and required).");
            }
            if (a.Substring(Math.Min(pos, a.Length)).Trim() != "") errs.Add("can't read the attributes; write them as id=\"…\" type=\"…\" required=\"true\".");
            bool idOk = IdRx.IsMatch(q.Id);
            if (q.Id == "") errs.Add("the question has no id=\"…\".");
            else if (!idOk) errs.Add("the id \"" + q.Id + "\" may only use lowercase letters, digits, - and _ (at most 64).");
            if (q.Type != "single" && q.Type != "multiple" && q.Type != "short" && q.Type != "long")
                errs.Add("type must be single, multiple, short or long" + (q.Type == "" ? "." : " (not \"" + q.Type + "\")."));
            string where = "Question " + num + " (" + (idOk ? "id \"" + q.Id + "\", " : "") + "line " + q.Line + ")";
            foreach (var e in errs) Err(doc, where + ": " + e);
            return where;
        }

        static void Err(QnDoc doc, string e) { if (doc.Errors.Count < MaxErrors) doc.Errors.Add(e); }

        static string Block(List<string> lines)
        {
            int a = 0, b = lines.Count;
            while (a < b && lines[a].Trim() == "") a++;
            while (b > a && lines[b - 1].Trim() == "") b--;
            return string.Join("\n", lines.GetRange(a, b - a));
        }

        // Literal text in a fenced block longer than any run of backticks inside it, so no line of it can close the block
        // (and nothing in it can read as a heading, a directive or another question).
        public static string Fence(string text, string nl)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            int run = 0, longest = 0;
            foreach (char c in text) { if (c == '`') { run++; if (run > longest) longest = run; } else run = 0; }
            string f = new string('`', Math.Max(3, longest + 1));
            return f + "text" + nl + text.Replace("\n", nl) + nl + f;
        }

        // The answered file: the source's own lines in order (title, intro, questions, context, options untouched), a
        // summary after the version marker, and one answer block after each question.
        public static string Export(QnDoc doc, Dictionary<string, QnAnswer> answers, Dictionary<string, string> meta, bool draft)
        {
            string nl = doc.Newline;
            var ends = new Dictionary<int, QnQuestion>();
            foreach (var q in doc.Questions) ends[q.End] = q;
            var counts = new Dictionary<string, int> { { QnAnswer.Answered, 0 }, { QnAnswer.Delegated, 0 }, { QnAnswer.Skipped, 0 }, { QnAnswer.Unanswered, 0 } };
            foreach (var q in doc.Questions) counts[QnAnswer.StatusOf(q, Get(answers, q.Id))]++;
            var sb = new StringBuilder();
            var lines = doc.Lines;
            for (int i = 0; i <= lines.Length; i++)
            {
                QnQuestion q;
                if (ends.TryGetValue(i, out q)) AnswerBlock(sb, q, Get(answers, q.Id), nl);
                if (i == lines.Length) break;
                if (doc.Skip[i]) continue;
                if (i > 0 && doc.Skip[i - 1] && lines[i - 1].Trim() == "<!-- /answer -->" && lines[i].Trim() == "") continue;   // the blank line an earlier export added
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append(nl);
                if (i == doc.Marker)
                {
                    sb.Append("<!-- answers format=\"" + Version + "\"");
                    foreach (var kv in meta) sb.Append(' ').Append(kv.Key).Append("=\"").Append((kv.Value ?? "").Replace("\"", "'").Replace("-->", "- ->")).Append('"');
                    sb.Append(" -->").Append(nl);
                    sb.Append(draft ? "> **Draft answers, NOT submitted** (saved when this questionnaire was replaced in Claude Status)." : "> **Answered questionnaire** (Claude Status, questionnaire format v1).").Append(nl);
                    sb.Append("> " + doc.Questions.Count + " questions: " + counts[QnAnswer.Answered] + " answered, " + counts[QnAnswer.Delegated] + " Let the AI decide, " + counts[QnAnswer.Skipped] + " skipped, " + counts[QnAnswer.Unanswered] + " unanswered.").Append(nl);
                    sb.Append("> Each question is followed by its answer block. \"Let the AI decide\" = decide yourself and record the decision when implementing. \"Skipped\" and \"Unanswered\" = no answer, not approval. Read comments together with the selected options.").Append(nl);
                    sb.Append("<!-- /answers -->").Append(nl);
                }
            }
            return sb.ToString();
        }

        static QnAnswer Get(Dictionary<string, QnAnswer> d, string id) { QnAnswer a; return d != null && d.TryGetValue(id, out a) ? a : null; }

        static void AnswerBlock(StringBuilder sb, QnQuestion q, QnAnswer a, string nl)
        {
            string st = QnAnswer.StatusOf(q, a), rec = QnAnswer.RecOf(q, a);
            if (sb.Length >= nl.Length && sb.ToString(sb.Length - nl.Length, nl.Length) != nl) sb.Append(nl);
            if (sb.Length < 2 * nl.Length || sb.ToString(sb.Length - 2 * nl.Length, 2 * nl.Length) != nl + nl) sb.Append(nl);   // one blank line before it
            sb.Append("<!-- answer id=\"" + q.Id + "\" status=\"" + st + "\" recommendation=\"" + rec + "\" -->").Append(nl);
            string label = st == QnAnswer.Answered ? "Answered" : st == QnAnswer.Delegated ? "Let the AI decide" : st == QnAnswer.Skipped ? "Skipped" : "Unanswered";
            string recText = rec == "used" ? " (used the recommendation)" : rec == "edited" ? " (started from the recommendation, then changed it)" : "";
            sb.Append("**Answer to `" + q.Id + "`: " + label + "**" + (st == QnAnswer.Answered ? recText : "")).Append(nl);
            if (st == QnAnswer.Answered && q.Choice)
            {
                sb.Append(nl).Append("Selected:").Append(nl);
                foreach (var id in a.Sel) sb.Append("- `" + id + "` " + q.Option(id).Label).Append(nl);
                if (a.Other && a.OtherText.Trim() != "") sb.Append("- Other (my own answer):").Append(nl).Append(nl).Append(Fence(a.OtherText, nl)).Append(nl);
            }
            else if (st == QnAnswer.Answered) sb.Append(nl).Append(Fence(a.Text, nl)).Append(nl);
            else if (st == QnAnswer.Delegated) sb.Append(nl).Append("The AI decides: use your judgment, and record the decision and why when implementing.").Append(nl);
            else sb.Append(nl).Append("No answer. Don't treat this as approval or agreement.").Append(nl);
            if (a != null && a.Comment.Trim() != "") sb.Append(nl).Append("Comment:").Append(nl).Append(nl).Append(Fence(a.Comment, nl)).Append(nl);
            sb.Append("<!-- /answer -->").Append(nl).Append(nl);
        }

        // A file name from the imported file's name only (never a path from the document or the phone).
        public static string SafeBase(string sourceName)
        {
            string b = sourceName ?? "";
            b = b.Substring(Math.Max(b.LastIndexOf('\\'), b.LastIndexOf('/')) + 1);   // not Path.*: it throws on characters like | or ?
            if (b.LastIndexOf('.') > 0) b = b.Substring(0, b.LastIndexOf('.'));
            var sb = new StringBuilder();
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars());
            foreach (char c in b) sb.Append(bad.Contains(c) || char.IsControl(c) ? '_' : c);
            b = sb.ToString().Trim(' ', '.');
            if (b.Length > 80) b = b.Substring(0, 80).Trim(' ', '.');
            if (b == "") b = "questionnaire";
            if (Regex.IsMatch(b, "^(?i:con|prn|aux|nul|com[0-9]|lpt[0-9])(\\..*)?$")) b = "_" + b;
            return b;
        }
    }

    public class QnSubmission { public string Sid = "", File = "", Path = ""; public int Rev; public DateTime AtUtc; public bool Done; }

    // The one active questionnaire. All public methods are thread-safe (phone requests arrive on server threads).
    public class QuestionnaireDesk
    {
        readonly string dir, activeDir, historyDir;
        readonly object gate = new object();
        public readonly string DefaultOutputDir;
        string outputDir = "";
        public event Action Changed;

        QnDoc doc;
        string id = "", sourceName = "", sourceFrom = "", sha = "", warning = "";
        DateTime importedUtc, savedUtc;
        int rev;
        Dictionary<string, QnAnswer> answers = new Dictionary<string, QnAnswer>();
        List<QnSubmission> subs = new List<QnSubmission>();

        public QuestionnaireDesk(string stateDir, string defaultOutputDir)
        {
            dir = stateDir; activeDir = Path.Combine(dir, "active"); historyDir = Path.Combine(dir, "history");
            DefaultOutputDir = defaultOutputDir;
            Load();
        }

        // Where answered files go: the folder chosen on the desktop (Settings in the Questionnaires tab), or the default.
        public string OutputDir { get { lock (gate) return outputDir != "" ? outputDir : DefaultOutputDir; } }
        public void SetOutputDir(string chosen) { lock (gate) outputDir = chosen ?? ""; Fire(); }
        public string HistoryDir { get { return historyDir; } }
        public string Warning { get { lock (gate) return warning; } }
        public bool Active { get { lock (gate) return doc != null; } }
        public QnDoc Doc { get { lock (gate) return doc; } }
        public string Id { get { lock (gate) return id; } }
        public int Revision { get { lock (gate) return rev; } }
        public string SourceName { get { lock (gate) return sourceName; } }
        public string SourceFrom { get { lock (gate) return sourceFrom; } }
        public DateTime ImportedUtc { get { lock (gate) return importedUtc; } }
        public DateTime SavedUtc { get { lock (gate) return savedUtc; } }
        public string SourcePath { get { return Path.Combine(activeDir, "source.md"); } }
        public List<QnSubmission> Submissions { get { lock (gate) return subs.FindAll(s => s.Done); } }
        public Dictionary<string, int> Counts()
        {
            lock (gate)
            {
                var c = new Dictionary<string, int> { { QnAnswer.Answered, 0 }, { QnAnswer.Delegated, 0 }, { QnAnswer.Skipped, 0 }, { QnAnswer.Unanswered, 0 } };
                if (doc != null) foreach (var q in doc.Questions) { QnAnswer a; answers.TryGetValue(q.Id, out a); c[QnAnswer.StatusOf(q, a)]++; }
                return c;
            }
        }
        // What the phone's board long-poll carries, so the page refetches when it changes.
        public string Marker { get { lock (gate) return doc == null ? "" : id + "." + rev + "." + subs.FindAll(s => s.Done).Count; } }

        void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }

        void Load()
        {
            try
            {
                string st = Path.Combine(activeDir, "state.json");
                if (!File.Exists(st)) return;
                var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(st, Encoding.UTF8));
                var parsed = QnFormat.ParseBytes(File.ReadAllBytes(SourcePath));
                if (!parsed.Ok) { warning = "The saved questionnaire couldn't be read again; its files are kept in " + activeDir + "."; return; }
                id = S(d, "id"); sourceName = S(d, "source"); sourceFrom = S(d, "from"); sha = S(d, "sha256");
                importedUtc = T(d, "imported"); savedUtc = T(d, "saved");
                object o;
                rev = d.TryGetValue("rev", out o) && o is int ? (int)o : 0;
                answers = ParseAnswers(parsed, d.TryGetValue("answers", out o) ? o : null, true);
                if (d.TryGetValue("subs", out o) && o is System.Collections.IList)
                    foreach (var x in (System.Collections.IList)o)
                    {
                        var sd = x as Dictionary<string, object>;
                        if (sd == null) continue;
                        object r;
                        subs.Add(new QnSubmission { Sid = S(sd, "sid"), File = S(sd, "file"), Path = S(sd, "path"), AtUtc = T(sd, "at"), Done = sd.TryGetValue("done", out r) && r is bool && (bool)r, Rev = sd.TryGetValue("rev", out r) && r is int ? (int)r : 0 });
                    }
                doc = parsed;
            }
            catch (Exception e) { doc = null; warning = "The saved questionnaire couldn't be read (" + e.Message + "); its files are kept in " + activeDir + "."; }
        }

        static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is string ? (string)v : ""; }
        static DateTime T(Dictionary<string, object> d, string k)
        {
            DateTime t;
            return DateTime.TryParse(S(d, k), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : DateTime.MinValue;
        }
        static string Iso(DateTime t) { return t == DateTime.MinValue ? "" : t.ToUniversalTime().ToString("o"); }

        // lenient = the saved state (drop what no longer fits instead of refusing everything).
        static Dictionary<string, QnAnswer> ParseAnswers(QnDoc d, object raw, bool lenient)
        {
            string err;
            var r = ParseAnswers(d, raw, lenient, out err);
            return r ?? new Dictionary<string, QnAnswer>();
        }

        static Dictionary<string, QnAnswer> ParseAnswers(QnDoc d, object raw, bool lenient, out string error)
        {
            error = null;
            var res = new Dictionary<string, QnAnswer>();
            if (raw == null) return res;
            var m = raw as Dictionary<string, object>;
            if (m == null) { error = "answers isn't an object"; return null; }
            foreach (var kv in m)
            {
                var q = d.Find(kv.Key);
                if (q == null) { if (lenient) continue; error = "unknown question \"" + kv.Key + "\""; return null; }
                string e;
                var a = QnAnswer.From(kv.Value, q, out e);
                if (a == null) { if (lenient) continue; error = "question \"" + q.Id + "\": " + e; return null; }
                res[q.Id] = a;
            }
            return res;
        }

        static string Canon(QnDoc d, Dictionary<string, QnAnswer> a)
        {
            var list = new List<object>();
            foreach (var q in d.Questions) { QnAnswer x; list.Add(a.TryGetValue(q.Id, out x) ? (object)x.ToJson() : null); }
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(list);
        }

        Dictionary<string, object> AnswersJson()
        {
            var o = new Dictionary<string, object>();
            foreach (var kv in answers) o[kv.Key] = kv.Value.ToJson();
            return o;
        }

        // Writes the state file so it survives a crash or power loss the moment this returns (flushed to disk, then swapped in).
        void Persist(string folder)
        {
            var subList = new List<object>();
            foreach (var s in subs) subList.Add(new Dictionary<string, object> { { "sid", s.Sid }, { "file", s.File }, { "path", s.Path }, { "rev", s.Rev }, { "at", Iso(s.AtUtc) }, { "done", s.Done } });
            var d = new Dictionary<string, object> { { "v", 1 }, { "id", id }, { "source", sourceName }, { "from", sourceFrom }, { "sha256", sha }, { "imported", Iso(importedUtc) },
                { "saved", Iso(savedUtc) }, { "rev", rev }, { "answers", AnswersJson() }, { "subs", subList } };
            Durable(Path.Combine(folder, "state.json"), new UTF8Encoding(false).GetBytes(Json.Pretty(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d))));
        }

        public static void Durable(string path, byte[] data)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fs.Write(data, 0, data.Length); fs.Flush(true); }
            try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
            finally { if (File.Exists(tmp)) try { File.Delete(tmp); } catch { } }
        }

        // ---------- desktop: import ----------
        // Reads and checks a file the user picked or dropped. Null bytes + errors in the doc = refuse it.
        public static QnDoc Preview(string path, out byte[] bytes)
        {
            bytes = null;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return Fail("The file doesn't exist.");
                if (!fi.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) && !fi.Extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)) return Fail("Only Markdown (.md) files can be imported.");
                if (fi.Length > QnFormat.MaxBytes) return Fail("The file is larger than " + (QnFormat.MaxBytes / 1024) + " KB.");
                bytes = File.ReadAllBytes(path);
                return QnFormat.ParseBytes(bytes);
            }
            catch (Exception e) { bytes = null; return Fail("Couldn't read the file: " + e.Message); }
        }
        static QnDoc Fail(string why) { var d = new QnDoc(); d.Errors.Add(why); return d; }

        // What the import confirmation says (and whether it replaces something).
        public string ImportSummary(QnDoc d)
        {
            var sb = new StringBuilder();
            sb.Append("Title: ").Append(d.Title).Append("\r\n");
            if (d.Project != "") sb.Append("Project: ").Append(d.Project).Append("\r\n");
            int req = d.Questions.FindAll(q => q.Required).Count;
            sb.Append(d.Questions.Count).Append(d.Questions.Count == 1 ? " question" : " questions").Append(": ").Append(d.TypeSummary()).Append(" (").Append(req).Append(" required)\r\n");
            lock (gate)
            {
                if (doc != null)
                {
                    int saved = Meaningful(), done = subs.FindAll(s => s.Done).Count;
                    sb.Append("\r\nThis REPLACES the active questionnaire \"").Append(doc.Title).Append("\"");
                    sb.Append(saved > 0 ? " (" + saved + " answers saved" + (done > 0 ? ", submitted " + done + "×" : ", not submitted") + ")." : " (no answers yet).");
                    sb.Append(" It's kept in the history folder" + (saved > 0 ? ", with its answers so far written out as a draft answered file" : "") + "; the phone will show the new one.");
                }
            }
            return sb.ToString();
        }

        // Imports a previewed file: the bytes are kept exactly as they were, under the app's own folder. Returns a note
        // about the replaced questionnaire ("" if none).
        public string Import(string fromPath, byte[] bytes, QnDoc d)
        {
            if (d == null || !d.Ok || bytes == null) throw new InvalidOperationException("not a valid questionnaire");
            string note;
            lock (gate)
            {
                note = ArchiveLocked();
                Directory.CreateDirectory(dir);
                string tmp = Path.Combine(dir, "incoming-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                Durable(Path.Combine(tmp, "source.md"), bytes);
                var rnd = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
                id = Convert.ToBase64String(rnd).TrimEnd('=').Replace('+', '-').Replace('/', '_');   // 22 characters, token-shaped
                using (var h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                sourceName = Path.GetFileName(fromPath); sourceFrom = fromPath;
                importedUtc = DateTime.UtcNow; savedUtc = DateTime.MinValue; rev = 0;
                answers = new Dictionary<string, QnAnswer>(); subs = new List<QnSubmission>();
                Persist(tmp);
                Directory.Move(tmp, activeDir);
                doc = d; warning = "";
            }
            Fire();
            return note;
        }

        // Answers with something in them (a choice, text, Let the AI decide, Skip or a comment); the phone keeps blank ones too.
        int Meaningful()
        {
            int n = 0;
            foreach (var kv in answers) { var q = doc == null ? null : doc.Find(kv.Key); if (q != null && (QnAnswer.StatusOf(q, kv.Value) != QnAnswer.Unanswered || kv.Value.Comment.Trim() != "")) n++; }
            return n;
        }

        // Moves the active questionnaire (source, state and, if it has answers, a draft answered file) into history.
        public string Archive() { string n; lock (gate) n = ArchiveLocked(); Fire(); return n; }

        string ArchiveLocked()
        {
            if (!Directory.Exists(activeDir)) { doc = null; return ""; }
            Directory.CreateDirectory(historyDir);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + QnFormat.SafeBase(sourceName);
            string dest = Path.Combine(historyDir, name);
            for (int k = 2; Directory.Exists(dest); k++) dest = Path.Combine(historyDir, name + "-" + k);
            string draftNote = "";
            if (doc != null && Meaningful() > 0)
            {
                string f = QnFormat.SafeBase(sourceName) + ".draft-answers.md";
                var meta = Meta("");
                File.WriteAllText(Path.Combine(activeDir, f), QnFormat.Export(doc, answers, meta, true), new UTF8Encoding(false));
                draftNote = " Its answers so far are in " + f + " there.";
            }
            string title = doc != null ? doc.Title : "the previous questionnaire";
            Directory.Move(activeDir, dest);
            doc = null; id = ""; answers = new Dictionary<string, QnAnswer>(); subs = new List<QnSubmission>(); rev = 0;
            return "\"" + title + "\" was moved to " + dest + "." + draftNote;
        }

        Dictionary<string, string> Meta(string sid)
        {
            return new Dictionary<string, string> { { "questionnaire", id }, { "source", QnFormat.SafeBase(sourceName) + Path.GetExtension(sourceName ?? "") }, { "source-sha256", sha },
                { "revision", rev.ToString() }, { "submission", sid }, { sid == "" ? "saved" : "submitted", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") } };
        }

        public string Original()
        {
            try { return new UTF8Encoding(false).GetString(File.ReadAllBytes(SourcePath)).TrimStart('﻿'); } catch { return ""; }
        }

        // The current answers as an answered file (not written anywhere): the desktop's "Copy current answers" recovery path.
        public string DraftMarkdown() { lock (gate) return doc == null ? "" : QnFormat.Export(doc, answers, Meta(""), true); }

        // ---------- phone ----------
        public Dictionary<string, object> PhoneData()
        {
            lock (gate)
            {
                if (doc == null) return new Dictionary<string, object> { { "q", null } };
                var qs = new List<object>();
                foreach (var q in doc.Questions)
                {
                    var opts = new List<object>();
                    foreach (var o in q.Options) opts.Add(new Dictionary<string, object> { { "id", o.Id }, { "l", o.Label } });
                    qs.Add(new Dictionary<string, object> { { "id", q.Id }, { "t", q.Type }, { "r", q.Required }, { "x", q.Text }, { "e", q.Explanation }, { "c", q.Context },
                        { "o", opts }, { "rc", new List<string>(q.Recommended) }, { "rw", q.RecWhy } });
                }
                return new Dictionary<string, object>
                {
                    { "q", new Dictionary<string, object> { { "id", id }, { "title", doc.Title }, { "project", doc.Project }, { "intro", doc.Intro }, { "src", sourceName }, { "qs", qs } } },
                    { "rev", rev }, { "answers", AnswersJson() }, { "subs", SubsJson() },
                };
            }
        }

        List<object> SubsJson()
        {
            var l = new List<object>();
            foreach (var s in subs) if (s.Done) l.Add(new Dictionary<string, object> { { "sid", s.Sid }, { "file", s.File }, { "rev", s.Rev }, { "at", (long)(s.AtUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds } });
            return l;
        }

        static Dictionary<string, object> R(string result, string msg) { var d = new Dictionary<string, object> { { "result", result } }; if (msg != null) d["msg"] = msg; return d; }

        // A draft from the phone, based on revision 'baseRev'. Refused if the questionnaire was replaced, or if a newer draft
        // arrived meanwhile (another device; force = the user chose to keep this phone's version over it).
        public Dictionary<string, object> Save(string qid, int baseRev, object raw, bool force)
        {
            Dictionary<string, object> res;
            lock (gate)
            {
                if (doc == null || qid != id) return R("replaced", "This questionnaire was replaced or removed on the laptop.");
                string err;
                var parsed = ParseAnswers(doc, raw, false, out err);
                if (parsed == null) return R("bad_request", "The laptop refused these answers: " + err + ".");
                if (Canon(doc, parsed) == Canon(doc, answers)) { res = R("saved", null); res["rev"] = rev; return res; }
                if (baseRev != rev && !force) { res = R("conflict", "These answers were changed elsewhere since this phone loaded them."); res["rev"] = rev; res["answers"] = AnswersJson(); return res; }
                var before = answers; int beforeRev = rev; DateTime beforeAt = savedUtc;
                answers = parsed; rev++; savedUtc = DateTime.UtcNow;
                try { Persist(activeDir); }
                catch (Exception e) { answers = before; rev = beforeRev; savedUtc = beforeAt; return R("error", "The laptop couldn't save: " + e.Message); }
                res = R("saved", null); res["rev"] = rev;
            }
            Fire();
            return res;
        }

        // Submit = write a NEW answered file, durably, then acknowledge. The same submission id (a resend, a double tap, a
        // retry after a lost answer or a laptop restart) returns the file it already wrote instead of writing another.
        public Dictionary<string, object> Submit(string qid, string sid, int baseRev, object raw, bool allowGaps)
        {
            Dictionary<string, object> res;
            lock (gate)
            {
                if (doc == null || qid != id) return R("replaced", "This questionnaire was replaced or removed on the laptop; nothing was submitted.");
                var known = subs.Find(s => s.Sid == sid);
                if (known != null && known.Done && File.Exists(known.Path)) return Delivered(known);
                string err;
                var parsed = ParseAnswers(doc, raw, false, out err);
                if (parsed == null) return R("bad_request", "The laptop refused these answers: " + err + ".");
                bool same = Canon(doc, parsed) == Canon(doc, answers);
                if (!same && baseRev != rev) { res = R("conflict", "These answers were changed elsewhere since this phone loaded them. Nothing was submitted."); res["rev"] = rev; res["answers"] = AnswersJson(); return res; }
                var gaps = new List<string>();
                foreach (var q in doc.Questions) { QnAnswer a; parsed.TryGetValue(q.Id, out a); string st = QnAnswer.StatusOf(q, a); if (q.Required && (st == QnAnswer.Skipped || st == QnAnswer.Unanswered)) gaps.Add(q.Id); }
                if (gaps.Count > 0 && !allowGaps) { res = R("gaps", "Some required questions are skipped or unanswered."); res["ids"] = gaps; return res; }
                var before = answers; int beforeRev = rev;
                try
                {
                    if (!same) { answers = parsed; rev++; savedUtc = DateTime.UtcNow; }
                    string outDir = outputDir != "" ? outputDir : DefaultOutputDir;
                    Directory.CreateDirectory(outDir);
                    var sub = known ?? new QnSubmission { Sid = sid };
                    sub.Rev = rev; sub.AtUtc = DateTime.UtcNow;
                    if (known == null) subs.Add(sub);
                    // the chosen name is recorded before the file is written, so a crash in between reuses that same name
                    if (sub.Path == "" || !Path.GetDirectoryName(sub.Path).Equals(outDir, StringComparison.OrdinalIgnoreCase) || (File.Exists(sub.Path) && !Ours(sub.Path, sid)))
                    {
                        sub.Path = FreeName(outDir, QnFormat.SafeBase(sourceName), subs);
                        sub.File = Path.GetFileName(sub.Path);
                    }
                    Persist(activeDir);
                    byte[] text = new UTF8Encoding(false).GetBytes(QnFormat.Export(doc, answers, Meta(sid), false));
                    if (File.Exists(sub.Path)) Durable(sub.Path, text);   // our own half-finished earlier attempt
                    else
                    {
                        string tmp = sub.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fs.Write(text, 0, text.Length); fs.Flush(true); }
                        try { File.Move(tmp, sub.Path); }   // never replaces a file that appeared meanwhile
                        finally { if (File.Exists(tmp)) try { File.Delete(tmp); } catch { } }
                    }
                    sub.Done = true;
                    Persist(activeDir);
                    res = Delivered(sub);
                }
                catch (Exception e)
                {
                    if (!same) { answers = before; rev = beforeRev; }
                    var s2 = subs.Find(s => s.Sid == sid);
                    if (s2 != null) s2.Done = false;
                    try { Persist(activeDir); } catch { }
                    return R("error", "The laptop couldn't write the answered file: " + e.Message);
                }
            }
            Fire();
            return res;
        }

        static bool Ours(string path, string sid)
        {
            try { return File.ReadAllText(path).Contains("submission=\"" + sid + "\""); } catch { return false; }
        }

        static string FreeName(string outDir, string b, List<QnSubmission> taken)
        {
            for (int k = 1; k < 10000; k++)
            {
                string p = Path.Combine(outDir, b + (k == 1 ? ".answered.md" : ".answered-" + k + ".md"));
                if (!File.Exists(p) && !taken.Exists(s => s.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) return p;
            }
            throw new IOException("no free file name");
        }

        Dictionary<string, object> Delivered(QnSubmission s)
        {
            var r = R("delivered", null);
            r["file"] = s.File; r["sid"] = s.Sid; r["rev"] = rev;
            r["at"] = (long)(s.AtUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return r;
        }
    }

    // ---------- the desktop tab ----------
    public class QuestionnaireView : UserControl
    {
        readonly QuestionnaireDesk desk;
        readonly float scale;
        readonly Wrap title, facts, progress, result, outInfo, hint, warn;
        readonly TextBox resultPath;
        readonly Button import, viewOriginal, copyDraft, archive, copyAnswer, openAnswer, openFolder, changeOut, resetOut, history, guide;
        readonly FlowLayoutPanel resultButtons;
        public Action<string> SetOutputDir;   // saves the chosen folder in config.json ("" = default)
        public string GuidePath = "";

        int S(float v) { return (int)Math.Round(v * scale); }

        public QuestionnaireView(QuestionnaireDesk desk, float scale)
        {
            this.desk = desk; this.scale = scale;
            Font = SystemFonts.MessageBoxFont; BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            AutoScroll = true;
            var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(S(14), S(10), S(14), S(10)) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title = new Wrap { Font = new Font(Font.FontFamily, Font.Size * 1.45f, FontStyle.Bold), Dock = DockStyle.Fill };
            facts = new Wrap { ForeColor = QTheme.Muted, Dock = DockStyle.Fill };
            progress = new Wrap { Dock = DockStyle.Fill };
            warn = new Wrap { ForeColor = QTheme.Yellow, Dock = DockStyle.Fill };
            var row1 = Row();
            import = QTheme.Primary(QTheme.Btn("Import questionnaire…", (s, e) => PickAndImport()));
            viewOriginal = QTheme.Btn("View original Markdown", (s, e) => ShowText("Original Markdown: " + desk.SourceName, desk.Original()));
            copyDraft = QTheme.Btn("Copy current answers (draft)", (s, e) => Copy(desk.DraftMarkdown(), "Current answers copied (a draft: not submitted)."));
            archive = QTheme.Btn("Remove…", (s, e) => ArchiveAsk());
            foreach (var b in new[] { import, viewOriginal, copyDraft, archive }) row1.Controls.Add(b);
            var resultHead = Head("Answered file");
            result = new Wrap { Dock = DockStyle.Fill };
            resultPath = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = QTheme.Field, ForeColor = QTheme.Text, AccessibleName = "Answered file path", Margin = new Padding(0, S(2), 0, S(6)) };
            resultButtons = Row();
            copyAnswer = QTheme.Btn("Copy answered Markdown", (s, e) => { var p = LastPath(); if (p != null) Copy(File.ReadAllText(p, Encoding.UTF8), "Answered Markdown copied."); });
            openAnswer = QTheme.Btn("Open file", (s, e) => { var p = LastPath(); if (p != null) Launch(p, null); });
            openFolder = QTheme.Btn("Open containing folder", (s, e) => { var p = LastPath(); if (p != null) Launch("explorer.exe", "/select,\"" + p + "\""); });
            foreach (var b in new[] { copyAnswer, openAnswer, openFolder }) resultButtons.Controls.Add(b);
            var outHead = Head("Where answered files are saved");
            outInfo = new Wrap { ForeColor = QTheme.Muted, Dock = DockStyle.Fill };
            var row3 = Row();
            changeOut = QTheme.Btn("Change folder…", (s, e) => PickOutput());
            resetOut = QTheme.Btn("Use default", (s, e) => { if (SetOutputDir != null) SetOutputDir(""); });
            history = QTheme.Btn("Open history folder", (s, e) => { Directory.CreateDirectory(desk.HistoryDir); Launch(desk.HistoryDir, null); });
            guide = QTheme.Btn("Open the format guide", (s, e) => { if (File.Exists(GuidePath)) Launch(GuidePath, null); else MessageBox.Show(this, "QUESTIONNAIRE_GUIDE.md isn't next to Claude Status. It's in the repository's questionnaires folder; re-run install.ps1 to copy it here.", "Claude Status"); });
            foreach (var b in new[] { changeOut, resetOut, history, guide }) row3.Controls.Add(b);
            hint = new Wrap { ForeColor = QTheme.Muted, Dock = DockStyle.Fill, Text = "Import a questionnaire your AI assistant (Claude Code, Codex, …) wrote by following QUESTIONNAIRE_GUIDE.md (or drop a .md file here). Answer it in the phone dashboard's Questions view; submitting there writes a new answered file on this laptop. Nothing is sent to any AI: copy the answered file back into your project yourself. One questionnaire at a time." };
            foreach (Control c in new Control[] { title, facts, progress, warn, row1, resultHead, result, resultPath, resultButtons, outHead, outInfo, row3, hint }) body.Controls.Add(c);
            Controls.Add(body);
            QTheme.Style(body);
            QTheme.Primary(import);

            AllowDrop = true;
            Droppable(this);
            desk.Changed += OnChanged;
            RefreshView();
        }

        static FlowLayoutPanel Row() { return new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) }; }
        Wrap Head(string t) { return new Wrap { Text = t.ToUpperInvariant(), ForeColor = QTheme.Muted, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill, Padding = new Padding(0, S(14), 0, S(2)) }; }

        void Droppable(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) => { var f = Dropped(e); e.Effect = f != null ? DragDropEffects.Copy : DragDropEffects.None; };
            c.DragDrop += (s, e) => { var f = Dropped(e); if (f != null) BeginInvoke((MethodInvoker)(() => ImportPath(f))); };
            foreach (Control k in c.Controls) Droppable(k);
        }

        static string Dropped(DragEventArgs e)
        {
            var files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            return files != null && files.Length == 1 ? files[0] : null;
        }

        protected override void Dispose(bool disposing) { if (disposing) desk.Changed -= OnChanged; base.Dispose(disposing); }

        bool pending;
        void OnChanged()
        {
            if (!IsHandleCreated || pending) return;
            pending = true;
            try { BeginInvoke((MethodInvoker)(() => { pending = false; if (!IsDisposed) RefreshView(); })); } catch { pending = false; }
        }

        string LastPath()
        {
            var subs = desk.Submissions;
            var p = subs.Count > 0 ? subs[subs.Count - 1].Path : null;
            if (p != null && File.Exists(p)) return p;
            MessageBox.Show(this, "The answered file isn't there any more" + (p != null ? ":\r\n" + p : "."), "Claude Status");
            return null;
        }

        public void RefreshView()
        {
            var d = desk.Doc;
            bool on = d != null;
            title.Text = on ? d.Title : "No questionnaire";
            facts.Text = on ? (d.Project != "" ? "Project: " + d.Project + " · " : "") + d.Questions.Count + " questions: " + d.TypeSummary()
                + " · imported " + desk.ImportedUtc.ToLocalTime().ToString("g") + " from " + desk.SourceFrom : "";
            var c = desk.Counts();
            progress.Text = !on ? "" : "On the phone: " + c[QnAnswer.Answered] + " answered, " + c[QnAnswer.Delegated] + " Let the AI decide, " + c[QnAnswer.Skipped] + " skipped, " + c[QnAnswer.Unanswered] + " unanswered"
                + (desk.SavedUtc > DateTime.MinValue ? " · last saved " + desk.SavedUtc.ToLocalTime().ToString("g") + " (revision " + desk.Revision + ")" : " · nothing answered yet");
            warn.Text = desk.Warning;
            viewOriginal.Enabled = copyDraft.Enabled = archive.Enabled = on;
            var subs = desk.Submissions;
            var last = subs.Count > 0 ? subs[subs.Count - 1] : null;
            result.Text = last == null ? (on ? "Not submitted yet. Review and submit on the phone." : "") : "Submitted " + last.AtUtc.ToLocalTime().ToString("g") + (subs.Count > 1 ? " (" + subs.Count + " submissions; this is the latest)" : "") + ":";
            resultPath.Text = last == null ? "" : last.Path;
            resultPath.Visible = resultButtons.Visible = last != null;
            outInfo.Text = desk.OutputDir + (desk.OutputDir == desk.DefaultOutputDir ? " (default)" : "") + "\r\nThe imported original is never changed; each submission is a new file (name.answered.md, then name.answered-2.md, …).";
            resetOut.Enabled = desk.OutputDir != desk.DefaultOutputDir && SetOutputDir != null;
            changeOut.Enabled = SetOutputDir != null;
        }

        void PickAndImport()
        {
            using (var dlg = new OpenFileDialog { Filter = "Markdown (*.md)|*.md;*.markdown", Title = "Import a questionnaire", CheckFileExists = true })
                if (dlg.ShowDialog(this) == DialogResult.OK) ImportPath(dlg.FileName);
        }

        public void ImportPath(string path)
        {
            byte[] bytes;
            var d = QuestionnaireDesk.Preview(path, out bytes);
            if (!d.Ok || bytes == null)
            {
                ShowText("Can't import " + Path.GetFileName(path), QnFormat.Rejected + "\r\n\r\nWhat's wrong:\r\n• " + string.Join("\r\n• ", d.Errors));
                return;
            }
            string summary = desk.ImportSummary(d);
            if (!Confirm(desk.Active ? "Replace the questionnaire?" : "Import questionnaire", summary, desk.Active ? "Replace" : "Import")) return;
            try
            {
                string note = desk.Import(path, bytes, d);
                if (note != "") MessageBox.Show(this, "Imported \"" + d.Title + "\".\r\n\r\n" + note, "Claude Status");
            }
            catch (Exception e) { MessageBox.Show(this, "Couldn't import it: " + e.Message, "Claude Status"); }
        }

        void ArchiveAsk()
        {
            if (!Confirm("Remove the questionnaire?", "\"" + desk.Doc.Title + "\" leaves the phone and moves to the history folder. Its source, its saved answers (as a draft answered file) and earlier answered files are kept.", "Remove")) return;
            try { MessageBox.Show(this, desk.Archive(), "Claude Status"); } catch (Exception e) { MessageBox.Show(this, "Couldn't remove it: " + e.Message, "Claude Status"); }
        }

        void PickOutput()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Where should answered questionnaire files be saved?", SelectedPath = desk.OutputDir })
                if (dlg.ShowDialog(this) == DialogResult.OK && SetOutputDir != null) SetOutputDir(dlg.SelectedPath);
        }

        void Copy(string text, string done)
        {
            try { Clipboard.SetText(text); MessageBox.Show(this, done, "Claude Status"); }
            catch (Exception e) { MessageBox.Show(this, "Couldn't copy: " + e.Message, "Claude Status"); }
        }

        void Launch(string file, string args)
        {
            try { Process.Start(new ProcessStartInfo(file) { Arguments = args ?? "", UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, "Couldn't open it: " + e.Message, "Claude Status"); }
        }

        bool Confirm(string head, string text, string ok)
        {
            using (var f = Dialog(head, text, true))
            {
                var yes = QTheme.Primary(QTheme.Btn(ok, null)); yes.DialogResult = DialogResult.OK;
                var no = QTheme.Btn("Cancel", null); no.DialogResult = DialogResult.Cancel;
                ((FlowLayoutPanel)f.Tag).Controls.Add(yes); ((FlowLayoutPanel)f.Tag).Controls.Add(no);
                f.AcceptButton = yes; f.CancelButton = no;
                return f.ShowDialog(this) == DialogResult.OK;
            }
        }

        void ShowText(string head, string text)
        {
            using (var f = Dialog(head, text, false))
            {
                var close = QTheme.Btn("Close", null); close.DialogResult = DialogResult.Cancel;
                ((FlowLayoutPanel)f.Tag).Controls.Add(close); f.CancelButton = close;
                f.ShowDialog(this);
            }
        }

        // A dark dialog with read-only text (selectable, scrolls) and a button row in Tag.
        Form Dialog(string head, string text, bool small)
        {
            var f = new Form { Text = head, StartPosition = FormStartPosition.CenterParent, Size = new Size(S(small ? 560 : 760), S(small ? 340 : 560)), MinimizeBox = false, ShowInTaskbar = false, BackColor = QTheme.Bg, ForeColor = QTheme.Text, Font = Font };
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = small, Dock = DockStyle.Fill, Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"), Font = small ? Font : new Font("Consolas", Font.Size + 0.5f) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(8)) };
            f.Controls.Add(box); f.Controls.Add(buttons);
            QTheme.Style(f);
            box.SelectionStart = 0; box.SelectionLength = 0;
            f.Tag = buttons;
            return f;
        }
    }
}
