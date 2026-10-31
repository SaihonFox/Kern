# WORKFLOW — how the agents work together

## Roles
`lead` (architect/orchestrator) · `compiler-dev` · `vm-dev` · `std-dev` · `cli-dev` · `qa` · `reviewer` (read-only) · `mechanic` (cheap). Defined in `agents-src/`, generated into `.opencode/agents/` by `node tools/apply-models.mjs`.

## One slice = one task brief
```
GOAL:      one sentence
SCOPE:     files/projects you may touch; what NOT to touch
READ:      spec sections / docs to read (ranges)
DONE WHEN: build 0 errors + named tests green (+ example X passes)
REPORT:    changed files, test result, open questions (max 15 lines)
```
Slices should fit in ~1–3 hours of human work / well under 1M tokens. If a slice grows, split it.

## Loop
plan → implement → `dotnet build` → `dotnet test` → fix → (risky? `reviewer`) → update `STATUS.md` → next slice.

## Parallelism
Independent slices in different projects may run concurrently as separate sessions (e.g. `std-dev` on a library while `compiler-dev` works on binder). Never two agents on the same file. Shared-file conflicts (e.g. `Krn.cs` diagnostic codes) → lead serialises.

## Reports (formats)
`STATUS.md`:
```
## <date> — M<n> <name>
Done: …   Tests: N passed / M failed   Next: …   Blocked: …
```
`DECISIONS_NEEDED.md` entry:
```
### Q<n>: <short title>   (milestone, agent, date)
Context: 2–4 lines. Options: A) … B) … C) …   Recommendation: X because …   Impact if wrong: …
```
`CHANGELOG_LANG.md`: `- <date> <what changed in the language> — spec section — example/test`.
The owner answers in the file or in chat; the lead moves answered items to `docs/DECISIONS.md` and the spec.

## When to stop and ask
Spec silent/ambiguous; two spec statements conflict; a decision proves bad (dead end, > 1.5× budget, big redesign); a stage-2 feature seems needed; a destructive/external action is needed.
Stopping = write the question, then continue with unrelated work or report to the owner.

## Definition of Done for any change
Builds, tests green, style check clean, spec/docs updated if behaviour changed, `STATUS.md` updated, no TODO without a `DECISIONS_NEEDED` entry.
