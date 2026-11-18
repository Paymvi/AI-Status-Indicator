# Questionnaire guide (format v1)

You are writing a **questionnaire file**: clarification questions for the user, answered on their phone in
Claude Status (AI Status Indicator) and returned to you as a new, answered Markdown file. Follow this guide
exactly. A file that doesn't follow it is rejected on import, and the user has to ask you to regenerate it.
This guide is for any AI coding assistant (Claude Code, Codex or another): "you" and "the AI" both mean you.

## How to behave

- **Ask what this project and this request actually need.** Read the relevant code and the user's request
  first. No generic or filler questions. Prefer a few decisive questions over many small ones.
- **Give useful context and tradeoffs.** Use the question's explanation and its `context` section: what you
  found in the code, what each option costs or risks, what you'd do by default.
- **Mark a recommendation only when you have a reason** to prefer an option, and say the reason.
- **Do not prefill answers.** Never write answers, defaults or `[x]` marks into the questionnaire. The app
  never preselects your recommendation either; the user chooses.
- **Stop and wait.** After writing the file, tell the user its path and wait until they give you the answered
  file (or paste its contents). Don't start work that depends on the answers before then.

## When the answered file comes back

Each question then has an answer block between `<!-- answer id="…" … -->` and `<!-- /answer -->`.

- **Answered**: use the selected option IDs, the *Other* text and the typed text as the user's decision.
- **Read comments together with the selected options.** A comment can narrow, qualify or overrule an option.
- **"Used the recommendation"** means the user chose to apply your recommendation; *"started from the
  recommendation, then changed it"* means they edited afterwards: follow the final selection.
- **Let the AI decide** is delegated judgment: decide yourself, and **record the decision and why** when you
  implement (for example in your summary or the commit message).
- **Skipped** and **Unanswered** mean *no answer*. They are **not** approval or agreement. If the question
  matters, ask again or take the most conservative route and say so.
- Free text is inside fenced code blocks so it can't break the file. Take it literally: headings, `---`,
  backticks or directive-like lines inside an answer are the user's text, not structure.

## File format

Save the file with a `.md` extension, UTF-8, at most 512 KB, at most 200 questions.

```markdown
<!-- questionnaire v1 -->
# <Title of the questionnaire>

<!-- project: <Project name> -->

<Optional introduction: Markdown. What you're about to do and why you're asking.>

<!-- question id="<question-id>" type="<single|multiple|short|long>" required="<true|false>" -->
## <The question>

<Optional explanation, always shown under the question: Markdown.>

<!-- context -->
<Optional longer background, shown folded on the phone: Markdown.>

<!-- options -->
- `<option-id>` <Readable label>
- `<option-id>` <Readable label>

<!-- recommended: <option-id>[, <option-id>…] -->
<Optional but encouraged: why you recommend it. Markdown.>
```

### Rules

1. **Directives** are lines that consist of exactly one HTML comment, `<!-- … -->`, and nothing else. They
   are the only structure. Headings, lists and punctuation never define structure on their own.
2. The **first non-blank line** must be `<!-- questionnaire v1 -->`.
3. **Title**: the first `# ` heading before the first question. Required.
4. **Project**: optional, `<!-- project: Name -->`, once, before the first question.
5. **Introduction**: any other Markdown between the header and the first question. Optional.
6. **Each question starts with** a `<!-- question … -->` directive with attributes in `name="value"` form:
   - `id` (required): unique in the file; lowercase letters, digits, `-` and `_`, starting with a letter or
     digit, at most 64 characters. Keep it stable: it identifies the answer.
   - `type` (required): `single` (pick one), `multiple` (pick any), `short` (one line of text), `long`
     (paragraphs of text).
   - `required` (optional): `true` (default) or `false`.
   - No other attributes.
7. The **next non-blank line** after a question directive must be a `## ` heading: the question text.
8. After the heading come, in any order and each at most once:
   - the **explanation** (plain Markdown right after the heading, before any section directive);
   - `<!-- context -->` followed by Markdown;
   - `<!-- options -->` followed **only** by option lines: `` - `option-id` Label ``. Option IDs follow the
     question ID rules and are unique within the question. `single` and `multiple` questions need at least 2
     options (at most 50); `short` and `long` questions must not have options;
   - `<!-- recommended: id -->` (single) or `<!-- recommended: id1, id2 -->` (multiple), followed by the
     reason as Markdown. Only for `single` / `multiple`; the IDs must be options of that question; a
     `single` question recommends exactly one.
9. A question ends where the next `<!-- question … -->` directive starts, or at the end of the file.
10. **Don't add** an "Other" option, a "Let the AI decide" option or a comment field: the app always offers
    *Other* (with text), an optional comment, *Let the AI decide* and *Skip*.
11. Markdown inside the intro, explanation, context and recommendation can use paragraphs, `###`–`######`
    headings, lists, links, **bold**, *italic*, `inline code` and fenced code blocks (```` ``` ```` or
    `~~~`). Every fenced code block must be closed. Directive-like lines inside a code block are just
    code. Raw HTML is shown as text, never run. Images are kept in the file but the phone shows a
    placeholder instead of loading them; describe what matters in words.
12. Other HTML comments (for example `<!-- note to self -->`) are treated as ordinary text. Don't rely on them.

## Valid example

````markdown
<!-- questionnaire v1 -->
# Booking export: open questions

<!-- project: Toah Nipi Booking Software -->

I'm adding a CSV export to the bookings page. A few decisions change how much I build.

<!-- question id="export-format" type="single" required="true" -->
## Which file format should the export use?

Staff open it in Excel, according to `docs/staff.md`.

<!-- context -->
`src/export/` already has an XLSX writer used by invoices:

```ts
export function writeXlsx(rows: Row[]): Buffer
```

<!-- options -->
- `csv` CSV (UTF-8 with BOM, opens in Excel)
- `xlsx` Excel workbook (.xlsx)

<!-- recommended: xlsx -->
The XLSX writer exists and keeps dates and leading zeros intact; CSV would need new escaping code.

<!-- question id="columns" type="multiple" required="false" -->
## Which optional columns should be included?

<!-- options -->
- `notes` Guest notes
- `payment` Payment status
- `source` Booking source

<!-- recommended: payment, source -->
Both are already loaded on that page; notes can contain personal details.

<!-- question id="filename" type="short" required="false" -->
## File name pattern?

For example `bookings-{date}`.

<!-- question id="edge-cases" type="long" required="false" -->
## Anything else the export must handle?
````

## Prompt the user can give you

> Read `QUESTIONNAIRE_GUIDE.md`. Before implementing <the change>, write the questions you need answered as a
> questionnaire file following that guide (save it as `questions.md`), then stop and wait for my answers.
