using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Арифметика, сравнения и пользовательские операторы.</summary>
public sealed partial class Interpreter
{
	private static string SymbolOf(OpCode op) => op switch
	{
		OpCode.Add => "+", OpCode.Sub => "-", OpCode.Mul => "*", OpCode.Div => "/", OpCode.Mod => "%", OpCode.Pow => "**",
		OpCode.BitAnd => "&", OpCode.BitOr => "|", OpCode.BitXor => "^", OpCode.Shl => "<<", OpCode.Shr => ">>",
		OpCode.Neg => "-", OpCode.BitNot => "~",
		OpCode.Eq => "==", OpCode.Ne => "!=", OpCode.Lt => "<", OpCode.Gt => ">", OpCode.Le => "<=", OpCode.Ge => ">=",
		_ => op.ToString(),
	};

	private static KernPanic DivZero() => new("деление на ноль");

	internal KValue Binary(OpCode op, KValue a, KValue b)
	{
		if (a.IsNumber && b.IsNumber) return NumericBinary(op, a, b);
		if (a.Kind == ValueKind.Char && b.IsInteger && op is OpCode.Add or OpCode.Sub)
			return KValue.Char((char)(op == OpCode.Add ? a.Bits + b.Bits : a.Bits - b.Bits));
		if (a.Kind == ValueKind.Char && b.Kind == ValueKind.Char && op == OpCode.Sub) return KValue.I32((int)(a.Bits - b.Bits));
		if (a.Kind == ValueKind.Str && op == OpCode.Add) return KValue.Str(a.AsStr + ValueOps.ToStr(b));
		if (b.Kind == ValueKind.Str && op == OpCode.Add && !a.IsNilOrNone && a.Kind != ValueKind.Ref) return KValue.Str(ValueOps.ToStr(a) + b.AsStr);
		if (op == OpCode.Mul && TryRepeat(a, b, out var repeated)) return repeated;
		if (op == OpCode.Add && a.Ref is KListObject la && b.Ref is KListObject lb)
		{
			var joined = new List<KValue>(la.Items.Count + lb.Items.Count);
			joined.AddRange(la.Items);
			joined.AddRange(lb.Items);
			return KValue.Obj(new KListObject(la.ElementType, joined));
		}
		if (a.IsNilOrNone || b.IsNilOrNone) throw new KernPanic($"операция {SymbolOf(op)} над {(a.IsNilOrNone ? a.Kind : b.Kind).ToString().ToLowerInvariant()}");
		return CallOperator(SymbolOf(op), [a, b]);
	}

	/// <summary>Повтор `str/char/list * целое` и `целое * str/char/list`; отрицательный множитель — panic, 0 — пусто.</summary>
	private static bool TryRepeat(KValue a, KValue b, out KValue result)
	{
		result = default;
		KValue seq, count;
		if (b.IsInteger && (a.Kind is ValueKind.Str or ValueKind.Char || a.Ref is KListObject)) { seq = a; count = b; }
		else if (a.IsInteger && (b.Kind is ValueKind.Str or ValueKind.Char || b.Ref is KListObject)) { seq = b; count = a; }
		else return false;

		if (count.Bits < 0) throw new KernPanic("повтор: отрицательный множитель");
		if (count.Bits > int.MaxValue) throw new KernPanic("повтор: слишком большой множитель");
		int n = (int)count.Bits;
		if (seq.Ref is KListObject list)
		{
			var items = new List<KValue>(list.Items.Count * n);
			for (int i = 0; i < n; i++) items.AddRange(list.Items);
			result = KValue.Obj(new KListObject(list.ElementType, items));
			return true;
		}
		string unit = seq.Kind == ValueKind.Char ? ((char)seq.Bits).ToString() : seq.AsStr;
		result = KValue.Str(new System.Text.StringBuilder(unit.Length * n).Insert(0, unit, n).ToString());
		return true;
	}

	private static KValue NumericBinary(OpCode op, KValue a, KValue b)
	{
		if (a.IsFloat || b.IsFloat)
		{
			var kind = Num.ResultFloat(a, b);
			double x = a.AsF64, y = b.AsF64;
			double r = op switch
			{
				OpCode.Add => x + y, OpCode.Sub => x - y, OpCode.Mul => x * y, OpCode.Div => x / y, OpCode.Mod => x % y,
				OpCode.Pow => Math.Pow(x, y),
				_ => throw new KernPanic($"операция {SymbolOf(op)} не определена для вещественных чисел"),
			};
			return Num.FromDouble(kind, r);
		}
		var k = Num.ResultInt(a.Kind, b.Kind);
		bool u64 = k == ValueKind.U64;
		long p = a.Bits, q = b.Bits;
		switch (op)
		{
			case OpCode.Add: return Num.Wrap(k, unchecked(p + q));
			case OpCode.Sub: return Num.Wrap(k, unchecked(p - q));
			case OpCode.Mul: return Num.Wrap(k, unchecked(p * q));
			case OpCode.Div:
				if (q == 0) throw DivZero();
				return Num.Wrap(k, u64 ? unchecked((long)((ulong)p / (ulong)q)) : p == long.MinValue && q == -1 ? p : p / q);
			case OpCode.Mod:
				if (q == 0) throw DivZero();
				return Num.Wrap(k, u64 ? unchecked((long)((ulong)p % (ulong)q)) : q == -1 ? 0 : p % q);
			case OpCode.Pow:
			{
				if (q < 0) throw new KernPanic("целая степень с отрицательным показателем: используйте вещественные числа");
				long r = 1, bs = p;
				for (long e = q; e > 0; e >>= 1)
				{
					if ((e & 1) != 0) r = unchecked(r * bs);
					bs = unchecked(bs * bs);
				}
				return Num.Wrap(k, r);
			}
			case OpCode.BitAnd: return Num.Wrap(k, p & q);
			case OpCode.BitOr: return Num.Wrap(k, p | q);
			case OpCode.BitXor: return Num.Wrap(k, p ^ q);
			case OpCode.Shl: return Num.Wrap(a.Kind, p << (int)(q & (Num.BitsOf(a.Kind) - 1)));
			case OpCode.Shr:
			{
				int sh = (int)(q & (Num.BitsOf(a.Kind) - 1));
				return Num.Wrap(a.Kind, a.IsUnsigned ? unchecked((long)((ulong)p >> sh)) : p >> sh);
			}
		}
		throw new KernPanic($"неизвестная арифметическая операция {op}");
	}

