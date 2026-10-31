# M1–M3 completion checklist (verified 2026-10-31)

Purpose: the owner asked to "finish M1–M3 and then re-check everything". Every ROADMAP
acceptance bullet is listed with an honest status and the evidence. Statuses: ✅ done and
exercised · 🟡 partial / works for stage-1 examples but a named gap remains · ❌ missing.

## Owner decisions 2026-10-31 (recorded in `DECISIONS_NEEDED.md`)

Tuple names come from the expected type (no `(x = 1, …)` literal); only language features count
toward the five-feature test batch; stage-2 examples get **stub libraries** so they compile
(running stays out of scope); `main` supports both `void` and `-> i32` (exit code); loop-as-
expression and `dict`/`str` iteration are in M1–M3; tuples need structural `==`, hash and
`compare`; auto-push after green batches; `archive/legacy-tests` stays.

**Landed after the decisions (2026-10-31):** lambdas with by-value captures; extension functions,
properties and operators; parameter default values; the repeat operator (`Repeat` opcode);
`main() -> i32` exit codes; native type registration (`SymbolKind.Native`). Still open from the
checklist: mutable captures (cells), local functions, tuple `==`/hash in the binder, loop-as-
expression, `dict`/`str` iteration, imports, `result`/`Ok`/`Err`, inheritance audit, attribute
binding, reflection stubs, stage-2 stubs.

## M1 — Lexer, parser, AST, diagnostics

| Item | Status | Evidence / gap |
|---|---|---|
| Numbers: decimal, hex, binary, `0h` octal, `.5`, suffixes, separators | ✅ | lexer tests (archived suite), examples 01 |
| Number exponents (`1e-6`) | ❌ | `LEX005` (KNOWN_ISSUES); use `0.000001` |
| Strings: escapes, `\uNNNN`, raw `r`, verbatim `v`, triple quotes | ✅ | examples 01/12; `StringLiteralText` |
| Interpolation `f`, `fr`, `fv`; `${}`, `${=}`, format specs | ✅ | Batch 0; `InterpolationTests` ran before archiving |
| Regex literal `x"…"` value | ❌ | parses, value is M7 (`KRN2101`) |
| Declarations: `fun`, `type`, `unit`, `unit!`, attributes, `project`, `use`, `#type/#fun`, extension members, operators, `op []` | ✅ | parser tests; examples |
| Statements/expressions: `if`, `match` (all pattern forms), all `loop` forms, `stop/next/ret`, lambdas (parse), local functions (parse), `await` (parse), ranges, leading-dot chains, tuples (parse) | ✅ | parser coverage in archived suite; iteration loop now binds |
| Labels `loop@outer` / `stop outer` | ❌ | round 27 syntax, not parsed/bound |
| Error recovery, several diagnostics per file, spans | ✅ | parser diagnostics in CLI output |
| All examples parse | ✅ | `StageOneSyntaxTests` passed before archiving |

M1 verdict: **🟡 complete for stage 1 except number exponents and regex literal values (M7) and loop labels.**

## M2 — Symbols, types, conversions, overloads

| Item | Status | Evidence / gap |
|---|---|---|
| Type/function/global symbols, member tables | ✅ | `SymbolTable`, `DeclarationCollector` |
| Import resolver (`use path.module[…]`) | ❌ | `use` is parsed and ignored; import names stay unknown (example audit: ~230 `KRN2103` cascades) |
| Primitives, nullable `T?`, unions, tuple types, function types, `never`, error type | ✅ | type resolver tests |
| Builtin generics: `list/array/dict/set/collection` | ✅ | `collection<T>` upcast exists |
| `result<T,E>` type | 🟡 | type exists (`BuiltinGenericKind.Result`), `Ok`/`Err` constructors/methods missing |
| `task<T>` type | 🟡 | exists; async end-to-end gaps (M5) |
| Generic constraints, union alias substitution | 🟡 | basic constraints; substitution edge cases unverified |
| Native generic member substitution (`list<T>.first() -> T`) | ❌ | KNOWN_ISSUES: binds as `object` |
| Conversions (widening, nullable, boxing, collection upcast, constant narrowing) | ✅ | constant literal narrowing added in Batch 0 |
| Overload resolution per spec §15 (`KRN1401/1402`) | ✅ | resolver + tests |

