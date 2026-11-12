// Stand-in for interactive claude.exe in the VS Code tests (compiled by the tests; never real Claude).
// - Writes its exact argv (as the C runtime / CommandLineToArgvW parse it) to %MOCK_ARGV_DIR%\<session>.json.
// - Reports progress like hook.ps1 does (state\sessions\<session>.json in %MOCK_HOOK_DIR%): WORKING, then
//   NEEDS_YOU if the prompt contains "ASK" (it waits for a line typed in the terminal), then DONE.
// - Then idles like interactive Claude, until "/exit" or the terminal closes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class MockInteractiveClaude
{
    static string sid = "", hookDir = "", first = "";
    static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

    static void Hook(string state, string ev, string detail)
    {
        if (hookDir == "" || sid == "") return;
        var me = Process.GetCurrentProcess();
        string now = DateTime.UtcNow.ToString("o");
        if (first == "") first = now;
        var o = new Dictionary<string, object> { { "v", 2 }, { "session_id", sid }, { "state", state }, { "event", ev }, { "detail", detail }, { "project", Path.GetFileName(Environment.CurrentDirectory) },
            { "updated", now }, { "state_since", now }, { "first_seen", first }, { "claude_pid", me.Id }, { "claude_started", me.StartTime.ToUniversalTime().ToString("o") } };
        string f = Path.Combine(hookDir, sid + ".json"), tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, Ser.Serialize(o), new UTF8Encoding(false));
        if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        hookDir = Environment.GetEnvironmentVariable("MOCK_HOOK_DIR") ?? "";
        string argvDir = Environment.GetEnvironmentVariable("MOCK_ARGV_DIR") ?? "";
        string prompt = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--session-id" || args[i] == "--resume") && i + 1 < args.Length) sid = args[i + 1];
            if (args[i] == "--" && i + 1 < args.Length) { prompt = args[i + 1]; break; }
        }
        if (argvDir != "" && sid != "")
        {
            var rec = new Dictionary<string, object> { { "argv", args }, { "cwd", Environment.CurrentDirectory }, { "pid", Process.GetCurrentProcess().Id },
                { "apiKey", Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "" }, { "task", Environment.GetEnvironmentVariable("CLAUDE_STATUS_TASK") ?? "" } };
            File.WriteAllText(Path.Combine(argvDir, sid + "." + Process.GetCurrentProcess().Id + ".json"), Ser.Serialize(rec), new UTF8Encoding(false));
        }
        Console.WriteLine("mock claude · session " + sid + " · cwd " + Environment.CurrentDirectory);
        if (prompt != null)
        {
            Console.WriteLine("> " + prompt.Replace("\n", "\n  "));
            Hook("WORKING", "UserPromptSubmit", "");
            Thread.Sleep(prompt.Contains("SLOW") ? 8000 : 800);
            if (prompt.Contains("ASK"))
            {
                Hook("NEEDS_YOU", "PreToolUse", "AskUserQuestion");
                Console.WriteLine("Question: which color? (type an answer and press Enter)");
                string answer = Console.ReadLine();
                if (argvDir != "") File.WriteAllText(Path.Combine(argvDir, sid + ".answer.txt"), answer ?? "", new UTF8Encoding(false));
                Hook("WORKING", "PostToolUse", "");
                Thread.Sleep(500);
            }
            if (prompt.Contains("FAILLIMIT")) Hook("ERROR", "StopFailure", "rate_limit");
            else Hook("DONE", "Stop", "");
            Console.WriteLine("Done. Waiting for your next message (/exit to quit).");
        }
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            if (line.Trim() == "/exit") break;
            Hook("WORKING", "UserPromptSubmit", ""); Thread.Sleep(300); Hook("DONE", "Stop", "");
        }
        return 0;
    }
}
