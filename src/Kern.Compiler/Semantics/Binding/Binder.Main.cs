using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Проверка сигнатуры точки входа (spec §13): `main` без параметров или с одним
/// `list&lt;str&gt;`; возвращает `void` или `i32` (код выхода процесса — DECISIONS 2026-10-31).
/// </summary>
public sealed partial class Binder
{
	private void CheckMainSignature(FunctionSymbol symbol)
	{
		if (symbol.Name != "main" || symbol.Owner is not null)
			return;

		if (!IsVoid(_returnType) &&
			!(_returnType is PrimitiveType { Primitive: PrimitiveKind.I32 }))
		{
			Report(
				"KRN2107",
				"Функция main может возвращать void или i32 (код выхода)",
				DeclarationSpan(symbol));
		}

		if (symbol.Parameters.Count == 0)
			return;
		if (symbol.Parameters.Count == 1 && IsStringList(symbol.Parameters[0].Type))
			return;

		Report(
			"KRN2107",
			"Функция main может быть без параметров или принимать один list<str> args",
			DeclarationSpan(symbol));
	}

	/// <summary>spec §13: единственный допустимый параметр <c>main</c> — <c>list&lt;str&gt;</c>.</summary>
	private static bool IsStringList(KernType type) =>
		type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List, Arguments.Count: 1 } list &&
		list.Arguments[0] == PrimitiveType.Str;
}
