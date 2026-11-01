# Golden-output assumptions for the core examples (01–06)

The spec does not define `to_str()` for collections, tuples, `unit` values or `type` values;
§9 only says a `unit` string is "based on its fields" (`docs/spec/kern.en.md:473`).
The `expected.out` files in `01_basics` … `06_generics_operators` use the conventions below.
They come from the owner's earlier design (`reference/src/Kern.Vm/Values/ValueOps.cs`, read-only)
and from output examples written in `examples/*/src/*.kern` comments — not from a running compiler.

- top level: `str` unquoted, `char` unquoted, `bool` -> `true`/`false`, `nil` -> `nil`,
  integer in decimal, `f64` in the invariant shortest round-trip form (`R`/`G`):
  `0.5`, `12.56636`, `11.704699910719626`.
- `list` / `array`: `[a, b, c]` (matches comments `// [1, 2, 3, 4, 5]`, `// [0, 2, 4, 6]`).
- `dict`: `{k: v, k2: v2}`, insertion order of the literal.
- tuple: `(a, b)`; named tuple: `(x=1, y=2)`.
- `unit`: `Name(field=value, ...)` in declaration order (`Point(x=1, y=2)`, `Box(value=5)`).
- elements inside collections/tuples: `str` quoted and escaped (`"аня"`), `char` quoted (`'k'`),
  matching `ValueOps.Repr`.
- `typeof<T>()` prints the type name (`i32`).

Resolved 2026-11-01 (Q25, owner batch "finish everything"):

1. `to_str`/`to_debug` formatting is fixed by the convention above and implemented in `KernFormat` (B8b). The `expected.out` files were regenerated with quoted nested `str`/`char`.
2. `examples/24_reflection` comments now show the quoted `field_names`/`attribute_names` output; `04_flow` is an active golden with `("аня", 25)`.
3. `dict` printing/iteration remains insertion order (implementation detail, not part of Q25).
