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

			// Протокольные/нативные предки (`iter<T>`, `stream`, `collection<T>` и т.п.) —
			// не ClassType и не дают `open`-методов в MemberTable. `~fun next` у такого типа
			// реализует протокол, а не переопределяет класс: Q29 к нему не применяется.
			if (symbol.Bases.Any(baseType => baseType is not ClassType))
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

			// Q29 расширен на операторы и свойства (2026-11-01): у них нет `open`-синтаксиса,
			// поэтому правило — существование одноимённого члена у предка.
			foreach (var @operator in symbol.Members.Operators)
			{
				if (!@operator.IsOverride)
					continue;
				var inherited = ancestors.Any(ancestor => ancestor.Members.Operators.Any(candidate =>
					candidate.OpSymbol == @operator.OpSymbol &&
					candidate.Parameters.Count == @operator.Parameters.Count));
				if (inherited)
					continue;
				var span = @operator.Declaration?.Span ?? symbol.Declaration?.Span ?? default;
				_diagnostics.Add(new Diagnostic(
					"KRN2101",
					$"Оператор '{symbol.Name}.{@operator.OpSymbol}' помечен '~', но в предках нет " +
						$"оператора '{@operator.OpSymbol}' с числом параметров {@operator.Parameters.Count}",
					span));
			}

			foreach (var property in symbol.Members.OwnProperties)
			{
				if (!property.IsOverride)
					continue;
				var inherited = ancestors.Any(ancestor =>
					ancestor.Members.OwnProperties.Any(candidate => candidate.Name == property.Name));
				if (inherited)
					continue;
				var span = property.Declaration?.Span ?? symbol.Declaration?.Span ?? default;
				_diagnostics.Add(new Diagnostic(
					"KRN2101",
					$"Свойство '{symbol.Name}.{property.Name}' помечено '~', но в предках нет свойства " +
						$"'{property.Name}'",
					span));
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
