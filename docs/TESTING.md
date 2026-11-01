# TESTING

## Layers
1. **Unit tests** (xunit, `src/Kern.Tests`): lexer, parser, conversions, overload resolution, binder diagnostics, codegen/round-trip of `.kbc`, optimizer equivalence (-O0/-O1/-O2 print the same), formatter idempotence, builder logic. External libraries tested here too: `Features/Backend` covers `Kern.Lib.Backend` (units + loopback servers); DB/DI are covered by examples 15/26.
2. **Snippet tests**: `KernHarness.Run("""…kern source…""")` compiles + runs a snippet in-process and returns captured stdout (normalise `\r\n` → `\n`). Use for language-feature tests.
3. **Golden tests = the examples**: every `examples/<name>/` is a project (convention layout `src/main.kern`, optional `build.kern`). Next to `src/` lives:
   - `expected.out` — exact expected stdout (LF-normalised),
   - optional `input.txt` (stdin), `args.txt` (one argument per line),
   - optional `.compile-only` (typecheck only) and `.pending` (expected output not yet agreed — see DECISIONS_NEEDED),
   - optional `prebuild.txt`: `kernc` commands (one per line, relative to the example dir, e.g. `dll ../mathx`) executed before the run.
   A directory is an example if it has `src/`, `build.kern` or `*.kern` files; folders like `31_kbc/` that only contain sub-examples are not examples themselves.
   `Examples/GoldenTests` discovers directories, runs each through the real pipeline (in-process, `-O1`), also at `-O0` and `-O2` once M11 exists, and compares.
4. **CLI tests**: run `kernc` as a process on temp dirs (convention project, `build.kern` workspace, `dll` + consumer).
5. **Cross-platform**: path separators, CRLF vs LF in outputs, culture (use invariant culture everywhere — `InvariantGlobalization=true`).

## Rules for expected outputs (important)
- The old examples were **never executed**. `expected.out` must be derived by the QA agent **from the spec and by reading the example**, not by pasting what the implementation printed.
  If the example's behaviour is unclear or looks like a spec gap → `.pending` + question in `DECISIONS_NEEDED.md`.
- A failing golden test is triaged as: implementation bug / wrong expectation / bad example / spec gap. Never "fix" an expectation to match a bug.
- If an example uses a feature the spec does not have, fix the **example** (and tell the lead), or add the feature to the spec through `DECISIONS_NEEDED.md` if it looks valuable.
- After **every** language change run the whole golden set (owner rule: re-verify all examples after each language change).

## Time and flakiness
Tests with threads/async/time must not depend on wall-clock precision; use generous timeouts and deterministic synchronisation. Network tests use loopback only.

## Logs
`dotnet test --logger "console;verbosity=normal" > logs/test.log 2>&1` and `dotnet build Kern.slnx > logs/build.log 2>&1` (PowerShell: use `*> file`). Summaries in `STATUS.md`.
