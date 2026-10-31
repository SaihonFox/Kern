using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	private const string TypeOfName = "typeof";
	private const string NameOfName = "name_of";

	/// <summary>
	/// Контекстные встроенные формы рефлексии времени компиляции (spec §17/§18):
	/// <c>typeof&lt;T&gt;()</c> (значение <c>type</c>) и <c>name_of&lt;T&gt;()</c> (строка).
	/// Встроенная форма сильнее пользовательской функции только при явных аргументах-типах;
	/// без них видимая функция с тем же именем побеждает (`typeof(x)` пока не реализован).
	/// Неверное использование — KRN2101, новых кодов не вводим.
	/// </summary>
	private BoundExpression? TryBindMetaCall(
		string name,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		if (name is not (TypeOfName or NameOfName))
			return null;

		var isTypeOf = name == TypeOfName;
		var form = isTypeOf ? TypeOfName : NameOfName;

		// `typeof(x)`/`name_of(x)`: видимая пользовательская функция важнее встроенной формы.
		if (typeArguments.Count == 0)
		{
			if (UserFunctionVisible(name))
				return null;

			Report("KRN2101", $"'{form}' ожидает ровно один аргумент-тип: {form}<T>()", span);
			return new BoundErrorExpression(span);
		}

		if (arguments.Count > 0)
		{
			Report("KRN2101", $"'{form}<T>()' не принимает аргументов-значений", span);
			return new BoundErrorExpression(span);
		}

		if (typeArguments.Count != 1)
		{
			Report(
				"KRN2101",
				$"'{form}<T>()' ожидает ровно один аргумент-тип, а получил {typeArguments.Count}",
				span);
			return new BoundErrorExpression(span);
		}

		var type = _resolver.Resolve(typeArguments[0], _typeScope);
		if (type is ErrorType)
			return new BoundErrorExpression(span);

		return isTypeOf
			? new BoundTypeOfExpression(type, span)
			: new BoundNameOfExpression(type, span);
	}

	/// <summary>Есть ли видимая пользовательская функция с таким именем (модуль, импорт, член своего типа).</summary>
	private bool UserFunctionVisible(string name)
	{
		if (VisibleFunctions(name).Count > 0)
			return true;
		if (_current?.Owner is { } owner && owner.Members.GetFunctions(name).Count > 0)
			return true;
		return _module.NativeFunctions.Any(function => function.NativeSelfType is null && function.Name == name);
	}
}
