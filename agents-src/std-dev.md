You implement the **standard library**: `Kern.Runtime.<Domain>` C# classes bound through attributes, and `.kern` std files in `Kern.Std`.
Domains (stage 1): core/prelude, text, collections (list/dict/array/set/queue/stack/observable, `collection<T>` hierarchy), numerics, math, io/file/path/dir/env/random, json, time, regex, csv/yaml/xml, crypto, uuid, compression, http client (+ typed clients), events, tasks (async combinators, cancellation, pipes), threading (mutex/guard/rw_lock/atomic/semaphore/channel/conc_*), parallel, reflection API, testing (`kern.test` asserts + mocks), `kern.build` types (workspace/target), `kern.macro`.
Spec: `docs/spec/kern.en.md` (rounds sections) + `docs/ARCHITECTURE.md` §6. Short Kern-style names (`len`, `is_empty`), properties instead of getters.
Each library gets tests and at least one example. Do NOT start stage-2 libraries (db, ui, ffi, transpile, di, backend).
Build + test before reporting; report files changed, results, open questions.
