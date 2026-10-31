# TOKEN_BUDGET — ~100M tokens, intermediary provider

The owner bought ~100M tokens (via an intermediary; unknown whether cached input is billed fully). Assume the worst: **every request re-sends the whole context and it is counted**.

## Rough plan
Stage 1 caps sum to ~72M (see `ROADMAP.md`); ~28M reserve for rework, debugging, surprises. Check usage at the end of every milestone (ask the owner to read the balance; record it in `STATUS.md`).

## Habits that save tokens (mandatory)
1. **Short sessions.** One slice = one session; start a fresh session for the next slice. Long sessions re-send a huge history on every step.
2. **Small instruction files.** `AGENTS.md` is loaded every time — keep it short. Put detail in `docs/` and read it by section.
3. **Read by range / grep**, never `cat` a 2000-line file. Ask sub-agents for summaries, not dumps.
4. **Quiet logs.** Redirect build/test output to `logs/` and read only the tail/errors (`dotnet build -v q`, `dotnet test --nologo`).
5. **No re-reading after editing** — the edit tool already confirms.
6. **Right model for the job.** `lead`/`reviewer` (strong, expensive) = few, high-value turns; `*-dev` (balanced) = bulk work; `mechanic` (cheap) = mechanical. Do not run the strong model in long loops.
7. **Batch mechanical edits** into scripts (`node tools/*.mjs`) instead of dozens of tool calls.
8. **Fail fast**: if the same error survives 3 fix attempts, stop, summarise the attempts, and ask the lead/owner instead of looping.
9. **Don't copy reference code blindly** — a verified small piece is cheaper than debugging a large unverified one, but reading all of `reference/` is expensive; grep for the specific piece.
10. **Split "implement" and "test+docs" into different sessions** (lesson 2026-10-27: three B1/B2 sessions hit the step cap). A brief that says "implement + ~20 tests + docs + logs" does not fit one session. The landing order that works: (a) production code + one smoke test, (b) tests, (c) docs (the lead writes language-visible spec/`CHANGELOG_LANG.md` entries). Each session starts from a green baseline the lead has verified, and the next brief must list exactly what remains — never "finish the slice".
11. **One `dotnet build`/`dotnet test` at a time.** Two sub-agents building the same solution lock `obj/`/`bin/` and produce fake failures that burn steps. Run build-heavy slices sequentially; parallel sessions only for read-only or disjoint-tooling work.

## Danger signs (report to the owner at once)
Milestone > 1.5× cap · same test failing > 3 sessions · agent re-reading the same large files repeatedly · more than ~8M tokens burnt without a green build.
