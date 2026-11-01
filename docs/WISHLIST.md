# WISHLIST — stage 2+ (do NOT start without the owner's explicit go-ahead)

Order is a suggestion; ask the owner which first. Round 27 additions are **not** stage 2: they are scheduled as M14 in `docs/ROADMAP.md`.
1. **External libraries** (separate projects in `libs/`, built with `libs/Kern.Libs.slnx` to `libs/_out/<name>/`, attributes identical to std, discovered by the builder):
   `kern.db` (+ `.sqlite/.postgres/.mysql`, mini-ORM `orm.table<T>`), `kern.ffi` (C/C++ DLLs: `native_lib`, `fn("strlen", "i64(str)")`), `kern.transpile` (Kern → C#/Kotlin, user mapping scripts), `kern.di` (both variants), `kern.backend` (router, middleware, controllers, JWT/sessions, WebSocket, SSE; needs `kern.di`).
   **Implemented 2026-11-02:** `kern.db*`, `kern.di`, `kern.backend*` (see `docs/LIBRARIES.md`); `kern.ffi` and `kern.transpile` remain.
   Designs are in `docs/spec/kern.en.md` rounds 13–21 and `docs/LIBRARIES.md`; the old attempt is in `reference/libs/` (read-only).
2. **UI** (own, not tied to Avalonia): desktop/mobile/web, React-like routing, widget hierarchy (`button/label/…` with a common base — native inheritance exists), Compose-style `state<T>` recomposition, images, grid, dialogs. Avalonia may be one backend. Needs a design round with the owner.
3. **Architecture examples**: mvvm, mvc, mvp, mvi, viper, state-machine — as `examples/arch_*`.
4. **Tooling**: LSP server, debugger hooks, package fetching + wrapper (M13). (`kernc test` is implemented 2026-11-01; `kernc wasm`, `kernc new`, `kernc doc`, `kernc run --watch` are M14, not stage 2.)
5. **Language**: `in/ref/out`, `unit` identity, inlining and dead-function elimination in the optimizer, exact `MaxStack`. (Generic unions are answered in round 27 / M14.)
