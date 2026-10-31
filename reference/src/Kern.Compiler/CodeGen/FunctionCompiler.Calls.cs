using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.CodeGen;

internal sealed partial class FunctionCompiler
{
	private static bool HasReceiverSlot(FunctionSymbol f) => f.IsExtension || (!f.IsStatic && f.DeclaringType is not null);

	private void EmitCall(BoundCall c)
	{
		if (c.Receiver is not null && !(c.Function is NativeMemberSymbol nm0 && nm0.Member.IsStatic)) Emit(c.Receiver);
		else if (c.Receiver is not null) { Emit(c.Receiver); B.Emit(OpCode.Pop); }
		WithSafe(c.IsSafe && c.Receiver is not null, () =>
		{
			EmitArguments(c.Function, c.Arguments, c.EvaluationOrder);
			bool hasReceiver = c.Receiver is not null && !(c.Function is NativeMemberSymbol { Member.IsStatic: true });
			EmitInvoke(c.Function, c.Arguments.Count, c.OwnerTypeArgs, c.TypeArgs, hasReceiver, c.IsNonVirtual || c.Receiver is BoundParentThis);
		});
	}

	/// <summary>
	/// Генерирует инструкцию вызова. Предполагается, что на стеке уже лежат [получатель?] аргументы.
	/// <paramref name="ownerArgs"/> — аргументы типа-владельца, <paramref name="methodArgs"/> — аргументы функции.
	/// </summary>
	private void EmitInvoke(FunctionSymbol f, int argc, IReadOnlyList<TypeSymbol> ownerArgs, IReadOnlyList<TypeSymbol> methodArgs, bool hasReceiver, bool nonVirtual)
	{
		if (f is NativeMemberSymbol nm)
		{
			B.Emit(OpCode.CallNative, M.Intern(nm.Member.Key), argc + (hasReceiver ? 1 : 0), G.SigList([.. ownerArgs, .. methodArgs]));
			return;
		}
		int idx = FunctionIndexFor(f);
		bool instance = HasReceiverSlot(f);
		var sigArgs = instance ? methodArgs : [.. ownerArgs, .. methodArgs];
		int sl = G.SigList(sigArgs);
		bool virt = instance && !f.IsExtension && !nonVirtual && !f.IsConstructor
			&& (f.IsOpen || f.IsAbstract || f.IsOverride || (f.DeclaringType is { IsOpen: true } or { IsAbstract: true } && f.Property is not null));
		B.Emit(virt ? OpCode.CallVirt : OpCode.Call, idx, argc, sl);
	}

	/// <summary>Выталкивает аргументы в позиционном порядке с учётом порядка вычисления.</summary>
	private void EmitArguments(FunctionSymbol? f, IReadOnlyList<BoundExpr> args, IReadOnlyList<int>? order)
	{
		if (order is null || IsAscending(order))
		{
			for (int i = 0; i < args.Count; i++) EmitArg(f, args, i);
			return;
		}
		// вычисляем в порядке исходника во временные переменные, затем загружаем позиционно
		var temps = new int[args.Count];
		var done = new HashSet<int>();
		foreach (int i in order)
		{
			temps[i] = AllocTemp();
			EmitArg(f, args, i);
			B.Emit(OpCode.StLoc, temps[i]);
			done.Add(i);
		}
		for (int i = 0; i < args.Count; i++)
			if (!done.Contains(i)) { temps[i] = AllocTemp(); EmitArg(f, args, i); B.Emit(OpCode.StLoc, temps[i]); }
		for (int i = 0; i < args.Count; i++) B.Emit(OpCode.LdLoc, temps[i]);
	}

	private static bool IsAscending(IReadOnlyList<int> order)
	{
		for (int i = 1; i < order.Count; i++) if (order[i] < order[i - 1]) return false;
		return true;
	}

	private void EmitArg(FunctionSymbol? f, IReadOnlyList<BoundExpr> args, int i)
	{
		if (args[i] is BoundDefaultArgument da) EmitDefaultArgument(da, f, i);
		else EmitCopy(args[i]);
	}

	private void EmitDefaultArgument(BoundDefaultArgument da, FunctionSymbol? f, int index)
	{
		if (da.Value is not null) { EmitCopy(da.Value); return; }
		if (f is NativeMemberSymbol nm && index < nm.Member.Params.Count && nm.Member.Params[index].DefaultValue is { } dv)
		{
			EmitConstant(dv, da.Type);
			return;
		}
		B.Emit(OpCode.LdNone); // нативная сторона подставит значение по умолчанию
	}

	private void EmitConstant(object? v, TypeSymbol type)
	{
		switch (v)
		{
			case null: B.Emit(OpCode.LdNil); break;
			case bool b: B.Emit(b ? OpCode.LdTrue : OpCode.LdFalse); break;
			case string s: B.Emit(OpCode.LdStr, M.Intern(s)); break;
			case char c: B.Emit(OpCode.LdConst, M.AddConstant(c)); break;
			case long or ulong or int:
				EmitNumber(new Lexing.NumberValue(System.Numerics.BigInteger.Parse(v.ToString()!), 0, false, null), type);
				break;
			case double d: EmitNumber(new Lexing.NumberValue(0, d, true, null), type); break;
			default: B.Emit(OpCode.LdNone); break;
		}
	}

	private void EmitDelegateCall(BoundDelegateCall dc)
	{
		Emit(dc.Callee);
		WithSafe(dc.IsSafe, () =>
		{
			foreach (var a in dc.Arguments) EmitCopy(a);
			B.Emit(OpCode.CallClosure, dc.Arguments.Count);
		});
	}

	private void EmitNew(BoundNew n)
	{
		EmitArguments(n.Constructor, n.Arguments, n.EvaluationOrder);
		if (n.Constructor is NativeMemberSymbol nm)
		{
			B.Emit(OpCode.CallNative, M.Intern(nm.Member.Key), n.Arguments.Count, G.SigList(n.TypeArgs));
			return;
		}
		if (n.Type is not DeclaredType dt)
			throw new InvalidOperationException($"создание значения типа {n.Type.ToDisplayString()} без конструктора не поддерживается");
		int ctor = n.Constructor is { } c ? FunctionIndexFor(c) : G.DefaultCtor.TryGetValue(dt.Decl, out int di) ? di : -1;
		B.Emit(dt.Decl.IsValueType ? OpCode.NewUnit : OpCode.NewObj, G.TypeIndex[dt.Decl], ctor, n.Arguments.Count, G.SigList(n.TypeArgs.Count > 0 ? n.TypeArgs : dt.Args));
	}
}
