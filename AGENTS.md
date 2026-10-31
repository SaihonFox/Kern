# AGENTS.md — rules for every agent working on Kern

Kern is a statically typed, expression-oriented programming language implemented in C# / .NET 10:
Lexer → Parser → AST → declarations → Binder → bound tree → CodeGenerator → `.kbc` bytecode → VM interpreter.
The owner (Saihon) speaks Russian. You are building the project **from scratch** from the spec; `reference/` holds an old,
never-compiled attempt that you may mine for ideas and copy from **piece by piece after verifying it**.

## Read order (read only what the task needs; use ranges/grep, never dump huge files)
1. `STATUS.md` — where we are. 2. `docs/ROADMAP.md` — milestone you are on. 3. `docs/spec/kern.en.md` — language spec (source of truth).
4. `docs/DECISIONS.md` — what the owner decided. 5. `docs/ARCHITECTURE.md` — projects and design. 6. `docs/KNOWN_ISSUES.md` — traps.
Others on demand: `BUILDER.md`, `TESTING.md`, `CONVENTIONS.md`, `WORKFLOW.md`, `TOKEN_BUDGET.md`, `OPEN_QUESTIONS.md`.

## Hard rules
- **Nothing is "done" until it builds and tests pass**: `dotnet build Kern.slnx` = 0 errors, `dotnet test` green. Save output to `logs/` (`build.log`, `test.log`).
  Never claim success you did not observe. Report failures honestly.
- **Spec wins.** If code and `docs/spec/kern.en.md` disagree, fix the code. If the spec is silent, ambiguous or looks wrong → do NOT invent:
  write a question with 2–4 options (mark your recommendation) to `DECISIONS_NEEDED.md` and continue with other work.
- **If a design decision turns out to be bad** (dead end, contradiction, big cost): STOP that line of work, write the problem and options
  to `DECISIONS_NEEDED.md`, tell the owner. Do not silently redesign.
- **Language-changing decisions** (anything visible in Kern syntax/semantics) go to `CHANGELOG_LANG.md` and into `docs/spec/kern.en.md` in the same change.
- Do not touch `reference/` (read-only). Do not use git commands (owner does not use git here). Never put API keys into files.
- Scope control: implement only the current milestone. Stage-2 items (see ROADMAP) are not started without owner approval.
- Keep edits small and focused; do not reformat unrelated code; do not rename public things without need.

## Style (enforced by `node tools/check-style.mjs`)
- C#: **tabs**, **CRLF** line endings (also .md, .kern, .csproj, .json), `[]` collection expressions, file-scoped namespaces,
  namespace == folder path, no flat folders with many files (one domain = one subfolder), no giant files (split > ~600 lines).
- Kern language naming: `snake_case` everywhere. C# naming: normal .NET style.
- Code comments and user-facing diagnostics/messages: **Russian**. Instructions, docs for agents, commit-like notes: English.
- Console output is always UTF-8.
- `Kern.Runtime.*` has a `Math` namespace → in there use `System.Math.X`. Add needed `using`s; verify with a real build.

## Delegation (lead agent only)
Give a sub-agent a *small, self-contained* task: goal, files to touch, spec sections, acceptance check. Expect back: what changed,
build/test result, open questions. Prefer many short sessions over one huge one (see `docs/TOKEN_BUDGET.md`).

## Reports (keep them short and current)
`STATUS.md` (done / tests green / next) · `DECISIONS_NEEDED.md` (questions with options) · `logs/*.log` · `CHANGELOG_LANG.md`.
