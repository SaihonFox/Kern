# DECISIONS_NEEDED

## Decided — owner answers (2026-10-26)

Recorded as given; the corresponding code/spec work is done in its own slice, not retroactively.
**Q1-Q16 all have an owner answer; Q17, Q18 and Q21 in the `## Open (new)` section below are the only open ones.** Language-visible ones (Q7, Q9, Q11, Q13, Q15) still owe their `docs/spec/kern.en.md` + `CHANGELOG_LANG.md` update in the slice that implements them.

- **Q13 → option 1:** implement copy-out/copy-back for stores into `unit` fields (`o.inner.x = 5` becomes load-copy → store → write the copy back into `o.inner`), including through locals and nested chains; a get-only link in the chain reports an error instead of dropping the write. Needs a sentence in spec §9 (value semantics) and a `CHANGELOG_LANG.md` line when implemented. Because copy-back makes statics work the same way, a mutable `#` static of `unit` type stops being a data-loss trap (see Q15a).
- **Q15b → option 1, confirmed 2026-10-26:** `init` is writable in the instance field/property initializers of the same type, in the ctor prologue (including `$`-parameter auto-assignment) and in the ctor body — nested blocks, `if`/`match`/`loop` and chained secondary ctors included; multiple writes during construction are allowed; `{ get; init } = 5` is legal. Forbidden: static initializers, ordinary methods/properties/operators, and lambdas or local functions declared inside the ctor (so a deferred call cannot write after construction). Static initializers run once, in declaration order, in the module `<static-init>` before the entry point (consistent with §12 line 1109 "a library's static initialization runs before the program"); no `init` writes there.
- **Q15a1 → confirmed:** no mutable top-level fields in stage 1. Top level keeps only immutable constants (`i32! limit = 5`, §3 line 168); all mutable module state lives in `#` static fields of a type. Consequence: `ROADMAP` M3-F shrinks from "modules/globals" to imports plus static members, and the collector's `ModuleSymbol.Globals` is limited to constants — update both when M3-F starts.
- **Q15a2 → confirmed:** a `#` static may have `unit` or `unit!` type. `unit!` values are deep-immutable constants (`#Point ORIGIN = Point(0, 0)`); mutable `unit` statics are allowed and documented as copy-on-read, with writes such as `Type.cache.x = 5` working through the Q13 copy-back path.


- **Q7 → option 1:** keep the full numeric-suffix set (`f16 f32 f64 i8…u64`, `b`), document it in spec §3 lines 143–144 plus a `CHANGELOG_LANG.md` line; lexer/tests unchanged.
- **Q12 → option 1:** dedicated block `KRN2110` non-exhaustive match, `KRN2111` unreachable arm, `KRN2112` pattern type mismatch, `KRN2113` malformed structural/constructor pattern, `KRN2114` unsupported pattern form; `KRN2102`/`KRN2109` retired for these cases. Severities taken as recommended (both coverage codes = warning, the other three = error) — owner may still tighten non-exhaustive to error.
- **Q16 → option 1:** new `KRN2191` «цепочка конструкторов образует цикл», error, chain-graph check at bind time, one diagnostic per cycle with the span on the closing `: $(...)`.

- **Q1 → option 1:** complete the English prose translation in short sections; `kern.ru.md` stays the owner copy.
- **Q2 → option 1:** close the question, but add the side-effecting RHS regression test it demanded (current tests are value-based only).
- **Q3 → option 1:** function types are identity-only in stage 1 (no parameter/result variance).
- **Q4 → option 1:** add `collection<T>` as a builtin generic root now, merge with the native `KernCollection<T>` root at M6.
- **Q5 → option 1:** `dict` gets no `collection<T>` parent in this stage; the M6 native hierarchy decides.
- **Q6 → option 1:** numeric widening exactly as C#, including lossy `iN→f32/f64` and `u64→f32` (matches current code; no spec edit needed).
- **Q8 → option 1:** lifted conversions allowed whenever `S→T` is implicit (`i32→i64?`, `Child→Base?`).
- **Q9 → option 1:** spec wins — `nil` is legal only for `T?`; `object` is not nullable. **Code change needed:** `Conversions.IsNilAssignable` must stop accepting `object` (spec §3 line 112 / §4 line 161 already say this; no spec edit needed).
- **Q10 → confirmed:** already implemented per option 1; nothing to do.
- **Q11 → option 1:** fields created by primary-constructor parameters are `pub`. Needs spec §8 line 388 / §9 line 430 wording plus a `CHANGELOG_LANG.md` line when implemented.
- **Q14 → option 1:** design each M14 API from the common protocol inside its slice, write the member list into the spec, owner vetoes before implementation.

