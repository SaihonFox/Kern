using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

public sealed partial class DeclarationCollector
{
	/// <summary>
	/// Проверка spec §2: конкретный <c>type</c> обязан реализовать все оставшиеся унаследованные
	/// абстрактные методы. Реализацией считается собственный нестатический неабстрактный метод
	/// с совместимой сигнатурой либо явный выбор родителя <c>$[A]foo</c>, указывающий на
	/// конкретный член. Конкретный член другого (не связанного по наследованию) родителя сам по
	/// себе реализацией не считается. Абстрактные типы и value-типы не проверяются.
	/// </summary>
	private void AuditAbstractMembers()
	{
		foreach (var symbol in _types.Values)
		{
			if (symbol.Kind != Types.SymbolKind.Class || symbol.IsAbstract || symbol.IsStatic || symbol.IsValueType)
				continue;

			var reported = new HashSet<string>(StringComparer.Ordinal);
			foreach (var ancestor in Inheritance.Ancestors(symbol))
				foreach (var group in ancestor.Members.OwnFunctions.Values)
					foreach (var function in group)
					{
						if (!function.IsAbstract || function.IsStatic)
							continue;
						if (IsAbstractMethodSatisfied(symbol, function))
							continue;

						// Одинаковая абстрактная сигнатура у нескольких предков — одно сообщение.
						if (!reported.Add(function.Signature))
							continue;

						var span = symbol.Declaration?.Span ?? default;
						_diagnostics.Add(new Diagnostic(
							"KRN2101",
							$"Конкретный тип '{symbol.Name}' не реализует абстрактный метод '{function.Name}' из '{ancestor.Name}'",
							span));
					}
		}
	}

	/// <summary>
	/// Реализован ли абстрактный метод <paramref name="abstractMethod"/> в конкретном типе:
	/// собственный метод, явный выбор родителя или конкретный предок, который уже реализовал
	/// этот же абстрактный член (тогда для потомка он не «остаётся» абстрактным).
	/// </summary>
	private static bool IsAbstractMethodSatisfied(TypeSymbol symbol, FunctionSymbol abstractMethod) =>
		HasOwnImplementation(symbol, abstractMethod) ||
		HasSelectedImplementation(symbol, abstractMethod) ||
		HasConcreteAncestorImplementation(symbol, abstractMethod);

	private static bool HasOwnImplementation(TypeSymbol symbol, FunctionSymbol abstractMethod)
	{
		foreach (var group in symbol.Members.OwnFunctions.Values)
			foreach (var candidate in group)
				if (IsImplementation(candidate, abstractMethod))
					return true;

		return false;
	}

	private static bool HasSelectedImplementation(TypeSymbol symbol, FunctionSymbol abstractMethod)
	{
		foreach (var selection in symbol.Members.ParentSelections)
			if (SignatureCompatible(selection, abstractMethod) &&
				symbol.Members.SelectedFunction(selection.Signature) is { IsAbstract: false, IsStatic: false })
				return true;

		return false;
	}

	/// <summary>
	/// Конкретная реализация в предке, который сам является потомком объявившего типа (или самим
	/// типом): наследуемый конкретный метод такого предка действительно реализует тот же
	/// абстрактный член. Метод несвязанного родителя сюда не попадает — spec §2.
	/// </summary>
	private static bool HasConcreteAncestorImplementation(TypeSymbol symbol, FunctionSymbol abstractMethod)
	{
		if (abstractMethod.Owner is not { } declaring)
			return false;

		foreach (var ancestor in Inheritance.Ancestors(symbol))
		{
			if (!IsSelfOrDescendant(ancestor, declaring))
				continue;

			foreach (var group in ancestor.Members.OwnFunctions.Values)
				foreach (var candidate in group)
					if (IsImplementation(candidate, abstractMethod))
						return true;

			foreach (var selection in ancestor.Members.ParentSelections)
				if (SignatureCompatible(selection, abstractMethod) &&
					ancestor.Members.SelectedFunction(selection.Signature) is { IsAbstract: false, IsStatic: false })
					return true;
		}

		return false;
	}

