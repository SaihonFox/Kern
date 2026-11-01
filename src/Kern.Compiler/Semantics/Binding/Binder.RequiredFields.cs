using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// Q18: корневой конструктор (без цепочки) обязан присвоить каждое обязательное поле
	/// своего типа на всех путях. Параметры <c>$</c>, инициализаторы и записи в теле
	/// отмечаются в <see cref="FlowState"/>; ветвления сливаются пересечением.
	/// </summary>
	private void CheckRequiredFields(FunctionSymbol constructor, TypeSymbol owner)
	{
		if (!_flow.Reachable)
			return;

		foreach (FieldSymbol field in owner.Members.Fields)
		{
			if (!field.IsRequired || field.IsStatic || !ReferenceEquals(field.Owner, owner))
				continue;
			if (_flow.IsFieldAssigned(field))
				continue;

			Report(
				"KRN2192",
				$"Конструктор типа '{owner.Name}' не инициализирует обязательное поле '{field.Name}'",
				DeclarationSpan(constructor));
		}
	}
}
