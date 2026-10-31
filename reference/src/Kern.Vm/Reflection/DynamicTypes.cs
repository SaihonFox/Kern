using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

/// <summary>Создание типов во время выполнения: класс или unit с полями (без родителей и методов; атрибуты — по желанию).</summary>
public static class DynamicTypes
{
	public sealed record FieldSpec(string Name, KType Type, IReadOnlyList<(string Name, KValue[] Args)> Attributes);

	public static KType Define(Interpreter vm, string name, bool isUnit, bool immutable, IReadOnlyList<FieldSpec> fields,
		IReadOnlyList<(string Name, KValue[] Args)> attributes)
	{
		if (string.IsNullOrWhiteSpace(name)) throw new KernPanic("define_type: пустое имя типа");
		var seen = new HashSet<string>();
		foreach (var f in fields)
			if (!seen.Add(f.Name)) throw new KernPanic($"define_type: поле '{f.Name}' объявлено дважды");

		var def = new TypeDef
		{
			Name = name,
			FullName = name,
			Kind = isUnit ? (immutable ? TypeKind.ImmutableUnit : TypeKind.Unit) : TypeKind.Class,
			Flags = MemberFlags.Pub,
			Attributes = [.. attributes.Select(a => ToUse(a.Name, a.Args))],
		};
		foreach (var f in fields)
			def.Fields.Add(new FieldDef
			{
				Name = f.Name,
				Type = ToSig(f.Type),
				Flags = MemberFlags.Pub,
				Attributes = [.. f.Attributes.Select(a => ToUse(a.Name, a.Args))],
			});

		int index = ModuleLoader.AddType(vm.Info, def);
		return vm.Types.Class(index, []);
	}

	private static AttributeUse ToUse(string name, KValue[] args) => new()
	{
		TypeName = name,
		Args = [.. args.Select(a => new ConstantArg { Value = ToConstant(a) })],
	};

	private static object? ToConstant(KValue v) => v.Kind switch
	{
		ValueKind.Nil or ValueKind.None => null,
		ValueKind.Bool => v.Bits != 0,
		ValueKind.Str => (string)v.Ref!,
		ValueKind.Char => (char)v.Bits,
		_ when v.IsFloat => v.AsF64,
		_ when v.IsNumber => v.Bits,
		_ => v.Ref?.ToString(),
	};

	/// <summary>Обратное к TypeFactory.Resolve для типов без параметров.</summary>
	public static TypeSig ToSig(KType t) => t switch
	{
		KPrimType p => new TypeSig.Prim(p.Kind),
		KClassType c => new TypeSig.Named(c.Def.FullName, [.. c.Args.Select(ToSig)]),
		KNullableType n => new TypeSig.Nullable(ToSig(n.Inner)),
		KFuncType f => new TypeSig.Func([.. f.Params.Select(ToSig)], ToSig(f.Return)),
		KTupleType tu => new TypeSig.Tuple([.. tu.Items.Select(ToSig)], tu.Names),
		KUnionType u => new TypeSig.Union([.. u.Members.Select(ToSig)]),
		KBuiltinGenericType b => new TypeSig.Named(b.Builtin, [.. b.Args.Select(ToSig)]),
		KNativeType nt => new TypeSig.Named(nt.Name.Split('<')[0], [.. nt.Args.Select(ToSig)]),
		_ => throw new KernPanic("define_type: тип поля не поддерживается: " + t.Name),
	};
}
