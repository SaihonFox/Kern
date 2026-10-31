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
- `dict`: `[k: v, k2: v2]`, insertion order of the literal.
- tuple: `(a, b)`; named tuple: `(x=1, y=2)`.
- `unit`: `Name(field=value, ...)` in declaration order (`Point(x=1, y=2)`, `Box(value=5)`).
- elements inside collections/tuples: `str` quoted and escaped (`"аня"`), `char` quoted (`'k'`),
  matching `ValueOps.Repr`.
- `typeof<T>()` prints the type name (`i32`).

Open items (a single spec paragraph would settle them):

1. Collection / dict / tuple / unit / type `to_str` is not specified anywhere.
2. `examples/24_reflection/src/main.kern:49` comments `println(field_names<User>())`
   as `// [name, age, email]` (no quotes), which contradicts the quoting rule above.
   If unquoted is the intended rule, `02_types` line 3 becomes `[аня: 25, борис: 31]`
   and `04_flow` lines 13–14 become `(аня, 25)`, `(борис, 31)`.
3. `dict` iteration/printing order is not specified (insertion order assumed).
