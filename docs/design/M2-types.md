# M2 design — symbols and types

Status: working design for milestone M2 (`docs/ROADMAP.md`). Spec wins over this file; if they disagree, fix this file.

## 1. Layers

```
TypeSyntax            parser output: how a type is written (spans, markers)
TypeCatalog           builtin primitives + builtin generic arities (no module context)
TypeResolver          TypeSyntax + SymbolTable -> KernType; reports KRN1001/KRN1002/KRN1003
KernType hierarchy    resolved, comparable, internable types (Semantics/Types)
Conversions           conversion classification between two KernType values
DeclarationCollector  symbols for modules/types/units/functions/fields/properties/members
OverloadResolver      applicability, generic inference, best-candidate, KRN1401/KRN1402
```

Type syntax lives in `Kern.Compiler.Syntax.Types`, the resolved model in `Kern.Compiler.Semantics.Types`.
The AST stores `TypeSyntax` (never type strings); canonical text is produced by `TypeSyntax.ToString()`.

## 2. TypeSyntax

- `NamedTypeSyntax(Name, Arguments, IsNullable, IsSingleAssignment, Span)` — `i32`, `list<T>`, `T?`, `i32!?`.
  `IsSingleAssignment` records `!` syntactically (local single-assignment / required field); the binder interprets it.
- `FunctionTypeSyntax(Parameters, ReturnType, Span)` — `fun(i32, str) -> bool`, `fun -> void`.
- `TupleTypeSyntax(Elements, Span)` — `(i32 x, i32 y)`; element names optional.
- `UnionTypeSyntax(Alternatives, Span)` — only for `type A = [X, Y]`.
- `GenericParameterNode(Name, Constraints, HasCtorConstraint, Span)` — `T`, `K[IHashable]`, `V[ctor()]`.
- `ParentTypeNode(Type, ConstructorArguments, HasArgumentList, Span)` — `[A]` means `[A()]`.
- `TypeDeclaration` exposes `GenericParameters`, `PrimaryParameters`, `Parents`; `UnitDeclaration` exposes
  `GenericParameters` and `PrimaryParameters` only - `unit` has no inheritance (spec §9).

Type grammar (parser): `fun(...) -> T` | `(T, T name)` | `Name<args>` | `Name` | alias union `[...]`,
then any order of `?` and `!` suffixes (at most one each). Generic arguments are full type syntax.

## 3. KernType model (`Semantics/Types`)

- `PrimitiveType` + `PrimitiveKind`: `I8 I16 I32 I64 U8 U16 U32 U64 F16 F32 F64 Bool Char Str Void Object` and
  universal `Inum Fnum Num`, plus `Never` and `None` (internal error-less state is not a type; `None` exists only
  for flow analysis and is not nameable).
- `TypeParameterType(Name, ConstraintTypes)`.
- `ClassType(TypeSymbol, IReadOnlyList<KernType> Arguments)` and `UnitType` (same shape, value semantics; `unit!` is
  `UnitType` with `IsImmutable`).
- `BuiltinGenericType(BuiltinGenericKind Kind, Arguments)` — `list<T> array<T> dict<K,V> set<T> result<T,E> task<T>`
  plus `collection<T>` (the unified root from DECISIONS §C; it becomes a native symbol in M6 but exists in the model from
  M2) and stage-1 names (`event<T>`, `observable<T>`, `channel<T>`, `pipe<T>`, `shared<T>`) parsed as builtin generics
  with fixed arity; unknown arity = KRN1002.
- `NullableType(Underlying)`, `UnionType(Alternatives)`, `TupleType(Elements)`, `FunctionType(Parameters, Return)`,
  `ErrorType` (absorbs further diagnostics; never produces cascading errors).
- Structural equality + `ToString()` canonical text; a `TypeCache`/interner keeps one instance per shape.

Primitive name table: `i8 i16 i32 i64 u8 u16 u32 u64 f16 f32 f64 bool char str void object inum fnum num never`
and the `none`/`nil` literal types (binder only). Aliases: `map<K,V>` is the spec's older spelling of `dict<K,V>`
(the spec §14 example uses `map`; accept both, canonical text `dict`).

