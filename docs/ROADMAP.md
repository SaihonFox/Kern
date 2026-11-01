# ROADMAP — stage 1 (core + std + CLI)

Order matters; do not start a milestone before the previous one builds and its tests are green. Token numbers are **soft caps** (see `TOKEN_BUDGET.md`);
if a milestone exceeds 1.5× its cap, stop and report in `DECISIONS_NEEDED.md`.
Every milestone ends with: `dotnet build Kern.slnx` 0 errors · `dotnet test` green · `node tools/check-style.mjs` clean · `STATUS.md` updated · `logs/` saved.

| # | Milestone | Cap | Main owner |
|---|---|---|---|
| M0 | Bootstrap solution, tooling, CI | 0.5M | lead + mechanic |
| M1 | Lexer, parser, AST, diagnostics | 2.5M | compiler-dev |
| M2 | Symbols, type system, declarations, overloads | 3M | compiler-dev |
| M3 | Binder (expressions, flow, patterns, closures, units, inheritance) | 7M | compiler-dev |
| M4 | Bytecode, `.kbc` format, CodeGenerator, optimizer | 2.5M | compiler-dev + vm-dev |
| M5 | VM / interpreter, panics, async scheduler | 4M | vm-dev |
| M6 | Native binding framework + prelude std (core/text/collections/numerics/math/io) | 3M | vm-dev + std-dev |
| M7 | Std libraries (json, file/env, time, regex, csv/yaml/xml, crypto, uuid, compress, http, events, tasks, threading, parallel, reflection API, kern.test, mocks) | 7M | std-dev |
| M8 | CLI `kernc` + project convention + `build.kern` builder + library discovery | 3.5M | cli-dev |
| M9 | Reflection (runtime + compile-time) and macros | 2.5M | compiler-dev + vm-dev + cli-dev |
| M10 | `.kbc` via `use`: stubs + linker, `kernc dll` | 2.5M | cli-dev |
| M11 | `kernc fmt`, `kernc repl`, `-O2` peephole | 1.5M | compiler-dev + cli-dev |
| M12 | Hardening: all stage-1 examples golden, GitHub Actions CI on 3 OS (owner pushes the repo), docs sync, regenerate the Russian spec | 2.5M | qa + lead |
| M13 | (needs owner OK) package fetching + wrapper | — | cli-dev |
| M14 | Round 27 additions: language, data structures, libraries, doc/tooling | 6M | compiler-dev + vm-dev + std-dev + cli-dev |

## M0 — Bootstrap
- `Kern.slnx`, `Directory.Build.props` (net10.0, latest, nullable, implicit usings, invariant globalization, deterministic), `.editorconfig` (tabs, CRLF), `.gitattributes`.
- Projects (see `ARCHITECTURE.md` §1): `Kern.Core, Kern.Bytecode, Kern.Vm, Kern.Binding, Kern.Runtime, Kern.Compiler, Kern.Std, Kern.Cli, Kern.Tests`. Namespaces == folders.
- xunit test project with `Harness/KernHarness` (compile+run a snippet, capture stdout) and `Examples/GoldenTests` (see `TESTING.md`).
- `node tools/check-style.mjs` clean on the empty skeleton; `.github/workflows/ci.yml` stays a template until M12.
- **Spec translation (owner decision):** `mechanic` translates `docs/spec/kern.ru.md` into English (`docs/spec/kern.en.md`), keeping code blocks, diagnostics text and Russian identifiers/examples verbatim; rename the Russian original to `kern.ru.md`; `reviewer` spot-checks ≥ 20% of sections against the original; then make the English file the one named in `AGENTS.md`. Language changes are made in English only; the Russian copy is regenerated at M12.
- Accept: `dotnet build` and `dotnet test` run (1 trivial test).

