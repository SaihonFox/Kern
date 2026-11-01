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
	private const string FieldNamesName = "field_names";
	private const string AttributeNamesName = "attribute_names";
	private const string HasAttributeName = "has_attribute";

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
		if (name is not (TypeOfName or NameOfName or FieldNamesName or AttributeNamesName or HasAttributeName))
			return null;

		var form = name;

		// `typeof(x)`/`name_of(x)`: видимая пользовательская функция важнее встроенной формы.
		if (typeArguments.Count == 0)
		{
			if (UserFunctionVisible(name))
				return null;

			Report("KRN2101", $"'{form}' ожидает ровно один аргумент-тип: {form}<T>()", span);
			return new BoundErrorExpression(span);
		}

		if (name != HasAttributeName && arguments.Count > 0)
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

		return name switch
		{
			TypeOfName => new BoundTypeOfExpression(type, span),
			NameOfName => new BoundNameOfExpression(type, span),
			FieldNamesName => MetaFieldNames(type, span),
			AttributeNamesName => MetaAttributeNames(type, span),
			_ => MetaHasAttribute(type, arguments, span),
		};
	}

	/// <summary><c>field_names&lt;T&gt;()</c>: имена экземплярных полей в порядке объявления.</summary>
	private BoundExpression MetaFieldNames(KernType type, TextSpan span)
	{
		var names = InstanceFields(type).Select(field => field.Name).ToArray();
		return StringList(names, span);
	}

	/// <summary><c>attribute_names&lt;T&gt;()</c>: имена атрибутов типа.</summary>
	private BoundExpression MetaAttributeNames(KernType type, TextSpan span)
	{
		var names = MetaTypeSymbol(type)?.Attributes.Select(attribute => attribute.Name).ToArray() ?? [];
		return StringList(names, span);
	}

	/// <summary><c>has_attribute&lt;T&gt;("name")</c>: есть ли атрибут у типа.</summary>
	private BoundExpression MetaHasAttribute(KernType type, IReadOnlyList<CallArgumentNode> arguments, TextSpan span)
	{
		if (arguments.Count != 1 ||
			BindExpression(arguments[0].Value) is not BoundLiteralExpression { Value: string name })
		{
			Report("KRN2101", "has_attribute<T> ожидает один строковый литерал с именем атрибута", span);
			return new BoundErrorExpression(span);
		}

		bool found = MetaTypeSymbol(type)?.Attributes.Any(attribute => attribute.Name == name) == true;
		return new BoundLiteralExpression(found, null, PrimitiveType.Bool, span);
	}

	private BoundExpression StringList(IReadOnlyList<string> values, TextSpan span)
	{
		var listType = new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.Str]);
		return new BoundCollectionLiteral(
			[.. values.Select(value => new BoundCollectionItem(
				new BoundLiteralExpression(value, null, PrimitiveType.Str, span),
				IsSpread: false))],
			listType,
			span);
	}

	private static IEnumerable<FieldSymbol> InstanceFields(KernType type) =>
		MetaTypeSymbol(type)?.Members.Fields.Where(field =>
			!field.IsStatic &&
			!field.Name.StartsWith("$slot", StringComparison.Ordinal)) ?? [];

	private static TypeSymbol? MetaTypeSymbol(KernType type) => type switch
	{
		ClassType @class => @class.Symbol,
		UnitType unit => unit.Symbol,
		_ => null,
	};

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
