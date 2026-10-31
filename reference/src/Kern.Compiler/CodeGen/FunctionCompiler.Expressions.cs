using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.CodeGen;

internal sealed partial class FunctionCompiler
{
	/// <summary>mutable `unit` копируется при чтении из хранилища в контексте присваивания/передачи.</summary>
	private static bool IsMutableUnit(TypeSymbol t) => t is UnitType { Decl.DeclKind: Semantics.Symbols.TypeDeclKind.Unit };

	private void EmitCopy(BoundExpr e)
	{
		Emit(e);
		if (IsMutableUnit(e.Type) && e is BoundLocal or BoundParameter or BoundField or BoundProperty or BoundIndex or BoundThis or BoundTupleItem)
			B.Emit(OpCode.CopyUnit);
	}

	/// <summary>Вычисляет выражение; на стеке остаётся ровно одно значение (кроме never-выражений).</summary>
	private void Emit(BoundExpr e)
	{
		if (G.OptimizationLevel >= 1 && e is BoundBinary or BoundUnary or BoundIf) e = Optimization.ConstantFolder.Fold(e);
		switch (e)
		{
			case BoundNumberLiteral n: EmitNumber(n.Value, n.Type); break;
			case BoundStringLiteral s: B.Emit(OpCode.LdStr, M.Intern(s.Value)); break;
			case BoundCharLiteral c: B.Emit(OpCode.LdConst, M.AddConstant(c.CodePoint <= 0xFFFF ? (char)c.CodePoint : '?')); break;
			case BoundBoolLiteral b: B.Emit(b.Value ? OpCode.LdTrue : OpCode.LdFalse); break;
			case BoundNilLiteral: B.Emit(OpCode.LdNil); break;
			case BoundNoneLiteral: B.Emit(OpCode.LdNone); break;
			case BoundInterpolatedString it: EmitInterpolation(it); break;
			case BoundLocal l: LoadVar(l.Local); break;
			case BoundParameter p: LoadVar(p.Parameter); break;
			case BoundThis or BoundParentThis: B.Emit(OpCode.LdThis); break;
			case BoundField f: EmitFieldRead(f); break;
			case BoundProperty p: EmitPropertyRead(p); break;
			case BoundIndex ix: EmitIndexRead(ix); break;
			case BoundTupleItem ti: EmitTupleItem(ti); break;
			case BoundFunctionRef fr: EmitFunctionRef(fr); break;
			case BoundNonNull nn: Emit(nn.Operand); B.Emit(OpCode.AssertNonNil); break;
			case BoundCall c: EmitCall(c); break;
			case BoundDelegateCall dc: EmitDelegateCall(dc); break;
			case BoundNew nw: EmitNew(nw); break;
			case BoundDefaultArgument da: EmitDefaultArgument(da, null, 0); break;
			case BoundConversion cv: EmitConversion(cv); break;
			case BoundUnary u: EmitUnary(u); break;
			case BoundBinary bin: EmitBinary(bin); break;
			case BoundCoalesce co: EmitCoalesce(co); break;
			case BoundAssignment a: EmitAssignment(a, keep: true); break;
			case BoundCompoundAssignment ca: EmitCompound(ca, keep: true); break;
			case BoundTypeTest tt:
				Emit(tt.Operand);
				B.Emit(tt.Negated ? OpCode.NotIsType : OpCode.IsType, G.Sig(tt.TestType));
				break;
			case BoundTypeOf to: B.Emit(OpCode.TypeOf, G.Sig(to.Target)); break;
			case BoundList li: EmitCollection(OpCode.NewList, li.ElementType, li.Elements); break;
			case BoundArray ar: EmitCollection(OpCode.NewArray, ar.ElementType, ar.Elements); break;
			case BoundDict d: EmitDict(d); break;
			case BoundTuple t:
				foreach (var el in t.Elements) EmitCopy(el);
				B.Emit(OpCode.NewTuple, G.Sig(t.Type), t.Elements.Count);
				break;
			case BoundRange r: EmitRange(r); break;
			case BoundResultCreate rc:
				if (rc.Value is null) B.Emit(OpCode.LdVoid); else EmitCopy(rc.Value);
				B.Emit(OpCode.NewResult, rc.IsOk ? 1 : 0);
				break;
			case BoundBlock b: EmitBlock(b, discard: false); break;
			case BoundIf i: EmitIf(i); break;
			case BoundMatch m: EmitMatch(m); break;
			case BoundLoop lp: EmitLoop(lp); break;
			case BoundWhile wl: EmitWhile(wl); break;
			case BoundStop: EmitLoopJump(stop: true); break;
			case BoundNext: EmitLoopJump(stop: false); break;
			case BoundReturn r:
				if (r.Value is { } v && !IsVoidType(v.Type)) { EmitCopy(v); EmitDisposals(0); B.Emit(OpCode.Ret); }
				else
				{
					if (r.Value is { } vv) EmitDiscard(vv);
					EmitDisposals(0);
					B.Emit(OpCode.RetVoid);
				}
				break;
			case BoundAwait aw: Emit(aw.Operand); B.Emit(OpCode.Await); break;
			case BoundLambda lam: EmitLambda(lam); break;
			case BoundErrorExpr: throw new InvalidOperationException("в генератор попало выражение с ошибкой привязки");
			default: throw new NotSupportedException($"выражение {e.GetType().Name} не поддерживается генератором");
		}
	}

