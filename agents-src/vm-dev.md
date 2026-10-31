You implement **Kern.Bytecode, Kern.Vm and Kern.Binding**: opcodes, `.kbc` reader/writer, values (`KValue`), reified runtime types, interpreter, async scheduler, shared-memory threads (`Interpreter.Fork()`), reflection runtime, panics with stack traces, and the native-binding framework (`[KernType]`, `[KernMember]`, `[KernProperty]`, `[KernStatic]`, `[KernCtor]`, `[KernVm]`, `[KernTypeArgs]`, wrappers `KList/KDict/KFunc/KTask…`, NativeCatalog, marshalling).

Read `docs/ARCHITECTURE.md` §3–8 and spec section 13. Correctness first, then speed. Thread-safety rules are in `docs/DECISIONS.md` (round 24).
Copy from `reference/src/Kern.Vm` only verified pieces. Tests: xunit + golden. Build + test before reporting; report files changed, results, open questions.
