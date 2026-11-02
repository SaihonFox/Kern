# DECISIONS — everything the owner (Saihon) decided

Legend: **[FINAL]** decided by the owner · **[SUPERSEDED]** replaced later (do not implement) · **[DEFERRED]** owner postponed.
Language details live in `docs/spec/kern.en.md`; this file is the index of *why* and *what changed*. Newest decisions override older ones.

## A. Project process (agents era, 2026-10-18)
- [FINAL] Build **from scratch by the spec**; old code is `reference/` (copy piece by piece, only after verifying; see `KNOWN_ISSUES.md`).
- [FINAL] Instructions in English; **code comments and diagnostics in Russian**; no git required for agents.
- [FINAL] First pass scope = **core + std + CLI** (see `ROADMAP.md`). Stage-2 = external libraries, UI, architecture examples; LSP MVP implemented 2026-11-02.
- [FINAL] Correctness bar: **examples are golden tests + xunit tests**.
- [FINAL] When a decision proves bad → **stop and ask the owner** (`DECISIONS_NEEDED.md`), do not silently redesign.
- [FINAL] Permissions: agents may run `dotnet` and edit files in the project folder; anything else → ask.
- [FINAL] Reports: `STATUS.md`, `DECISIONS_NEEDED.md`, logs of build/tests in `logs/`, `CHANGELOG_LANG.md`.
- [FINAL] Platforms: Windows + Linux + macOS, with CI (template in `.github/workflows/ci.yml`). Owner develops on Windows, .NET 10 SDK.
- [FINAL] Budget ≈ 60M tokens total; agent decides how to spend (the budget file was removed in 2026-11-02 by the owner's request).

## B. Architecture (rounds 1–2)
- [FINAL] Bytecode + VM (stack machine); `kernc exe` = `.kbc` + VM host. Interpreter now; the Kern→C#/Kotlin translator is the external library `libs/Kern.Lib.Transpile` (implemented 2026-11-02).
- [FINAL] Reified generics: `typeof<T>()` and `typeof(str name)` coexist as overloads.
- [FINAL] Several csproj in one `Kern.slnx`; **namespaces match folder paths**; no flat folders with many files; no single `KernLang` namespace.
- [FINAL] Std binding: **C# classes with attributes** (`[KernType]`, `[KernMember]`, `[KernProperty]`, `[KernStatic]`, `[KernCtor]`, …) plus interop `use dotnet.X[...]`.
  FORBIDDEN: ExternRegistry style, `#[extern]` on every member, string registries like `"len.get"`, merging overloads/generics/return types. Real property/setter/overload/generic support is required.
- [FINAL] Console is always UTF-8 (the old bug: Russian text printed as `?`).
- [FINAL] Libraries beyond BCL (stage 2): web server/backend, DB/ORM (SQLite/PostgreSQL/MySQL), ui, ffi, transpiler, DI. They are **external** libs (not built into the language, no extra DLLs in kernc output). DB/ORM, DI, backend, FFI and transpiler are implemented in `libs/` (2026-11-02); UI remains planned. Std stage-1 still has xml/yaml/csv, json, regex, crypto, uuid, compression, http client, time, test+mocks.

## C. Language (see spec for syntax)
- [FINAL] `snake_case` for everything; short names (`len`, `is_empty`, `first`, `last`); prefer properties over getters (`stack.size`).
- [FINAL] No try/catch: `result<T,E>` + `panic` + `ret`. No `!` for result propagation (use `match` + `ret Err(...)`). `!` = non-nil assertion only.
- [FINAL] Unsuffixed numeric literals convert like C# constants (`u8 x = 5` ok, `300` error).
- [FINAL] `a in b` is an operator on `b`; class generic args are inferred from ctor args (`Box(5)` → `Box<i32>`).
- [FINAL] Secondary ctors chain to the primary: `ctor(i32 x) : $(x, 0)`; missing chain = `KRN2190`.
- [FINAL] No `enum` keyword: enum = union of units (`type Color = [Red, Green]`); with data = union of units with fields; match Rust-style (`Red => …`, `StreetAddress(city, street, house) => …`).
- [FINAL] No interfaces: abstract `@type` with `@fun`; `#[interface]` is only a marker for translation.
- [FINAL] Top-level declarations are `pub` by default; members default `priv`.
- [FINAL] Power operator `**` (right-assoc, `**=`); `await` is a prefix operator (`await a + await b` works); `list + list` concatenates.
- [FINAL] Custom iteration: `iter<T>` protocol (`~fun next -> T?`, nil ends) or legacy `iter() -> list/array/dict`; `async loop` uses `async_iter<T>` (`next() -> task<T?>`); `unit` `==` is structural; class `==` by reference unless `op ==`.
- [FINAL] Round 4: swap `a, b = b, a`; multi-declare `i32 a = 1, b = 2, c`; braces optional for single-statement bodies; missing return type = void (inference only for lambdas/operators/`fun f 5`); no `..` varargs in params (use list); local nested functions; lazy imports; getter-only `i32 b => a`; native attributes on fields/properties.
- [FINAL] `loop` replaces for/foreach/while: `loop var i in 0..5`, `loop cond body`, `loop { }` (infinite), do-while `loop { … }: cond`, `loop 0..5 body`; bare `loop i in …` assigns an existing variable. Results are lists of body values. `stop`/`next` work inside value expressions (SaveSp/RestoreSp in bytecode).
- [FINAL] `match` arms use `=>` (not `->`); guards `pattern if guard => …` and `Type n: guard => …`; structural patterns `Point(x: 0, y: var y)`; one-line `match x { 1 => "a"; _ => "b" }`.
- [FINAL] String prefixes: `f"…${x}"`, `r"…"` raw, `x"…"` regex literal (= `kern.regex.regex(…)`), `v"…"` verbatim; combos `fr`, `fx`, `fv`.
- [FINAL] `use T x = e` disposes (calls `dispose()`) on block exit incl. `ret/stop/next`, not on panic. The same mechanism is the **lock**: `use var g = m.guard()` (no `lock` keyword).
- [FINAL] Events/delegates merged without new keywords: `event<T>`, `signal` with `+=`/`-=`/`emit`; `observable<T>`; ordinary function types for single delegates.
- [FINAL] Attributes on properties/fields/params; **the compiler must emit attributes into bytecode** (types, functions, fields, properties) — reflection and DI depend on it.
- [FINAL] Unified collection interface: `collection<T>` root, inheriting derived from the C# class hierarchy for native types (`set<T>`, `queue<T>`, `stack<T>`, `observable<T>`); builtin `list/array/str/dict` get the parent by the compiler rule `Conversions.TryCollectionUpcast` (no separate `range` type). Kern types do NOT inherit native types.
- [FINAL] Reflection Kotlin+C# style at runtime (`kern.reflection`) **and** compile-time (`name_of<T>()`, `field_names<T>()`…); dynamic types (`reflect.define_type`).
- [FINAL] Macros (compile-time codegen): macro = Kern function over a description of the AST, expanded **as text before parsing**; v1: add members, generate types/functions, expression macros `f!(…)`, compile diagnostics.
- [DEFERRED] `in/ref/out`; identity semantics for `unit`. Generics for unions are **answered** in §J (round 27).

### New in the final decision round (2026-10-18/19)
- [FINAL] **`str * inum`, `char * inum` (→ `str`), `inum * str`, `inum * char`, `list<T> * inum` (→ `list<T>`)**; negative multiplier = `panic`; 0 → empty. (Replaces the old docs example `op i32.*(str)`.)
- [FINAL] **Top-level constants** via the immutable syntax `i32! x = 5` / `var! x = 5`. No `const` keyword.
- [FINAL] **`never` type**: `panic`, `exit`, `ret`, `stop`, `next` (and calls to `never`-returning functions) are divergent; a block ending in them needs no value; flow analysis treats following code as unreachable. (Reference had `panic` as non-never → `KRN2104`.)
- [FINAL] **Lambdas keep the explicit result type**: `fun(i32 n) -> i32 n * n`, `fun -> i32 { … }`, void block `fun(str s) { … }`. No inference of lambda result from the body.
- [FINAL] **Leading-dot chains**: a line starting with `.` continues the previous expression (`xs\n\t.map(…)\n\t.filter(…)`).
- [FINAL] Spec statement: no `:=` operator, no `const`.

## D. Concurrency (rounds 6, 23, 24)
- [SUPERSEDED] Isolated VM per thread with copied messages (round 6).
- [FINAL] **Shared-memory threads**: all threads see the same objects/statics/closures; each worker thread has its own interpreter (`Interpreter.Fork()`: shared Module/Info/types/statics, own scheduler+frames). Program is responsible for data races (as in C#/Java).
- [FINAL] Tools: `mutex` (re-entrant) + `use var g = m.guard()`, `try_guard(ms)`, `m.run(f)`, `rw_lock` (`read()/write()`), `amutex` (async lock, `await am.lock()`), `atomic`, `semaphore`, `channel<T>` (by reference), `shared<T>`; thread-safe `conc_list/conc_dict/conc_queue`; `parallel.map/each/for_range/reduce`; `thread.run(f)` → `task<R>` on a shared pool.
- [FINAL] Async: `task.all/any/race/settled/select/timeout/delay/run/retry/with_cancel`, `cancel_token` (cooperative, `child()` cascade), `pool(n)`, `pipe<T>(capacity)` (async channel inside one VM) + `pipe_stream`, `async loop`. Tasks complete only on the thread of their VM; worker results return via `Scheduler.BeginExternal/EndExternal`.

## E. Libraries (stage 1 inside core std)
- [FINAL] `kern.http` client (async, `result<http_reply,str>`) + typed client in **two variants**: generic `http_client.get<T>()` and attribute-based Refit-like proxy `#[get("/users/{id}")]`.
- [FINAL] `kern.test`: assertions + mocks (`mock.create<T>()` for abstract types, `returns/returns_for/handle/fail/verify/...`). `kernc test` runner is implemented (2026-11-01) for `#[test]` functions; the separate test task in `build.kern` remains `<target>:test`.
- [FINAL] `kern.time`: `time`, `duration`, `datetime`, `stopwatch`; `kernc run --time` prints program run time.
- [FINAL] `kernc fmt`: tabs + CRLF, comments preserved, token-checked; `kernc repl`; `-O0/-O1/-O2`.
- [FINAL] Compression: gzip/deflate/brotli, zip.

## F. `.kbc` and build system
- [FINAL] **`.kbc` libraries via `use`**: `kernc dll` embeds interface stubs (source with bodies replaced by `panic("kbc")`, `main` removed; functions without explicit return type keep bodies) as module attributes `kern.stub{path,module,text}`; consumers add stubs as sources and a **linker** copies the real code (design in spec "Раунд 25").
- [SUPERSEDED] Listing `.kbc` in `kern.toml`.
- [FINAL] **`kern.toml` is removed entirely.** Everything lives in the Gradle-like Kern builder `build.kern` (`docs/BUILDER.md`). A project without `build.kern` works by convention: `src/` + `main.kern`; `kernc run file.kern` works for a single file.
- [FINAL] Builder must support: `.kbc` deps and external Kern DLL libs (`kern.db`, …), compiler settings, profiles + environment variables, multi-project (`include`). Package fetching by name: **both** forms wanted — local paths first, then minimal URL+hash packages and a wrapper-like pinned launcher (stage 1.5/2).
- **[FINAL, SUPERSEDED 2026-10-21]** Builder DSL originally allowed trailing lambda callbacks (`w.target("app") fun (target t) { … }`). The current language form places callbacks inside the call argument list: `w.target("app", fun (target t) { … })`; no trailing-lambda syntax is accepted.
- [FINAL] CLI scope stage 1: `run / check / test / build / dis`, `exe / dll`, workspace via `build.kern`, `fmt`, `repl`. `test` runs `#[test]` functions (added 2026-11-01).

## G. UI, architecture examples, tooling (stage 2)
- [DEFERRED] UI is NOT in the first pass. Wishes: own UI base (not Avalonia-only), desktop + mobile + web, React-like routing, widget hierarchy (`button/label/…` with a common base), Compose-style state. Avalonia may be one backend.
- [DEFERRED] Architecture examples: mvvm, mvc, mvp, mvi, viper, state.
- Transpiler `libs/Kern.Lib.Transpile` (Kern → C#/Kotlin, user mapping scripts) implemented 2026-11-02; LSP MVP implemented 2026-11-02 (`src/Kern.Lsp`, `docs/LSP.md`). DB/ORM (`kern.db*`), DI (`kern.di`), backend (`kern.backend*`) and FFI (`kern.ffi`) are implemented 2026-11-02. Remaining stage-2: UI and architecture examples.
- Design of stage-2 libraries is documented in `docs/spec/kern.en.md` (rounds 13–21) and `docs/LIBRARIES.md`.

## H. Owner preferences (apply always)
- Re-verify **all examples** after every language change. Ask clarifying questions (with options) instead of guessing, even on small points.
- Keep `docs/spec/kern.en.md` updated with what is learned; every delivery includes the updated spec.
- C# style: `[]` collection expressions, proper namespaces, no flat folders with many files; tabs + CRLF in all source files incl. `.md`.

## I. Last clarifications (2026-10-19)
- [FINAL] **Indexing**: `xs[i]`, `s[i]`, `d[k]`; **slices** `xs[1..3]`, `s[1..=3]` return new list/str; **negative index counts from the end** (Python-like: `xs[-1]` is the last element); out-of-range = panic.
- [FINAL] `str.len` counts **UTF-16 code units** (as in C#).
- [FINAL] `decimal` and `bigint` are **native std types** in stage 1 (arithmetic, comparison, `<=>`, to/from str, conversions).
- [FINAL] **Binary data**: add `array<u8>` / `list<u8>`; compression/crypto/file APIs accept and return bytes; keep convenience helpers (base64/hex/utf8 encode/decode).
- [FINAL] **Shadowing is allowed**: a nested block may redeclare a name from an outer scope (compiler emits a warning, not an error).
- [FINAL] The spec is translated to **English in M0** by `mechanic` (Russian original kept as `docs/spec/kern.ru.md`); reviewed by `reviewer`. From then on the English file is canonical for agents; the Russian copy is regenerated for the owner at M12.
- [FINAL] **GitHub + CI (Actions on Windows/Linux/macOS) wanted at the end of stage 1.** Agents still do not run git; the owner pushes. Activate `.github/workflows/ci.yml` at M12.
- Owner's provider: only an API key `sk-…` is known (no base URL, no model list) → use `tools/probe-provider.mjs` (see `SETUP.md`) to discover endpoint style and model ids.

## J. Round 27 additions (2026-10-26)
- [FINAL] **Tail recursion:** only direct self-recursion in tail position is guaranteed; the VM must keep a constant stack for it. Mutual recursion and non-tail calls are not optimized.
- [FINAL, 2026-11-01] **Diamond inheritance:** one shared ancestor. In `type C()[A, B]` where `B[A]`, the fields of `A` exist once in `C` and `A` is initialized once; initialization order is direct parents in declaration order with every ancestor before its descendants (topological, de-duplicated). Selectors `$[A]member` and `$[B]member` reach the same shared `A` member.
- [FINAL] **`with`:** `x:{ a = 1, b = 2 }` returns a copy with named fields replaced for both `unit` and `type`; the original is unchanged; nested `with` is allowed; no new keyword.
- [FINAL] **Loop labels:** `loop@outer ...`, `stop outer`, `next outer` work for every `loop` form; labels are lexical, may target any enclosing labeled loop, and duplicates are errors.
- [FINAL] **Generators:** a function declared `-> seq<T>` that contains `yield` is lazy; calling it returns a sequence whose body runs during iteration; `yield v` emits one element. `seq<T>` supports lazy chains.
- [FINAL] **`is` patterns:** the right side of `is` accepts the full `match` pattern grammar; bindings are visible to the right of `&&` and in the then-branch, not after `||` or negation.
- [FINAL] **Generic sums:** `type Option<T> = [Some<T>, Nothing]`; variants are separately declared units (`unit Some<T>(T value)`, `unit Nothing`); the alias only lists them and does not create constructors or a nominal type.
- [FINAL, revised 2026-11-01] **`result` and the `ok`/`error` markers:** `result<T, E>` takes any types (union of the built-in variants `Ok<T>`/`Err<E>`; constructors `Ok(v)`/`Err(e)`; patterns unchanged). The labelled form `result<T[ok], E[error]>` is the **constrained** form: `[ok]` requires `T` to inherit the marker base type `ok`, `[error]` requires `E` to inherit the marker base type `error`; user variants are written `type SomeOK[ok]` / `type SomeError[error]`. Labels are allowed only in `result` arguments (first must be `ok`, second `error`); `ok`/`error` without arguments are the marker types, while `ok<T>`/`error<E>` remain aliases of the built-in variants. This revises the 2026-10-26 note that labels were mere parameter labels.
- [FINAL] **`span<T>`:** mutable, stack-only view like C# `Span<T>`; not stored in fields/closures; copies share elements. Frozen collections are `frozen_list<T>`, `frozen_dict<K,V>`, `frozen_set<T>`.
- [FINAL] **Data structures:** `deque<T>`, `priority_queue<T>`, `sorted_dict<K,V>`, `sorted_set<T>`, `bitset`, `ring_buffer<T>`, `lazy<T>`; where applicable they follow the common collection protocol.
- [FINAL] **Libraries:** `kern.process`, `kern.path`, `kern.net`, `kern.log`, `kern.cli`, `kern.config`, `kern.ansi`, `kern.time` (including time zones), plus separate `kern.toml`/`kern.ini`. Implemented 2026-11-02: `kern.process`, `kern.path`, `kern.log`, `kern.cli`, `kern.config`, `kern.random`, `kern.auth`; still planned: `kern.net`, `kern.ansi`, `kern.toml`, `kern.ini`, time zones.
- [FINAL] **Nesting protection:** both compiler and VM limits; defaults 256 levels and 10,000 frames; both are configurable; exceeding a limit is a diagnostic/panic, not a process crash.
- [FINAL] **Documentation:** `///` is Markdown attached to a declaration; fenced `kern` code blocks are doctests (compile + run; exact output checked only with a `// expect:` marker); `kernc doc` emits Markdown.
- [FINAL] **Tooling:** `kernc wasm` targets WASI; `kernc new app|lib|service` creates conventional projects; `kernc run --watch` rebuilds and restarts on changes (not hot module reload).
- [FINAL] **Sources/literals:** a first-line `#!...` shebang is skipped; numeric literals accept `_` separators (`1_000_000`). Both are implemented.