	// ------------------------------------------------------------ литералы

	private void EmitNumber(Lexing.NumberValue n, TypeSymbol type)
	{
		var kind = (type as PrimitiveType)?.Kind ?? PrimKind.I32;
		if (kind is PrimKind.INum) kind = PrimKind.I32;
		if (kind is PrimKind.FNum or PrimKind.Num) kind = PrimKind.F64;
		if (kind == PrimKind.I32 && !n.IsFloat && n.Integer >= int.MinValue && n.Integer <= int.MaxValue)
		{
			B.Emit(OpCode.LdI32, (int)n.Integer);
			return;
		}
		object value = kind switch
		{
			PrimKind.I8 => (sbyte)Int(n), PrimKind.I16 => (short)Int(n), PrimKind.I32 => (int)Int(n), PrimKind.I64 => (long)Int(n),
			PrimKind.U8 => (byte)Int(n), PrimKind.U16 => (ushort)Int(n), PrimKind.U32 => (uint)Int(n), PrimKind.U64 => (ulong)Int(n),
			PrimKind.F16 => (Half)Dbl(n), PrimKind.F32 => (float)Dbl(n), PrimKind.F64 => Dbl(n),
			_ => n.IsFloat ? Dbl(n) : (object)(long)Int(n),
		};
		B.Emit(OpCode.LdConst, M.AddConstant(value));
	}

	private static System.Numerics.BigInteger Int(Lexing.NumberValue n) => n.IsFloat ? new System.Numerics.BigInteger(n.Float) : n.Integer;
	private static double Dbl(Lexing.NumberValue n) => n.IsFloat ? n.Float : (double)n.Integer;

	private void EmitInterpolation(BoundInterpolatedString s)
	{
		int count = 0;
		foreach (var part in s.Parts)
		{
			switch (part)
			{
				case BoundInterpolationText t:
					if (t.Text.Length == 0) continue;
					B.Emit(OpCode.LdStr, M.Intern(t.Text)); count++;
					break;
				case BoundInterpolationValue v:
					if (v.Debug) { B.Emit(OpCode.LdStr, M.Intern(v.SourceText + "=")); count++; }
					Emit(v.Value);
					if (v.Format is { } f) B.Emit(OpCode.Format, M.Intern(f));
					count++;
					break;
			}
		}
		if (count == 0) B.Emit(OpCode.LdStr, M.Intern(""));
		else B.Emit(OpCode.Concat, count);
	}

	// ------------------------------------------------------------ доступ

