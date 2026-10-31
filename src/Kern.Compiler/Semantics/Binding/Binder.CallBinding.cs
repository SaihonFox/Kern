using Kern.Binding.Formatting;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	private BoundExpression BindCall(CallExpression call)
	{
		var arguments = new CallArgumentNode[call.Arguments.Count];
		for (var index = 0; index < call.Arguments.Count; index++)
			arguments[index] = new CallArgumentNode(null, call.Arguments[index], call.Arguments[index].Span);
		return BindCall(call.Target, arguments, [], call.Span);
	}

	private BoundExpression BindStructuredCall(StructuredCallExpression call) =>
		BindCall(call.Target, call.Arguments, call.TypeArguments, call.Span);

	private BoundExpression BindGenericCall(GenericCallExpression call)
	{
		var arguments = new CallArgumentNode[call.Arguments.Count];
		for (var index = 0; index < call.Arguments.Count; index++)
			arguments[index] = new CallArgumentNode(null, call.Arguments[index], call.Arguments[index].Span);
		return BindCall(call.Target, arguments, call.TypeArguments, call.Span);
	}

	private BoundExpression BindCall(
		ExpressionNode target,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		if (target is MemberAccessExpression member)
		{
			// `T.method(...)`: приёмник — имя типа, а не выражение (M3-D3b). Локальная
			// переменная с тем же именем важнее типа — это решает TypeReceiverOf.
			return TypeReceiverOf(member.Target) is { } staticType
				? BindTypeQualifiedCall(staticType, member, arguments, typeArguments, span)
				: BindMemberCall(member, arguments, typeArguments, span);
		}

		if (target is not IdentifierExpression identifier)
		{
			// Немедленный вызов лямбды (fun(...) ...)(...) или значения в скобках.
			if (target is ParenthesizedExpression or LambdaExpression or SelfExpression)
				return BindValueCall(target, null, arguments, typeArguments, span);

			Report("KRN2109", "Такая цель вызова пока не поддерживается (M3-D)", span);
			return new BoundErrorExpression(span);
		}

		// Локальная или захваченная переменная функционального типа важнее одноимённой функции.
		if (_scope.Lookup(identifier.Name) is { } variable && variable.Type is FunctionType)
			return BindValueCall(identifier, variable, arguments, typeArguments, span);

		// Рефлексия времени компиляции (spec §17/§18): `typeof<T>()`/`name_of<T>()`.
		// Пользовательская функция с тем же именем важнее, если аргументов-типов нет.
		if (TryBindMetaCall(identifier.Name, arguments, typeArguments, span) is { } metaCall)
			return metaCall;

		// Метод своего экземпляра без `$` (spec §8): `hello()`, `area()` внутри тела типа.
		if (TryBindImplicitMemberCall(identifier.Name, arguments, typeArguments, span) is { } implicitCall)
			return implicitCall;

		// Встроенные Ok/Err: значение без синтезированного конструктора (spec §16).
		if (identifier.Name is ResultTypes.OkName or ResultTypes.ErrName &&
			VisibleType(identifier.Name) is { } resultVariant &&
			ResultTypes.IsResultVariant(resultVariant))
		{
			return BindResultConstruction(resultVariant, identifier.Name == ResultTypes.OkName, arguments, span);
		}

		if (VisibleType(identifier.Name) is { } type)
		{
			// Нативный тип без конструкторов, у которого есть одноимённая модульная функция, —
			// это фабрика (`regex("…")`, `streamwriter("…")`): имя типа не должно её перекрывать
			// (spec §12.9). Настоящий тип без конструктора по-прежнему сообщает KRN2103.
			if (type.Kind == Types.SymbolKind.Native &&
				type.Members.Constructors.Count == 0 &&
				VisibleFunctions(identifier.Name).Any(function => function.NativeSelfType is null))
			{
				return BindNamedCall(identifier.Name, arguments, typeArguments, span);
			}

			return BindConstructorCall(type, arguments, typeArguments, span);
		}

		return BindNamedCall(identifier.Name, arguments, typeArguments, span);
	}

	/// <summary>Создание <c>Ok(value)</c>/<c>Err(error)</c> для встроенного result (spec §16).</summary>
	private BoundExpression BindResultConstruction(
		TypeSymbol variant,
		bool isOk,
		IReadOnlyList<CallArgumentNode> arguments,
		TextSpan span)
	{
		if (arguments.Count != 1)
		{
			Report("KRN2101", $"'{variant.Name}' ожидает ровно одно значение", span);
			return new BoundErrorExpression(span);
		}

		var value = BindExpression(arguments[0].Value);
		if (IsError(value.Type))
			return new BoundErrorExpression(span);

		var type = new UnitType(variant, [value.Type], false);
		return new BoundResultConstructionExpression(
			value,
			ResultTypes.FieldOf(variant),
			isOk,
			type,
			span);
	}

	private BoundExpression BindNamedCall(
		string name,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		if (name is ResultTypes.OkName or ResultTypes.ErrName &&
			VisibleType(name) is { } resultVariant &&
			ResultTypes.IsResultVariant(resultVariant))
		{
			return BindResultConstruction(resultVariant, name == ResultTypes.OkName, arguments, span);
		}

		var candidates = new List<FunctionSymbol>(VisibleFunctions(name));
		// Без квалификации внутри любого члена своего типа видны и статические методы
		// этого типа (spec §8: `#fun` — это `static`; M3-D3b). Остальные кандидаты не
		// выбрасываем: порядок в списке не задаёт приоритет, его решает обычный выбор
		// перегрузки (OverloadResolver). Поэтому более точная сигнатура побеждает, а
		// совпадающие сигнатуры дают KRN1402, а не «тихую» подмену модульной функции.
		if (_current?.Owner is { } owner)
			candidates.AddRange(owner.Members.GetFunctions(name).Where(function => function.IsStatic));
		if (!_module.NativeFunctions.Any(function => function.NativeSelfType is null && function.Name == name))
			candidates.AddRange(BuiltinCatalog.Where(function => function.Name == name));

		return BindTargetlessCall(candidates, name, arguments, typeArguments, span);
	}

	private BoundExpression BindMemberCall(
		MemberAccessExpression member,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var receiver = member.Target is SelfExpression self
			? BindSelf(self)
			: BindExpression(member.Target);
		if (IsError(receiver.Type))
			return new BoundErrorExpression(span);

		// `obj?.method(...)`: при nil результатом становится nil (spec §3).
		if (member.IsConditional && receiver.Type is NullableType nullable)
		{
			return BindConditionalAccess(receiver, nullable, span, temporary =>
				BindMemberCallOn(temporary, member with { IsConditional = false }, arguments, typeArguments, span));
		}

		return BindMemberCallOn(receiver, member, arguments, typeArguments, span);
	}

	private BoundExpression BindMemberCallOn(
		BoundExpression receiver,
		MemberAccessExpression member,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var owner = OwnerOf(receiver.Type);
		var candidates = new List<FunctionSymbol>();
		var fromNative = false;
		if (owner is not null)
		{
			var declared = owner.Members.GetFunctions(member.Member);
			// Статический метод через экземпляр — неверное направление (M3-D3b).
			if (declared.Count > 0 && declared.All(function => function.IsStatic))
			{
				Report(
					"KRN2103",
					$"Статический член '{member.Member}' нельзя вызывать через экземпляр; нужен тип '{owner.Name}'",
					member.Span);
				return new BoundErrorExpression(span);
			}

			candidates.AddRange(declared.Where(function => !function.IsStatic));
		}
		if (candidates.Count == 0)
		{
			fromNative = true;
			candidates.AddRange(NativeMembers(receiver.Type, member.Member)
				.Where(function => function.Kind is not (FunctionKind.Getter or FunctionKind.Setter)));
		}
		if (candidates.Count == 0)
			foreach (var extensions in _moduleScope.FindExtensionsFor(receiver.Type))
				candidates.AddRange(extensions.GetFunctions(member.Member));
		if (candidates.Count == 0)
		{
			// B6b1/B8a/B8c: протоколы hash/compare/clone идут после обычных и нативных членов, но до KRN2103.
			if (TryBindProtocolMember(receiver, member.Member, arguments, span) is { } protocol)
				return protocol;
			if (owner is null)
				Report("KRN2103", $"У типа '{receiver.Type}' нет методов", member.Span);
			else
				Report("KRN2103", $"Неизвестный метод '{member.Member}' у типа '{receiver.Type}'", member.Span);
			return new BoundErrorExpression(span);
		}

		var (boundArguments, callArguments, byCallArgument) = BindArguments(arguments);
		IReadOnlyList<KernType>? explicitTypes = null;
		if (typeArguments.Count > 0)
			explicitTypes = _resolver.ResolveAll(typeArguments, _typeScope);

		var call = new CallArguments(callArguments, receiver.Type, explicitTypes, span, member.Member);
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

		WarnDeprecated(resolution.Function!, span);
		var returnType = CallResultType(resolution.Function!, resolution.ReturnType);
		if (fromNative && receiver.Type is BuiltinGenericType builtin)
		{
			var nativeArgumentTypes = new List<KernType>(resolution.Arguments.Count);
			foreach (var argument in resolution.Arguments)
				if (argument.Argument is { } callArgument)
					nativeArgumentTypes.Add(callArgument.Type);

			var substituted = BuiltinMemberSignatures.ResolveReturnType(
				builtin,
				member.Member,
				nativeArgumentTypes,
				out string? substitutionError);
			if (substitutionError is not null)
			{
				Report("KRN2102", substitutionError, span);
				return new BoundErrorExpression(span);
			}

			if (substituted is not null)
				returnType = substituted;
		}

		// Выбор родителя $[A].foo — статическая привязка именно к реализации A: без
		// динамического поиска по наследникам (иначе селектор мог бы «переехать»).
		var isParentSelection = owner is not null && owner.Members.IsParentSelection(resolution.Function!);
		return new BoundCallExpression(
			resolution.Function!,
			ordered,
			returnType,
			span,
			receiver)
		{
			Virtual = IsVirtualCall(receiver, resolution.Function!) && !isParentSelection,
		};
	}

	private BoundExpression BindConstructorCall(
		TypeSymbol type,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var candidates = new List<FunctionSymbol>(type.Members.Constructors);
		if (candidates.Count == 0)
		{
			Report("KRN2103", $"У типа '{type.Name}' нет конструктора", span);
			return new BoundErrorExpression(span);
		}

		var (_, callArguments, byCallArgument) = BindArguments(arguments);
		IReadOnlyList<KernType>? explicitTypes = typeArguments.Count > 0
			? _resolver.ResolveAll(typeArguments, _typeScope)
			: null;
		var constructedArguments = type.GenericParameters.Count == 0
			? []
			: explicitTypes ?? InferConstructorTypeArguments(type, callArguments);
		var instanceType = Instantiate(type, constructedArguments);

		var call = new CallArguments(callArguments, instanceType, explicitTypes, span, type.Name);

		// Проверяем вызов по конструкторам с подставленными выведенными аргументами,
		// а в bound-узел кладём исходный символ (у него есть байткод).
		var resolvedCandidates = candidates;
		var substituted = new List<FunctionSymbol>();
		if (type.GenericParameters.Count > 0 &&
			constructedArguments.All(argument => !(argument is ErrorType)))
		{
			var map = new Dictionary<string, KernType>(StringComparer.Ordinal);
			for (var index = 0; index < type.GenericParameters.Count && index < constructedArguments.Count; index++)
				map[type.GenericParameters[index].Name] = constructedArguments[index];
			foreach (var candidate in candidates)
				substituted.Add(SubstituteConstructor(candidate, map));
			resolvedCandidates = substituted;
		}

		var resolution = OverloadResolver.Resolve(resolvedCandidates, call);
		if (!resolution.Success)
		{
			// Один generic-конструктор и позиционные аргументы: связываем напрямую после
			// подстановки выведенных аргументов (resolver не всегда принимает выведенный T).
			if (substituted.Count == 1 && callArguments.Count == substituted[0].Parameters.Count &&
				TryBindGenericConstructorArguments(substituted[0], callArguments, byCallArgument) is { } manual)
			{
				WarnDeprecatedType(type, span);
				return new BoundObjectCreationExpression(type, candidates[0], manual, instanceType, span);
			}

			_diagnostics.AddRange(resolution.Diagnostics);
			return new BoundErrorExpression(span);
		}

		var constructor = resolution.Function!;
		if (substituted.Count > 0)
		{
			var position = substituted.IndexOf(constructor);
			if (position >= 0)
				constructor = candidates[position];
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

		WarnDeprecatedType(type, span);
		return new BoundObjectCreationExpression(type, constructor, ordered, instanceType, span);
	}

	private (
		List<BoundExpression> Bound,
		List<CallArgument> Calls,
		Dictionary<CallArgument, BoundExpression> ByCallArgument) BindArguments(
		IReadOnlyList<CallArgumentNode> arguments)
	{
		var bound = new List<BoundExpression>(arguments.Count);
		var calls = new List<CallArgument>(arguments.Count);
		var map = new Dictionary<CallArgument, BoundExpression>(ReferenceEqualityComparer.Instance);
		foreach (var argument in arguments)
		{
			var expression = BindExpression(argument.Value);
			var constant = (expression as BoundLiteralExpression)?.Numeric;
			var callArgument = argument.Name is null
				? CallArgument.Positional(expression.Type, constant, argument.Span)
				: CallArgument.Named(argument.Name, expression.Type, constant, argument.Span);
			bound.Add(expression);
			calls.Add(callArgument);
			map[callArgument] = expression;
		}

		return (bound, calls, map);
	}
}
