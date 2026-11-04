using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// B8a/B8c: материализация <c>#[derive(hash/compare/clone)]</c> в обычные члены типа.
/// Раньше протокол работал только через специальный путь binder'а и не попадал в
/// <c>MemberTable</c>, <c>.kbc</c> и рефлексию. Теперь для каждого derived-протокола
/// создаётся синтетический оператор с настоящим телом (<c>ValueHash</c>/<c>ValueCompare</c>/
/// <c>CloneObject</c>), поэтому <c>type.methods</c>/<c>get_method</c> его видят, а вызов
/// остаётся тем же.
/// </summary>
public sealed partial class Binder
{
	private void MaterializeDerivedProtocols(List<BoundFunction> functions)
	{
		foreach (TypeSymbol type in _module.Types.Values)
		{
			if (type.Declaration is not { } declaration)
				continue;

			var protocols = new List<FunctionSymbol>();
			TextSpan span = declaration.Span;
			KernType selfType = type.IsValueType
				? new UnitType(type, [.. type.GenericParameters], type.IsImmutable)
				: new ClassType(type, [.. type.GenericParameters]);

			if (DeriveCatalog.Has(declaration, DerivedProtocol.Hash) && !HasManualHash(type))
			{
				BoundFunction function = CreateProtocolFunction(
					type,
					HashOperator,
					[],
					PrimitiveType.U64,
					selfType,
					self => new BoundHashExpression(self, PrimitiveType.U64, span, Structural: !type.IsValueType),
					span);
				functions.Add(function);
				protocols.Add(function.Symbol);
			}

			if (DeriveCatalog.Has(declaration, DerivedProtocol.Compare) && !HasManualCompare(type))
			{
				var parameter = new ParameterSymbol("other", selfType, null, false, false, span);
				BoundFunction function = CreateProtocolFunction(
					type,
					CompareOperator,
					[parameter],
					PrimitiveType.I32,
					selfType,
					self => new BoundCompareExpression(
						self,
						new BoundVariableExpression("other", 1, selfType, span),
						PrimitiveType.I32,
						span,
						Structural: !type.IsValueType),
					span);
				functions.Add(function);
				protocols.Add(function.Symbol);
			}

			if (DeriveCatalog.Has(declaration, DerivedProtocol.Clone) && !HasManualClone(type))
			{
				BoundFunction function = CreateProtocolFunction(
					type,
					CloneOperator,
					[],
					selfType,
					selfType,
					self => type.IsValueType ? self : new BoundCloneExpression(self, selfType, span),
					span);
				functions.Add(function);
				protocols.Add(function.Symbol);
			}

			if (protocols.Count > 0)
				type.Members = type.Members.WithOperators([.. type.Members.Operators, .. protocols]);
		}
	}

	/// <summary>Синтетический оператор протокола: слот 0 — <c>$</c>, дальше параметры.</summary>
	private static BoundFunction CreateProtocolFunction(
		TypeSymbol type,
		string name,
		IReadOnlyList<ParameterSymbol> parameters,
		KernType returnType,
		KernType selfType,
		Func<BoundExpression, BoundExpression> body,
		TextSpan span)
	{
		var symbol = new FunctionSymbol(name, FunctionKind.Operator)
		{
			Owner = type,
			IsStatic = false,
			IsOpen = type.IsOpen,
			OpSymbol = name,
			Parameters = parameters,
			ReturnType = returnType,
		};

		var boundParameters = new List<BoundParameter>(parameters.Count + 1)
		{
			new(new ParameterSymbol("$", selfType, null, false, false, span), 0),
		};
		for (var index = 0; index < parameters.Count; index++)
			boundParameters.Add(new BoundParameter(parameters[index], index + 1));

		var self = new BoundVariableExpression("$", 0, selfType, span);
		var block = new BoundBlock([], body(self), FallsThrough: false, span);
		return new BoundFunction(symbol, boundParameters, block, returnType, parameters.Count + 1);
	}
}