	private void EmitFieldRead(BoundField f)
	{
		var fs = f.Field;
		if (fs.IsEnumEntry && fs.DeclaringType is { } et)
		{
			B.Emit(OpCode.LdEnum, G.TypeIndex[et], et.EnumEntries.IndexOf(fs));
			return;
		}
		if (fs.IsStatic || fs.IsGlobal || f.Receiver is null)
		{
			if (f.Receiver is not null) { Emit(f.Receiver); B.Emit(OpCode.Pop); }
			B.Emit(OpCode.LdStatic, G.GlobalIndex[fs]);
			return;
		}
		Emit(f.Receiver);
		WithSafe(f.IsSafe, () => B.Emit(OpCode.LdField, FieldSlot(fs)));
	}

	/// <summary>`recv?.x`: если на стеке nil — оставляем его и пропускаем действие.</summary>
	private void WithSafe(bool safe, Action action)
	{
		if (!safe) { action(); return; }
		var end = new Label();
		B.Jump(OpCode.JmpIfNil, end);
		action();
		B.Mark(end);
	}

	private void EmitPropertyRead(BoundProperty p)
	{
		var prop = p.Property;
		if (p.Receiver is not null) Emit(p.Receiver);
		WithSafe(p.IsSafe && p.Receiver is not null, () =>
		{
			if (prop.Native is { } nm)
			{
				B.Emit(OpCode.CallNative, M.Intern(nm.GetterKey ?? throw new InvalidOperationException($"у нативного свойства {prop.Name} нет геттера")),
					p.Receiver is null ? 0 : 1, G.SigList(p.Receiver is null ? [] : OwnerArgs(p.Receiver.Type)));
				return;
			}
			var getter = prop.Getter ?? throw new InvalidOperationException($"у свойства {prop.Name} нет геттера");
			EmitInvoke(getter, 0, p.Receiver is null ? [] : OwnerArgs(p.Receiver.Type), [], hasReceiver: p.Receiver is not null, nonVirtual: p.Receiver is BoundParentThis);
		});
	}

	private void EmitTupleItem(BoundTupleItem t)
	{
		Emit(t.Target);
		WithSafe(t.IsSafe, () =>
		{
			B.Emit(OpCode.LdI32, t.Index);
			B.Emit(OpCode.Index);
		});
	}

	private void EmitIndexRead(BoundIndex ix)
	{
		Emit(ix.Target);
		WithSafe(ix.IsSafe, () =>
		{
			if (UseBuiltinIndex(ix.Target.Type, ix.Indices.Count))
			{
				Emit(ix.Indices[0]);
				B.Emit(OpCode.Index);
				return;
			}
			foreach (var i in ix.Indices) EmitCopy(i);
			var g = ix.Getter ?? throw new InvalidOperationException("у индексатора нет `op []`");
			EmitInvoke(g, ix.Indices.Count, OwnerArgs(ix.Target.Type), [], hasReceiver: true, nonVirtual: false);
		});
	}

	private static bool UseBuiltinIndex(TypeSymbol target, int indexCount) =>
		indexCount == 1 && target is ListType or ArrayType or DictType or TupleType or PrimitiveType { Kind: PrimKind.Str };

	// ------------------------------------------------------------ ссылки на функции, лямбды

	private void EmitFunctionRef(BoundFunctionRef fr)
	{
		var f = fr.Function;
		if (fr.Receiver is null)
		{
			B.Emit(OpCode.LdFunc, FunctionIndexFor(f));
			return;
		}
		Emit(fr.Receiver);
		bool virt = !f.IsExtension && f is not NativeMemberSymbol && (f.IsOpen || f.IsAbstract || f.IsOverride);
		if (virt) B.Emit(OpCode.BindMethod, M.Intern(f.Name));   // виртуальный: выбор реализации по имени в рантайме
		else B.Emit(OpCode.BindFunc, FunctionIndexFor(f));
	}

	private int FunctionIndexFor(FunctionSymbol f)
	{
		if (f is NativeMemberSymbol nm) return G.NativeDef(nm);
		if (G.FuncIndex.TryGetValue(f, out int i)) return i;
		if (f.IsConstructor && f.DeclaringType is { } t && G.DefaultCtor.TryGetValue(t, out int di)) return di;
		throw new InvalidOperationException($"функция {f} не объявлена в модуле");
	}

