using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

public sealed partial class DeclarationCollector
{
	/// <summary>
	/// Q29 (spec §2): <c>~fun</c> законен только тогда, когда предок объявляет нестатический
	/// метод с тем же именем и числом параметров, помеченный <c>@fun</c> (абстрактный) или
	/// <c>open fun</c>. Иначе это, скорее всего, опечатка, и виртуальный диспатч просто не
	/// найдёт переопределение — сообщаем KRN2101 на объявлении <c>~fun</c>. Операторы и
	/// свойства вне Q29: проверяются только методы (<c>FunctionKind.Method</c>).
	/// </summary>
	private void AuditOverrides()
	{
		foreach (var symbol in _types.Values)
		{
			if (symbol.IsStatic)
				continue;

			var ancestors = Inheritance.Ancestors(symbol);

			foreach (var group in symbol.Members.OwnFunctions.Values)
				foreach (var function in group)
				{
					if (!function.IsOverride || function.IsStatic || function.Kind != FunctionKind.Method)
						continue;
					// Явный выбор родителя `~fun $[A].foo` привязывается к конкретному члену
					// и не требует open: это форма реализации, а не переопределение по имени.
					if (function.ParentSelector is not null)
						continue;

					var problem = FindOverrideProblem(ancestors, function);
					if (problem is null)
						continue;

					var span = function.Declaration?.Span ?? symbol.Declaration?.Span ?? default;
					_diagnostics.Add(new Diagnostic("KRN2101", problem, span));
				}
		}
	}

	/// <summary>
	/// Почему у <paramref name="function"/> нет законного переопределяемого предка:
	/// <c>null</c> — есть подходящий <c>open</c>/<c>@fun</c> метод. Иначе — готовое сообщение,
	/// различающее отсутствие имени, несовпадение арности и неоткрытый предок.
	/// </summary>
	private static string? FindOverrideProblem(IReadOnlyList<TypeSymbol> ancestors, FunctionSymbol function)
	{
		var sameName = false;
		var sameArity = false;
		var name = $"{function.Owner?.Name}.{function.Name}";

		foreach (var ancestor in ancestors)
			foreach (var group in ancestor.Members.OwnFunctions.Values)
				foreach (var inherited in group)
				{
					if (inherited.IsStatic || inherited.Kind != FunctionKind.Method ||
						inherited.Name != function.Name)
						continue;

					sameName = true;
					if (inherited.Parameters.Count != function.Parameters.Count)
						continue;

					sameArity = true;
					if (inherited.IsAbstract || inherited.IsOpen)
						return null;
				}

		if (!sameName)
			return $"Метод '{name}' помечен '~', но в предках нет 'open'/'@fun' метода '{function.Name}'";
		if (!sameArity)
			return $"Метод '{name}' помечен '~', но в предках нет 'open'/'@fun' метода " +
				$"'{function.Name}' с числом параметров {function.Parameters.Count}";

		return $"Метод '{name}' помечен '~', но одноимённый метод в предке не помечен 'open'/'@fun'";
	}
}
