using Kern.Bytecode;
using Kern.Compiler.Semantics.Binding.Members;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	// ============================================================ поиск оператора

	private sealed record OperatorHit(FunctionSymbol Function, ResolvedCall Call);

	/// <summary>Ищет оператор `op&lt;symbol&gt;` у типа левого операнда (члены + расширения). null — не найден.</summary>
	private OperatorHit? FindOperator(string symbol, BoundExpr left, IReadOnlyList<BoundExpr> rest, TextSpan span, bool report)
	{
		if (left.IsError || rest.Any(r => r.IsError)) return null;
		var leftType = left.Type is NullableType n ? n.Inner : left.Type;
		string name = "op" + symbol;

		var ms = Members.FindInstance(leftType, name, File);
		var candidates = new List<FunctionSymbol>(ms.Functions);
		foreach (var f in Extensions.Operators(symbol))
			if (f.ExtensionOf is { } eo && MemberLookup.Unify(eo, leftType, []) && !candidates.Contains(f)) candidates.Add(f);
		if (candidates.Count == 0) return null;

		var args = rest.Select(r => new CallArgument(r.Type, r.Span, null, r is BoundNumberLiteral { Value.Suffix: null } lit ? lit.Value : null)).ToList();
		var request = new OverloadRequest(args, null, leftType, ms.OwnerMap);
		var result = OverloadResolver.ResolveTiers(
			[candidates.Where(f => !f.IsExtension).ToList(), candidates.Where(f => f.IsExtension).ToList()], request);
		if (result.IsSuccess) return new OperatorHit(result.Selected!.Function, result.Selected!);
		if (report) OverloadDiagnostics.Report(Project.Diagnostics, File.File, span, name, result, request);
		return null;
	}

	private BoundExpr OperatorArg(OperatorHit hit, int index, BoundExpr value) =>
		index < hit.Call.Slots.Count ? Convert(value, hit.Call.Slots[index].ParameterType, value.Span) : value;

	/// <summary>Примитив операнда; параметр типа с числовым ограничением (`T[num]`) ведёт себя как это ограничение.</summary>
	private static PrimitiveType? Prim(TypeSymbol t)
	{
		var x = t is NullableType n ? n.Inner : t;
		if (x is TypeParamType tp)
			x = tp.Param.Bounds.OfType<PrimitiveType>().FirstOrDefault(b => b.IsNumeric || b.IsUniversalNumeric);
		return x as PrimitiveType;
	}

	// ============================================================ унарные

	private BoundExpr BindUnary(Syn.UnaryExpr u, TypeSymbol? expected)
	{
		// отрицательный числовой литерал сворачивается сразу
		if (u.Op == Syn.UnaryOp.Negate && u.Operand is Syn.NumberLitExpr)
		{
			var lit = Bind(u.Operand);
			if (lit is BoundNumberLiteral nl) return NegateLiteral(nl) with { Span = u.Span };
			return lit;
		}

		var operand = Bind(u.Operand);
		if (operand.IsError) return new BoundUnary(u.Op, null, operand, ErrorType.Instance, u.Span);

		string sym = u.Op switch { Syn.UnaryOp.Plus => "+", Syn.UnaryOp.Negate => "-", _ => "!" };
		var p = Prim(operand.Type);

		if (u.Op == Syn.UnaryOp.Not)
		{
			if (operand.Type is PrimitiveType { Kind: PrimKind.Bool })
				return new BoundUnary(u.Op, null, operand, PrimitiveType.Bool, u.Span);
			if (operand.Type is NullableType && operand is BoundLocal or BoundParameter or BoundField or BoundProperty)
				return new BoundUnary(u.Op, null, operand, PrimitiveType.Bool, u.Span); // `!x` — «значения нет» (nil/none)
		}
		else if (p is { IsNumeric: true } or { IsUniversalNumeric: true } && operand.Type is not NullableType)
		{
			if (u.Op == Syn.UnaryOp.Negate && p.IsUnsignedInt)
				return ErrorExpr(Krn.NoOperator, $"унарный минус неприменим к беззнаковому типу '{p.Name}'", u.Span);
			return new BoundUnary(u.Op, null, operand, operand.Type, u.Span);
		}

		var hit = FindOperator(sym, operand, [], u.Span, report: false);
		if (hit is not null)
			return new BoundUnary(u.Op, hit.Function, operand, hit.Call.ReturnType, u.Span);
		return ErrorExpr(Krn.NoOperator, $"оператор '{sym}' не определён для типа '{operand.Type.ToDisplayString()}'", u.Span);
	}

	// ============================================================ бинарные

	private BoundExpr BindBinary(Syn.BinaryExpr b, TypeSymbol? expected)
	{
		switch (b.Op)
		{
			case Syn.BinaryOp.And or Syn.BinaryOp.Or:
				return BindLogical(b);
			case Syn.BinaryOp.Elvis:
				return BindElvis(b, expected);
			case Syn.BinaryOp.Eq or Syn.BinaryOp.Ne:
				return BindEquality(b);
		}

		var left = Bind(b.Left);
		var right = Bind(b.Right, b.Op is Syn.BinaryOp.In or Syn.BinaryOp.NotIn ? null : (left.IsError ? null : left.Type));
		if (left.IsError || right.IsError)
			return new BoundBinary(b.Op, null, left, right, ErrorType.Instance, b.Span);

		switch (b.Op)
		{
			case Syn.BinaryOp.RefEq or Syn.BinaryOp.RefNe:
				if (!IsReferenceType(left.Type) || !IsReferenceType(right.Type))
					return ErrorExpr(Krn.NoOperator, "`===`/`!==` применимы только к значениям ссылочных типов (`type`)", b.Span);
				return new BoundBinary(b.Op, null, left, right, PrimitiveType.Bool, b.Span);

			case Syn.BinaryOp.In or Syn.BinaryOp.NotIn:
				return BindMembership(b, left, right);

			case Syn.BinaryOp.Lt or Syn.BinaryOp.Gt or Syn.BinaryOp.Le or Syn.BinaryOp.Ge:
				return BindComparison(b, left, right);

			default:
				return BindArithmetic(b, left, right);
		}
	}

	private static bool IsReferenceType(TypeSymbol t) =>
		(t is NullableType n ? n.Inner : t) is ClassType or NativeType or ListType or DictType or ArrayType or TaskType or FuncType or UnionType
		|| t is NilType or NoneType;

	private BoundExpr BindLogical(Syn.BinaryExpr b)
	{
		var left = BindCondition(b.Left, b.Op == Syn.BinaryOp.And ? "левый операнд `&&`" : "левый операнд `||`");
		// правый операнд видит факты левого: `x != nil && x.len > 0`
		var saved = Flow.Current;
		var branch = Flow.Fork();
		ApplyCondition(left, whenTrue: b.Op == Syn.BinaryOp.And ? branch : null, whenFalse: b.Op == Syn.BinaryOp.Or ? branch : null);
		Flow.Current = branch;
		var right = BindCondition(b.Right, b.Op == Syn.BinaryOp.And ? "правый операнд `&&`" : "правый операнд `||`");
		// факты правого операнда не переживают выражение; присваивания — переживают консервативно (берём пересечение)
		Flow.Current = FlowJoin(saved, branch);
		return new BoundBinary(b.Op, null, left, right, PrimitiveType.Bool, b.Span);
	}

	private BoundExpr BindElvis(Syn.BinaryExpr b, TypeSymbol? expected)
	{
		var left = BindAllowingNone(b.Left, expected);
		var saved = Flow.Fork();
		var right = Bind(b.Right, expected ?? (left.Type is NullableType ln ? ln.Inner : null));
		Flow.Current = FlowJoin(saved, Flow.Current);
		if (left.IsError || right.IsError) return new BoundCoalesce(left, right, ErrorType.Instance, b.Span);

		var inner = left.Type is NullableType nt ? nt.Inner : left.Type;
		if (left.Type is NilType)
			return new BoundCoalesce(left, right, right.Type, b.Span);
		if (TryConvert(right, inner, out var conv))
			return new BoundCoalesce(left, conv, inner, b.Span);
		var common = Conversions.CommonType(inner, right.Type);
		if (common is not null && TryConvert(right, common, out var conv2))
			return new BoundCoalesce(Convert(left, common is NullableType ? left.Type : common, left.Span, false), conv2, common, b.Span);
		return ErrorExpr(Krn.TypeMismatch,
			$"справа от `?:` ожидается тип, совместимый с '{inner.ToDisplayString()}', получено '{right.Type.ToDisplayString()}'", b.Right.Span);
	}

	private BoundExpr BindEquality(Syn.BinaryExpr b)
	{
		bool noneCheck = b.Left is Syn.NoneLitExpr or Syn.NilLitExpr || b.Right is Syn.NoneLitExpr or Syn.NilLitExpr;
		var left = noneCheck ? BindAllowingNone(b.Left) : Bind(b.Left);
		var right = noneCheck ? BindAllowingNone(b.Right, left.IsError ? null : left.Type) : Bind(b.Right, left.IsError ? null : left.Type);
		if (left.IsError || right.IsError) return new BoundBinary(b.Op, null, left, right, PrimitiveType.Bool, b.Span);

		if (left is BoundNoneLiteral or BoundNilLiteral || right is BoundNoneLiteral or BoundNilLiteral)
		{
			var other = left is BoundNoneLiteral or BoundNilLiteral ? right : left;
			var lit = left is BoundNoneLiteral or BoundNilLiteral ? left : right;
			if (lit is BoundNilLiteral && other.Type is not (NullableType or NilType or ErrorType))
				Warn(Krn.TypeMismatch, $"сравнение с nil значения типа '{other.Type.ToDisplayString()}', которое не может быть nil", b.Span);
			return new BoundBinary(b.Op, null, left, right, PrimitiveType.Bool, b.Span);
		}

		// числа: приведение литерала/повышение
		var lp = Prim(left.Type);
		var rp = Prim(right.Type);
		if (lp is { IsNumeric: true } && rp is { IsNumeric: true } || (lp?.IsUniversalNumeric ?? false) || (rp?.IsUniversalNumeric ?? false))
		{
			if (TryUnifyNumeric(ref left, ref right, out _))
				return new BoundBinary(b.Op, null, left, right, PrimitiveType.Bool, b.Span);
		}

		var hit = FindOperator("==", left, [right], b.Span, report: false);
		if (hit is not null)
			return new BoundBinary(b.Op, hit.Function, left, OperatorArg(hit, 0, right), PrimitiveType.Bool, b.Span);

		// структурное равенство совместимых типов
		if (Conversions.IsImplicit(left.Type, right.Type) || Conversions.IsImplicit(right.Type, left.Type))
		{
			var common = Conversions.CommonType(left.Type, right.Type)!;
			return new BoundBinary(b.Op, null, Convert(left, common, left.Span), Convert(right, common, right.Span), PrimitiveType.Bool, b.Span);
		}
		return ErrorExpr(Krn.NoOperator, $"нельзя сравнить '{left.Type.ToDisplayString()}' и '{right.Type.ToDisplayString()}'", b.Span);
	}

	private BoundExpr BindComparison(Syn.BinaryExpr b, BoundExpr left, BoundExpr right)
	{
		string sym = b.Op switch { Syn.BinaryOp.Lt => "<", Syn.BinaryOp.Gt => ">", Syn.BinaryOp.Le => "<=", _ => ">=" };
		var lp = Prim(left.Type);
		var rp = Prim(right.Type);
		if (left.Type is not NullableType && right.Type is not NullableType)
		{
			if ((lp?.IsNumeric ?? false) || (lp?.IsUniversalNumeric ?? false) || (rp?.IsNumeric ?? false) || (rp?.IsUniversalNumeric ?? false))
			{
				if (TryUnifyNumeric(ref left, ref right, out _))
					return new BoundBinary(b.Op, null, left, right, PrimitiveType.Bool, b.Span);
			}
			if (lp is { Kind: PrimKind.Str or PrimKind.Char } && lp.Equals(rp))
				return new BoundBinary(b.Op, null, left, right, PrimitiveType.Bool, b.Span);
		}
		var hit = FindOperator(sym, left, [right], b.Span, report: false) ?? FindOperator("<=>", left, [right], b.Span, report: false);
		if (hit is not null)
			return new BoundBinary(b.Op, hit.Function, left, OperatorArg(hit, 0, right), PrimitiveType.Bool, b.Span);
		return ErrorExpr(Krn.NoOperator, $"оператор '{sym}' не определён для '{left.Type.ToDisplayString()}' и '{right.Type.ToDisplayString()}'", b.Span);
	}

	/// <summary>`"ab" * 3`, `'-' * 20`, `3 * "ab"`, `[1, 2] * 2`: повтор последовательности. Отрицательный множитель — panic во время выполнения.</summary>
	private BoundExpr? TryBindRepeat(Syn.BinaryExpr b, BoundExpr left, BoundExpr right)
	{
		static bool Repeatable(TypeSymbol t) => t is ListType or PrimitiveType { Kind: PrimKind.Str or PrimKind.Char };
		static bool Count(TypeSymbol t) => t is PrimitiveType { IsInteger: true } or PrimitiveType { Kind: PrimKind.INum };

		bool seqLeft;
		if (Repeatable(left.Type) && Count(right.Type)) seqLeft = true;
		else if (Count(left.Type) && Repeatable(right.Type)) seqLeft = false;
		else return null;

		var seq = seqLeft ? left : right;
		var count = seqLeft ? right : left;
		if (count.Type is PrimitiveType { Kind: PrimKind.INum } && !TryConvert(count, PrimitiveType.I32, out count))
			return ErrorExpr(Krn.TypeMismatch, "множитель повтора должен быть целым числом", count.Span);

		TypeSymbol result = seq.Type is ListType ? seq.Type : PrimitiveType.Str;
		return seqLeft
			? new BoundBinary(b.Op, null, seq, count, result, b.Span)
			: new BoundBinary(b.Op, null, count, seq, result, b.Span);
	}

	private BoundExpr BindArithmetic(Syn.BinaryExpr b, BoundExpr left, BoundExpr right)
	{
		string sym = b.Op switch
		{
			Syn.BinaryOp.Add => "+", Syn.BinaryOp.Sub => "-", Syn.BinaryOp.Mul => "*", Syn.BinaryOp.Div => "/",
			Syn.BinaryOp.Rem => "%", _ => "**",
		};
		var lp = Prim(left.Type);
		var rp = Prim(right.Type);

		if (left.Type is not NullableType && right.Type is not NullableType)
		{
			// повтор: str/char/list * целое и целое * str/char/list
			if (b.Op == Syn.BinaryOp.Mul && TryBindRepeat(b, left, right) is { } repeat) return repeat;

			// конкатенация строк и char
			if (b.Op == Syn.BinaryOp.Add && lp is { Kind: PrimKind.Str } && rp is { Kind: PrimKind.Str or PrimKind.Char })
				return new BoundBinary(b.Op, null, left, right, PrimitiveType.Str, b.Span);

			// конкатенация списков: list<T> + list<T> -> list<T> (новый список)
			if (b.Op == Syn.BinaryOp.Add && left.Type is ListType ll && right.Type is ListType rl)
			{
				if (ll.Element.Equals(rl.Element)) return new BoundBinary(b.Op, null, left, right, left.Type, b.Span);
				return ErrorExpr(Krn.NoOperator,
					$"нельзя сложить '{left.Type.ToDisplayString()}' и '{right.Type.ToDisplayString()}': типы элементов различаются", b.Span);
			}

			if (lp is { IsNumeric: true } or { IsUniversalNumeric: true } && rp is { IsNumeric: true } or { IsUniversalNumeric: true })
			{
				if (TryUnifyNumeric(ref left, ref right, out var common))
				{
					if (b.Op == Syn.BinaryOp.Pow && right.Type is PrimitiveType { IsFloat: true } && common is { IsInteger: true })
						return ErrorExpr(Krn.NoOperator, "возведение целого в дробную степень: приведите к f64", b.Span);
					return new BoundBinary(b.Op, null, left, right, left.Type is TypeParamType && left.Type.Equals(right.Type) ? left.Type : common, b.Span);
				}
				return ErrorExpr(Krn.NoOperator,
					$"нельзя применить '{sym}' к '{left.Type.ToDisplayString()}' и '{right.Type.ToDisplayString()}' без явного приведения", b.Span);
			}
		}

		var hit = FindOperator(sym, left, [right], b.Span, report: false);
		if (hit is not null)
			return new BoundBinary(b.Op, hit.Function, left, OperatorArg(hit, 0, right), hit.Call.ReturnType, b.Span);
		return ErrorExpr(Krn.NoOperator, $"оператор '{sym}' не определён для '{left.Type.ToDisplayString()}' и '{right.Type.ToDisplayString()}'", b.Span);
	}

	private BoundExpr BindMembership(Syn.BinaryExpr b, BoundExpr left, BoundExpr right)
	{
		bool negated = b.Op == Syn.BinaryOp.NotIn;
		var container = right.Type is NullableType n ? n.Inner : right.Type;
		FunctionSymbol? fn = null;

		var hit = FindOperator("in", right, [left], b.Span, report: false);
		if (hit is not null) fn = hit.Function;
		else
		{
			bool ok = container switch
			{
				ListType l => Conversions.IsImplicit(left.Type, l.Element),
				ArrayType a => Conversions.IsImplicit(left.Type, a.Element),
				DictType d => Conversions.IsImplicit(left.Type, d.Key),
				PrimitiveType { Kind: PrimKind.Str } => left.Type is PrimitiveType { Kind: PrimKind.Str or PrimKind.Char },
				_ => false,
			};
			if (!ok)
				return ErrorExpr(Krn.NoOperator, $"оператор `in` не определён для '{left.Type.ToDisplayString()}' и '{right.Type.ToDisplayString()}'", b.Span);
		}
		return new BoundBinary(b.Op, fn, hit is null ? left : OperatorArgSwap(hit, left), right, PrimitiveType.Bool, b.Span);
	}

	private BoundExpr OperatorArgSwap(OperatorHit hit, BoundExpr left) => OperatorArg(hit, 0, left);

	/// <summary>Приводит два числовых операнда к общему типу (повышение в семействе, константное приведение литерала).</summary>
	private bool TryUnifyNumeric(ref BoundExpr left, ref BoundExpr right, out PrimitiveType common)
	{
		common = PrimitiveType.I32;
		var lp = Prim(left.Type);
		var rp = Prim(right.Type);
		if (lp is null || rp is null) return false;
		if (lp.Equals(rp)) { common = lp; return true; }

		// литерал без суффикса подстраивается под другой операнд
		if (right is BoundNumberLiteral { Value.Suffix: null } && TryConvert(right, left.Type, out var r1)) { right = r1; common = lp; return true; }
		if (left is BoundNumberLiteral { Value.Suffix: null } && TryConvert(left, right.Type, out var l1)) { left = l1; common = rp; return true; }

		if (Conversions.IsNumericConversion(lp, rp)) { left = Convert(left, rp, left.Span); common = rp; return true; }
		if (Conversions.IsNumericConversion(rp, lp)) { right = Convert(right, lp, right.Span); common = lp; return true; }
		return false;
	}

	// ============================================================ is / !

	private BoundExpr BindTypeTest(Syn.TypeTestExpr t)
	{
		var operand = Bind(t.Operand);
		var type = ResolveType(t.Type);
		if (operand.IsError || type.IsError) return new BoundTypeTest(operand, type, t.Negated, t.Span);
		return new BoundTypeTest(operand, type, t.Negated, t.Span);
	}

	private BoundExpr BindNonNull(Syn.NonNullExpr n)
	{
		var operand = Bind(n.Operand);
		if (operand.IsError) return operand;
		if (operand.Type is NullableType nt) return new BoundNonNull(operand, nt.Inner, n.Span);
		Warn(Krn.NonNullOnNonNullable, $"оператор `!` применён к значению типа '{operand.Type.ToDisplayString()}', которое не может быть nil", n.Span);
		return new BoundNonNull(operand, operand.Type, n.Span);
	}

	// ============================================================ индексация

	private BoundExpr BindIndex(Syn.IndexExpr ix)
	{
		var target = Bind(ix.Target);
		var indices = ix.Indices.Select(i => Bind(i)).ToList();
		if (target.IsError || indices.Any(i => i.IsError)) return new BoundIndex(target, indices, null, null, false, ErrorType.Instance, ix.Span);

		var type = target.Type is NullableType n ? n.Inner : target.Type;
		if (target.Type is NullableType)
			return ErrorExpr(Krn.TypeMismatch, $"индексация значения типа '{target.Type.ToDisplayString()}', которое может быть nil", ix.Span);

		var hit = FindOperator("[]", target, indices, ix.Span, report: false);
		if (hit is not null)
		{
			var conv = indices.Select((x, i) => OperatorArg(hit, i, x)).ToList();
			return new BoundIndex(target, conv, hit.Function, null, false, hit.Call.ReturnType, ix.Span);
		}

		if (indices.Count == 1)
		{
			var idx = indices[0];
			bool intIndex = Prim(idx.Type) is { IsInteger: true } || idx is BoundNumberLiteral { Value.IsFloat: false };
			bool rangeIndex = idx is BoundRange;
			switch (type)
			{
				case ListType l when intIndex: return Indexed(l.Element);
				case ArrayType a when intIndex: return Indexed(a.Element);
				case ListType or ArrayType when rangeIndex: return Indexed(type);
				case PrimitiveType { Kind: PrimKind.Str } when intIndex: return Indexed(PrimitiveType.Char);
				case PrimitiveType { Kind: PrimKind.Str } when rangeIndex: return Indexed(PrimitiveType.Str);
				case DictType d:
				{
					var key = Convert(idx, d.Key, idx.Span);
					return new BoundIndex(target, [key], null, null, false, d.Value, ix.Span);
				}
			}
			BoundExpr Indexed(TypeSymbol result) => new BoundIndex(target, [idx], null, null, false, result, ix.Span);
		}
		return ErrorExpr(Krn.IndexNotSupported, $"тип '{target.Type.ToDisplayString()}' не поддерживает индексацию `[]` с такими аргументами", ix.Span);
	}

	// ============================================================ typeof(str name)

	private BoundExpr BindTypeofValue(Syn.TypeofValueExpr tv)
	{
		var os = Scope.LookupFunctions("typeof");
		var arg = Bind(tv.Operand);
		if (os is null)
			return ErrorExpr(Krn.InvalidTypeOf, "функция `typeof(str)` недоступна: подключите `use kern.reflection`", tv.Span);
		var g = new GroupResult("typeof", os.Functions, null, false, null, null, tv.Span);
		return ResolveGroupCall(g, [new BoundArg(null, arg, tv.Operand.Span)], [], tv.Span);
	}
}
