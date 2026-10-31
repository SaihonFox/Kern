# reference/ — OLD ATTEMPT (read-only, never compiled)

Written without a .NET SDK over 25 "rounds". It contains real design work (see `docs/ARCHITECTURE.md` and `docs/spec/kern.md`) but also many compile errors and semantic bugs
(`docs/KNOWN_ISSUES.md`). Use it to **find ideas and copy small pieces after verifying them in the new solution**; never copy whole projects blindly.

Layout: `src/` (core, bytecode, vm, binding, runtime, compiler, std, cli, tests), `libs/` (stage-2 external libraries), `tools/` (old audit scripts).
Parts that depend on `kern.toml` (`Kern.Cli/Build`, `Kern.Cli/Kbc/KbcConfig.cs`, `ProjectLoader`) must be redesigned around `build.kern` (`docs/BUILDER.md`).
Already included there: `str/char/list * inum` repeat operator (binder + VM), attribute emission for fields/properties in `CodeGenerator`.
Search it with grep (`rg "class Binder"`), do not read it wholesale.