	internal KValue Unary(OpCode op, KValue v)
	{
		if (op == OpCode.Neg)
		{
			if (v.IsFloat) return Num.FromDouble(v.Kind, -v.AsF64);
			if (v.IsInteger) return Num.Wrap(v.Kind, unchecked(-v.Bits));
		}
		else if (op == OpCode.BitNot && v.IsInteger) return Num.Wrap(v.Kind, ~v.Bits);
		if (v.IsNilOrNone) throw new KernPanic($"унарная операция {SymbolOf(op)} над nil");
		return CallOperator(SymbolOf(op), [v]);
	}

	private bool CompareOp(OpCode op, KValue a, KValue b)
	{
		if (op is OpCode.Eq or OpCode.Ne)
		{
			bool eq = EqualsValues(a, b);
			return op == OpCode.Eq ? eq : !eq;
		}
		if ((a.IsNumber || a.Kind == ValueKind.Char) && (b.IsNumber || b.Kind == ValueKind.Char))
		{
			int c = Num.Compare(a, b);
			if (c == 2) return false;
			return op switch { OpCode.Lt => c < 0, OpCode.Gt => c > 0, OpCode.Le => c <= 0, _ => c >= 0 };
		}
		if (a.Kind == ValueKind.Str && b.Kind == ValueKind.Str)
		{
			int c = string.CompareOrdinal(a.AsStr, b.AsStr);
			return op switch { OpCode.Lt => c < 0, OpCode.Gt => c > 0, OpCode.Le => c <= 0, _ => c >= 0 };
		}
		if (a.IsNilOrNone || b.IsNilOrNone) throw new KernPanic($"сравнение {SymbolOf(op)} с nil");
		return CallOperator(SymbolOf(op), [a, b]).AsBool;
	}

	/// <summary>`==`: числа по значению, объекты — через пользовательский `op ==`, иначе структурно (unit) либо по ссылке (type).</summary>
	private bool EqualsValues(KValue a, KValue b)
	{
		if (a.Ref is KObject ao && b.Ref is KObject && TryUserOperator("==", [a, b], out var r)) return r.AsBool;
		if (a.Ref is KObject && b.Ref is KObject && !ReferenceEquals(a.Ref, b.Ref) && !Info.Classes[((KObject)a.Ref).Type.DefIndex].IsValueType
			&& ((KObject)a.Ref).Type.Def.Kind != TypeKind.Enum) return false;
		if (a.IsNumber && b.IsNumber) return Num.Compare(a, b) == 0;
		return ValueOps.StructuralEquals(a, b);
	}

	private static bool RefEquals(KValue a, KValue b)
	{
		if (a.Kind == ValueKind.Ref || a.Kind == ValueKind.Str) return b.Kind == a.Kind && ReferenceEquals(a.Ref, b.Ref);
		return a.Kind == b.Kind && a.Bits == b.Bits;
	}

	// ------------------------------------------------------------ пользовательские операторы

	private KValue CallOperator(string sym, KValue[] args)
	{
		if (TryUserOperator(sym, args, out var r)) return r;
		string types = string.Join(" и ", args.Select(a => TypeOf(a).Name));
		throw new KernPanic($"оператор '{sym}' не определён для {types}");
	}

	private bool TryUserOperator(string sym, KValue[] args, out KValue result)
	{
		result = default;
		if (!Info.Operators.TryGetValue(sym, out var cands)) return false;
		foreach (int fi in cands)
		{
			var fd = _funcs[fi];
			bool ext = fd.Flags.HasFlag(MemberFlags.Extension);
			if (fd.Params.Count != args.Length - 1) continue;
			if (!ReceiverMatches(fd, ext, args[0])) continue;
			bool ok = true;
			for (int i = 1; i < args.Length && ok; i++)
				ok = IsInstance(args[i], Types.Resolve(fd.Params[i - 1].Type, [], []));
			if (!ok) continue;
			KType[] ca = args[0].Ref is KObject ko && fd.Owner >= 0 ? ClassArgsFor(fi, ko) : [];
			result = InvokeFunction(fi, args.AsSpan(1), args[0], true, [], ca, []);
			return true;
		}
		return false;
	}

	private bool ReceiverMatches(FunctionDef fd, bool ext, KValue receiver)
	{
		if (ext) return fd.ExtensionOf is { } sig && IsInstance(receiver, Types.Resolve(sig, [], []));
		return fd.Owner >= 0 && receiver.Ref is KObject o && Info.IsSubtype(o.Type.DefIndex, fd.Owner);
	}
}