M2 verdict: **🟡 imports, generic ctor inference, `result` constructors and native generics are the remaining M2 gaps.**

## M3 — Binder

| Item | Status | Evidence / gap |
|---|---|---|
| Literals, variables, member access, calls, indexing/slices, collections/dicts | ✅ | examples 01–06 partially; built-in indexing tests |
| Operators (arithmetic, comparison, bitwise, shifts, repeat, `in`/`!in`) | ✅ | Batch 1 membership |
| String interpolation, Elvis `?:`, safe call `?.` | ✅ | Batch 0; example 01 runs |
| Multi-assignment/swap, `use`/dispose, constant conversions | ✅ | Batch 0 |
| Destructuring (list/array, `..rest`, `_`) | ✅ | Batch 1; example 02 line 38 |
| Tuples: literals, named tuples, item access, tuple destructuring | ✅ | Batch 1: `BoundTupleExpression`/`TupleGet`, `p.x`, `(i32 a, str b) = pair`, `var (a, b)`; example 02 checks and runs |
| Lambdas as values, captures | ❌ | parsed only; `MakeClosure` opcode unused by compiler |
| Local functions (hoisting, recursion) | ❌ | parsed only (`KRN2109`) |
| `await` as an expression | ❌ | parsed only; `KRN2101` |
| Flow analysis: definite assignment, smart casts, `never`, unreachable | ✅ | flow tests (archived) + `02_types` flow parts |
| `match`: literals, types, unions, ranges, or-patterns, lists, structural, guards, coverage | ✅ | B4 slices; or-pattern bindings still `KRN2114` |
| Loops: condition/infinite/do-while, `stop`/`next` in expressions, iteration | 🟡 | `SaveSp/RestoreSp` paths exist; loop-as-expression (list result) missing; `dict`/`str` iteration missing |
| `unit`/`unit!`: value semantics, structural `==`, hash/compare/clone, auto `to_str` | ✅ | B6/B8 slices |
| Inheritance: single parent, `@type`/`~fun`/abstract/open, multiple inheritance `[A(…), B(…)]`, `$[A].member` | 🟡 | single parent/override work in examples 06; multiple inheritance and parent selections need the Batch 2 audit |
| Properties (auto/get/set/init/computed) | ✅ | examples 06, init/`init` tests |
| Extension functions/properties/operators | 🟡 | operator/extension basics exist; audit pending |
| Constructors: primary, secondary, chains, generic ctor inference | 🟡 | chains done; `Box(5) → Box<i32>` missing |
| Attributes bound to symbols (not only warnings) | ❌ | parsed + `deprecated`/`must_use` diagnostics; not stored on `MemberTable`/symbols |
| Compile-time reflection stubs (`name_of<T>`, `typeof<T>`) | ❌ | M3 item, not started (examples 24) |
| `use`/dispose | ✅ | Batch 0 + M5 slice; native `dispose` blocked by `IsStatic` (KNOWN_ISSUES) |

M3 verdict: **🟡 core is in place; lambdas, local functions, `await`, generic-ctor
inference, imports, inheritance audit, attribute binding, reflection stubs and the remaining
tuple/loop-expression forms remain.** Tuple support, `in`/`!in`, list/tuple destructuring, the
`!is` flow fix and the spurious-unreachable fix after iteration loops landed in Batch 1; example
02 is green.

## Definition of "M1–M3 done"

1. Every ❌/🟡 above is closed or explicitly re-scoped with owner approval.
2. `kernc check` green for stage-1 examples `01–12, 14, 16–18, 21–24, 28–31` (13/15/19/20/25–28 are
   stage 2); 16/17 may stay compile-only per ROADMAP.
3. `kernc run` on examples 01–06 matches `expected.out`.

## After that — M4 (bytecode/codegen/optimizer)

Already done: pools, `.kbc` v2 writer/reader/validator, line tables, disassembler, append-only
opcode policy, `SaveSp/RestoreSp` opcodes.
M4 remaining: **attributes emitted for types/functions/fields/properties**, generic reification
signatures audit, constant folder (`-O1`), peephole groundwork for `-O2` (M11), and a bytecode
test batch once the owner's five-feature test rule is satisfied.