	private static bool IsSelfOrDescendant(TypeSymbol candidate, TypeSymbol ancestor)
	{
		if (ReferenceEquals(candidate, ancestor))
			return true;

		foreach (var inherited in Inheritance.Ancestors(candidate))
			if (ReferenceEquals(inherited, ancestor))
				return true;

		return false;
	}

	private static bool IsImplementation(FunctionSymbol candidate, FunctionSymbol abstractMethod) =>
		!candidate.IsStatic && !candidate.IsAbstract && SignatureCompatible(candidate, abstractMethod);

	/// <summary>
	/// Имя, generic-арность и типы параметров (как в идентичности перегрузки, spec §15).
	/// Возвращаемый тип в сигнатуру не входит.
	/// </summary>
	private static bool SignatureCompatible(FunctionSymbol candidate, FunctionSymbol abstractMethod)
	{
		if (candidate.Name != abstractMethod.Name || candidate.GenericArity != abstractMethod.GenericArity)
			return false;
		if (candidate.Parameters.Count != abstractMethod.Parameters.Count)
			return false;

		for (var index = 0; index < candidate.Parameters.Count; index++)
			if (candidate.Parameters[index].Passing != abstractMethod.Parameters[index].Passing ||
				!TypesCompatible(candidate.Parameters[index].Type, abstractMethod.Parameters[index].Type))
				return false;

		return true;
	}

	/// <summary>
	/// Структурное сравнение с игнорированием параметров типа: аргументы generic-предка не
	/// подставляются, поэтому <c>T</c> считается совместимым с конкретным типом. Это
	/// консервативно (пропуск лучше ложного срабатывания), но вложенные оболочки проверяются.
	/// </summary>
	private static bool TypesCompatible(KernType candidate, KernType target)
	{
		if (candidate.Equals(target))
			return true;
		if (candidate is TypeParameterType || target is TypeParameterType)
			return true;

		return (candidate, target) switch
		{
			(NullableType left, NullableType right) => TypesCompatible(left.Underlying, right.Underlying),
			(ClassType left, ClassType right) =>
				ReferenceEquals(left.Symbol, right.Symbol) && ArgumentsCompatible(left.Arguments, right.Arguments),
			(UnitType left, UnitType right) =>
				ReferenceEquals(left.Symbol, right.Symbol) && ArgumentsCompatible(left.Arguments, right.Arguments),
			(BuiltinGenericType left, BuiltinGenericType right) =>
				left.BuiltinKind == right.BuiltinKind && ArgumentsCompatible(left.Arguments, right.Arguments),
			(TupleType left, TupleType right) =>
				left.Elements.Count == right.Elements.Count &&
				ElementsCompatible(left.Elements, right.Elements),
			(FunctionType left, FunctionType right) =>
				ArgumentsCompatible(left.Parameters, right.Parameters) &&
				TypesCompatible(left.ReturnType, right.ReturnType),
			(UnionType left, UnionType right) =>
				ArgumentsCompatible(left.Alternatives, right.Alternatives),
			_ => false
		};
	}

	private static bool ArgumentsCompatible(IReadOnlyList<KernType> candidate, IReadOnlyList<KernType> target)
	{
		if (candidate.Count != target.Count)
			return false;

		for (var index = 0; index < candidate.Count; index++)
			if (!TypesCompatible(candidate[index], target[index]))
				return false;

		return true;
	}

	private static bool ElementsCompatible(IReadOnlyList<TupleElement> candidate, IReadOnlyList<TupleElement> target)
	{
		for (var index = 0; index < candidate.Count; index++)
			if (!TypesCompatible(candidate[index].Type, target[index].Type))
				return false;

		return true;
	}
}
