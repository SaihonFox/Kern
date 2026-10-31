using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.CodeGen;

internal sealed partial class FunctionCompiler
{
	/// <summary>
	/// Проверяет паттерн на значении, которое поставляет <paramref name="load"/> (кладёт значение на стек).
	/// Несовпадение — переход в <paramref name="fail"/> при сбалансированном стеке; успех — связанные переменные записаны.
	/// </summary>
	private void EmitPattern(BoundPattern p, Action load, Label fail)
	{
		switch (p)
		{
			case BoundWildcardPattern:
				break;
			case BoundBindingPattern b:
				load();
				StoreBinding(b);
				break;
			case BoundRestPattern r:
				if (r.Local is { } rl) { load(); DeclareAndStore(rl); }
				break;
			case BoundTypePattern tp:
				load();
				if (tp.TestType is NilType) { B.Emit(OpCode.LdNil); B.Emit(OpCode.Eq); }
				else B.Emit(OpCode.IsType, G.Sig(tp.TestType));
				B.Jump(OpCode.JmpIfNot, fail);
				if (tp.Local is { } tl) { load(); DeclareAndStore(tl); }
				break;
			case BoundValuePattern vp:
				load();
				Emit(vp.Value);
				if (vp.IsRange) B.Emit(OpCode.In);
				else EmitBinaryOperation(Syn.BinaryOp.Eq, vp.Equality, p.Type);
				B.Jump(OpCode.JmpIfNot, fail);
				break;
			case BoundDestructurePattern d:
				EmitDestructure(d, load, fail);
				break;
			default:
				throw new NotSupportedException($"паттерн {p.GetType().Name} не поддерживается генератором");
		}
	}

	private void EmitDestructure(BoundDestructurePattern d, Action load, Label fail)
	{
		switch (d.Kind)
		{
			case DestructureKind.Tuple:
				SpillAndMatch(d.Elements, load, fail, expand: () => B.Emit(OpCode.Destructure, d.Elements.Count, 0));
				break;
			case DestructureKind.Collection:
			{
				int rest = d.Elements.ToList().FindIndex(e => e is BoundRestPattern);
				int fixedCount = d.Elements.Count - (rest >= 0 ? 1 : 0);
				load();
				B.Emit(OpCode.CountIs, fixedCount, rest >= 0 ? 1 : 0);
				B.Jump(OpCode.JmpIfNot, fail);
				SpillAndMatch(d.Elements, load, fail, expand: () => B.Emit(OpCode.Destructure, d.Elements.Count, rest >= 0 ? rest + 1 : 0));
				break;
			}
			case DestructureKind.Object:
			{
				if (!d.TestType.Equals(d.Type))
				{
					load();
					B.Emit(OpCode.IsType, G.Sig(d.TestType));
					B.Jump(OpCode.JmpIfNot, fail);
				}
				for (int i = 0; i < d.Elements.Count && i < d.Members.Count; i++)
				{
					if (d.Elements[i] is BoundWildcardPattern) continue;
					var member = d.Members[i];
					int tmp = AllocTemp();
					load();
					switch (member)
					{
						case FieldSymbol f: B.Emit(OpCode.LdField, FieldSlot(f)); break;
						case PropertySymbol pr when pr.Native is { } nm:
							B.Emit(OpCode.CallNative, M.Intern(nm.GetterKey!), 1, G.SigList(OwnerArgs(d.TestType)));
							break;
						case PropertySymbol pr:
							EmitInvoke(pr.Getter!, 0, OwnerArgs(d.TestType), [], true, false);
							break;
						default: throw new NotSupportedException($"член {member.Name} нельзя разобрать в паттерне");
					}
					B.Emit(OpCode.StLoc, tmp);
					EmitPattern(d.Elements[i], () => B.Emit(OpCode.LdLoc, tmp), fail);
				}
				break;
			}
			case DestructureKind.ResultOk:
			case DestructureKind.ResultErr:
			{
				load();
				B.Emit(OpCode.ResultIsOk);
				if (d.Kind == DestructureKind.ResultErr) B.Emit(OpCode.Not);
				B.Jump(OpCode.JmpIfNot, fail);
				if (d.Elements.Count > 0 && d.Elements[0] is not BoundWildcardPattern)
				{
					int tmp = AllocTemp();
					load();
					B.Emit(OpCode.ResultValue);
					B.Emit(OpCode.StLoc, tmp);
					EmitPattern(d.Elements[0], () => B.Emit(OpCode.LdLoc, tmp), fail);
				}
				break;
			}
		}
	}

	/// <summary>`expand` превращает значение в N значений (первое на вершине); раскладываем по временным и сопоставляем подпаттерны.</summary>
	private void SpillAndMatch(IReadOnlyList<BoundPattern> elements, Action load, Label fail, Action expand)
	{
		var temps = elements.Select(_ => AllocTemp()).ToArray();
		load();
		expand();
		foreach (int t in temps) B.Emit(OpCode.StLoc, t);
		for (int i = 0; i < elements.Count; i++)
		{
			int t = temps[i];
			EmitPattern(elements[i], () => B.Emit(OpCode.LdLoc, t), fail);
		}
	}
}