## Decided - owner answers (2026-10-27)

- **Q19 -> decided:** the receiver `$` has exactly one member-access spelling: `$field` (`$x = 5`, `$x + 1`, `$ * 2`). The long form `$.field` is an **error** (`$.x = 5` must not compile). Spec §1 line 34 currently documents both forms ("сокращённо: `$field`, полная: `$.field`") - that sentence must become short-form-only, with a Russian diagnostic for `$.` when this lands. Change scope when implemented: parser/AST (`$.` rejection), spec line 34 and any `$.` samples, `CHANGELOG_LANG.md`, examples (already short-form only: 0 occurrences of `$.` in `examples/`), existing tests (already use `$field`). The parent-selection form from spec §2 (`fun $[A].foo(...)`, `$[A].member`) is a qualified name rather than a `this.` access, so it is assumed to keep its dot - please correct that reading if it is wrong. **Not implemented yet - the owner asked to record it for later.**

## Decided — owner additions (2026-10-27, batch "language polish")

The owner asked for these eight items; they are approved stage-1 work. Where the message allowed several readings, the notes below state the **assumed** semantics - correct me and the implementing slice changes it.

1. **Bitwise operators.** `& | ^ ~ << >>` plus compounds `&= |= ^= <<= >>=` for integer types only (`bool` keeps `&&`/`||`/`!`). C# precedence (multiplicative > additive > shifts > relational > equality > `&` > `^` > `|` > `&&` > `||`), C# masking of the shift count (`x << (n & 31/63)`), result type = the wider operand after the usual numeric widening, `~` keeps its operand type. `^` stops being "reserved for XOR". Parser trap: `>>` must still close nested generics (`list<list<i32>>`), so the type parser splits the token.
2. **`match` patterns.** Range patterns `1..=5`, `1..5` and char ranges; or-patterns `p1 | p2` with the Rust rule that every alternative binds the same names with the same types; list patterns `[a, b, ..rest]`, `[first, ..]`, `[]` for `list<T>`/`array<T>` (not `str`); the structural-pattern rest `..` finally works. Guards apply to the whole arm. Coverage (`KRN2110`): or-pattern alternatives count individually, list and integer-range patterns do not make a match exhaustive (documented), `nil`+type logic unchanged.
3. **Discarded `result`/`task` warning.** New warning `KRN2305` when an expression statement drops a value of type `result<T, E>` or `task<T>`; `_ = expr` is the explicit opt-out; `#[must_use]` on a function/type extends the same warning to its return type.
4. **Indexer and `hash`/`compare` protocols.** `op [](...) -> T` with one or more index parameters, called as `x[a, b]`, for read access on custom types; built-in collections keep native indexing. `op hash() -> u64` and `op compare(other) -> i32`: `unit`/`unit!` get structural defaults (lexicographic field order, field-mixed hash; field types must be hashable/comparable), classes default to reference identity, both overridable; `dict<K, V>` requires a hashable `K` (bind-time diagnostic for a non-hashable key).
5. **`lazy` stays a library feature.** No `lazy` field modifier and no keyword: `lazy<T>` is a std type (`kern.collections`, already listed in M14) and may live in external libraries; `lazy<T> l(fun() -> T)`, thread-safe once, `value` computes on first read. No language change.
6. **Attribute catalog.** Core attributes documented in the spec: `#[deprecated("msg")]` (use warning), `#[must_use]`, `#[inline]`/`#[no_inline]` (accepted optimizer hints, no-op until M11), `#[cfg(target = "wasm"|"native"|"cli", os = ...)]` (M14, `kernc wasm`), `#[test]` (stage-2 runner), `#[derive(...)]` (item 8). Library attributes (`#[get]`, `#[inject]`, ...) stay library-defined. Unknown attribute: **assumed** warning `KRN2307`, not an error, so libraries can introduce attributes before the compiler knows them.
7. **`array` literal/typing.** `array<T>` stays a builtin type implemented as an internal library; untyped `[1, 2, 3]` remains `list<i32>`, while a typed literal `<i32>[1, 2, 3]` takes its kind from the expected type, so `array<i32> a = <i32>[1, 2, 3]` builds an array directly (no intermediate list). No implicit `list<T> -> array<T>` conversion. Note (B3b1): the old spec wording `array<i32>[1, 2, 3]` (a type prefix before `[`) is not parsed and was replaced in spec 3 by the typed-literal form above - say if you want the prefix form back.
8. **Built-in derives before macros.** `#[derive(hash, compare, to_str, to_debug, clone)]` on `type`/`unit`/`unit!` generates the members in the compiler, not via macros; `json` waits for the json std/reflection slice. On `unit` the structural `hash`/`compare` defaults from item 4 already exist, so a derive there only documents/overrides. A hand-written member with the same signature wins or is a bind error (decide in the slice and document).

