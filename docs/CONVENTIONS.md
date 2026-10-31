# CONVENTIONS

## Solution layout (stage 1)
```
Kern.slnx  Directory.Build.props  .editorconfig  .gitattributes
src/
  Kern.Core/        Kern.Core.*          source files, spans, diagnostics
  Kern.Bytecode/    Kern.Bytecode        opcodes, KbcModule, defs, type signatures, reader/writer
  Kern.Vm/          Kern.Vm.{Values,Types,Execution,Async,Reflection}
  Kern.Binding/     Kern.Binding.{Attributes,Catalog,Wrappers,Marshalling}
  Kern.Runtime/     Kern.Runtime.<Domain>   one subfolder + namespace per domain (Text, Collections, Json, Time, Threading, Tasks, …)
  Kern.Compiler/    Kern.Compiler.{Lexing,Syntax,Parsing,Semantics,CodeGen,Optimization,Formatting}
  Kern.Std/         embedded *.kern std files (std/**)
  Kern.Cli/         Kern.Cli.{Commands,Build,Kbc,Macros,Infrastructure}
  Kern.Tests/       Kern.Tests.{Harness,Parsing,Binding,Execution,Examples,…}
examples/  docs/  tools/  logs/  reference/ (read-only)
libs/   (stage 2 only)
```
Dependencies only point downward: Core ← Bytecode ← Vm ← Binding ← Runtime; Compiler → Core, Bytecode, Binding; Cli → all; Tests → all.

## C# style
- Tabs, CRLF, file-scoped namespaces, namespace == folder path (checked by `tools/check-style.mjs`).
- Collection expressions `[]`, pattern matching, `required`/`init`, records for immutable data, no `var` abuse where the type is not obvious.
- One domain = one folder; no folder with > ~15 files; split files > ~600 lines (partial classes by concern are fine: `Binder.Patterns.cs`).
- Public API of std natives follows the attribute rules in `ARCHITECTURE.md` §6; Kern names are snake_case.
- No `static` mutable state shared across compilations; no `Console.Write` outside CLI/natives for print.
- Comments in Russian, concise (why, not what). Diagnostics in Russian with codes `KRNxxxx` (semantic) and `PARxxx`/`LEXxxx` (syntax).

## Kern style (for examples/std `.kern`)
Tabs, `snake_case`, 4-wide tab display, no semicolons, `f"…"` for interpolation, `fun main {}` without parentheses when no args.

## Diagnostics
Stable codes; one place (`Krn.cs`) lists code → message template. Errors carry spans; warnings can be promoted by `warnings_as_errors`. Never throw for user errors — report diagnostics; panics are only for runtime Kern panics.

## Performance baseline
Interpreter loop: avoid allocations per instruction; numeric fast paths for `i32`. Don't optimise before the golden set passes.
