using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// Вызов без приёмника: модульная функция, встроенная функция prelude или статический
	/// метод. У статического метода нет приёмника-экземпляра, поэтому
	/// <see cref="BoundCallExpression.Receiver"/> остаётся null, а в байткод уходят только
	/// аргументы из параметров (M3-D3b). Этим же путём идут <c>T.method(...)</c> и вызов
	/// статического метода без квалификации внутри своего типа.
	/// </summary>
	private BoundExpression BindTargetlessCall(
		List<FunctionSymbol> candidates,
		string name,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		// Аргументы связываются до проверки кандидатов: иначе ошибки внутри аргументов
		// терялись бы при неизвестном имени функции.
		var (_, callArguments, byCallArgument) = BindArguments(arguments);
		IReadOnlyList<KernType>? explicitTypes = null;
		if (typeArguments.Count > 0)
			explicitTypes = _resolver.ResolveAll(typeArguments, _typeScope);

		if (candidates.Count == 0)
		{
			Report("KRN2103", $"Неизвестная функция '{name}'", span);
			return new BoundErrorExpression(span);
		}

		var call = new CallArguments(callArguments, receiverType: null, explicitTypes, span, name);
		var resolution = OverloadResolver.Resolve(candidates, call);
		if (!resolution.Success)
		{
			_diagnostics.AddRange(resolution.Diagnostics);
			return new BoundErrorExpression(span);
		}

		var ordered = new List<BoundExpression>(resolution.Arguments.Count);
		foreach (var argument in resolution.Arguments)
		{
			if (argument.Argument is null)
			{
				if (BindDefaultArgument(argument.Parameter, span) is not { } defaultValue)
				{
					Report("KRN2108", $"У параметра '{argument.Parameter.Name}' нет значения по умолчанию", span);
					return new BoundErrorExpression(span);
				}

				ordered.Add(defaultValue);
				continue;
			}

			var expression = byCallArgument[argument.Argument];
			ordered.Add(ConvertWithConversion(expression, argument.Parameter.Type, argument.Conversion, expression.Span));
		}

		var returnType = resolution.ReturnType ?? PrimitiveType.Void;
		WarnDeprecated(resolution.Function!, span);
		return new BoundCallExpression(resolution.Function!, ordered, returnType, span);
	}
}
