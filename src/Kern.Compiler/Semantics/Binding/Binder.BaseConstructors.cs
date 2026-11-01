using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Вызовы конструкторов прямых родителей (spec §2, §8): в корне цепочки конструкторов наследника
/// пролог вызывает все прямые родительские конструкторы в порядке объявления <c>[A, B]</c>,
/// затем выполняет собственные инициализаторы наследника. <c>[A]</c> — это <c>[A()]</c>,
/// <c>[A(x)]</c> передаёт выражение из заголовка типа. Родитель инициализируется до наследника.
/// </summary>
public sealed partial class Binder
{
	/// <summary>
	/// Привязывает вызовы всех прямых родителей для корневого конструктора. Пустой список —
	/// родителей нет, у конструктора есть цепочка <c>: $(...)</c> (родителей инициализирует её
	/// корень), у родителей нет конструкторов или объявление отвергнуто как ромбовидное
	/// (тогда двойной инициализации не будет даже при продолжении компиляции).
	/// </summary>
	private IReadOnlyList<BoundBaseConstructorCall> BindBaseConstructors(FunctionSymbol constructor)
	{
		if (constructor.Owner is not { } owner || owner.IsStatic)
			return [];

		var result = new List<BoundBaseConstructorCall>();
		foreach (var baseType in Inheritance.DirectBases(owner))
			if (BindBaseConstructor(constructor, baseType) is { } call)
				result.Add(call);

		return result;
	}

	/// <summary>Один прямой родитель: аргументы из его узла в заголовке, разрешение перегрузки.</summary>
	private BoundBaseConstructorCall? BindBaseConstructor(FunctionSymbol constructor, ClassType baseType)
	{
		if (constructor.Owner is not { } owner)
			return null;

		var candidates = new List<FunctionSymbol>();
		foreach (var candidate in baseType.Symbol.Members.Constructors)
			if (!candidate.IsStatic)
				candidates.Add(candidate);
		if (candidates.Count == 0)
			return null;

		var parentNode = ParentNodeOf(owner, baseType.Symbol);
		var argumentNodes = parentNode?.ConstructorArguments ?? [];
		var nodes = new CallArgumentNode[argumentNodes.Count];
		for (var index = 0; index < argumentNodes.Count; index++)
			nodes[index] = new CallArgumentNode(null, argumentNodes[index], argumentNodes[index].Span);

		TextSpan span = parentNode?.Span ?? DeclarationSpan(constructor);

		// Аргументы видят `$`, параметры этого конструктора и (для primary) его поля;
		// поток — копия, как у цепочки: пролог не должен влиять на тело.
		var previousFlow = _flow;
		var previousNoneReadDepth = _noneReadDepth;
		List<CallArgument> callArguments;
		Dictionary<CallArgument, BoundExpression> byCallArgument;
		_flow = previousFlow.Clone();
		try
		{
			var (_, calls, map) = BindArguments(nodes);
			callArguments = calls;
			byCallArgument = map;
		}
		finally
		{
			_flow = previousFlow;
			_noneReadDepth = previousNoneReadDepth;
		}

		// Получатель разрешения — сам родитель `Base<T>`, а не наследник: так
		// GenericInference связывает `<T>` родителя с аргументами из заголовка `[Base<T>]`
		// и находит синтезированный конструктор без параметров.
		var call = new CallArguments(callArguments, baseType, explicitTypeArguments: null, span, baseType.Symbol.Name);
		var resolution = OverloadResolver.Resolve(candidates, call);
		if (!resolution.Success)
		{
			_diagnostics.AddRange(resolution.Diagnostics);
			return null;
		}

		var ordered = new List<BoundExpression>(resolution.Arguments.Count);
		foreach (var argument in resolution.Arguments)
		{
			if (argument.Argument is null)
			{
				if (BindDefaultArgument(argument.Parameter, span) is not { } defaultValue)
				{
					Report("KRN2108", $"У параметра '{argument.Parameter.Name}' нет значения по умолчанию", span);
					return null;
				}

				ordered.Add(defaultValue);
				continue;
			}

			var expression = byCallArgument[argument.Argument];
			ordered.Add(ConvertWithConversion(expression, argument.Parameter.Type, argument.Conversion, expression.Span));
		}

		return new BoundBaseConstructorCall(resolution.Function!, ordered, span);
	}

	/// <summary>
	/// Узел родителя из заголовка типа. Сопоставление по имени: <see cref="TypeSymbol.Bases"/>
	/// не хранит синтаксис, а порядок после ошибок разрешения не совпадает с исходным.
	/// </summary>
	private static ParentTypeNode? ParentNodeOf(TypeSymbol owner, TypeSymbol parent) =>
		owner.Declaration is TypeDeclaration declaration
			? declaration.Parents.FirstOrDefault(node =>
				node.Type is NamedTypeSyntax named && named.Name == parent.Name)
			: null;
}
