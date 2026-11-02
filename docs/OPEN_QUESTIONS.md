# OPEN_QUESTIONS — known gaps. Proceed with the **default** unless the owner answers otherwise; log the choice in `CHANGELOG_LANG.md` and `DECISIONS_NEEDED.md` (so the owner can veto).

## Language gaps (the spec is silent or vague)
| # | Question | Default to implement |
|---|---|---|
| 1 | ~~Indexing/slicing~~ **ANSWERED** (DECISIONS §I): slices yes, negative index from the end | — |
| 2 | Integer overflow / division by zero | wrap like C# `unchecked` for `+ - *`; `/` and `%` by zero = panic "деление на ноль"; `i64.min / -1` = no crash |
| 3 | ~~`decimal`/`bigint`~~ **ANSWERED**: yes, native std types | — |
| 4 | ~~Byte buffers~~ **ANSWERED**: `array<u8>`/`list<u8>` + helpers | — |
| 5 | `unit` as dict key / set element (hash) | structural hash for `unit`/`unit!`; classes by reference unless `op ==`+`hash()` defined |
| 6 | `main` return value / exit code | `fun main` returns void → exit 0; `exit(code)` ends process; panic → exit code 1 with trace on stderr |
| 7 | Process end while tasks pending | main finishes → program exits; pending tasks are dropped (document it) |
| 8 | `init` property semantics and `{ get; init }` | settable only in constructor/initializer context; error `KRN…` otherwise |
| 9 | String escapes (`\n \t \" \\ \u{1F600}`) | C#-like plus `\u{…}`; `len` counts UTF-16 units (**answered**) |
| 10 | Format spec in interpolation `${x:08d}` `${x:.2f}` `${x:<10}` | support align `< > ^`, width, fill, `d x X b o f e` , precision; invalid = compile error when constant |
| 11 | `a == b` between different numeric types | numeric promotion then compare; `i32 == f64` allowed |
| 12 | ~~Shadowing~~ **ANSWERED**: allowed (warning) | — |
| 13 | Recursion limit | spec says default depth 10_000 frames and configurable; code currently uses `Interpreter.MaxCallDepth = 65_536` and it is not configurable (see `KNOWN_ISSUES.md`) |
| 14 | ~~Generics for unions (`type Option<T> = [Some<T>, None]`)~~ **ANSWERED** (round 27, DECISIONS §J): variants are separate units; the alias lists them | — |
| 15 | `unit` identity (spec §9 "not defined yet") | none (skip) |
| 16 | `in/ref/out` parameters | deferred by owner; do not implement |
| 17 | Exact members of round-27 data structures (`span<T>`, `frozen_*`, `deque`, `priority_queue`, `sorted_dict`, `sorted_set`, `bitset`, `ring_buffer`, `lazy<T>`) and the remaining `kern.*` libraries | names per `DECISIONS.md` §J; design each API in its M14 slice before implementing. `seq<T>` is done (2026-11-02) |

## Std / API gaps
- `use dotnet.X[...]` interop (способ C in ARCHITECTURE §6) is **not implemented**; no M6.5 slice was taken.
- Names of std modules not listed in the spec: follow the reference (`kern.core, kern.text, kern.collections, kern.numerics, kern.math, kern.io, kern.json, kern.time, kern.regex, kern.csv, kern.yaml, kern.xml, kern.crypto, kern.uuid, kern.compress, kern.http, kern.events, kern.tasks, kern.pipes, kern.threading, kern.reflection, kern.test, kern.build, kern.macro`) plus round-27 `kern.process, kern.path, kern.net, kern.log, kern.cli, kern.config, kern.ansi, kern.toml, kern.ini, kern.auth, kern.random`.
- `kern.server` / `kern.web` names do not exist; the HTTP server is the external `libs/Kern.Lib.Backend` (`kern.backend` + `kern.backend.server.*`). The client is `kern.http`.

## Process gaps
- CI: GitHub Actions runs on Windows/Ubuntu/macOS (`.github/workflows/ci.yml`); fixes for the first runs are in `66fcc80` (canceled-task completion, `check-style` path separators, `.gitattributes` CRLF).
- UI and architecture examples are still deferred; LSP MVP is implemented (`docs/LSP.md`).
