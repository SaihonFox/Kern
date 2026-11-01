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
	private sealed class BuilderField(string fieldName, KType type)
	{
		public string Name { get; } = fieldName;
		public KType Type { get; } = type;
		public List<AttributeUse> Attributes { get; } = [];
	}

	private readonly List<BuilderField> _fields = [];
	private readonly List<AttributeUse> _attributes = [];
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
		_fields.Add(new BuilderField(fieldName, type));
		return this;
	}

	/// <summary>Добавить атрибут последнему полю (<c>field_attribute("column")</c>).</summary>
	public KernTypeBuilder FieldAttribute(string attribute)
	{
		if (_fields.Count == 0)
			throw new KernPanic("type_builder: field_attribute без поля");
		_fields[^1].Attributes.Add(new AttributeUse(attribute));
		return this;
	}

	/// <summary>Добавить атрибут типу.</summary>
	public KernTypeBuilder AddAttribute(string attribute, IReadOnlyList<KConst> arguments)
	{
		_attributes.Add(new AttributeUse(attribute, [.. arguments]));
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
			Attributes = [.. _attributes],
		};
		for (var index = 0; index < _fields.Count; index++)
		{
			BuilderField field = _fields[index];
			definition.Fields.Add(new FieldDef
			{
				Name = field.Name,
				Slot = index,
				Type = ToSignature(field.Type),
				Attributes = [.. field.Attributes],
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

	/// <summary><c>field_attribute(name)</c>: атрибут последнего поля.</summary>
	[KernName("field_attribute")]
	[KernMember]
	[KernReturn("type_builder")]
	public static KValue FieldAttribute(KNativeBox self, string name) =>
		KValue.Obj(new KNativeBox(Require(self).FieldAttribute(name)));

	/// <summary><c>attribute(name[, args])</c>: атрибут типа.</summary>
	[KernMember]
	[KernReturn("type_builder")]
	public static KValue Attribute(KNativeBox self, string name) =>
		KValue.Obj(new KNativeBox(Require(self).AddAttribute(name, [])));

	[KernMember]
	[KernName("attribute")]
	[KernReturn("type_builder")]
	public static KValue AttributeArgs(KNativeBox self, string name, KValue arguments)
	{
		var args = new List<KConst>();
		if (arguments.Ref is KList list)
			foreach (KValue item in list.Items)
				args.Add(ToConst(item));
		return KValue.Obj(new KNativeBox(Require(self).AddAttribute(name, args)));
	}

	/// <summary><c>build()</c>: зафиксировать тип и вернуть значение <c>type</c>.</summary>
	[KernMember]
	[KernReturn("type")]
	public static KValue Build(Interpreter interpreter, KNativeBox self)
	{
		KClassType type = Require(self).Build(interpreter.Types);
		if (!interpreter.Module.TypePool.Contains(type.Definition))
			interpreter.Module.TypePool.Add(type.Definition);
		return KValue.Obj(type);
	}

	private static KernTypeBuilder Require(KNativeBox self) => self.Value switch
	{
		KernTypeBuilder builder => builder,
		_ => throw new KernPanic($"type_builder: ожидался построитель типа, получено {self.Value.GetType().Name}"),
	};

	private static KType RequireType(KValue value) =>
		value.Ref as KType ?? throw new KernPanic($"type_builder.field: ожидался тип, получено {value.TypeName}");

	private static KConst ToConst(KValue value) => value.Kind switch
	{
		ValueKind.Str => KConst.Str(value.AsStr),
		ValueKind.Bool => KConst.Bool(value.AsBool),
		ValueKind.Char => KConst.Char(value.AsChar),
		ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64 => KConst.I64(value.AsI64),
		ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => KConst.I64((long)value.AsU64),
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => KConst.F64(value.AsF64),
		ValueKind.Nil => KConst.Nil,
		_ => KConst.Str(Kern.Binding.Formatting.KernFormat.Value(value)),
	};
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
	{		ValueKind.Ref when value.Ref is KObject instance => KValue.Obj(instance.Type),
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

	/// <summary><c>reflect.types()</c>: объявленные типы модуля.</summary>
	[KernStatic]
	[KernMember]
	[KernReturn("list<type>")]
	public static KValue Types(Interpreter interpreter) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. interpreter.Module.TypePool.Select(definition => KValue.Obj(interpreter.Types.Named(definition)))]));

	/// <summary><c>reflect.find_type(name)</c>: тип по полному или короткому имени.</summary>
	[KernStatic]
	[KernMember]
	[KernName("find_type")]
	[KernReturn("type?")]
	public static KValue FindType(Interpreter interpreter, string name)
	{
		foreach (TypeDef definition in interpreter.Module.TypePool)
			if (definition.FullName == name || definition.Name == name)
				return KValue.Obj(interpreter.Types.Named(definition));
		return KValue.Nil;
	}

	/// <summary><c>reflect.functions()</c>: функции уровня модуля.</summary>
	[KernStatic]
	[KernMember]
	[KernReturn("list<method_info>")]
	public static KValue Functions(Interpreter interpreter) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. ModuleFunctions(interpreter).Select(method => KValue.Obj(new KNativeBox(new KernMethodInfo(null, method))))]));

	/// <summary><c>reflect.find_function(name)</c>: первая функция модуля с именем.</summary>
	[KernStatic]
	[KernMember]
	[KernName("find_function")]
	[KernReturn("method_info?")]
	public static KValue FindFunction(Interpreter interpreter, string name)
	{
		FunctionDef? found = ModuleFunctions(interpreter).FirstOrDefault(function => function.Name == name);
		return found is null ? KValue.Nil : KValue.Obj(new KNativeBox(new KernMethodInfo(null, found)));
	}

	/// <summary><c>reflect.functions_with_attribute(name)</c>.</summary>
	[KernStatic]
	[KernMember]
	[KernName("functions_with_attribute")]
	[KernReturn("list<method_info>")]
	public static KValue FunctionsWithAttribute(Interpreter interpreter, string name) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. ModuleFunctions(interpreter)
				.Where(function => function.Attributes.Any(attribute => attribute.Name == name))
				.Select(function => KValue.Obj(new KNativeBox(new KernMethodInfo(null, function))))]));

	/// <summary><c>reflect.module_attributes()</c>.</summary>
	[KernStatic]
	[KernMember]
	[KernName("module_attributes")]
	[KernReturn("list<attribute_info>")]
	public static KValue ModuleAttributes(Interpreter interpreter) =>
		KValue.Obj(new KList(
			interpreter.Types.Prim(PrimKind.Object),
			[.. interpreter.Module.Attributes.Select(attribute => KValue.Obj(new KNativeBox(new KernAttributeInfo(attribute))))]));

	private static IEnumerable<FunctionDef> ModuleFunctions(Interpreter interpreter) =>
		interpreter.Module.FunctionPool.Where(function =>
			(function.Flags & MemberFlags.Native) == 0 &&
			function.OwnerType < 0 &&
			!function.Name.StartsWith("<", StringComparison.Ordinal));
}