Every item is language-visible: its slice updates `docs/spec/kern.en.md` + `CHANGELOG_LANG.md` and adds tests. Slice order lives in `docs/ROADMAP.md` ("Owner batch 2026-10-27").

## Open (new)

### Q20: async functions and `task<T>` - exact return-type rule
Owner statement (2026-10-27, verbatim): "асинхронные функции возвращают task(task<T>) как в шарпах." The spec only says `async fun ... await expr` and that `main` may be async (§13 lines 771-788); it never states how the declared return type of an async function maps to `task`. The doubled `task(...)` also needs one confirmation.

Options:
1. C#-like mapping: `async fun f() -> i32` has type `task<i32>` when called; `await f()` yields `i32`; `main` may be `async` and is awaited by the runtime. The author never writes `task` in the signature (recommended).
2. Explicit wrapper: the author writes `async fun f() -> task<i32>`, and `task` must not be nested; a bare `-> i32` async function is an error.
3. Literal nesting `task<task<T>>`: an async function returning `task<i32>` produces `task<task<i32>>`, one `await` per level.

Recommendation: option 1 - it matches C#, keeps `await expr` single-level as the spec already shows, and reads naturally with `task<T>` from §23 (`task.all`, `task.delay`, `pipe.send -> task<bool>`). Please confirm or correct, in particular whether the doubled "task(task<T>)" in the message meant "a task of T".

### Q17: `unit!` fields cannot be written during construction (blocks custom `init` bodies)
Reviewer finding on the freshly landed `init` slice. `init` writes are now allowed during construction for `init` **properties** (auto -> backing field, body -> accessor call), but a plain `unit!` **field** store is still rejected by `Binder.cs:541` (`KRN2105`) even inside a ctor of the same type. Why it matters: (a) the VM now has a per-object construction window that deliberately permits `unit!` field writes while a ctor runs, so the binder is stricter than the runtime; (b) a `unit!` type with a hand-written `init $._x = value` accessor can never compile, because the accessor body is bound with `_constructionType == null`; (c) spec §4 (lines 220-230) defines `!` on a field as a *required field* ("must be initialized when the object is created"), explicitly **not** single-assignment, so a ctor must be able to assign it.

Options:
1. Allow `$`-field stores of `unit!` fields while inside a ctor or initializer of that type (same contexts as `init` writes) and treat an `init` accessor body as a construction context for its owner; keep `set` and all post-construction writes forbidden (recommended - matches the VM window, the spec wording, and makes `init` usable on `unit!`).
2. Keep fields sealed; allow only `init` accessors to write. Document that `unit!` types must fill state through primary-ctor parameters, `init` properties and initializers, and report a specific message for plain `$.x = v`. Cheaper, but a hand-written `init` accessor still needs the accessor-body exemption from option 1, so it is not really a smaller change.
3. Forbid `unit!` field writes everywhere, including ctors, and document that `unit!` state is set only through constructor parameters and initializers. Simplest rule, but then a `!` required field can never be filled in a secondary ctor body.

Recommendation: option 1.

### Q18: `!` required fields are never checked
`FieldSymbol.IsRequired` (`!` on a field, spec §4 lines 220-230) is collected and mapped to `MemberFlags.Readonly` in `CodeGenerator.cs:102`, but no VM or binder path reads that flag, and no pass verifies that every construction path assigns a required field. `type User { !str name }` therefore compiles, `User()` runs, and reading `name` yields `none` typed as `str`.

Options:
1. Enforce in the binder: for every chain root (a ctor that does not chain), after binding prologue and body require each required field to be definitely assigned on every path; initializers and `$`-parameter copies count as assignment; chained ctors inherit the root's analysis. Report a new error (e.g. `KRN2192`) at the ctor span. Recommended.
2. Same analysis, but a warning for now.
3. Defer: record in `docs/KNOWN_ISSUES.md`, add a runtime check on first read of a required field in M5/M6.
4. Drop `!`-on-field semantics; require `init` or a primary-ctor parameter instead (spec change).

