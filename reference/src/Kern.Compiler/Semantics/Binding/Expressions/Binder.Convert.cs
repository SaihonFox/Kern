using System.Numerics;
using Kern.Bytecode;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	/// <summary>Неявное приведение значения к целевому типу; при невозможности — ошибка KRN2001.</summary>
	public BoundExpr Convert(BoundExpr e, TypeSymbol target, TextSpan span, bool report = true)
	{
		if (e.IsError || target.IsError) return e;
		if (TryConvert(e, target, out var result)) return result;
		if (report)
			Error(Krn.TypeMismatch, $"нельзя неявно преобразовать '{e.Type.ToDisplayString()}' в '{target.ToDisplayString()}'", span);
		return new BoundErrorExpr(span);
	}

	public bool TryConvert(BoundExpr e, TypeSymbol target, out BoundExpr result)
	{
		result = e;
		if (e.IsError || target.IsError) return true;

		// константное приведение литерала без суффикса
		if (e is BoundNumberLiteral { Value.Suffix: null } lit && lit.Type is PrimitiveType lp && !lp.Equals(Unwrap(target)))
		{
			if (ConstantConversions.CanConvert(lit.Value, target))
			{
				var inner = Unwrap(target);
				if (inner is PrimitiveType { IsUniversalNumeric: true })
				{
					result = target is NullableType ? new BoundConversion(lit, ConversionKind.ToNullable, true, target, e.Span) : lit;
					return true;
				}
				var retyped = new BoundNumberLiteral(lit.Value, inner, lit.Span);
				result = target is NullableType ? new BoundConversion(retyped, ConversionKind.ToNullable, true, target, e.Span) : retyped;
				return true;
			}
		}

		var kind = Conversions.Classify(e.Type, target);
		if (kind == ConversionKind.None) return false;
		result = kind == ConversionKind.Identity ? e : new BoundConversion(e, kind, false, target, e.Span);
		return true;
	}

	private static TypeSymbol Unwrap(TypeSymbol t) => t is NullableType n ? n.Inner : t;

	public BoundExpr ConvertToBool(BoundExpr e, TextSpan span, string what)
	{
		if (e.IsError) return e;
		if (e.Type is PrimitiveType { Kind: PrimKind.Bool }) return e;
		// `if x` для nullable/ссылочных значений: «значение есть» — допустимо
		if (e.Type is NullableType) return e;
		Error(Krn.ConditionNotBool, $"{what} должно иметь тип bool, получено '{e.Type.ToDisplayString()}'", span);
		return new BoundErrorExpr(span);
	}

	/// <summary>Тип по умолчанию для числового литерала без суффикса.</summary>
	public static PrimitiveType DefaultNumberType(NumberValue v)
	{
		if (v.IsFloat) return PrimitiveType.F64;
		if (v.Integer >= int.MinValue && v.Integer <= int.MaxValue) return PrimitiveType.I32;
		if (v.Integer >= long.MinValue && v.Integer <= long.MaxValue) return PrimitiveType.I64;
		return PrimitiveType.U64;
	}

	/// <summary>Тип значения, подходящий для хранения в переменной/коллекции: Never→error, nil/none без контекста — ошибка вывода.</summary>
	public TypeSymbol InferStorageType(BoundExpr e, TextSpan span)
	{
		switch (e.Type)
		{
			case NilType:
				Error(Krn.CannotInferVariableType, "по значению `nil` нельзя вывести тип; укажите его явно (`T? x = nil`)", span);
				return ErrorType.Instance;
			case NoneType:
				Error(Krn.CannotInferVariableType, "по значению `none` нельзя вывести тип", span);
				return ErrorType.Instance;
			case NeverType:
				Error(Krn.CannotInferVariableType, "выражение не возвращает значения; тип переменной вывести нельзя", span);
				return ErrorType.Instance;
			case PrimitiveType { Kind: PrimKind.Void }:
				Error(Krn.CannotInferVariableType, "выражение не имеет значения (void)", span);
				return ErrorType.Instance;
			default:
				return e.Type;
		}
	}

	public static BoundNumberLiteral NegateLiteral(BoundNumberLiteral n) =>
		new(new NumberValue(BigInteger.Negate(n.Value.Integer), -n.Value.Float, n.Value.IsFloat, n.Value.Suffix), n.Type, n.Span);
}
