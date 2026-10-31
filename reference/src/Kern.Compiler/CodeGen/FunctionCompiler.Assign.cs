using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.CodeGen;

internal sealed partial class FunctionCompiler
{
	/// <summary>
	/// Доступ к цели присваивания: <c>Spill</c> вычисляет получателя/индексы один раз во временные переменные,
	/// <c>Push</c> кладёт «префикс» (получатель, индексы) на стек, <c>Load</c> заменяет префикс текущим значением,
	/// <c>Store</c> снимает префикс и значение и записывает.
	/// </summary>
	private sealed record Access(Action Spill, Action Push, Action Load, Action Store, bool IsSimple, bool IsSafe);

	private Access MakeAccess(BoundExpr target, bool spill)
	{
		switch (target)
		{
			case BoundLocal l:
				return new(() => { }, () => { }, () => LoadVar(l.Local), () => StoreVar(l.Local), true, false);
			case BoundParameter p:
				return new(() => { }, () => { }, () => LoadVar(p.Parameter), () => StoreVar(p.Parameter), true, false);
			case BoundField f when f.Field.IsStatic || f.Field.IsGlobal || f.Receiver is null:
			{
				int gi = G.GlobalIndex[f.Field];
				return new(() => { }, () => { }, () => B.Emit(OpCode.LdStatic, gi), () => B.Emit(OpCode.StStatic, gi), true, false);
			}
			case BoundField f:
			{
				int t = spill ? AllocTemp() : -1;
				int slot = FieldSlot(f.Field);
				return new(
					() => { if (spill) { Emit(f.Receiver!); B.Emit(OpCode.StLoc, t); } },
					() => { if (spill) B.Emit(OpCode.LdLoc, t); else Emit(f.Receiver!); },
					() => B.Emit(OpCode.LdField, slot), () => B.Emit(OpCode.StField, slot), false, f.IsSafe);
			}
			case BoundProperty p:
			{
				var prop = p.Property;
				bool hasRecv = p.Receiver is not null;
				int t = spill && hasRecv ? AllocTemp() : -1;
				var ownerArgs = hasRecv ? OwnerArgs(p.Receiver!.Type) : [];
				bool nonVirt = p.Receiver is BoundParentThis;
				return new(
					() => { if (t >= 0) { Emit(p.Receiver!); B.Emit(OpCode.StLoc, t); } },
					() => { if (!hasRecv) return; if (spill) B.Emit(OpCode.LdLoc, t); else Emit(p.Receiver!); },
					() =>
					{
						if (prop.Native is { } nm) B.Emit(OpCode.CallNative, M.Intern(nm.GetterKey!), hasRecv ? 1 : 0, G.SigList(ownerArgs));
						else EmitInvoke(prop.Getter ?? throw new InvalidOperationException($"у свойства {prop.Name} нет геттера"), 0, ownerArgs, [], hasRecv, nonVirt);
					},
					() =>
					{
						if (prop.Native is { } nm) B.Emit(OpCode.CallNative, M.Intern(nm.SetterKey!), (hasRecv ? 1 : 0) + 1, G.SigList(ownerArgs));
						else EmitInvoke(prop.Setter ?? prop.Initer ?? throw new InvalidOperationException($"у свойства {prop.Name} нет сеттера"), 1, ownerArgs, [], hasRecv, nonVirt);
						B.Emit(OpCode.Pop);
					}, false, p.IsSafe);
			}
			case BoundIndex ix:
			{
				bool builtin = UseBuiltinIndex(ix.Target.Type, ix.Indices.Count);
				int n = ix.Indices.Count;
				var temps = spill ? Enumerable.Range(0, n + 1).Select(_ => AllocTemp()).ToArray() : [];
				var ownerArgs = OwnerArgs(ix.Target.Type);
				return new(
					() =>
					{
						if (!spill) return;
						Emit(ix.Target); B.Emit(OpCode.StLoc, temps[0]);
						for (int i = 0; i < n; i++) { Emit(ix.Indices[i]); B.Emit(OpCode.StLoc, temps[i + 1]); }
					},
					() =>
					{
						if (spill) foreach (int t in temps) B.Emit(OpCode.LdLoc, t);
						else { Emit(ix.Target); foreach (var i in ix.Indices) Emit(i); }
					},
					() =>
					{
						if (builtin) B.Emit(OpCode.Index);
						else EmitInvoke(ix.Getter ?? throw new InvalidOperationException("у индексатора нет `op []` для чтения"), n, ownerArgs, [], true, false);
					},
					() =>
					{
						if (builtin) B.Emit(OpCode.StIndex);
						else
						{
							EmitInvoke(ix.Setter ?? throw new InvalidOperationException("у индексатора нет `op []` для записи"), n + 1, ownerArgs, [], true, false);
							B.Emit(OpCode.Pop);
						}
					}, false, ix.IsSafe);
			}
			default:
				throw new NotSupportedException($"{target.GetType().Name} не может быть целью присваивания");
		}
	}

	private void EmitAssignment(BoundAssignment a, bool keep)
	{
		Mark(a.Span);
		var acc = MakeAccess(a.Target, spill: false);
		if (acc.IsSimple)
		{
			EmitCopy(a.Value);
			if (keep) B.Emit(OpCode.Dup);
			acc.Store();
			return;
		}
		int tmp = keep ? AllocTemp() : -1;
		var nil = new Label(); var end = new Label();
		acc.Push();
		if (acc.IsSafe) B.Jump(OpCode.JmpIfNil, nil);
		EmitCopy(a.Value);
		if (keep) { B.Emit(OpCode.Dup); B.Emit(OpCode.StLoc, tmp); }
		acc.Store();
		if (keep) B.Emit(OpCode.LdLoc, tmp);
		if (acc.IsSafe)
		{
			B.Jump(OpCode.Jmp, end);
			B.Mark(nil);
			B.Emit(OpCode.Pop);
			if (keep) B.Emit(OpCode.LdNil);
			B.Mark(end);
		}
	}

	private void EmitCompound(BoundCompoundAssignment c, bool keep)
	{
		Mark(c.Span);
		var acc = MakeAccess(c.Target, spill: true);
		int tmp = keep ? AllocTemp() : -1;
		var nil = new Label(); var end = new Label();
		acc.Spill();
		if (acc.IsSafe) { acc.Push(); B.Jump(OpCode.JmpIfNil, nil); B.Emit(OpCode.Pop); }
		acc.Push();
		acc.Push();
		acc.Load();
		Emit(c.Value);
		EmitBinaryOperation(ToBinary(c.Op), c.Operator, c.Target.Type);
		if (c.ResultConversion == ConversionKind.ImplicitNumeric)
			B.Emit(OpCode.CastType, G.Sig(c.Target.Type is NullableType n ? n.Inner : c.Target.Type));
		if (keep) { B.Emit(OpCode.Dup); B.Emit(OpCode.StLoc, tmp); }
		acc.Store();
		if (keep) B.Emit(OpCode.LdLoc, tmp);
		if (acc.IsSafe)
		{
			B.Jump(OpCode.Jmp, end);
			B.Mark(nil);
			B.Emit(OpCode.Pop);
			if (keep) B.Emit(OpCode.LdNil);
			B.Mark(end);
		}
	}
}
