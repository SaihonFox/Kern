using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// B8a/B8c: конфликты <c>#[derive(...)]</c> с ручными членами (spec §7/§16). Одна диагностика
	/// KRN2106 на протокол; span — атрибут, который этот протокол объявляет. Сам ручной член
	/// продолжает выигрывать на месте вызова: объявленный <c>op hash</c>/<c>op compare</c>/
	/// <c>op clone</c> проверяется в протокольном пути binder'а раньше derive, а обычный метод
	/// с тем же именем находит <see cref="BindMemberCall"/>.
	/// </summary>
	private void CheckDeriveConflicts()
	{
		foreach (var type in _module.Types.Values)
		{
			var declaration = type.Declaration;
			if (declaration is null)
				continue;

			if (DeriveCatalog.Find(declaration, DerivedProtocol.Hash) is { } hash && HasManualHash(type))
			{
				Report(
					"KRN2106",
					$"Протокол 'hash' уже реализован типом '{type.Name}': #[derive(hash)] конфликтует с ручным членом",
					hash.Span);
			}

			if (DeriveCatalog.Find(declaration, DerivedProtocol.Compare) is { } compare && HasManualCompare(type))
			{
				Report(
					"KRN2106",
					$"Протокол 'compare' уже реализован типом '{type.Name}': #[derive(compare)] конфликтует с ручным членом",
					compare.Span);
			}

			if (DeriveCatalog.Find(declaration, DerivedProtocol.Clone) is { } clone && HasManualClone(type))
			{
				Report(
					"KRN2106",
					$"Протокол 'clone' уже реализован типом '{type.Name}': #[derive(clone)] конфликтует с ручным членом",
					clone.Span);
			}

			if (DeriveCatalog.Find(declaration, DerivedProtocol.ToStr) is { } toStr && HasManualSimple(type, "to_str"))
			{
				Report(
					"KRN2106",
					$"Протокол 'to_str' уже реализован типом '{type.Name}': #[derive(to_str)] конфликтует с ручным членом",
					toStr.Span);
			}

			if (DeriveCatalog.Find(declaration, DerivedProtocol.ToDebug) is { } toDebug && HasManualSimple(type, "to_debug"))
			{
				Report(
					"KRN2106",
					$"Протокол 'to_debug' уже реализован типом '{type.Name}': #[derive(to_debug)] конфликтует с ручным членом",
					toDebug.Span);
			}
		}
	}

	/// <summary>Ручной <c>hash</c>: <c>op hash()</c> без параметров или нестатический метод <c>hash()</c>.</summary>
	private static bool HasManualHash(TypeSymbol type) =>
		type.Members.Operators.Any(function =>
			function.OpSymbol == HashOperator && function.Parameters.Count == 0) ||
		type.Members.GetFunctions(HashOperator).Any(function =>
			!function.IsStatic && function.Parameters.Count == 0);

	/// <summary>Ручной <c>compare</c>: <c>op compare(T)</c> с одним параметром или нестатический метод <c>compare(T)</c>.</summary>
	private static bool HasManualCompare(TypeSymbol type) =>
		type.Members.Operators.Any(function =>
			function.OpSymbol == CompareOperator && function.Parameters.Count == 1) ||
		type.Members.GetFunctions(CompareOperator).Any(function =>
			!function.IsStatic && function.Parameters.Count == 1);

	/// <summary>Ручной <c>clone</c>: <c>op clone()</c> без параметров или нестатический метод <c>clone()</c>.</summary>
	private static bool HasManualClone(TypeSymbol type) =>
		type.Members.Operators.Any(function =>
			function.OpSymbol == CloneOperator && function.Parameters.Count == 0) ||
		type.Members.GetFunctions(CloneOperator).Any(function =>
			!function.IsStatic && function.Parameters.Count == 0);

	/// <summary>Ручной <c>to_str</c>/<c>to_debug</c>: нестатический метод без параметров.</summary>
	private static bool HasManualSimple(TypeSymbol type, string name) =>
		type.Members.GetFunctions(name).Any(function =>
			!function.IsStatic && function.Parameters.Count == 0);
}
