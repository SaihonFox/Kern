using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// use/dispose (spec §12). Тип resource-переменной должен иметь нестатический
/// <c>dispose()</c> без параметров; сам вызов на выходе из блока печатает
/// <see cref="CodeGen.CodeGenerator"/> по кадрам очистки.
/// </summary>
public sealed partial class Binder
{
	/// <summary>
	/// <c>use T x = expr</c> / <c>use var x = expr</c> (spec §12): локальная переменная,
	/// освобождаемая <c>x.dispose()</c> при выходе из блока — включая <c>ret</c>,
	/// <c>stop</c> и <c>next</c>; при панике <c>dispose()</c> не вызывается. Тип обязан
	/// иметь нестатический метод <c>dispose()</c> без параметров, иначе KRN2101.
	/// </summary>
	private BoundStatement BindUseStatement(UseStatement statement)
	{
		var declared = statement.Type is null ? null : _resolver.Resolve(statement.Type, _typeScope);
		var initializer = BindExpression(statement.Initializer, declared);
		if (declared is not null)
			initializer = Convert(initializer, declared, statement.Initializer.Span);

		var type = declared ?? initializer.Type;
		if (declared is null && initializer.Type is NilType)
		{
			Report("KRN2102", $"Нельзя вывести тип '{statement.Name}' в use из nil; укажите тип явно", statement.Span);
			type = ErrorType.Instance;
		}

		FunctionSymbol? dispose = FindDispose(type);
		if (dispose is null && !IsError(type))
			Report(
				"KRN2101",
				$"Тип '{type}' не имеет метода dispose() без параметров; use требует освобождаемый тип",
				statement.Span);

		var variable = DeclareLocal(statement.Name, type, isSingleAssignment: false, statement.Span);
		DeclareFlow(variable, true, initializer.Type);

		return new BoundUseStatement(
			statement.Name,
			variable.Slot,
			type,
			initializer,
			dispose,
			statement.Span);
	}

	/// <summary>
	/// Метод освобождения для <c>use</c>: нестатический <c>dispose()</c> без параметров
	/// (собственный или нативный член встроенного типа), иначе <c>null</c>.
	/// </summary>
	private FunctionSymbol? FindDispose(KernType type)
	{
		var owner = OwnerOf(type);
		if (owner is not null)
		{
			foreach (var function in owner.Members.GetFunctions("dispose"))
				if (!function.IsStatic && function.Parameters.Count == 0)
					return function;
		}

		foreach (var function in NativeMembers(type, "dispose"))
			if (function.Parameters.Count == 0)
				return function;

		return null;
	}
}
