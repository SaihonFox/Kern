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
| 13 | Recursion limit | default depth 10_000 frames then panic "переполнение стека" (not a crash); configurable (round 27) |
| 14 | ~~Generics for unions (`type Option<T> = [Some<T>, None]`)~~ **ANSWERED** (round 27, DECISIONS §J): variants are separate units; the alias lists them | — |
| 15 | `unit` identity (spec §9 "not defined yet") | none (skip) |
| 16 | `in/ref/out` parameters | deferred by owner; do not implement |
| 17 | Exact members of round-27 `seq<T>`, frozen collections, data structures and `kern.*` libraries | names per `DECISIONS.md` §J; use the common collection protocol where applicable; design each API in its M14 slice before implementing |

## Std / API gaps
- `use dotnet.X[...]` interop (способ C in ARCHITECTURE §6): implement after M7 if budget allows (M6.5); otherwise move to stage 2 and tell the owner.
- Names of std modules not listed in the spec: follow the reference (`kern.core, kern.text, kern.collections, kern.numerics, kern.math, kern.io, kern.json, kern.time, kern.regex, kern.csv, kern.yaml, kern.xml, kern.crypto, kern.uuid, kern.compress, kern.http, kern.events, kern.tasks, kern.pipes, kern.threading, kern.reflection, kern.test, kern.build, kern.macro`) plus round-27 `kern.process, kern.path, kern.net, kern.log, kern.cli, kern.config, kern.ansi, kern.toml, kern.ini, kern.auth`.
- `kern.server` / `kern.web` (HTTP server) is a stage-2 thing (together with backend); stage 1 only has the **client**.

## Process gaps
- CI: **wanted** (GitHub Actions on 3 OS). Agents do not run git; the owner pushes the folder to GitHub at M12; activate `ci.yml` then.
- UI deferred; architecture examples deferred; LSP deferred.