Recommendation: option 1 with a dedicated `KRN2192`; the coverage analysis is the existing `BinderFlow` machinery, so cost is moderate.

### Q21: empty literal `[]` when the expected type already fixes the collection
B3a rejects every untyped empty literal with `KRN2102` («укажите тип элементов явно»), matching spec 3 «Пустые коллекции (тип обязателен): `<i32>[]`». But the same spec explains the ban as «невозможно определить тип и вид коллекции»: in `list<i32> xs = []` both the element type and the kind are known from the declaration, so the rationale does not apply (B3b will read the kind from the expected type anyway).

Options:
1. Allow `[]` whenever the expected type is `list<T>`/`array<T>`/`dict<K, V>`; keep `<T>[]` valid and keep `var a = []` an error. Recommended - consistent with the rationale, no new syntax, lands with B3b.
2. Keep rejecting every `[]` and require the typed form even when the expected type is known (current B3a behaviour).
3. Allow `[]` only for `list<T>` (not `array`/`dict`) to keep the kind unambiguous.
4. Drop the empty literal entirely and require `<T>[]`.

Recommendation: option 1, implemented in B3b.

### Q22: duplicate keys in a dict literal
The spec does not say what `["a": 1, "a": 2]` does. B3b2 implements last-write-wins at runtime (like C#) and adds no static diagnostic.

Options:
1. Last write wins silently (current B3b2 behaviour). Recommended - simple, matches `KDict.Set`.
2. Compile error when two literal keys are compile-time constants and equal.
3. Runtime panic on a duplicate key.
4. First write wins.

Recommendation: option 1.

### Q23: `op hash`/`op compare` - invocation surface and what overrides affect
Item 4 approves `op hash() -> u64` and `op compare(other) -> i32`, structural defaults for `unit`/`unit!`, reference defaults for classes, both overridable, and `dict<K, V>` requiring a hashable `K`. The spec does not say how these protocols are invoked, and the dict runtime currently keys on `KValue` reference equality for `unit` values, so a structural default needs a real runtime comparer.

Options:
1. Protocols are internal and also directly callable: `==`/`!=` and `<`/`>`/`<=`/`>=` on user types use `compare`, dict keys use `hash`, and `x.hash()`/`x.compare(y)` work as ordinary member calls (operator members callable by name). Recommended - one mechanism, testable, matches "protocols".
2. Internal only: no direct `x.hash()`/`x.compare(y)` syntax; only `==`, ordering and dict keys use them.
3. Library functions only: `hash(x)`/`compare(x, y)` in std; `op` declarations are just the spelling the compiler looks for.
4. Something else.

Sub-question: if a `unit` overrides `op hash`, should `dict` keys use the override (the VM would need a per-dict hash/equality callback that runs Kern code) or keep the structural default (override affects only `==`/direct calls)? Recommendation: use the override - a dict key that ignores its own hash contract is a trap - even if that makes the runtime comparer callback-based.

Recommendation: option 1, with dict keys honouring overrides. B6a (structural `==`/`!=` for units, reference identity for classes, structural unit hash for dict keys, hashability diagnostic) is done. B6b is split: **B6b1** (structural `compare` default for `unit`/`unit!`, direct `x.hash()`/`x.compare(y)`, ordering via `compare`, user overrides on any type) **is implemented (2026-10-29, option 1)** and does not touch the dict sub-question; **B6b2** (dict keys honouring overrides, user `op ==` for `type`) waits for this answer.

### Q24: ordering of `type` values without `op compare`
B6b1 gives `unit`/`unit!` a structural lexicographic `compare` default, but a `type` (class) has only reference identity: equality and `hash` are identity-based, and there is no meaningful order between two unrelated instances. B6b1 therefore binds `<`/`>`/`<=`/`>=` and `x.compare(y)` for a class only when it declares `op compare`; without one they stay `KRN2101`. `x.hash()` and `==`/`!=` work via identity.

Options:
1. Keep that: ordering on a class requires `op compare` (`KRN2101` otherwise). Recommended - no arbitrary order.
2. Order by identity hash (`RuntimeHelpers.GetHashCode`), so any two classes are comparable but the order is stable only within a run.
3. Panic at runtime when two different references are compared.
4. Something else.

Recommendation: option 1.

B6b1 implements option 1 (2026-10-29): a class without `op compare` reports `KRN2101` for `compare`/ordering; `hash` and `==`/`!=` work by identity.

### Q25: rendering format of `#[derive(to_str)]` / `to_debug`
The spec says only that a `unit` gets `to_str()` "representing the value based on its fields" (§9, line 526) — the concrete format is nowhere defined, and `to_debug` has no definition at all. `examples/GOLDEN_ASSUMPTIONS.md` records the convention taken from the owner's earlier design and `reference/src/Kern.Vm/Values/ValueOps.cs`: `Name(field=value, ...)`, nested `str`/`char` quoted, top-level `str`/`char` unquoted.

Options:
1. Adopt that convention: `to_str` = reference `ToStr` (`Point(x=1, y=2)`, top-level `str`/`char` unquoted), `to_debug` = reference `Repr` (always quoted). The two differ only for bare `str`/`char` values; for `unit`/`type` both produce the same string. Golden outputs stay valid. Recommended - nothing new is invented.
2. Implement `to_str` only for now (same convention) and keep `to_debug` as `KRN2101` until a debug format is designed separately.
3. Positional display: `to_str` = `Point(1, 2)` (values in order), `to_debug` = `Point(x=1, y=2)`.
4. Rust-like: `to_str` = `Point(1, 2)`, `to_debug` = `Point { x: 1, y: 2 }`.

Recommendation: option 1; pick option 3 if `to_str` and `to_debug` must visibly differ for units.

### Q26: `#[derive(clone)]` - shallow or deep copy
B8c implements a shallow (memberwise) copy: `unit`/`unit!` fields are copied by value recursively, references (`type`, `list`, `dict`, closures) are shared, `unit`/`unit!` derive is a no-op.

Options:
1. Keep shallow (implemented): predictable, no cycle handling; a deep copy of a class needs a `clone` protocol on fields plus cycle protection. Recommended.
2. Deep copy: recursively clone class fields; a cycle is an error/panic.
3. Two protocols: `clone` (shallow) and `deep_clone` (deep).

Recommendation: option 1.

## Deferred work (owner request, 2026-10-27)

- **Examples refresh** (owner asked, then said "пока не делать"): provide examples covering `type`, inheritance, multiple inheritance, etc.; fix the existing examples against everything decided so far and report which files were changed and what exactly; add new examples. Survey done while recording: 41 `.kern` files under `examples/01..31`; all use the short `$field` form (no `$.`); inheritance is shown only as `@type` + `~fun` (`examples/03_units`, `22_events_patterns`) and single-parent `[A]` (`examples/23_native_inheritance`, `26_di`). No example exercises spec §2 multiple inheritance `type C(str name, i32 id)[A(name), B(id)]`, parent-ctor arguments `[A(x * 2)]`, `$[A].member` selection, or abstract members left abstract; no example shows `task<T>` async (see Q20).

## Answered questions (history) - no action needed

### Q1: M0 English specification review
The subagent runner was unavailable during M0, so the requested mechanic translation and reviewer spot-check could not be delegated. `docs/spec/kern.en.md` currently preserves the Russian source structure and all language-visible material but is not yet a complete English prose translation.

Options:
1. Complete the prose translation manually in short sections (recommended).
2. Temporarily keep the bilingual-preserving copy and defer translation review.
3. Restore subagent execution and delegate the translation/review before M0 closure.

Recommendation: option 1 if no subagent capacity becomes available; keep `kern.ru.md` unchanged as the owner copy.

### Q2: Short-circuit implementation timing (M1, 2026-10-21)
`&&`/`||` syntax is recognized and represented as `BinaryExpression`, but the current repository has no binder, code generator, bytecode instructions, or VM execution path. Short-circuit behavior is now specified: skip RHS for `false && rhs` and `true || rhs`.

Options:
1. Implement in the planned M4/M5 codegen/VM slices, with side-effecting RHS regression tests (recommended).
2. Add an interim AST node/flag in M1 and later lower it to control-flow instructions.
3. Implement a standalone syntax-tree evaluator before the planned binder/runtime milestones.

Recommendation: option 1; it places semantics at the execution layer and avoids duplicate throwaway execution machinery. Do not mark this behavior implemented until runtime tests prove RHS is skipped.

### Q3: Function-type compatibility (M2 conversions, 2026-10-22)
Spec §15 says conversions follow C#; C# delegates support variance (`Func<in T, out R>`), but Kern function types `fun(i32) -> str` have no variance marker in the spec.

Options:
1. Identity only in stage 1: parameter and result types must match exactly (recommended). Simple, predictable; lambdas are explicitly typed anyway.
2. Result covariance only (a function returning `Derived` fits `fun() -> Base`), parameters invariant.
3. Full C# delegate variance for reference types (parameters contravariant, result covariant).

Recommendation: option 1 now, revisit with the owner if typed HTTP/mocks need option 2. Impact if wrong: some lambda assignments need a wrapper lambda instead of direct passing; no runtime change.

### Q4: `collection<T>` root in the type model (M2, 2026-10-22)
DECISIONS §C makes `collection<T>` the root of `list/array/dict/set/...`, but the M2 type catalog (docs/design/M2-types.md §3) currently lists only `list/array/dict/set/result/task`.

Options:
1. Add `collection<T>` as a builtin generic root now, so conversions and indexing work before M6 natives land (recommended).
2. Wait for the native `KernCollection<T>` from M6 and model the root then; M2 only handles concrete builtins.

Recommendation: option 1; it costs one catalog entry and makes `collection<T>` usable in examples (`examples/06`, `examples/23`). Impact if wrong: a duplicate root definition would have to be merged at M6.

### Q6: Numeric widening — how far to follow C# (M2, 2026-10-22)
Spec §15 says conversions follow C#; §3 says smaller numeric types are promoted to larger ones **within a family** (that clause is about inferring collection element types). C# allows lossy implicit integer→float conversions (`i32→f32`, `i64→f64`, `u64→f32`), which the code now accepts.

Options:
1. Follow C# exactly, including lossy `iN→f32/f64` and `u64→f32` (current code, recommended for §15 consistency).
2. Forbid integer→float except where lossless (`i8/i16/u8/u16→f32`, `i32/u32→f64`), as the first draft of `docs/design/M2-types.md` said.
3. Forbid all implicit integer→float; require an explicit conversion.

Recommendation: option 1, because §15 and §16 line 782 both say "as in C#" and overload resolution depends on a single consistent table. Impact if wrong: implicit precision loss without a diagnostic; option 2/3 shrink the overload candidate set.

### Q7: Numeric literal suffixes beyond `b`/`f64` (M2, 2026-10-22)
Spec §3 lines 143–144 list only `5f64` and `5b`, but the lexer accepts `f16 f32 i8 i16 i32 i64 u8 u16 u32 u64` too and tests rely on them.

Options:
1. Keep the full suffix set and document it in §3 (recommended; it matches the type list and `docs/OPEN_QUESTIONS.md` #2).
2. Restrict to `b`/`f64` only and make other suffixes lexer errors (breaks current tests/lexer).
3. Keep the lexer accepting them but leave them undocumented (status quo — not acceptable long-term).

Recommendation: option 1; add one sentence to the spec and a `CHANGELOG_LANG` line. Impact if wrong: examples/tests using `5f32` would have to change.

### Q8: Lifted conversions into nullable targets (M2, 2026-10-22)
`i32? x = 5` clearly works when the types are identical. It is unclear whether `i64? x = 5`, `i64? y = some_i32`, and `Base? b = child` should convert, or require an explicit wrap.

Options:
1. Allow lifted conversion when `S -> T` is an implicit conversion (`i32 -> i64?`, `Child -> Base?`) (recommended, same as C#).
2. Allow only identity lifted (`T -> T?`); anything else is a type error.

Recommendation: option 1; it is what C# does and keeps nullable arithmetic ergonomic. Impact if wrong: extra type errors in otherwise reasonable code.

### Q9: `nil` assignable to `object` (M2, 2026-10-22)
`Conversions.IsNilAssignable` currently accepts both `T?` and `object`; spec §3 line 112 and §4 line 161 say `nil` is legal **only** for a nullable type.

Options:
1. Spec wins: `nil` only for `T?`; `object` is not nullable (recommended).
2. Treat `object` as nullable-compatible (`object o = nil` allowed), matching .NET reference semantics.

Recommendation: option 1. Impact if wrong: code storing `nil` in `object` needs `object?`/explicit nullable; runtime representation already distinguishes nil.

### Q10: `unit!` vs `unit` conversions (M2, 2026-10-22)
Spec defines `unit!` as immutable, but not whether `unit!` can be passed where `unit` is expected (or vice versa).

Options:
1. `unit!` -> `unit` allowed implicitly, `unit` -> `unit!` forbidden (recommended: immutable values are safe to copy into mutable slots, the reverse would break immutability).
2. No conversions between them in either direction.
3. Both directions allowed.

Recommendation: option 1. Impact if wrong: union/enum helpers taking `unit` would need explicit conversions; option 3 would let `unit!` values be mutated through aliases.

Status (2026-10-22): implemented per option 1 — `ConversionKind.UnitImmutableErase`, rank 4; spec §9 updated; `InheritanceConversionTests`. Owner can veto by reopening this item.

### Q11: Visibility of fields created by primary constructor parameters (M2, 2026-10-22)
Spec §8 line 388 and §9 line 430 say primary-constructor parameters automatically create the corresponding fields, but say nothing about their visibility. Spec §2 line 41 says fields default to `priv`, while the examples read/write those fields from outside the type: `examples/03_units/src/main.kern:51` (`b.x = 10.0`), `examples/09_collections_json` (`back.name`, `back.age`), `examples/22_events_patterns`.

Options:
1. Fields created by primary-constructor parameters are `pub` by default (recommended: keeps all examples valid without edits; they are part of the type's public shape, like a record).
2. They are `priv` like ordinary fields; then examples 03, 09, 22 must be changed (owner forbids changing examples).
3. The header may state visibility per parameter (`type Point(pub f64 x)`), which is new syntax not in the spec.

Recommendation: option 1; document it in §8/§9 and `CHANGELOG_LANG.md`. Impact if wrong: either examples stop binding or the language gains header modifiers (needs a separate decision).

### Q12: `match` diagnostics: codes and severity (M3-E, 2026-10-22)
The spec requires exhaustive `match` and forbids unreachable arms, but names no diagnostic codes or severity. The basic M3-E binder currently uses existing `KRN2102` (pattern type mismatch) and `KRN2109` (unsupported pattern); exhaustiveness/unreachable checks are deferred. The old `reference/` attempt used `KRN2201–2206`, which collide with the live codegen codes.

Options:
1. Reserve a dedicated block: `KRN2110` non-exhaustive (warning), `KRN2111` unreachable arm (warning), `KRN2112` pattern type mismatch (error), `KRN2113` malformed structural/constructor pattern (error), `KRN2114` unsupported pattern form (error); update `docs/design/M3-binder.md` (recommended).
2. Reuse `KRN2102`/`KRN2109` only; report coverage problems as warnings without stable codes until the spec defines them.
3. Use one `KRN2110` for all coverage problems, with severity/message distinguishing them.

Recommendation: option 1. Impact if wrong: diagnostics churn and tests need code renames; language syntax does not change. Owner can veto; until then no new codes are introduced.

### Q13: Nested unit-field stores under value semantics (M3-D, 2026-10-22)
`o.inner.x = 5` where `inner` is a `unit` field currently loads a copy of `inner`, stores `x` into the copy, and loses the write; `o.inner.x` still reads the old value. The spec says units are value types and does not spell out nested field stores.

Options:
1. Support copy-out/copy-back: load `inner`, mutate the copy, store it back into `o` (recommended for value semantics; may also be needed through local units).
2. Diagnose the form as unsupported (`KRN2109`) until nested value mutation is implemented; no silent loss.
3. Treat `inner` as a reference for member stores (violates the stated value semantics; not recommended).

Recommendation: option 2 now as the safety fix, then option 1 when M3-D properties/field initialization are finished. Impact if wrong: examples using nested units would need temporary locals or explicit reconstruction.

### Q5: `dict<K, V>` and the `collection<T>` root (M2 conversions, 2026-10-22)
DECISIONS §C makes `collection<T>` the root of `list/array/dict/set/...`, but `dict` has two type parameters and `collection` has one, so no element type can be derived without inventing a rule. Slice C therefore only implements `list<T>/array<T>/set<T> -> collection<T>` and `str -> collection<char>`; `dict` has no base for now.

Options:
1. Leave `dict` without a `collection<T>` parent in M2; M6 native hierarchy (runtime adapters) decides it (recommended).
2. `dict<K, V> -> collection<K>` (keys only); loses values.
3. `dict<K, V> -> collection<dict<K, V>>` (dict as its own element); artificial for iteration.

Recommendation: option 1. Impact if wrong: one conversion rule (and its rank) has to be added later; no code structure changes.

### Q14: Round 27 std API surface (M14, 2026-10-26)
The owner fixed the names in round 27, but not the exact members: `seq<T>` chains, how `frozen_*` values are created from mutable collections, the extra operations of `deque/priority_queue/sorted_dict/sorted_set/bitset/ring_buffer/lazy<T>`, and the functions of the new `kern.*` libraries. The spec currently says only “common collection protocol where applicable” and “exact members fixed during implementation”.

Options:
1. Design each API in its M14 slice from the common protocol, write the member list into the spec, and let the owner veto before implementation (recommended).
2. The owner provides the exact member lists now; the spec is updated before any code.
3. Implement only constructors/literals and the common protocol first; add specific operations on demand.

Recommendation: option 1. Impact if wrong: member names may change later, which is a language-visible break; no runtime or syntax redesign is implied.

### Q15: Static field initializers and `init` phases (M3-D, 2026-10-26)
Instance order is **decided by the spec** (§8 line 827): parent initializers → fields (including those introduced by primary-ctor parameters) → `$`-parameter auto-assignment → ctor body, and a default ctor with the same initializers is generated for types without ctors (line 828).
Open items (owner review 2026-10-26):
(a1) **statics are type members only** — §8 lines 435–437 define `#` as a member marker (`#type field`, `#fun method`) and the spec has no top-level `#`; top-level declarations are instead constants (`i32! limit = 5`, §3 line 168). Still unclear: do **mutable** top-level fields/globals exist at all (ROADMAP M3-F and `ModuleSymbol.Globals` assume yes), and if so, where do their initializers run?
(a2) **a static of `unit` type** — reading a `unit` field yields a copy, so `#Inner cache` + `Inner.cache.x = 5` would lose the write (the Q13 class of bug); an immutable `unit!` static has no such problem and is a natural place for constants;
(a3) when static initializers run: the spec only mentions library static init (§12 line 1109: "static initialization of a library runs before the program") and gives no order for a module's own static state;
(b) which phases may write `init`-only properties: the ctor body only, or also field initializers (line 236 says "only while the object is being constructed"; line 434 repeats it). C# (`init` setters may be assigned in ctor, object initializer, `with`, and the property initializer) and Kotlin (`val` initialised by a property initializer, an `init {}` block, or a primary-ctor parameter; secondary ctors must delegate `: this(...)`) both allow the initializer form.

Options:
1. (recommended) `#` statics run in declaration order in the module's `<static-init>`, once, before the module entry point (consistent with §12 line 1109); mutable top-level globals are **not** part of stage 1 — top-level state must be immutable (`i32! limit = 5`) or a `#` static; `unit!` statics allowed, mutable `unit` statics rejected with a clear diagnostic until Q13 lands; `init` writes allowed in instance field initializers, the ctor prologue and the ctor body, but not in static initializers or ordinary methods.
2. Same as 1, but keep mutable top-level globals (ROADMAP M3-F unchanged) and give them the same `<static-init>`; `unit` statics allowed with a documented copy-on-read rule.
3. Static initializers run lazily on first access to the type (C# static-ctor / Kotlin companion semantics); top-level globals dropped; `init` writes in the ctor body only.

Recommendation: option 1 — it keeps emission in one place (module startup) and matches "the same initializers" wording for the default ctor. Impact if wrong: static emission moves from module startup to first use, and one binder rule flips; no syntax change.

Current behaviour until this is answered (2026-10-26): a static field/property initializer is a hard `KRN2109` **error** — such fields used to be dropped silently, and a visible error was preferred to silent data loss. This means spec-valid declarations like `#i32 seed = 1` do not compile yet; they will as soon as this question is resolved (option 1 is the plan for the next M3-D slice).

### Q16: Diagnostic code for a constructor-chain cycle (M3-D2, 2026-10-26)
Spec §8 line 829 requires `: $(...)` chains and defines `KRN2190` for the *missing* chain, but says nothing about a cycle (`ctor A : $(...)` → B, `B : $(...)` → A). Such a module binds today and always panics at runtime by hitting the VM call-depth limit; the initializers never run. This is documented in `KNOWN_ISSUES.md`, but a compile-time diagnostic needs a code, and inventing one silently would collide with the reserved `KRN21xx` block.
Options:
1. New code `KRN2191` «цепочка конструкторов образует цикл» (error), detected on the chain graph at bind time (recommended: the spec already reserves `KRN2190` for the neighbour condition, so 2191 reads naturally and does not overload an existing meaning).
2. Reuse `KRN2190` for both missing and cyclic chains with different messages (no new code, but the spec text for 2190 only covers the missing case).
3. No compile-time check in stage 1: keep the `KNOWN_ISSUES.md` entry, let the runtime frame-limit panic be the behaviour.
Recommendation: option 1; the check is a small graph walk over `owner.Members.Constructors` and turns an "accepted but always panics" program into a normal type error. Impact if wrong: one code rename and message change in the binder and its tests; no syntax or runtime change.