## 4. Conversions (`Conversions.Classify(source, target)`)

Order of checks, first match wins:
1. identity (`ErrorType` always converts to/from anything to stop cascades);
2. numeric: implicit widening per C# (`i8<i16<i32<i64`, `u8<u16<u32<u64`, `f16<f32<f64`, plus
   `i8/i16/u8/u16 -> f32/f64`, `i32/u32 -> f32/f64`, `i64/u64 -> f32/f64`); universal `inum` accepts integers,
   `fnum` accepts floats, `num` accepts both (a concrete type never converts to the *other* family's universal);
3. `T` -> `T?` (nullable wrap), `T?` -> `T?` identity, `T?` -> `T` is not implicit; lifted `S -> T?` when `S -> T`
   is implicit (`i32 -> i64?`, `Child -> Base?`) — see DECISIONS_NEEDED Q8; any type including `T?` -> `object` (boxing);
   `nil` is assignable to nullable types only, not to `object` (Q9);
4. instance -> base class / base unit (single and multiple inheritance), `list<T>`/`array<T>`/`set<T>` -> `collection<T>`
   and `str` -> `collection<char>` (compiler rule, DECISIONS §C); `dict<K,V>` parent is still open (Q5), so no rule yet;
5. union: `T` -> `union` when `T` converts to a member; union -> union when every alternative fits;
6. tuple/function: tuple elements are converted element-wise, element names are ignored (C# rule); function types are
   identity-only in stage 1 (Q3: no variance);
7. contextual `str` <-> `list<char>` (spec §3, §15): only selected by the expected type, never inferred.
- Constant conversion of unsuffixed numeric literals is handled separately by
  `Conversions.ClassifyConstant(NumericLiteral, target)` — the literal's value must fit the target (C# rule, DECISIONS §C).
- Unspecified widening/narrowing is not allowed; the binder reports a type error (code assigned when the binder lands).

## 5. Overload resolution (spec §15)

Candidate: function symbols with matching name. Steps: arity/argument mapping (positional then named, missing must have
defaults, variadic normal vs expanded form) -> generic inference (explicit args fix arity; skipped parameters can
participate in inference only when the parameter type mentions a type parameter) -> constraint check -> applicability
-> better-candidate comparison per spec (identity > implicit conversion, target-type rule, signed over unsigned;
non-generic > generic, normal > expanded, fewer defaults, more specific, member > extension) -> ambiguity `KRN1402`
or no candidate `KRN1401`. Duplicate identical signatures are rejected earlier by `SymbolTable` (`KRN1400`).

## 6. Slice order

| # | Slice | Owner | Done when |
|---|---|---|---|
| A | `TypeSyntax` + parser + full type/unit headers | compiler-dev | build, all tests, stage-1 audit green |
| B | `KernType` model, `TypeCatalog`, `TypeResolver` | compiler-dev | unit tests for primitives, generics, nullable, tuples, functions, unions, arity errors |
| C | `Conversions` classification | compiler-dev | table tests incl. numeric widening, constant literals, collections, unions, `str`<->`list<char>` |
| D | Declaration collector: type/unit/function symbols with members, generics, inheritance | compiler-dev | tests for members, inheritance, duplicate members |
| E | Overload resolution + inference | compiler-dev | spec §15 cases, `KRN1401`/`KRN1402` tests (32 tests, `OverloadResolverTests`) |
| F | Import AST + lazy module resolver | compiler-dev | items/wildcard retained; `KRN1101`/`KRN1102`; transitive/cyclic tests |

E is implemented in `Semantics/Overloads/` and covered by 32 tests: identity/widening, constant literals, signed-vs-unsigned, named/default arguments, generics and constraints, vararg normal/expanded, extensions, owner hiding, nullable/union, error recovery. Known limitation: comparison is pairwise, not a full tournament, so pathological non-transitive candidate sets may still produce `KRN1402`.
