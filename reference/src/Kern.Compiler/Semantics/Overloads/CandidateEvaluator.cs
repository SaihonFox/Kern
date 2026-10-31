using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Проверка применимости одного кандидата (как «applicable function member» в C#): generic-арность, сопоставление аргументов
/// параметрам (позиционные, именованные, значения по умолчанию, variadic в нормальной/расширенной форме), вывод generic-аргументов,
/// неявные преобразования аргументов, ограничения generic-параметров. Возвращаемый тип в проверке не участвует.
/// </summary>
internal static class CandidateEvaluator
{
	public readonly record struct Outcome(ResolvedCall? Call, RejectedCandidate? Rejection);

	public static Outcome Evaluate(FunctionSymbol f, OverloadRequest req)
	{
		var typeArgs = req.ExplicitTypeArguments;
		if (typeArgs.Count != 0 && typeArgs.Count != f.TypeParams.Count)
		{
			var msg = f.TypeParams.Count == 0
				? $"'{f.Name}' не generic, но заданы generic-аргументы"
				: $"ожидается generic-аргументов: {f.TypeParams.Count}, задано: {typeArgs.Count}";
			return Reject(f, RejectionReason.GenericArity, msg);
		}
		if (f.IsExtension && req.Receiver is null)
			return Reject(f, RejectionReason.ReceiverMismatch, "расширение вызывается только через получателя `значение.имя(...)`");

		var normal = TryForm(f, req, expanded: false);
		if (normal.Call is not null || !f.Parameters.Any(p => p.IsVariadic)) return normal;
		// C#: расширенная форма рассматривается, только если нормальная неприменима.
		return TryForm(f, req, expanded: true);
	}

	private static Outcome Reject(FunctionSymbol f, RejectionReason reason, string message, int? arg = null) =>
		new(null, new RejectedCandidate(f, reason, message, arg));

	private static string ArgLabel(OverloadRequest req, int i) =>
		req.Arguments[i].Name is { } n ? $"аргумент {i + 1} ('{n}')" : $"аргумент {i + 1}";

	private static Outcome TryForm(FunctionSymbol f, OverloadRequest req, bool expanded)
	{
		var args = req.Arguments;
		int n = f.Parameters.Count;
		int variadic = f.Parameters.FindIndex(p => p.IsVariadic);

		// ---------- 1. сопоставление аргументов параметрам
		var perParam = new List<int>?[n];
		var argToParam = new int[args.Count];
		bool namedSeen = false;
		int pos = 0;
		for (int i = 0; i < args.Count; i++)
		{
			var a = args[i];
			int param;
			if (a.Name is null)
			{
				if (namedSeen)
					return Reject(f, RejectionReason.PositionalAfterNamed, $"{ArgLabel(req, i)}: позиционный аргумент после именованного", i);
				if (expanded && variadic >= 0 && pos >= variadic) param = variadic;
				else if (pos < n) param = pos++;
				else return Reject(f, RejectionReason.TooManyArguments, $"слишком много аргументов: параметров {n}, передано {args.Count}", i);
			}
			else
			{
				namedSeen = true;
				param = f.Parameters.FindIndex(p => p.Name == a.Name);
				if (param < 0)
					return Reject(f, RejectionReason.UnknownNamedArgument, $"нет параметра с именем '{a.Name}'", i);
				if (expanded && param == variadic)
					return Reject(f, RejectionReason.UnknownNamedArgument, $"variadic-параметр '{a.Name}' нельзя задать по имени в расширенной форме", i);
			}

			if (perParam[param] is { } existing)
			{
				if (expanded && param == variadic && a.Name is null) existing.Add(i);
				else return Reject(f, RejectionReason.DuplicateArgument, $"параметр '{f.Parameters[param].Name}' получает значение дважды", i);
			}
			else perParam[param] = [i];
			argToParam[i] = param;
		}

		for (int j = 0; j < n; j++)
		{
			if (perParam[j] is not null) continue;
			var p = f.Parameters[j];
			if (p.IsVariadic) { if (expanded) continue; }
			else if (p.HasDefault) continue;
			return Reject(f, RejectionReason.MissingArgument, $"не передан обязательный аргумент '{p.Name}'");
		}

		// ---------- 2. подстановка владельца и вывод generic-аргументов
		var map = OwnerMapFor(f, req);
		var inferable = new List<TypeParamSymbol>();
		var explicitArgs = req.ExplicitTypeArguments;
		if (explicitArgs.Count > 0)
			for (int i = 0; i < explicitArgs.Count; i++) map[f.TypeParams[i]] = explicitArgs[i];
		else
			inferable.AddRange(f.TypeParams);

		if (f.ExtensionOf is { } extType)
		{
			var free = new HashSet<TypeParamSymbol>();
			TypeShape.CollectTypeParams(extType, free);
			foreach (var tp in free)
				if (!map.ContainsKey(tp) && !inferable.Contains(tp)) inferable.Add(tp);
		}

		TypeSymbol DeclaredOf(int param) =>
			expanded && param == variadic ? f.Parameters[param].Type : f.Parameters[param].EffectiveType;

		var inferrer = new TypeInferrer(inferable);
		if (f.ExtensionOf is not null) inferrer.InferFrom(f.ExtensionOf, req.Receiver!);
		for (int i = 0; i < args.Count; i++) inferrer.InferFrom(DeclaredOf(argToParam[i]), args[i].Type);
		if (inferrer.HasInferable && !inferrer.TryFix(map, out var inferError))
			return Reject(f, RejectionReason.InferenceFailed, inferError!);

		// ---------- 3. неявные преобразования
		ArgumentConversion? receiverConversion = null;
		TypeSymbol? receiverTarget = null;
		if (f.ExtensionOf is { } ext)
		{
			receiverTarget = ext.Substitute(map);
			var kind = Conversions.Classify(req.Receiver!, receiverTarget);
			if (kind == ConversionKind.None)
				return Reject(f, RejectionReason.ReceiverMismatch,
					$"получатель типа '{req.Receiver!.ToDisplayString()}' не приводится к '{receiverTarget.ToDisplayString()}'");
			receiverConversion = new ArgumentConversion(kind, false);
		}

		var targets = new TypeSymbol[args.Count];
		var declared = new TypeSymbol[args.Count];
		var conversions = new ArgumentConversion[args.Count];
		for (int i = 0; i < args.Count; i++)
		{
			declared[i] = DeclaredOf(argToParam[i]);
			targets[i] = declared[i].Substitute(map);
			var conv = Convert(args[i], targets[i]);
			if (conv is null)
				return Reject(f, RejectionReason.ArgumentConversion,
					$"{ArgLabel(req, i)}: нельзя неявно преобразовать '{args[i].Type.ToDisplayString()}' в '{targets[i].ToDisplayString()}'", i);
			conversions[i] = conv;
		}

		// ---------- 4. ограничения (часть применимости, как в C# 7.3+)
		foreach (var tp in f.TypeParams)
			if (map.TryGetValue(tp, out var arg) && !Conversions.SatisfiesConstraints(tp, arg, map, out var reason))
				return Reject(f, RejectionReason.ConstraintViolated, reason!);

		// ---------- 5. слоты параметров
		var slots = new List<ParameterSlot>(n);
		for (int j = 0; j < n; j++)
		{
			var p = f.Parameters[j];
			if (perParam[j] is not { } idx)
			{
				var kind = p.IsVariadic ? ParameterSlotKind.VariadicPack : ParameterSlotKind.Default;
				var t = (p.IsVariadic ? p.Type : p.EffectiveType).Substitute(map);
				slots.Add(new ParameterSlot(p, kind, t, [], []));
				continue;
			}
			var slotKind = expanded && p.IsVariadic ? ParameterSlotKind.VariadicPack : ParameterSlotKind.Argument;
			slots.Add(new ParameterSlot(p, slotKind, targets[idx[0]], [.. idx], [.. idx.Select(i => conversions[i])]));
		}

		var order = new List<int>();
		foreach (var p in argToParam)
			if (!order.Contains(p)) order.Add(p);
		bool natural = order.Zip(order.Skip(1)).All(t => t.First < t.Second);

		var owner = f.IsExtension ? null : f.DeclaringType;
		IReadOnlyList<TypeSymbol> ownerArgs = owner is { TypeParams.Count: > 0 }
			? [.. owner.TypeParams.Select(tp => map.TryGetValue(tp, out var t) ? t : tp.AsType)]
			: [];

		var call = new ResolvedCall
		{
			Function = f,
			TypeArgs = [.. f.TypeParams.Select(tp => map.TryGetValue(tp, out var t) ? t : tp.AsType)],
			OwnerTypeArgs = ownerArgs,
			Substitution = map,
			Slots = slots,
			ArgumentToParameter = argToParam,
			EvaluationOrder = natural ? null : order,
			IsExpandedForm = expanded && variadic >= 0,
			ReceiverConversion = receiverConversion,
			ReturnType = (f.ReturnType ?? PrimitiveType.Void).Substitute(map),
			IsGenericInstantiation = f.TypeParams.Count > 0 || inferable.Count > 0,
			ArgumentTargets = targets,
			DeclaredTargets = declared,
			ReceiverTarget = receiverTarget,
			DeclaredReceiverTarget = f.ExtensionOf,
		};
		return new Outcome(call, null);
	}

	/// <summary>Неявное преобразование аргумента (включая константное приведение литерала); null — невозможно.</summary>
	private static ArgumentConversion? Convert(CallArgument arg, TypeSymbol target)
	{
		var kind = Conversions.Classify(arg.Type, target);
		if (kind != ConversionKind.None) return new ArgumentConversion(kind, false);
		if (arg.Literal is { } lit && ConstantConversions.CanConvert(lit, target))
			return new ArgumentConversion(target is NullableType ? ConversionKind.ToNullable : ConversionKind.ImplicitNumeric, true);
		return null;
	}

	// ------------------------------------------------------------ подстановка типа-владельца

	private static Dictionary<TypeParamSymbol, TypeSymbol> OwnerMapFor(FunctionSymbol f, OverloadRequest req)
	{
		var map = new Dictionary<TypeParamSymbol, TypeSymbol>();
		if (req.OwnerMap is not null)
		{
			foreach (var (k, v) in req.OwnerMap) map[k] = v;
		}
		else if (!f.IsExtension && f.DeclaringType is { TypeParams.Count: > 0 } d && req.Receiver is { } receiver
				 && FindOwnerInstance(receiver, d) is { } instance)
		{
			foreach (var (k, v) in instance.TypeMap()) map[k] = v;
		}
		return map;
	}

	/// <summary>Экземпляр объявления <paramref name="decl"/> среди самого получателя и его базовых типов (с подставленными аргументами).</summary>
	private static DeclaredType? FindOwnerInstance(TypeSymbol receiver, TypeDeclSymbol decl)
	{
		IEnumerable<TypeSymbol> roots = receiver switch
		{
			NullableType n => [n.Inner],
			TypeParamType tp => tp.Param.Bounds,
			_ => [receiver],
		};
		foreach (var root in roots)
		{
			if (root is DeclaredType d && ReferenceEquals(d.Decl, decl)) return d;
			foreach (var b in Conversions.BaseTypes(root).OfType<DeclaredType>())
				if (ReferenceEquals(b.Decl, decl)) return b;
		}
		return null;
	}
}
