using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>
/// Динамический тип, собранный <c>reflect.define_type</c> (spec round 18): поля задаются
/// во время выполнения, <c>build()</c> возвращает значение <c>type</c>.
/// </summary>
[KernType("type_builder")]
public sealed class KernTypeBuilder(string name)
{
	private readonly List<(string Name, KType Type)> _fields = [];
	private bool _unit;
	private TypeDef? _definition;
	private KClassType? _type;

	public string TypeName { get; } = name;

	public KernTypeBuilder AsUnit()
	{
		_unit = true;
		return this;
	}

	public KernTypeBuilder AddField(string fieldName, KType type)
	{
		if (_definition is not null)
			throw new KernPanic("type_builder: поле нельзя добавить после build()");
		_fields.Add((fieldName, type));
		return this;
	}

	public KClassType Build(TypeFactory types)
	{
		if (_type is not null)
			return _type;

		var definition = new TypeDef
		{
			Name = TypeName,
			FullName = TypeName,
			Kind = _unit ? TypeDefKind.Unit : TypeDefKind.Class,
		};
		for (var index = 0; index < _fields.Count; index++)
		{
			(string fieldName, KType fieldType) = _fields[index];
			definition.Fields.Add(new FieldDef
			{
				Name = fieldName,
				Slot = index,
				Type = ToSignature(fieldType),
			});
		}

		_definition = definition;
		_type = types.Named(definition);
		return _type;
	}

	private static TypeSig ToSignature(KType type) => type switch
	{
		KPrimType primitive => TypeSig.Prim(primitive.Prim),
		KNullableType nullable => new TypeSig.NullableSig(ToSignature(nullable.Inner)),
		KClassType @class => new TypeSig.NamedSig(@class.Definition.FullName, [.. @class.TypeArgs.Select(ToSignature)]),
		_ => TypeSig.Object,
	};
}

/// <summary>Члены <c>type_builder</c>.</summary>
[KernType("type_builder")]
public static class TypeBuilderApi
{
	[KernName("as_unit")]
	[KernMember]
	[KernReturn("type_builder")]
	public static KValue AsUnit(KNativeBox self) => KValue.Obj(new KNativeBox(Require(self).AsUnit()));

	/// <summary><c>field(name, type)</c>: добавить поле; возвращает builder.</summary>
	[KernMember]
	[KernReturn("type_builder")]
	public static KValue Field(KNativeBox self, string name, KValue type) =>
		KValue.Obj(new KNativeBox(Require(self).AddField(name, RequireType(type))));

	/// <summary><c>build()</c>: зафиксировать тип и вернуть значение <c>type</c>.</summary>
	[KernMember]
	[KernReturn("type")]
	public static KValue Build(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(Require(self).Build(interpreter.Types));

	private static KernTypeBuilder Require(KNativeBox self) => self.Value switch
	{
		KernTypeBuilder builder => builder,
		_ => throw new KernPanic($"type_builder: ожидался построитель типа, получено {self.Value.GetType().Name}"),
	};

	private static KType RequireType(KValue value) =>
		value.Ref as KType ?? throw new KernPanic($"type_builder.field: ожидался тип, получено {value.TypeName}");
}

/// <summary>
/// <c>kern.reflection</c> (spec round 18): <c>reflect.define_type</c> и поиск типов по
/// атрибуту. Рефлексия времени выполнения для существующих типов — члены значения
/// <c>type</c> (<see cref="TypeMembers"/>).
/// </summary>
[KernType("reflect")]
public static class ReflectApi
{
	/// <summary><c>reflect.define_type("Name")</c>: начать построение типа.</summary>
	[KernStatic]
	[KernMember]
	[KernName("define_type")]
	[KernReturn("type_builder")]
	public static KValue DefineType(string name) => KValue.Obj(new KNativeBox(new KernTypeBuilder(name)));

	/// <summary><c>reflect.types_with_attribute("entity")</c>: типы модуля с атрибутом.</summary>
	[KernStatic]
	[KernMember]
	[KernName("types_with_attribute")]
	[KernReturn("list<type>")]
	public static KValue TypesWithAttribute(Interpreter interpreter, string name)
	{
		var types = new List<KValue>();
		foreach (TypeDef definition in interpreter.Module.TypePool)
		{
			if (definition.Attributes.Any(attribute => attribute.Name == name))
				types.Add(KValue.Obj(interpreter.Types.Named(definition)));
		}

		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), types));
	}

	/// <summary><c>reflect.type_of(value)</c>: значение <c>type</c> для объекта.</summary>
	[KernStatic]
	[KernMember]
	[KernName("type_of")]
	[KernReturn("type")]
	public static KValue TypeOf(Interpreter interpreter, KValue value) => value.Kind switch
	{
		ValueKind.Ref when value.Ref is KObject instance => KValue.Obj(instance.Type),
		ValueKind.Ref when value.Ref is KType type => KValue.Obj(type),
		ValueKind.Str => KValue.Obj(interpreter.Types.Prim(PrimKind.Str)),
		ValueKind.Bool => KValue.Obj(interpreter.Types.Prim(PrimKind.Bool)),
		ValueKind.Char => KValue.Obj(interpreter.Types.Prim(PrimKind.Char)),
		ValueKind.I8 => KValue.Obj(interpreter.Types.Prim(PrimKind.I8)),
		ValueKind.I16 => KValue.Obj(interpreter.Types.Prim(PrimKind.I16)),
		ValueKind.I32 => KValue.Obj(interpreter.Types.Prim(PrimKind.I32)),
		ValueKind.I64 => KValue.Obj(interpreter.Types.Prim(PrimKind.I64)),
		ValueKind.U8 => KValue.Obj(interpreter.Types.Prim(PrimKind.U8)),
		ValueKind.U16 => KValue.Obj(interpreter.Types.Prim(PrimKind.U16)),
		ValueKind.U32 => KValue.Obj(interpreter.Types.Prim(PrimKind.U32)),
		ValueKind.U64 => KValue.Obj(interpreter.Types.Prim(PrimKind.U64)),
		ValueKind.F16 => KValue.Obj(interpreter.Types.Prim(PrimKind.F16)),
		ValueKind.F32 => KValue.Obj(interpreter.Types.Prim(PrimKind.F32)),
		ValueKind.F64 => KValue.Obj(interpreter.Types.Prim(PrimKind.F64)),
		_ => KValue.Obj(interpreter.Types.Prim(PrimKind.Object)),
	};
}