## M1 — Syntax
Full lexical + syntactic grammar of the spec: keywords, literals (numbers with suffixes/hex/bin/`0h` octal, `.5`), strings with prefixes `f r x v fr fx fv`, `"""` raw multi-line, interpolation `${expr}`, `${expr=}`, `${val:.2f}`, comments; declarations (`fun`, `@fun`, `type`, `@type`, `unit`, `unit!`, top-level fields incl. `i32! x = 5`, `use`, `project a.b:`, attributes `#[..]`), members (fields, auto/computed/get-only properties, `ctor`, `op`, extension funs/props/ops, `#type/#fun` static, `~` override, `$` self, `$[A]member` parent selection, `..rest` in patterns), statements/expressions (`if/else`, `match` all forms, `loop` all forms, `async loop`, `stop/next/ret`, Elvis `?:`, `?.`, ranges `..`,`..=`,`:step`, lambdas `fun(i32 n) -> i32 body`, local functions, multi-declare, swap, destructuring, tuples, `await`, `use` as local declaration, **leading-dot continuation lines**), generics (`<T[constraint]>`, `ctor()` constraint), union aliases `type A = [X, Y]`.
Newline/continuation rules are decided by the parser (also used by the formatter). Error recovery: report several errors per file with spans. Diagnostics `PARxxx`/`KRNxxxx` with Russian text.
Accept: parser tests for every construct; round-trip token check; all `examples/**/*.kern` parse without errors (except intentionally invalid ones, none expected).

## M2 — Declarations and types
Symbols for modules/types/functions/properties/fields; declaration collector; import resolver (`use path.module[A, B]`, `use path.*`, lazy std loading); type system: primitives (`i8..u64, f16..f64, bool, char, str, void, object`, universal `inum/fnum/num`), nullable `T?`, `none`/`nil`, unions, tuples (named), function types, `list/array/dict/set…` as builtin generics, `result<T,E>`, `task<T>`, type params with constraints, **`never`**, error type; conversions (implicit numeric widening, constant conversion of unsuffixed literals, `T?`→`object`, base/collection hierarchy); overload resolution exactly as spec §15; operator lookup incl. extension operators.
Accept: unit tests for conversions and overload resolution incl. ambiguity/inapplicable diagnostics (`KRN1401/1402`).

## M3 — Binder
Expression/statement binding to a typed bound tree; flow analysis (smart casts via `if x`, `!= nil`, `is`, single-assignment `T!`, `none` state, definite assignment); `match` (values, types, unions, guards, structural patterns, `Ok/Err`, coverage warnings); `loop` result lists and typing; closures with mutable captures; local functions (hoisting, recursion); `unit` value semantics rules; `unit!` immutability; multiple inheritance with `$[A]` selection and ctor chains; properties (get/set/init, auto, get-only); extension functions/properties/operators; operators incl. the **repeat operator** `str/char/list * inum` (both orders); constructors (primary + secondary chain rule `KRN2190`); abstract/open/override rules; `never` handling; compile-time reflection `name_of<T>()` etc. (stub the evaluation until M9 if needed); attributes bound to symbols.
Accept: ≥ 200 binder tests; every example binds without errors (stage-1 examples) except those needing unimplemented std (track in STATUS).

**M1–M3 completion plan (owner request 2026-10-31):** `docs/M1_M3_PLAN.md` — five features per batch, tests written only after a batch, parallel agents on disjoint files, goal `kernc check` 01–20 green for stage-1 examples.

