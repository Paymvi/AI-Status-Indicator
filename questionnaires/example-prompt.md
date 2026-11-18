Works with any AI coding assistant (Claude Code, Codex, …). Copy `QUESTIONNAIRE_GUIDE.md` into the project (or
point the assistant at it; you can also mention it in the project's `CLAUDE.md` or `AGENTS.md`), then say:

> Read `QUESTIONNAIRE_GUIDE.md`. Before implementing <the change>, write the questions you need answered as a
> questionnaire file following that guide (save it as `questions.md`), then stop and wait for my answers.

When you have the answered file (Claude Status → Queue window → Questionnaires → Copy answered Markdown):

> Here are my answers (`questions.answered.md`). Continue, following QUESTIONNAIRE_GUIDE.md's rules for answers.
