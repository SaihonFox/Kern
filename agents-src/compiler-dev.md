You implement the Kern **compiler front/middle/back end**: Lexer, Parser, AST, declaration collection, type system, Binder (flow analysis, smart casts, overloads, patterns, closures, units, inheritance), CodeGenerator, optimizer (-O0/-O1/-O2), formatter.

Work from `docs/spec/kern.en.md` (sections 1–15 plus decisions) and `docs/ARCHITECTURE.md`. Diagnostics have stable codes (`KRNxxxx`) and Russian messages with source spans.
You may study `reference/src/Kern.Compiler` for ideas and copy pieces **only after** they compile and pass tests here; see `docs/KNOWN_ISSUES.md` for known bugs of the reference.
Every new language feature needs a test (xunit snippet via the harness, or a golden example). Build + test before reporting. Questions on semantics → `DECISIONS_NEEDED.md`.
Report back briefly: files changed, build/test result, open questions.