## M4 — Bytecode and codegen
**Status 2026-11-01:** pools, `.kbc` v2 writer/reader/validator, line tables, disassembler and append-only
opcodes are done; `PropertyDef` (with `OwnerType`, accessors, backing field and attributes) is now emitted
and round-trips, module-level attributes have no source syntax yet, and `ConstantFolder` (`-O1`) +
`PeepholeOptimizer` (`-O2`) live in `Kern.Compiler.Optimization`. Remaining: generic reification audit
of user generic bodies (`typeof<T>` in native positions) and a dedicated bytecode test batch growth.
Opcode set, pools (strings, consts, type signatures, functions, types, fields), `.kbc` binary format with magic `KBC2` + version (writer/reader, round-trip tests), source line tables, **attributes emitted for types/functions/fields/properties**, `SaveSp/RestoreSp` for `stop/next` inside expressions, generic reification signatures, constant folder (`-O1`), disassembler for `kernc dis`.
Accept: round-trip tests; codegen tests on snippets via the VM (needs M5 for execution; develop in parallel by milestones' halves).

## M5 — VM
`KValue` (kind + bits + ref), exact-typed numerics with C# wrap semantics, objects/units (value copy `CopyUnit`), lists/arrays/dicts/tuples, closures with cells, reified types (interned `KType`), calls/virtual calls/natives, panics with Russian message + stack trace, `use`/dispose, async (`KTask`, scheduler single event loop, `await`, external completion), enum values, reflection data structures. Console UTF-8.
Accept: `RunTests` family (control flow, closures, units, inheritance, patterns, async, panics) green.

Done slices: **`use`/dispose (2026-10-31)** — `Binder.Use.cs` + `CodeGenerator.Use.cs`, `UseDisposeTests`: block end / `ret` / `stop` / `next` dispose, panic does not; no new opcodes.

## M6 — Native binding + prelude
Attributes and `NativeCatalog` (scan assemblies → compiler symbols), `NativeResolver` (compiled delegates, not `MethodInfo.Invoke`), marshalling rules (`docs/ARCHITECTURE.md` §6), wrappers `KList/KArray/KDict/KFunc/KAction/KResult/KTask…`, native inheritance from C# hierarchy, builtin→`collection<T>` rule + adapters, module discovery (attributes `[KernModule]` replace the old `ModuleMap` table). Prelude modules: `kern.core` (`print, println, eprintln, panic, delay, exit`), `kern.text` (str, char, interpolation helpers), `kern.collections` (incl. slices and negative indices, `array<u8>`/`list<u8>`), `kern.numerics` (incl. native `decimal`, `bigint`), `kern.math`, `kern.io` (`readln`).

**Status 2026-11-01 — M6 closed:** `collection<T>` protocol/adapter over list/array/dict/str/set/queue/stack/observable (len/is_empty/contains/to_list, iteration, `in`, upcasts), `observable<T>`, native `decimal`/`bigint`, `kern.text`, `kern.io readln`, and native inheritance derived from the C# base classes (`streamwriter/streamreader -> stream`) with runtime `is` checks. Acceptance (examples 01–09 members, `len` properties, `xs[-1]`, `xs[1..3]`, `s[1..3]`) holds; example 23 also runs end-to-end.
Accept: `list/dict/str` members used by examples 01–09 work; `len` etc. are real properties; `xs[-1]`, `xs[1..3]`, `s[1..3]` behave per DECISIONS §I.

## M7 — Std libraries
See `std-dev.md` for the list. Each library: C# API + tests + docs line in spec. Typed HTTP client in both variants; mocks over abstract types (proxy mechanism in VM); tasks/threading/parallel exactly as `DECISIONS.md` §D; `kern.build` types (data classes used by M8); `kern.macro` data types (used by M9).
Accept: examples 07, 09, 12, 14, 18, 22, 23, 29, 30 run and match `expected.out`; typed HTTP clients covered by loopback xunit tests.

**Status 2026-11-01 — M7 closed:** events, compress, http (both clients), threading/worker/channel/mutex/amutex/rw_lock/atomic/semaphore/shared/conc_*, tasks (combinators, cancel_token, pipe/pipe_stream), parallel/pool, `kern.test` mocks (proxy over abstract `@type`s), runtime reflection (`kern.reflection`, `type` members, compile-time `field_names`/`attribute_names`/`has_attribute`) and `kern.build` types (in `Kern.Cli/Build/BuildPrelude.cs`) are implemented. VM additions: coroutine start for async calls, `async_iter<T>` lowering, proxy dispatch (`MemberFlags.Abstract` + `IKProxyHandler`), first-class top-level functions, `panic`/`exit` as `never`. `kern.macro` data types move to M9 together with macros. Tests +31 (suite **303/303**, goldens for 07/14/22/29/30 included); M7 acceptance examples 07, 09, 12, 14, 18, 22, 23, 29, 30 run end-to-end; examples 16, 17, 24 also check and (except the `#[test]`-only 16) run.

## M8 — CLI and builder
`kernc run|check|test|build|dis|exe|dll|clean|tasks|do|help|version`, `--time`, `-O0..2`, `-p profile`; project convention; `build.kern` workspace/target/profile/include/task API per `BUILDER.md`; hash-based skip; library discovery (no toml); `exe` = host + `program.kbc`.
Accept: example 11 (workspace) and conventions-based examples run through `kernc`; CLI tests.

**Status 2026-11-01 - M8 closed:** command set `run|check|test|build|dll|exe|tasks|do|dis|clean|fmt|repl|help|version`, `--time`, `-O0..-O2`, `-p profile`; `build.kern` workspace/target/profile/task include API; built-in tasks `<target>:compile|run|exe|dll|check|dis|test`, `:compile`, `:clean`; profile optimize/warnings-as-errors/env (env exported to `run` and user tasks), `t.args` defaults; hash skip covers sources, settings, profile-independent compiler options and **`.kbc` dependency contents**; `exe` copies the host plus `program.kbc` into `dist/` and the host runs the adjacent bytecode; `w.include` recursively builds child workspaces; external library discovery (`KERN_LIBS`, `libs`, `~/.kern/libs`, project/target `lib_path`, `t.lib`) loads `Kern.Lib.*.dll` into the native catalog. `kernc tasks` lists included projects, targets and tasks; included projects can be source dependencies (`t.deps = ["child:core"]`) and tasks addressed as `project:target:task`. Limits: `t.lib` is covered by discovery/error tests (stage-2 libraries themselves are absent); library/consumer types with the same simple name conflict. Tests: `BuilderM8Tests` + builder suite; example 11 and conventions-based examples run through `kernc`; suite **315/315**.

**Start of the command set done 2026-10-31:** `kernc run <файл.kern> [аргумент ...]` (compile + execute `main`, argv as `list<str>`, async `main` pumped, panics as `ошибка выполнения`), `kernc build <файл.kern> [-o out.kbc]` (writes/prints `.kbc`), plus `dis`/`help`/`version`; shared pipeline in `Infrastructure/SourceCompiler.cs`, 20 CLI tests.

**Project mode done 2026-10-31:** `kernc run|build|check|clean <dir>` per `BUILDER.md` §2 — sources `<dir>/src/**/*.kern` (else `<dir>/*.kern`), project name = folder name, `build` → `<dir>/out/<name>.kbc`, `check` compiles only, `clean` removes `out/`+`dist/`; single-file mode unchanged. Compiler support: `Kern.Core.Text.SourceBundle` (concat + source map), `CodeGenerator.Generate(bound, SourceBundle)` (per-file `SourceFile` + local lines), `DiagnosticText.Format`. M8 later closed the builder/hash/exe/library-discovery remainder (see status above); `dll` belongs to M10.

## M9 — Reflection and macros
Runtime `kern.reflection` (type/field/property/method/param/attribute info, `reflect.*`, `define_type`), compile-time `name_of<T>()`, `field_names<T>()`, …; macro pipeline (text expansion before parsing, `#[macro]`/`#[macro("expr")]`, `macro_ctx`, `derive`, diagnostics KRN1502/1503, nested expansion ≤ 16 passes).
Accept: examples 24, 28.

**Status 2026-11-01 - M9 closed:** runtime reflection is the full `type` surface
(`name/full_name/args/parents/fields/properties/methods/constructors/attributes`,
`is_enum/is_unit/is_abstract/is_interface/is_declared`, `enum_names`, `has_attribute/attribute`,
`get_field/get_property/get_method/get_methods`, `get/set`, `create`, `is_instance`,
`is_assignable_from`) with `field_info/property_info/method_info/param_info/attribute_info`
wrappers, static `reflect.*` and a minimal `define_type` builder (unit/class, fields, field/type
attributes). Compile-time `typeof<T>()`, `typeof(x)`, `name_of<T>()`, `field_names`,
`property_names`, `method_names`, `parent_names`, `attribute_names`, `has_attribute` are computed by
the binder. Macros are a textual pipeline before parsing: macro files compile as a separate
mini-project, attribute macros run on top-level `type`/`unit` (including `#[derive(name)]`),
expression macros `name!(...)` expand in up to 16 passes, and `macro_ctx` reports KRN1502/KRN1503.
Examples 24 and 28 check, run and have `expected.out`; suite **328/328**. `kernc dll` now builds
stubs from macro-expanded sources, so generated members are visible to consumers. Limits: macro
expansion is not run by single-file `CompileCore`; `define_type` has no generics/parents/methods;
attribute `instantiate()` is absent.

## M10 — `.kbc` via `use`
Interface stubs embedded by `kernc dll` (`kern.stub` module attributes), `KbcStubTransform`, `KbcLinker` (matching key `owner|name|flags|op|generics|paramSigs|ext|file`, operand relocation table per opcode, field-layout verification), builder `t.kbc(path)`.
Accept: example 31 (build `mathx` as dll, then run `app`).

**Status 2026-11-01 — M10 closed:** `kernc dll <project> [-o file.kbc]` compiles a library, clears the entry point, and embeds interface sources as `kern.stub(path, module, text)` module attributes; `StubGenerator` replaces bodies of functions/operators/constructors/accessors with `panic("kbc")`, keeps bodies only where the return type is inferred, and removes `main`. Consumers with `t.kbc(path)` load the library, add its stubs as project modules (so `use mathx.vec[...]` binds normally), then `KbcLinker` maps types/fields/functions/globals, verifies field layouts, rewrites pool operands (consts, types, signatures, signature lists, functions, static fields, field slots), replaces stubs with real code, appends private helpers/lambdas, and chains library static initialization before the program. Hash skip includes `.kbc` contents. Example 31 (`kernc dll mathx` then `kernc run app`) works end-to-end; tests `KbcLinkTests` cover dll/link/run, a missing dependency and repeat-build caching. Limits: types match by full name first and simple name second (same-name conflict), and stub field initializers can run before the linked static init (constant initializers are idempotent; no side-effect test yet).

## M11 — fmt, repl, optimizer
`kernc fmt [--check] [--stdout]` (tabs + CRLF, trailing whitespace, token-sequence verified, idempotent — conservative: no brace re-indent yet); `kernc repl` (declarations/state replay); `-O2` peephole (`Not;JmpIf`→`JmpIfNot`, jump threading, jump-to-next, load+pop, unreachable code, target/line remap) — done 2026-11-01, output equal at -O0/-O1/-O2 on examples 01–12, 21.
Accept: formatter idempotence tests over all examples; optimizer equivalence tests.

## M12 - Hardening
Every stage-1 example has `expected.out` and passes; CI matrix windows/ubuntu/macos green (if CI is used); docs synced (`spec`, `ARCHITECTURE`, `STATUS`); `KNOWN_ISSUES` updated; final report to the owner.

**Status 2026-11-01 - M12 nearly closed:** goldens for 09/18/23 and the un-parked 04_flow, formatter idempotence and clean parsing on every example source, `ExampleCompileTests` for `.compile-only` 16/17/31-mathx, and the Windows/Linux/macOS CI matrix. Suite **369/369**, style clean, goldens **21 active**. Only blocker: CI must run after the owner pushes to GitHub.

## M14 — Round 27 additions (2026-10-26)
Cross-cutting milestone for the owner's round-27 items (spec «Раунд 27», `DECISIONS.md` §J). Slices may be pulled into M1/M3/M7/M8/M11 when the touched subsystem is already stable, but M14 closes only when all slices are green. Do not start std/tools slices before the core syntax is implemented.

**Status 2026-11-01:** `with`-expression and `is` patterns landed (`WithExpressionTests`, 5 facts; `IsPatternTests`, 7 facts): copy of a `unit`/`type` with instance fields replaced; full `match` pattern syntax right of `is`/`!is`, bindings under `&&`/then-branch, generic/nullable smart casts, `KRN2101` for bindings under negation/`||`. Still open: generators/`seq<T>`, direct tail-call optimization, data structures, doc/CLI tooling.

1. **Syntax/AST (first slice):** loop labels `loop@outer`, `stop outer`/`next outer`; `with` (`x:{ a = 1 }`); `is` patterns; generic union aliases `type Option<T> = [Some<T>, Nothing]`; `result<T[ok], E[error]>` labels; `yield` and generator bodies. Parser/AST tests for every form.
2. **Binder/types:** generic union alias substitution; `is` pattern bindings and smart casts; generator typing (`seq<T>`); `result` label equivalence; nesting limit 256 (configurable).
3. **Codegen/VM:** direct tail-call optimization; `with` copies for `unit`/`type`; labeled jumps; generators and lazy `seq<T>`; VM frame limit 10,000 (configurable); `span<T>`, `frozen_*`, `deque`, `priority_queue`, `sorted_dict`, `sorted_set`, `bitset`, `ring_buffer`, `lazy<T>`.
4. **Std libraries:** `kern.process`, `kern.path`, `kern.net`, `kern.log`, `kern.cli`, `kern.config`, `kern.ansi`, `kern.time` (time zones), `kern.toml`, `kern.ini`.
5. **Docs/CLI:** `///` capture, doctest (compile + run, `// expect:` check), `kernc doc`, `kernc wasm` (WASI), `kernc new app|lib|service`, `kernc run --watch`.

Accept: new syntax has parser/binder/codegen tests; tail recursion runs a deep loop without stack growth; generators are lazy; data structures/libraries have std tests and examples; doctest/tooling covered by CLI tests; full baseline green. Already done: first-line shebang and numeric separators (`LexerTests`, `CodeGeneratorTests`).

## Owner batch 2026-10-27 — language polish (stage 1)
Recorded in `DECISIONS_NEEDED.md` ("Decided — owner additions"). Ordered by dependency; every slice updates `docs/spec/kern.en.md` + `CHANGELOG_LANG.md` and lands with tests. Two slices may run in parallel only if they do not touch the same lexer/parser/binder files.

1. **B1 — bitwise operators** (S/M, **done 2026-10-27**): `& | ^ ~ << >>` plus compounds, integer-only, C# precedence and shift masking, `>>` still closes nested generics. Lexer/parser/AST, binder operator table, codegen opcodes, VM/type tests, spec §3.
2. **B2 — discarded `result`/`task`** (S, **done 2026-10-27**): `KRN2305` warning, `_ = expr` opt-out (no `#[must_use]` yet). Binder statement path + tests.
3. **B3 — collection literals end-to-end** (L, re-scoped 2026-10-27 after recon, **done 2026-10-27**): split:
    - **B3a (done 2026-10-27)**: untyped `[…]` → `list<T>` (element unification and numeric widening, spread `..expr`, nested literals, diagnostics), `BoundCollectionLiteral` + `ListAppend`/`ListAppendRange`; execution tests.
    - **B3b1 (done)**: typed `<T>[…]` for lists/arrays — `list<T>` by default, `array<T>` when the expected type is exactly `array<T>` (so `array<i32> a = <i32>[1, 2, 3]` builds an array directly), `<T>[]` empty allowed, no implicit `list → array`, spread in an array literal is an error; Q21 (bare `[]` with a known expected type) stays open.
    - **B3b2 (done)**: dict literals — `<K, V>[k: v]` and untyped `["k": 1]` inference, `dict<K, V>` from the expected type, `DictSet` opcode, duplicate-key and key-type diagnostics.
4. **B4 — match patterns** (M, split 2026-10-27): **(a1, done)** range patterns `1..=5`/`1..5`/char ranges with `KRN2112`/`KRN2113`/`KRN2114` and the "ranges never exhaust" rule; **(a2, done)** or-patterns `p1 | p2` with per-alternative coverage (`true | false` exhausts `bool`) and `_` as catch-all — bindings inside an or-pattern are `KRN2114` for now; **(b1, done)** fixed-length list patterns `[a, b]`/`[]` for `list`/`array` with new `ListLen`/`ListGet` opcodes; **(b2, done)** `..rest`/`..` in list patterns (rest binds the remainder) and the structural rest `..`. Parser patterns + `Binder.Patterns`/`Binder.MatchCoverage` + `CodeGenerator.Match` + tests.
5. **B5 — indexing** (M, split 2026-10-27, **done 2026-10-27**): **(a, done)** user-defined `op [](…)` + `x[a, b]`, read-only; the first operator-call path (operator bodies become `BoundFunction`s and are emitted); **(b1, done)** built-in element indexing `xs[i]`/`d[k]` for `list`/`array`/`dict`, negative indices counted from the end, out-of-bounds `panic` (new `DictGet` opcode); **(b2, done)** `s[i]` for `str` and slices `xs[1..3]`/`s[1..=3]` returning a new list/string (spec, round 27).
6. **B6 — hash/compare protocols** (M/L, split 2026-10-27): **(a, done)** structural `==`/`!=` for `unit`/`unit!` (spec 9) and reference identity for `type`; structural value equality/hash for units so `dict` keys work (`KValue.Equals`/`GetHashCode`); hashability check for `dict` keys with a bind-time diagnostic; **(b1, done 2026-10-29)** `op hash() -> u64`/`op compare(other) -> i32` declarations, structural defaults for `unit`/`unit!` (lexicographic compare, hash consistent with `==`), identity hash for `type`, direct `x.hash()`/`x.compare(y)` and ordering via `compare`; `type` without `op compare` stays `KRN2101` (Q24). **(b2)** dict keys honouring overrides and user `op ==` for `type` waits for Q23.
7. **B7 — attribute catalog** (S/M, **done 2026-10-27**): `#[deprecated]` (+ use warning), `#[must_use]` (extends `KRN2305`), `#[inline]`/`#[no_inline]` accepted no-ops, unknown-attribute warning `KRN2307`; `#[cfg]`/`#[test]` documented now, implemented with M14/stage 2. Done: catalog + `KRN2307`/`KRN2306`/`KRN2305`, 5 tests; gaps (grouped syntax, member attributes, `.kbc` emission) are in `docs/KNOWN_ISSUES.md`.
8. **B8 — built-in derives** (M): `#[derive(hash, compare, to_str, to_debug, clone)]` generated by the compiler for `type`/`unit`/`unit!`; conflict with a hand-written member decided and tested; `json` deferred. **(hash, compare — done 2026-10-30):** `derive` in the attribute catalog, allowed on `type`/`unit`/`unit!`, new `DeriveCatalog`, target/name/duplicate diagnostics (`KRN2101`/`KRN2106`), conflict check in `Binder.Derives.cs`, `Bound*Expression.Structural` → new `ValueHashFields`/`ValueCompareFields` opcodes, class field-wise hash/lexicographic compare; `unit` derives are no-ops; `==`/`!=` unchanged. **(clone — done 2026-10-31):** shallow memberwise copy via `CloneObject`/`KObject.CloneShallow`, manual `op clone()` supported, same conflict/target rules, `unit` no-op. Remaining: **B8b** is done (2026-11-01, Q25): `to_str`/`to_debug` are accepted derives and the formatting convention is implemented (`KernFormat`, `to_debug`).
9. **B9 — `lazy<T>`** (std, M14): std type in `kern.collections` plus an external-library variant; no language change.

Accept: each slice green (build 0 errors, full `dotnet test`, style), spec + `CHANGELOG_LANG.md` updated, tests named after the feature; the full baseline stays green after every slice.

## Examples status for stage 1 (golden)
Run + golden: 01-12, 14, 18, 21-24, 28-31 (04 is active since Q25). Compile-only: 16 (`#[test]` is run by `kernc test`; `kernc run` prints only a hint), 17 (needs the internet; the typed clients are covered by loopback xunit tests), 31-mathx (library; covered by `ExampleCompileTests`).
4. **Stage-2 examples:** 13, 15, 19, 20, 25, 26, and 27 are syntax-audited with all examples but require external libraries to run; keep them in the repository without `.stage2` markers.

## Stage 2 (needs explicit owner go-ahead) — see `docs/WISHLIST.md`
