using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Вызов конструктора прямого родителя (slice A1, spec §8). В корне цепочки конструкторов
/// наследника пролог сначала инициализирует родительскую часть (<c>[A]</c> — это <c>[A()]</c>,
/// <c>[A(x)]</c> передаёт аргументы из параметров конструктора наследника), затем выполняются
/// собственные инициализаторы наследника. Родитель инициализируется до наследника (spec §2).
/// </summary>
public sealed partial class Binder
{
	/// <summary>
	/// Привязывает вызов прямого родителя для корневого конструктора. <c>null</c> — родителя
	/// нет, у конструктора есть цепочка <c>: $(...)</c> (родителя инициализирует её корень)
	/// или конструкторов у родителя нет.
	/// </summary>
	private BoundBaseConstructorCall? BindBaseConstructor(FunctionSymbol constructor)
	{
		if (constructor.Owner is not { } owner || owner.IsStatic)
			return null;
		if (Inheritance.DirectBase(owner) is not { } baseType)
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

		var call = new CallArguments(callArguments, InstanceType(owner), explicitTypeArguments: null, span, baseType.Symbol.Name);
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
