using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	private BoundExpr BindAssign(Syn.AssignExpr a)
	{
		bool simple = a.Op == Syn.AssignOp.Assign;

		BoundExpr target;
		if (simple)
		{
			bool saved = _noRead;
			_noRead = a.Target is Syn.IdentExpr;
			try { target = Bind(a.Target); }
			finally { _noRead = saved; }
		}
		else
		{
			target = Bind(a.Target);
		}
		if (target.IsError)
		{
			Bind(a.Value);
			return target;
		}

		var targetType = AssignableType(target);
		if (!CheckAssignable(target, a.Target.Span)) { Bind(a.Value); return new BoundErrorExpr(a.Span); }

		if (simple)
		{
			var value = Bind(a.Value, targetType);
			var rawType = value.Type;   // сужение (T? = значение T → T) считается по типу ДО неявного приведения
			value = Convert(value, targetType, a.Value.Span);
			AfterAssign(target, value, a.Span, rawType);
			return new BoundAssignment(target, value, PrimitiveType.Void, a.Span);
		}

		var rhs = Bind(a.Value);
		if (rhs.IsError) return new BoundCompoundAssignment(a.Op, null, target, rhs, Types.ConversionKind.Identity, PrimitiveType.Void, a.Span);

		var binOp = a.Op switch
		{
			Syn.AssignOp.Add => Syn.BinaryOp.Add, Syn.AssignOp.Sub => Syn.BinaryOp.Sub, Syn.AssignOp.Mul => Syn.BinaryOp.Mul,
			Syn.AssignOp.Div => Syn.BinaryOp.Div, Syn.AssignOp.Rem => Syn.BinaryOp.Rem, _ => Syn.BinaryOp.Pow,
		};
		var synthetic = new Syn.BinaryExpr(binOp, a.Target, a.Value, a.Span);
		var computed = BindArithmetic(synthetic, target, rhs);
		if (computed is not BoundBinary bb)
			return computed;

		var kind = Conversions.Classify(bb.Type, targetType);
		if (kind == Types.ConversionKind.None)
			return ErrorExpr(Krn.TypeMismatch,
				$"результат '{bb.Type.ToDisplayString()}' нельзя присвоить '{targetType.ToDisplayString()}' (используйте явное приведение)", a.Span);
		AfterAssign(target, bb, a.Span);
		return new BoundCompoundAssignment(a.Op, bb.Operator, target, bb.Right, kind, PrimitiveType.Void, a.Span);
	}

	private static TypeSymbol AssignableType(BoundExpr target) => target switch
	{
		BoundField { IsSafe: true, Type: NullableType n } => n.Inner,
		BoundProperty { IsSafe: true, Type: NullableType n } => n.Inner,
		_ => target.Type,
	};

	private void AfterAssign(BoundExpr target, BoundExpr value, TextSpan span, TypeSymbol? rawType = null)
	{
		var valueType = rawType ?? value.Type;
		switch (target)
		{
			case BoundLocal l:
				Flow.AssignLocal(l.Local, span, valueType);
				break;
			case BoundParameter p:
				Flow.AssignParameter(p.Parameter, valueType);
				break;
		}
	}

	private bool CheckAssignable(BoundExpr target, TextSpan span)
	{
		switch (target)
		{
			case BoundLocal or BoundParameter:
				return true;
			case BoundField f:
				if (f.Field.IsEnumEntry)
				{
					Error(Krn.NotAssignable, "элемент enum нельзя изменить", span);
					return false;
				}
				return CheckMutableReceiver(f.Receiver, span);
			case BoundProperty p:
			{
				var prop = p.Property;
				bool canWrite = prop.CanWrite || (prop.CanInit && InConstruction());
				if (!canWrite)
				{
					Error(Krn.NotAssignable, $"у свойства '{prop.Name}' нет `set`" + (prop.CanInit ? " (только `init`: допустимо при создании объекта)" : ""), span);
					return false;
				}
				return CheckMutableReceiver(p.Receiver, span);
			}
			case BoundIndex:
				return true;
			case BoundTupleItem:
				Error(Krn.NotAssignable, "элементы кортежа нельзя менять присваиванием", span);
				return false;
			default:
				Error(Krn.NotAssignable, "левая часть присваивания не является переменной, полем или свойством", span);
				return false;
		}
	}

	private bool InConstruction() => CurrentFunction is { IsConstructor: true } or { IsInit: true };

	private bool CheckMutableReceiver(BoundExpr? receiver, TextSpan span)
	{
		if (receiver?.Type is UnitType { IsImmutable: true } u && !InConstruction())
		{
			Error(Krn.ImmutableUnitMutation, $"'{u.Decl.Name}' — неизменяемый unit (`unit!`): его члены менять нельзя", span);
			return false;
		}
		return true;
	}
}