	private void EmitLambda(BoundLambda lam)
	{
		var retType = lam.ReturnType;
		var def = G.NewLambdaDef($"<lambda@{(_file is null ? 0 : _file.GetLineColumn(lam.Span.Start).Line)}>", lam.IsAsync, lam.Parameters,
			lam.IsAsync && retType is TaskType tt ? tt.Args[0] : retType, lam.Captures.Count, _file?.Path);
		int idx = G.IndexOf(def);
		var inner = new FunctionCompiler(G, this, def);
		for (int i = 0; i < lam.Captures.Count; i++) inner._captures[lam.Captures[i].Variable] = i;
		inner.BeginParams(lam.Parameters, null);
		inner.PreScan(lam.Body);
		inner.BeginParamCells(lam.Parameters);
		var bodyVoid = IsVoidType(lam.Body.Type) || (lam.Signature is { } sig && IsVoidType(lam.IsAsync && sig.Return is TaskType t2 ? t2.Args[0] : sig.Return));
		inner.Mark(lam.Body.Span);
		inner.EmitBodyReturn(lam.Body, bodyVoid);
		inner.Finish();

		foreach (var c in lam.Captures)
		{
			if (_captures.TryGetValue(c.Variable, out int ci)) B.Emit(OpCode.LdCellRef, ci);
			else if (_celled.Contains(c.Variable) && _slots.TryGetValue(c.Variable, out int slot)) B.Emit(OpCode.LdCellRef, ~slot);
			else
			{
				// переменная ещё не объявлена в ячейке (например объявлена позже): создаём пустую ячейку
				int s = c.Variable is LocalSymbol l ? SlotOf(l) : AllocTemp();
				_celled.Add(c.Variable); _slots[c.Variable] = s;
				B.Emit(OpCode.LdNone);
				B.Emit(OpCode.NewCell, s);
				B.Emit(OpCode.LdCellRef, ~s);
			}
		}
		B.Emit(OpCode.MakeClosure, idx, lam.Captures.Count);
	}

	// ------------------------------------------------------------ коллекции

	private void EmitCollection(OpCode op, TypeSymbol elem, IReadOnlyList<BoundExpr> items)
	{
		foreach (var i in items) EmitCopy(i);
		B.Emit(op, G.Sig(elem), items.Count);
	}

	private void EmitDict(BoundDict d)
	{
		foreach (var e in d.Entries) { EmitCopy(e.Key); EmitCopy(e.Value); }
		B.Emit(OpCode.NewDict, G.Sig(d.Type), d.Entries.Count);
	}

	private void EmitRange(BoundRange r)
	{
		if (r.Start is null) B.Emit(OpCode.LdNone); else Emit(r.Start);
		if (r.End is null) B.Emit(OpCode.LdNone); else Emit(r.End);
		if (r.Step is not null) Emit(r.Step);
		B.Emit(OpCode.RangeNew, (r.Inclusive ? 1 : 0) | (r.Step is not null ? 2 : 0));
	}

	// ------------------------------------------------------------ приведения

	private void EmitConversion(BoundConversion cv)
	{
		if (cv.IsConstant && cv.Operand is BoundNumberLiteral lit) { EmitNumber(lit.Value, cv.Type is NullableType n ? n.Inner : cv.Type); return; }
		EmitCopy(cv.Operand);
		switch (cv.Kind)
		{
			case ConversionKind.ImplicitNumeric:
				B.Emit(OpCode.CastType, G.Sig(cv.Type is NullableType nt ? nt.Inner : cv.Type));
				break;
			default:
				break; // динамическая типизация: остальные приведения ничего не делают в рантайме
		}
	}

	private static IReadOnlyList<TypeSymbol> OwnerArgs(TypeSymbol t) => t switch
	{
		NativeType n => n.Args,
		BuiltinGenericType b => b.Args,
		DeclaredType d => d.Args,
		NullableType nn => OwnerArgs(nn.Inner),
		_ => [],
	};
}
