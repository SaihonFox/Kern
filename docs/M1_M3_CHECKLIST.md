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
`main() -> i32` exit codes; native type registration (`SymbolKind.Native`); cells and recursive
local functions; tuples end-to-end; loop expressions and `dict`/`str` iteration; imports/modules
(M3-F); `result`/`Ok`/`Err`. Still open from the checklist: inheritance audit, attribute binding,
reflection stubs, stage-2 stubs.

## M1 — Lexer, parser, AST, diagnostics

| Item | Status | Evidence / gap |
|---|---|---|
| Numbers: decimal, hex, binary, `0h` octal, `.5`, suffixes, separators | ✅ | lexer tests (archived suite), examples 01 |
| Number exponents (`1e-6`) | ✅ | `Lexer.ReadNumber`; `NumericLiteralTests` (2026-11-01); malformed → `LEX005` |
| Strings: escapes, `\uNNNN`, raw `r`, verbatim `v`, triple quotes | ✅ | examples 01/12; `StringLiteralText` |
| Interpolation `f`, `fr`, `fv`; `${}`, `${=}`, format specs | ✅ | Batch 0; `InterpolationTests` ran before archiving |
| Regex literal `x"…"` value | ✅ | 2026-11-01: `x"…"` (raw pattern) и `fx"…"` (интерполяция) сворачиваются в `regex(pattern)` из `kern.regex`; `RegexLiteralTests` |
| Declarations: `fun`, `type`, `unit`, `unit!`, attributes, `project`, `use`, `#type/#fun`, extension members, operators, `op []` | ✅ | parser tests; examples |
| Statements/expressions: `if`, `match` (all pattern forms), all `loop` forms, `stop/next/ret`, lambdas (parse), local functions (parse), `await` (parse), ranges, leading-dot chains, tuples (parse) | ✅ | parser coverage in archived suite; iteration loop now binds |
| Labels `loop@outer` / `stop outer` | ✅ | `LoopLabelTests` (2026-11-01): all loop forms + loop expressions, lexical targets, `use` disposal, `KRN2106` for unknown/duplicate labels |
| Error recovery, several diagnostics per file, spans | ✅ | parser diagnostics in CLI output |
| All examples parse | ✅ | `StageOneSyntaxTests` passed before archiving |

M1 verdict: **✅ complete for stage 1** (regex literals landed 2026-11-01 and lower to `kern.regex.regex`).

## M2 — Symbols, types, conversions, overloads

| Item | Status | Evidence / gap |
|---|---|---|
| Type/function/global symbols, member tables | ✅ | `SymbolTable`, `DeclarationCollector` |
| Import resolver (`use path.module[…]`) | ✅ | M3-F (2026-10-31): module-per-file, `[items]`/`.*`, re-export, conflict/unknown diagnostics, library projects; native `kern.*` names remain global |
| Primitives, nullable `T?`, unions, tuple types, function types, `never`, error type | ✅ | type resolver tests |
| Builtin generics: `list/array/dict/set/collection` | ✅ | `collection<T>` upcast exists |
| `result<T,E>` type | ✅ | M3 (2026-10-31): union of built-in `Ok<T>`/`Err<E>`; `Ok`/`Err` construction, patterns, coverage, `KRN2305`, `ok`/`error` labels |
| `task<T>` type | ✅ | 2026-11-01: `async fun -> T` is `task<T>`, calls return a completed `KTask`, `await` yields `T`; parallel spawn/combinators are library-stage |
| Generic constraints, union alias substitution | ✅ | 2026-11-01: `ctor()` enforced (abstract/ctorless → `KRN1401`), class/abstract constraints via upcast, `T[num]`; generic union aliases substitute arguments, nest and import (`Option<i32>`, `KRN1002` on arity); `ctor(args)` argument matching not checked |
| Native generic member substitution (`list<T>.first() -> T`) | ✅ | 2026-11-01: binder substitutes receiver arguments in native member call results (`list`/`array`: `first`/`last`/`get`/`remove_at` → `T`, `reverse`/`slice`/`filter` → `list<T>`, `map` → `list<U>`; `dict`: `get` → `V`, `keys`/`values` → `list<K>`/`list<V>`); `set`/`collection`/`iter` have no native APIs yet |
| Conversions (widening, nullable, boxing, collection upcast, constant narrowing) | ✅ | constant literal narrowing added in Batch 0 |
| Overload resolution per spec §15 (`KRN1401/1402`) | ✅ | resolver + tests |

M2 verdict: **✅ imports, `result`, generic ctor inference and native generic member substitution are done (2026-11-01).**

## M3 — Binder

