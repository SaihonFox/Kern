# KNOWN_ISSUES — lessons from the old attempt (`reference/`) and traps to avoid

The reference code (~35k lines C#) was written **without ever being compiled** (the author had no dotnet). The owner built it on Windows in places and pasted errors.
Treat reference as a source of *ideas and structure*, not as truth. Below: every defect known so far, plus design traps.

## A. Compile-time traps (C#)
1. `Kern.Runtime.Math` namespace shadows `System.Math` inside `Kern.Runtime.*` → write `System.Math.Max(...)`.
2. Missing `using Kern.Runtime.Core;` for `Kv` (helpers `Kv.Of/As/Last/Strs`) in several files; missing `using Kern.Compiler.Semantics.Bound;` in the transpiler lowerer.
   Lesson: build early and often; do not trust "looks right".
3. In `Kern.Compiler` these names collide: `Syntax.{TypeKind, MemberFlags, FuncType}` vs `Bytecode.{TypeKind, MemberFlags}` vs `Semantics.Types.FuncType` → use `using Alias = …`.
4. `MacroData.cs` had warning CS9124 (primary-ctor parameter captured and also stored) — avoid capturing primary-ctor params into fields twice.
5. IDE0290/IDE0330 are only suggestions (primary ctor, `System.Threading.Lock`); not errors.
6. `ModuleMap.cs` maps C# namespaces → Kern modules; the namespace `Kern.Runtime.Math` exists, so name new C# namespaces carefully.

## B. Semantic bugs observed when the owner ran the examples
1. **`len` missing on `list<T>` / `dict<K,V>` (KRN2003)**: `len` was an *extension property* implemented as a generic static C# method on `KList<T>`; member lookup failed to unify the receiver with generic parameters.
   Design fix: first-class native members for builtin generics, not "extension with unification". Use `KERN_TRACE_MEMBERS=1`-style tracing in your new lookup code.
2. `contains` ambiguity (KRN1402) between list/collection overloads: make overload ranking prefer the more specific receiver (`list<T>` over `collection<T>`).
3. Conversions to `collection<T>` failed for `set/list/str` arguments and `streamwriter`→`stream` (KRN1404/KRN2001): the collection hierarchy conversion must be in `Conversions` and in generic inference.
4. `regex` type was not found when a program used `x"…"`/`use kern.regex[regex]` (KRN1001) — module/type registration gap.
5. **Attributes on properties/fields were never written into bytecode**, so DI `#[inject]` and reflection saw nothing (runtime panic "call of method now via nil"). Emit attributes for all members.
6. **`panic("…")` as the last statement of a function with a return type gave KRN2104 ("missing return") because `panic` was not `never`. Decision: `never` type (see spec).
7. Lambda result type: `fun(i32 x) -> x * k` parsed `x` as the result type. The language requires `-> Type` before the body: `fun(i32 x) -> i32 x * k`.
8. Example 31 (`kbc`) reported "module 'mathx.vec' not found" simply because `mathx` had not been built as a `.kbc` first — the build order must be explicit (builder task or clear error message).
9. Old examples used things the language does **not** have: `:=`, `const`, leading `.` chains (now **added** by decision), `panic` as non-diverging.
10. Console encoding: Russian printed as `?` until UTF-8 was forced before first write.
11. SQLite native loading from a library folder needed `ResolvingUnmanagedDll` handling and `SQLitePCL.Batteries_V2.Init()` (stage 2).
12. `.NET` exceptions inside natives must become Kern panics with the inner-exception chain in the message.

## C. Design traps
- **Thread safety**: `Interpreter.Fork()` shares Module/Info/types/statics. Lazy caches in shared structures need locks (enum value cache, type factory, reflection `AddType`). KTask/Scheduler belong to the owner thread only; results come back via `Scheduler.BeginExternal/EndExternal`. `await` only on the thread that owns the task.
- `static` mutable state in the compiler/catalog (e.g. `NativeType.CollectionRoot`) breaks parallel xunit tests: avoid statics or make the harness serial.
- Native generics: a bare `T` in a C# signature must not be treated as `T?`; only explicit `T?`/`[Nullable]` means nullable. Reified type args via `[KernTypeArgs] KType[]`.
- `list<T>` element type is `object` for `snapshot()/keys()/values()` of generic native classes — pass element types explicitly.
- Do not use `MethodInfo.Invoke` on hot paths (compile delegates/expression trees).
- Text-expansion macros shift line numbers; keep a line map so diagnostics point to the original source.
- `.kbc` linker matches stubs by key; generated constructors and `<static-init>` have no source spans — design stable keys for them from day one; type matching by simple name conflicts for same-named types (decide a qualified key).
- `kbc` cache hash must include `.kbc` dependencies.
- Peephole optimizer must not merge an instruction that is a jump target with its predecessor; recompute line tables.

## D. Things that were only designed (never run): treat with extra care
Threads/`conc_*`/`parallel.*`, `.kbc` stubs+linker, macros, compile-time reflection, `define_type`, typed HTTP proxy, mocks, builder hash-skip, formatter token verification, `-O2`.

## E. Current open gaps (updated 2026-11-04)

This section lists only **current** gaps. Items that were fixed earlier are recorded in
`CHANGELOG_LANG.md` / `STATUS.md` and are not repeated here.

### Compiler / language
- **Dict key hashability is checked only at literal binding.** `dict<K, V>` declarations, native constructors and non-literal paths do not re-check `K`.
- **Derived `hash`/`compare`/`clone` are not materialized as members.** They are applied by the protocol binder (`ValueHashFields`/`ValueCompareFields`/`CloneObject`) and do not appear in `MemberTable`, `.kbc` or reflection.
- **Recursive local functions require an explicit signature.** A local function with inferred return type cannot call itself while its body is bound.
- **Expected-type-driven literal kinds reach only some positions.** Local initializers, assignments and the final expression forward the hint; `if`/`match` branches and block expressions do not.
- **`is` constructor/structural patterns require `unit` types** (same restriction as `match`); bindings under `!is`/left of `||` are rejected.
- **`with`:** `unit!` is rejected by design; inside `[...]` a `:` starts a dictionary entry, so a with-expression as a list element needs a local variable first.
- **Named tuple literals `(x = 1, y = 2)` do not exist** (DECISIONS_NEEDED Q27).
- **`array<T>[...]` prefix syntax is intentionally not supported.** The approved form is `<T>[...]` with an expected `array<T>`.

### `use dotnet.X[...]`
- Open generic BCL types (`List<T>`, `Dictionary<K,V>`, …) are skipped.
- `params` is imported as an explicit `array<T>` parameter; C#-style variadic call syntax is not supported.
- by-ref/`out` parameters and delegates are not imported.
- Members whose return/parameter types are neither primitives nor the imported owner type are skipped.
- NativeAOT invokes imported members through `MethodInfo.Invoke` (no compiled delegates yet).

### Std / libraries
- **`kern.security`:** no BLAKE3, Ed25519 or argon2/bcrypt (not in BCL; own implementation is XL and needs crypto review).
- **`kern.num`:** `eigen_symmetric` handles real symmetric matrices only (Jacobi), `qr` requires rows >= columns; no sparse matrices or dedicated rational/vector types.
- **`kern.toml`:** no multiline basic/literal strings and no full date/time type model.
- **`kern.parse`:** only the basic builders/diagnostics listed in spec; no parser-combinator/PEG API.
- **`kern.regex`:** no `replace` with a function argument.
- **`kern.reflection` / `define_type`:** supports generics and parents, but dynamic types have no methods or property metadata; `type.fields` lists own fields only.
- **M7 runtime limits:** ordinary `list`/`dict` are not thread-safe (use `conc_*`/`mutex`); `conc_*.snapshot()/keys()/values()` return `list<object>`; a blocking `worker.join()` inside async stops the event loop; proxies (`mock`, `http_client.proxy`) cover abstract `@fun`s only; `mock` matches arguments by value equality (no matchers).
- **FFI:** cdecl only; no callbacks/structs/arrays/by-ref; returned pointers are not freed automatically.
- **DB/DI:** ORM stores non-primitive fields as JSON (no migrations/relations); PostgreSQL placeholders differ; `Kern.Lib.Di` factories need explicit lambda result types.
- **Backend:** `HttpListener`-based; rate limits/SSE replay are in memory; no static-file compression; controllers bind primitive parameters by `kern.param_names`.
- **`kern.ui` (Avalonia)** is absent — stage 2, needs owner approval and an external dependency.
- **`kernc wasm` (NetWasm)** is not implemented — stage 2, plan in `DECISIONS_NEEDED.md`.

### Tools / runtime
- **Capabilities:** `fs.read("scope")`/`fs.write("scope")` scopes are parsed but only capability names are enforced; package `permissions` are metadata only.
- **Profiler:** now reports inclusive and self time, but no call tree; async frames that interleave across `await` may attribute child time imperfectly.
- **Debugger:** synchronous only (no attach/remote/expression evaluation); `.kbc` stores no local names, so `locals` shows `paramN`/`slotN`; step pauses on the next bytecode instruction.
- **Optimizer:** `-O2` inlines `#[inline]` functions with branches/loops/multiple `ret`; async/generator/ctor/memo/native/ref stay calls; dead-function elimination keeps type methods and `Pub`/`#[test]` functions.
- **`build --native`:** some programs fall back to a self-contained single-file host (generic natives, external DLLs, `use dotnet`, reflection, foreign RID); needs the source tree and C++ toolchain.
- **`kernc test`:** async tests are pumped now, but tests with an implicit receiver (methods) are not supported.
- **Modules:** only one module-level static initializer is supported (`KRN1014`); native module functions are global (a std module must not reuse a common name); source modules have no qualified `mod.member` access (DECISIONS_NEEDED).
- **Project mode / `.kbc`:** library and consumer types with the same simple name conflict in `kernc dll`.

## F. Files in `reference/` that are *not* part of stage 1
`reference/libs/*` (old attempt) — design material only. The current `libs/` (Db, Di, Backend, Ffi, Transpile) is implemented; only `Kern.Lib.Ui` is still missing.
`reference/src/Kern.Cli/Kbc` and `Build` depend on `kern.toml` — redesign for `build.kern`.
`reference/src/Kern.Tests/Examples/ExamplesCompileTests.cs` enumerates `kern.toml` files — replace with convention-based discovery.
