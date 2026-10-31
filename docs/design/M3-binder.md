# M3 — binder and typed bound tree

Goal: turn the M2 syntax/declaration layer into a **typed bound tree**: every expression and statement is
checked against the type system, every name resolves to a symbol or a local slot, and language rules produce
`KRN2xxx` diagnostics instead of cascading errors.

## Pipeline position

```
Syntax CompilationUnit -> SymbolTable + DeclarationCollector (M2) -> Binder (M3)
    -> BoundModule -> CodeGenerator (M4) -> .kbc -> VM (M5)
```

The binder never mutates syntax, module symbols, or `KernType` instances. It returns a fresh bound tree plus
diagnostics; `ErrorType` is used as a recovery type so one mistake does not produce a cascade.

## Layout

- `Semantics/Bound/` — immutable bound nodes (`BoundExpression`, `BoundStatement`, `BoundFunction`,
  `BoundModule`). Nodes carry resolved `KernType`, constants and the resolved `FunctionSymbol`.
- `Semantics/Binding/` — `Binder`, `BoundScope`, `BoundVariable`, built-ins, operator/binary rules.
- Tests live in `src/Kern.Tests/Semantics/Binding/`.

## Scoping and variables

- Function scope starts with parameters (slots `0..n-1`), then locals; nested blocks create child scopes.
- A local is a `BoundVariable` with type, slot, mutability and definite-assignment state.
- `var! x` / `T! x` is single-assignment: a second assignment is `KRN2105`.
- Reading an unassigned local is `KRN2105` as well (later refined by flow analysis).
- Shadowing in a nested scope is allowed; same-scope redeclaration is `KRN2106`.

## Diagnostics (M3 slice A)

| Code | Meaning |
|---|---|
| `KRN2101` | incompatible operand types / unsupported operator |
| `KRN2102` | type mismatch (initializer, assignment, argument, condition, return) |
| `KRN2103` | unknown name or function |
| `KRN2104` | missing return / function body has no value of the declared type |
| `KRN2105` | assignment to immutable or unassigned read |
| `KRN2106` | invalid assignment target or duplicate local |
| `KRN2107` | wrong `main` signature |
| `KRN2108` | call shape not supported by the current slice (defaults, named arguments producing defaults) |
| `KRN2190` | secondary ctor of a type with a primary ctor has no `: $(...)` chain |
| `KRN2191` | constructor chain closes into a cycle that cannot reach a non-chaining constructor |

`KRN2104`, `KRN2190` and `KRN2191` already exist in the spec; the rest are introduced by M3 and are documented here
until the spec gets a diagnostics appendix.

## Slice order

| # | Slice | Content |
|---|---|---|
| A | expressions + functions | literals, locals/params, assignments, arithmetic/comparison/logic, `if`, `ret`, calls to top-level functions, built-in `println`/`print`/`panic`; function body value / missing return. |
| B | flow | definite assignment (`none` initial state, `KRN2105`), smart casts for nullable/union after `if x`/`if (x != nil)`/`is`, `never` divergence, `KRN2304` unreachable-code warning. |
| C | loops | `while`-form, `loop` forms, `stop`/`next`, `loop` as expression. |
| D | members | fields/properties/methods, `$`, constructors and chains, operators incl. `*` repeat, extension members. |
| E | match | patterns, exhaustiveness, binding. |
| F | modules | imported namespaces, `use`-based symbol availability, globals initialization order. |

Slice A is deliberately single-module and top-level-function-only; structs/classes/imports are handled by
later slices so the first tests stay small.