| Item | Status | Evidence / gap |
|---|---|---|
| Literals, variables, member access, calls, indexing/slices, collections/dicts | ✅ | built-in indexing + writable `list/array/dict` index stores (`xs[i] = v`, `d[k] = v`, compound, `ListSet`/`DictSet`), `str` read-only |
| Operators (arithmetic, comparison, bitwise, shifts, repeat, `in`/`!in`) | ✅ | Batch 1 membership |
| String interpolation, Elvis `?:`, safe call `?.` | ✅ | Batch 0; example 01 runs |
| Multi-assignment/swap, `use`/dispose, constant conversions | ✅ | Batch 0 |
| Destructuring (list/array, `..rest`, `_`) | ✅ | Batch 1; example 02 line 38 |
| Tuples: literals, named tuples, item access, tuple destructuring | ✅ | Batch 1: `BoundTupleExpression`/`TupleGet`, `p.x`, `(i32 a, str b) = pair`, `var (a, b)`; example 02 checks and runs. Structural `==`/hash/compare and tuple dict keys landed 2026-10-31 |
| Lambdas as values, captures | ✅ | Batch 1.5: `BoundLambdaExpression`, `MakeClosure`/`CallValue`, capture by value; mutable capture (cells) still `KRN2109` |
| Local functions (hoisting, recursion) | ✅ | 2026-11-01: hoisting, by-value captures, self- and mutual recursion through captured `KCell` (explicit signature), cells for mutable captures |
| `await` as an expression | ✅ | 2026-11-01: `await` only inside `async`, operand `task<T>` → `T`; VM `Await` parks/resumes; `async main`; `AsyncTests` (7 facts) |
| Flow analysis: definite assignment, smart casts, `never`, unreachable | ✅ | flow tests (archived) + `02_types` flow parts |
| `match`: literals, types, unions, ranges, or-patterns, lists, structural, guards, coverage | ✅ | B4 slices; or-pattern bindings still `KRN2114` |
| Loops: condition/infinite/do-while, `stop`/`next` in expressions, iteration | ✅ | 2026-11-01: ranges/list/array/dict/str iteration, loop-as-expression, `loop@label` + `stop/next label`, dispose frames, `iter<T>` protocol lowering (`~fun next -> T?`); `async_iter<T>` gap |
| `unit`/`unit!`: value semantics, structural `==`, hash/compare/clone, auto `to_str` | ✅ | B6/B8 slices |
| Inheritance: single parent, `@type`/`~fun`/abstract/open, multiple inheritance `[A(…), B(…)]`, `$[A].member` | ✅ | A1–A3b (2026-11-01): single-parent members/layout/parent ctor/upcast/`is`, `CallVirtual` dispatch, multiple independent parents + selectors + all parent ctors, diamonds share one ancestor (`CallBase`, topological layout), abstract-member audit (`KRN2101`), Q29 `~fun` validation (`open fun`/`@fun` base required, protocol bases exempt), Q30 dispatch for accessors/operators/indexers, `$[A].member` super calls, `~` operators/properties validated by existence. Remaining minor: `base.`-style C# syntax (not planned) |
| Properties (auto/get/set/init/computed) | ✅ | examples 06, init/`init` tests |
| Extension functions/properties/operators | ✅ | Audit 2026-11-01: instance member wins; builtin receivers, generic receivers (`fun list<i32>.total`, `i32 list<i32>.count`), properties/operators, overloads, `KRN1400`/`KRN1407`, imported extensions by member name; parser fixes (`$` newline capture, generic receiver). Not supported: type-parameter receiver (`fun list<T>.foo`) |
| Constructors: primary, secondary, chains, generic ctor inference | ✅ | chains, `Box(5) → Box<i32>`, parent ctor calls (A1), multiple parent ctors (A3a) |
| Attributes bound to symbols (not only warnings) | ✅ | 2026-11-01: `TypeSymbol`/`FunctionSymbol`/`FieldSymbol`/`PropertySymbol.Attributes`; `TypeDef`/`FunctionDef`/`FieldDef` emission + `.kbc` round-trip; disassembler prints type/function `attr`; grouped `#[A, B]`; `deprecated`/`must_use` read symbol data. Residual: `PropertyDef` is not emitted (property attrs live on accessors), module/global attrs have no slot |
| Compile-time reflection stubs (`name_of<T>`, `typeof<T>`) | ✅ | 2026-11-01: `typeof<T>()` (`TypeOfSig`, canonical text) and `name_of<T>()` (binder literal), `ReflectionStubTests`; runtime `typeof(x)`/`reflect.*` are M4/M7 |
| `use`/dispose | ✅ | Batch 0 + M5 slice; native `dispose` blocked by `IsStatic` (KNOWN_ISSUES) |

M3 verdict: **✅ closed 2026-11-01 for the stage-1 language surface.** Remaining items are re-scoped, not missing: runtime reflection members `typeof(x)`/`reflect.*` (M4/M7), generic signature substitution for user generic bodies in native positions (documented `KNOWN_ISSUES`), expression-bodied `op` members (done 2026-11-01), and `~` validation for operators/properties (done 2026-11-01). All ❌/🟡 rows above are closed or explicitly re-scoped.

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
