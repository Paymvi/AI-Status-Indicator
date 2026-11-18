# Questionnaires: how to use them

Answer your AI assistant's clarification questions on your phone instead of editing a Markdown file on the laptop.
Works with Claude Code, Codex or any assistant that can read and write files. Claude Status makes no AI calls: the
assistant writes a questionnaire file, you answer it on the phone, and you hand the answered file back yourself.

| File | For | What it is |
|------|-----|------------|
| [`QUESTIONNAIRE_GUIDE.md`](QUESTIONNAIRE_GUIDE.md) | the AI | The format and the rules for asking and for reading your answers. Copy it into any project. |
| [`example.questionnaire.md`](example.questionnaire.md) | both | A valid questionnaire using every question type. Import it to try the feature. |
| [`example-prompt.md`](example-prompt.md) | you | The prompts to copy. |
| this file | you | Step by step. |

**You need:** Claude Status running on the laptop, and the phone dashboard set up (see "Phone dashboard" in the main
README). The questionnaire tab works without the phone, but answering happens on the phone.

## 1. Ask the AI for a questionnaire

Copy `QUESTIONNAIRE_GUIDE.md` into your project (or mention it in `CLAUDE.md` / `AGENTS.md`), then say:

> Read `QUESTIONNAIRE_GUIDE.md`. Before implementing <the change>, write the questions you need answered as a
> questionnaire file following that guide (save it as `questions.md`), then stop and wait for my answers.

## 2. Import it on the laptop

1. Open the Queue window (the ☰◷ button on the Claude Status window) → **Questionnaires** tab.
2. **Import questionnaire…** and pick the file, or drag the `.md` file onto the tab.
3. Check the preview (title, project, number and types of questions), then **Import**.

If the file doesn't follow the guide, nothing is imported and you see which question and line is wrong. Ask the AI
to fix or regenerate it with `QUESTIONNAIRE_GUIDE.md`.

Only one questionnaire is active at a time. Importing another asks first; the old one moves to the history folder
(**Open history folder**) with any answers you'd given as a `.draft-answers.md` file.

## 3. Answer on the phone

Open the dashboard and tap **Questions**.

- **Start** (or **Resume at question N**), then one question at a time with **Back** / **Next**. **All questions**
  shows every question's state (answered, AI decides, skipped, unanswered) and lets you jump.
- **Choice questions:** pick one (radio) or several (checkboxes). **Other** lets you type your own answer.
  **Comment** is optional and is read together with your choice.
- **Recommendations** are marked *Recommended* with the reason. Nothing is preselected; **Use recommendation** picks
  them for you, and you can still change it.
- **Text questions:** type, or use your keyboard's microphone to dictate.
- **Let the AI decide**: you hand that decision to the assistant, which must record what it chose.
- **Skip**: no answer. The AI is told a skip is *not* agreement.
- **Context** and **About this questionnaire** fold open for background.

Everything saves as you go: first on the phone, then on the laptop (the line at the top says which). If the
connection drops you can keep answering; it syncs when it's back. The page needs the laptop to *open*, though.

## 4. Review and submit

After the last question tap **Review**. Every answer is listed with an **Edit** button. If required questions are
unanswered or skipped they're listed, and you must tick **Submit anyway** to send it like that.

Tap **Submit answers**. **Delivered to laptop** appears only once the laptop has saved the file. If the laptop is
unreachable, your answers stay on the phone and are delivered when it's back. Tapping twice never makes two files.

## 5. Give the answers back to the AI

On the laptop, the Questionnaires tab shows the saved file with **Copy answered Markdown**, **Open file** and
**Open containing folder**. Paste it into your project, or put the file there, and say:

> Here are my answers (`questions.answered.md`). Continue, following QUESTIONNAIRE_GUIDE.md's rules for answers.

## Where files go

- Answered files: `Documents\Claude Status\Questionnaires\<name>.answered.md` (then `-2`, `-3`, … for later
  submissions; nothing is ever overwritten). Change it with **Change folder…** in the tab.
- Your original file is never changed. A copy and your drafts are kept in Claude Status's `state\questionnaires`.

## Good to know

- Changed answers after submitting? Edit them and submit again: you get a new file.
- If the same questionnaire was changed on another device while this phone was offline, you're shown both and choose
  **Keep this phone's answers** or **Use the other version**.
- If a new questionnaire was imported while your phone held unsent answers to the old one, the phone keeps them
  (**Copy those answers as text**) and never mixes them into the new one.
- Images in a questionnaire aren't shown on the phone (a placeholder appears); they stay in the files.
