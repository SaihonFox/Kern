# M1–M3 completion plan (new working regime, 2026-10-31)

Owner request: finish M1–M3 completely, restart the test suite (old suite archived), write tests
only after a batch of **five** implemented functions, and let subagents work in parallel.

## Definition of done (from `docs/ROADMAP.md`)

- **M1 (syntax):** full lexer/parser of the spec; several errors per file with spans; parser tests
  for every construct. Current state: broad coverage (bitwise, interpolation, match patterns,
  ranges, loops, generics, attributes), remaining gaps are semantic rather than syntactic.
- **M2 (symbols/types):** symbols for modules/types/functions/properties/fields; import resolver;
  type system (primitives, nullable, unions, tuples, function types, builtins, `result`, `task`,
  constraints, `never`, error type); conversions; overload resolution per spec §15. Current state:
  most of it exists; `collection<T>`, union aliases, `result<T,E>` constructors and some generic
  inference are missing.
- **M3 (binder):** expressions/statements, flow analysis and smart casts, `match` (all forms,
  coverage), loops (incl. iteration), closures and local functions, `unit`/`unit!` rules,
  inheritance/abstract/open/override, properties, extension members, operators (incl. repeat),
  constructors and chains, `never`, compile-time reflection stubs, attributes on symbols.
  Current state (examples audit, 2026-10-31): iteration loops, swap, `?.`, constant conversions,
  interpolation and Elvis landed today; still missing: destructuring, `in`/`!in`, lambdas,
  local functions, `await` binding, generic ctor inference, imports, `result`/`Ok`/`Err`,
  inheritance edge cases.

## Working rules for this phase

1. **Five functions per batch**, then one focused test file per batch (owner rule). No tests for
   features between batches.
2. The old suite (`archive/legacy-tests/`) is **not built or run**; it is a reference for expected
   behaviour. New `src/Kern.Tests` starts with a single smoke test.
3. Every batch ends with: `dotnet build Kern.slnx` 0 errors, `dotnet test` green, style clean,
   `kernc check examples/01_basics` (and as many of 01–20 as possible) re-run, logs saved.
4. Language-visible work updates `docs/spec/kern.en.md` + `CHANGELOG_LANG.md` in the same batch.
5. **Parallel agents own disjoint files.** Two agents must not edit the same file; binder work is
   serialized (one binder agent at a time). Runtime/std work (new files under `Kern.Runtime`,
   `Kern.Binding`) may run in parallel with binder work. Concurrent `dotnet build` may hit file
   locks; retry once.
6. Stage-2 examples (13, 15, 19, 20, 25–28) and the examples refresh stay out of scope until the
   owner re-approves them.

## Batches

### Batch 0 — baseline (done 2026-10-31)
Iteration `loop … in …`, swap/multi-assignment, `?.`, constant literal conversions,
`f"…"` interpolation + `?:` (five features). Tests existed from the previous regime; they are now
archived, so this batch is covered by the smoke test + `kernc run examples/01_basics`.

### Batch 1 — core binder gaps I
1. Destructuring declaration and assignment (`(i32 a, list<i32> ..rest, _) = xs`).
2. `in` / `!in` operator for `list`/`array`/`dict`/`str`/`set`.
3. Lambdas as values (`fun(i32 n) -> i32 body`) with captures.
4. Local functions with hoisting and recursion.
5. Generic constructor inference (`unit Box<T>(T value)`; `Box(5)` → `Box<i32>`).
Then: one test file per feature group + `kernc check` on examples 02, 03, 05, 06, 16.

### Batch 2 — core binder gaps II
6. `await` as an expression in async functions (and `async main` end-to-end).
7. `result<T,E>` type + `Ok`/`Err` constructors and methods.
8. M3-F imports: module per file, `use path.module[items]`, `use path.module.*`, duplicate/unknown
   item diagnostics.
9. Inheritance audit: `@type`/`~fun`, multiple inheritance `[A(x), B(y)]`, `$[A].member`,
   abstract members left abstract.
10. Extension functions/properties/operators audit and missing pieces.
Then: tests for these five groups; re-run examples 07, 09, 12, 15, 17, 23.

### Batch 3 — flow, attributes, edge cases
11. Smart-cast gaps (`is`, `!is`, `?:`, match guards, loop conditions).
12. `stop`/`next` in expressions (`SaveSp`/`RestoreSp` paths) and loop result lists.
13. Repeat operator `str/char/list * inum` both orders.
14. Attributes bound to symbols (parser keeps them; binder must expose them to members).
15. Compile-time reflection `name_of<T>()`/`typeof<T>()` stubs and `never` propagation.
Then: tests; re-run remaining stage-1 examples.

## Metric

`kernc check` over `examples/01..20` must go 1/20 → 20/20 for stage-1 examples (excluding the
stage-2 four) before M3 is considered closed. Logs: `logs/examples-check.log`.
