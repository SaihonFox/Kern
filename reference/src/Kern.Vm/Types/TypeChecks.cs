using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Values;

namespace Kern.Vm.Types;

/// <summary>Проверки принадлежности значения reified-типу и определение типа значения.</summary>
public static class TypeChecks
{
	public static bool IsInstance(KValue v, KType t)
	{
		switch (t)
		{
			case KPrimType p: return IsPrim(v, p.Kind);
			case KNullableType n: return v.IsNilOrNone || IsInstance(v, n.Inner);
			case KUnionType u: foreach (var m in u.Members) if (IsInstance(v, m)) return true; return false;
			case KClassType c: return IsClass(v, c);
			case KTupleType tt:
				if (v.Ref is not KTupleObject to || to.Items.Length != tt.Items.Length) return false;
				for (int i = 0; i < to.Items.Length; i++) if (!IsInstance(to.Items[i], tt.Items[i])) return false;
				return true;
			case KFuncType: return v.Ref is KFunction;
			case KBuiltinGenericType b: return IsBuiltin(v, b);
			case KNativeType nt:
				if (v.Ref is KNativeBox nb ? nt.ClrType.IsInstanceOfType(nb.Value) : v.Ref is not null && nt.ClrType.IsInstanceOfType(v.Ref)) return true;
				return NativeAdapters.Accepts(v, nt.ClrType);   // встроенные потомки (list/array/str/dict/range) нативной базы
			default: return false;
		}
	}

	private static bool IsPrim(KValue v, PrimKind k) => k switch
	{
		PrimKind.Object => !v.IsNilOrNone,
		PrimKind.Void => v.Kind == ValueKind.Void,
		PrimKind.Nil => v.IsNil,
		PrimKind.Bool => v.Kind == ValueKind.Bool,
		PrimKind.Char => v.Kind == ValueKind.Char,
		PrimKind.Str => v.Kind == ValueKind.Str,
		PrimKind.I8 => v.Kind == ValueKind.I8, PrimKind.I16 => v.Kind == ValueKind.I16,
		PrimKind.I32 => v.Kind == ValueKind.I32, PrimKind.I64 => v.Kind == ValueKind.I64,
		PrimKind.U8 => v.Kind == ValueKind.U8, PrimKind.U16 => v.Kind == ValueKind.U16,
		PrimKind.U32 => v.Kind == ValueKind.U32, PrimKind.U64 => v.Kind == ValueKind.U64,
		PrimKind.F16 => v.Kind == ValueKind.F16, PrimKind.F32 => v.Kind == ValueKind.F32, PrimKind.F64 => v.Kind == ValueKind.F64,
		PrimKind.INum => v.IsInteger,
		PrimKind.FNum => v.IsFloat,
		PrimKind.Num => v.IsNumber,
		PrimKind.Type => v.Ref is KTypeObject,
		_ => false,
	};

	private static bool IsClass(KValue v, KClassType c)
	{
		var info = c.Module.Classes[c.DefIndex];
		if (info.Def.Kind == TypeKind.Union)
		{
			foreach (var m in info.Def.UnionMembers)
				if (IsInstance(v, c.Module.Types.Resolve(m, c.Args, []))) return true;
			return false;
		}
		if (v.Ref is not KObject o) return false;
		if (!c.Module.IsSubtype(o.Type.DefIndex, c.DefIndex)) return false;
		if (c.Args.Length == 0) return true;
		var actual = AncestorArgs(o.Type, c.DefIndex);
		if (actual is null || actual.Length != c.Args.Length) return actual is null;
		for (int i = 0; i < actual.Length; i++)
			if (!ReferenceEquals(actual[i], c.Args[i]) && !(c.Args[i] is KPrimType { Kind: PrimKind.Object })) return false;
		return true;
	}

	private static bool IsBuiltin(KValue v, KBuiltinGenericType b)
	{
		switch (b.Builtin)
		{
			case "list":
				return v.Ref is KListObject l && (b.Args.Length == 0 || ElemOk(l.ElementType, b.Args[0]));
			case "array":
				return v.Ref is KArrayObject a && (b.Args.Length == 0 || ElemOk(a.ElementType, b.Args[0]));
			case "dict":
				return v.Ref is KDictObject d && (b.Args.Length < 2 || (ElemOk(d.KeyType, b.Args[0]) && ElemOk(d.ValueType, b.Args[1])));
			case "result": return v.Ref is KResultObject;
			case "task": return v.Ref is KTask;
			case "kern.range": return v.Ref is KRange;
			default: return false;
		}
	}

	private static bool ElemOk(KType actual, KType wanted) =>
		ReferenceEquals(actual, wanted) || wanted is KPrimType { Kind: PrimKind.Object };

	/// <summary>Аргументы типа предка, видимые из конкретного типа (подстановка по цепочке Parents).</summary>
	public static KType[]? AncestorArgs(KClassType actual, int ancestorDef)
	{
		if (actual.DefIndex == ancestorDef) return actual.Args;
		var mod = actual.Module;
		foreach (var psig in actual.Def.Parents)
		{
			if (mod.Types.Resolve(psig, actual.Args, []) is not KClassType pc) continue;
			if (pc.DefIndex != ancestorDef && !mod.IsSubtype(pc.DefIndex, ancestorDef)) continue;
			var r = AncestorArgs(pc, ancestorDef);
			if (r is not null) return r;
		}
		return null;
	}

	/// <summary>Динамический тип значения.</summary>
	public static KType TypeOf(KValue v, TypeFactory f)
	{
		var obj = TypeFactory.Object;
		switch (v.Kind)
		{
			case ValueKind.None or ValueKind.Void: return TypeFactory.Void;
			case ValueKind.Nil: return TypeFactory.Prim(PrimKind.Nil);
			case ValueKind.Bool: return TypeFactory.Prim(PrimKind.Bool);
			case ValueKind.Char: return TypeFactory.Prim(PrimKind.Char);
			case ValueKind.Str: return TypeFactory.Str;
			case ValueKind.I8: return TypeFactory.Prim(PrimKind.I8);
			case ValueKind.I16: return TypeFactory.Prim(PrimKind.I16);
			case ValueKind.I32: return TypeFactory.Prim(PrimKind.I32);
			case ValueKind.I64: return TypeFactory.Prim(PrimKind.I64);
			case ValueKind.U8: return TypeFactory.Prim(PrimKind.U8);
			case ValueKind.U16: return TypeFactory.Prim(PrimKind.U16);
			case ValueKind.U32: return TypeFactory.Prim(PrimKind.U32);
			case ValueKind.U64: return TypeFactory.Prim(PrimKind.U64);
			case ValueKind.F16: return TypeFactory.Prim(PrimKind.F16);
			case ValueKind.F32: return TypeFactory.Prim(PrimKind.F32);
			case ValueKind.F64: return TypeFactory.Prim(PrimKind.F64);
		}
		return v.Ref switch
		{
			KObject o => o.Type,
			KListObject l => f.List(l.ElementType),
			KArrayObject a => f.Array(a.ElementType),
			KDictObject d => f.Dict(d.KeyType, d.ValueType),
			KTupleObject t => f.Tuple(t.Items.Select(i => TypeOf(i, f)).ToArray(), t.Names),
			KResultObject => f.Result(obj, obj),
			KTypeObject => TypeFactory.Prim(PrimKind.Type),
			KFunction => f.Func([], obj),
			KTask => f.Task(obj),
			KRange => f.Named("kern.range", []),
			KNativeBox nb => f.Named("dotnet." + (nb.Value.GetType().FullName ?? "object"), []),
			_ => obj,
		};
	}
}
