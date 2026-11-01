using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Описание поля типа для <c>type.fields</c> (spec round 18).</summary>
[KernType("field_info")]
public sealed class KernFieldInfo(KClassType owner, FieldDef field)
{
	public KClassType Owner { get; } = owner;

	public FieldDef Field { get; } = field;
}

/// <summary>Описание метода для <c>type.get_method</c>.</summary>
[KernType("method_info")]
public sealed class KernMethodInfo(KClassType owner, FunctionDef method)
{
	public KClassType Owner { get; } = owner;

	public FunctionDef Method { get; } = method;
}

/// <summary>Описание атрибута (<c>#[entity("users")]</c>).</summary>
[KernType("attribute_info")]
public sealed class KernAttributeInfo(AttributeUse attribute)
{
	public AttributeUse Attribute { get; } = attribute;
}

/// <summary>Члены <c>field_info</c> (Kotlin-стиль: <c>f.name</c>, <c>f.type</c>, <c>f.get(obj)</c>).</summary>
[KernType("field_info")]
public static class FieldInfoApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Field.Name;

	[KernProperty]
	[KernReturn("type")]
	public static KValue Type(Interpreter interpreter, KNativeBox self)
	{
		KernFieldInfo info = Require(self);
		try
		{
			return KValue.Obj(interpreter.Types.FromSig(info.Field.Type));
		}
		catch (InvalidOperationException)
		{
			return KValue.Obj(interpreter.Types.Prim(PrimKind.Object));
		}
	}

	/// <summary><c>get(obj)</c>: значение поля у объекта.</summary>
	[KernMember]
	public static KValue Get(KNativeBox self, KValue target) =>
		RequireTarget(self, target).GetField(Require(self).Field.Slot);

	private static KernFieldInfo Require(KNativeBox self) => self.Value switch
	{
		KernFieldInfo info => info,
		_ => throw new KernPanic($"field_info: ожидалось описание поля, получено {self.Value.GetType().Name}"),
	};

	private static KObject RequireTarget(KNativeBox self, KValue target) =>
		target.Ref is KObject instance && instance.Type.Definition == Require(self).Owner.Definition
			? instance
			: throw new KernPanic($"field_info.get: ожидался объект типа '{Require(self).Owner.Canonical}'");
}

/// <summary>Члены <c>method_info</c> (C#-стиль: <c>m.call(obj, [args])</c>).</summary>
[KernType("method_info")]
public static class MethodInfoApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Method.Name;

	/// <summary><c>call(obj, [args])</c>: вызвать метод в VM.</summary>
	[KernMember]
	public static KValue Call(Interpreter interpreter, KNativeBox self, KValue target, KValue arguments)
	{
		KernMethodInfo info = Require(self);
		KValue[] values = ListValues(arguments);
		KValue fn = KValue.Obj(_function(interpreter, info));
		KValue[] call = new KValue[values.Length + 1];
		call[0] = target;
		Array.Copy(values, 0, call, 1, values.Length);
		return interpreter.RunValue(fn, call);
	}

	private static FunctionDef _function(Interpreter interpreter, KernMethodInfo info)
	{
		foreach (TypeDef type in interpreter.Module.TypePool)
			if (type == info.Owner.Definition)
				foreach (int index in type.Methods)
					if (interpreter.Module.FunctionPool[index] == info.Method)
						return info.Method;
		return info.Method;
	}

	private static KernMethodInfo Require(KNativeBox self) => self.Value switch
	{
		KernMethodInfo info => info,
		_ => throw new KernPanic($"method_info: ожидалось описание метода, получено {self.Value.GetType().Name}"),
	};

	private static KValue[] ListValues(KValue arguments) =>
		arguments.Ref is KList list
			? [.. list.Items]
			: throw new KernPanic($"method_info.call: ожидался список аргументов, получено {arguments.TypeName}");
}

/// <summary>Члены <c>attribute_info</c>: имя и аргументы атрибута.</summary>
[KernType("attribute_info")]
public static class AttributeInfoApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Attribute.Name;

	[KernProperty]
	[KernReturn("list<object>")]
	public static KValue Args(Interpreter interpreter, KNativeBox self)
	{
		var values = Require(self).Attribute.Args.Select(ToValue).ToArray();
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	private static KernAttributeInfo Require(KNativeBox self) => self.Value switch
	{
		KernAttributeInfo info => info,
		_ => throw new KernPanic($"attribute_info: ожидался атрибут, получено {self.Value.GetType().Name}"),
	};

	internal static KValue ToValue(KConst value) => value.Kind switch
	{
		KConstKind.Str => KValue.Str(value.AsStr),
		KConstKind.Bool => KValue.Bool(value.AsBool),
		KConstKind.Char => KValue.Char(value.AsChar),
		KConstKind.I64 => KValue.I64(value.AsI64),
		KConstKind.F64 => KValue.F64(value.AsF64),
		KConstKind.Nil => KValue.Nil,
		_ => KValue.None,
	};
}

/// <summary>
/// Члены значения <c>type</c> (spec round 18): <c>name</c>, <c>fields</c>,
/// <c>has_attribute</c>, <c>attribute</c>, <c>get_method</c>, <c>create</c>, <c>get</c>.
/// Значение приходит как <see cref="KType"/> в <c>KValue.Ref</c>.
/// </summary>
[KernType("object")]
public static class TypeMembers
{
	[KernProperty]
	public static string Name(KValue self) => Require(self) switch
	{
		KClassType @class => @class.Definition.Name,
		KBuiltinGenericType builtin => builtin.Canonical,
		_ => Require(self).Canonical,
	};

	/// <summary><c>fields</c>: экземплярные поля в порядке объявления.</summary>
	[KernProperty]
	[KernReturn("list<field_info>")]
	public static KValue Fields(Interpreter interpreter, KValue self)
	{
		KClassType @class = RequireClass(self);
		var fields = new List<KValue>();
		foreach (TypeDef definition in Layout(@class))
			foreach (FieldDef field in definition.Fields)
			{
				if ((field.Flags & MemberFlags.Static) != 0 || field.Name.StartsWith("$slot", StringComparison.Ordinal))
					continue;
				fields.Add(KValue.Obj(new KNativeBox(new KernFieldInfo(@class, field))));
			}
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), fields));
	}

	[KernName("has_attribute")]
	[KernMember]
	public static bool HasAttribute(KValue self, string name) =>
		Attributes(RequireClass(self)).Any(attribute => attribute.Name == name);

	[KernMember]
	[KernReturn("attribute_info?")]
	public static KValue Attribute(KValue self, string name) =>
		Attributes(RequireClass(self)).FirstOrDefault(attribute => attribute.Name == name) is { } found
			? KValue.Obj(new KNativeBox(new KernAttributeInfo(found)))
			: KValue.Nil;

	[KernName("get_method")]
	[KernMember]
	[KernReturn("method_info?")]
	public static KValue GetMethod(Interpreter interpreter, KValue self, string name)
	{
		KClassType @class = RequireClass(self);
		foreach (TypeDef definition in Layout(@class))
			foreach (int index in definition.Methods)
			{
				FunctionDef method = interpreter.Module.FunctionPool[index];
				if (method.Name == name)
					return KValue.Obj(new KNativeBox(new KernMethodInfo(@class, method)));
			}
		return KValue.Nil;
	}

	/// <summary><c>create([args])</c>: создать экземпляр через конструктор (или поля).</summary>
	[KernMember]
	public static KValue Create(Interpreter interpreter, KValue self, KValue arguments)
	{
		KClassType @class = RequireClass(self);
		KValue[] values = arguments.Ref is KList list
			? [.. list.Items]
			: throw new KernPanic($"type.create: ожидался список аргументов, получено {arguments.TypeName}");

		KValue[] fields = new KValue[@class.Definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		var instance = new KObject(@class, fields, @class.Definition.Kind is TypeDefKind.Unit or TypeDefKind.ImmutableUnit);

		if (@class.Definition.Ctors.Count > 0)
		{
			FunctionDef constructor = interpreter.Module.FunctionPool[@class.Definition.Ctors[0]];
			KValue[] call = new KValue[values.Length + 1];
			call[0] = KValue.Obj(instance);
			Array.Copy(values, 0, call, 1, values.Length);
			interpreter.RunValue(KValue.Obj(constructor), call);
			return KValue.Obj(instance);
		}

		if (values.Length != fields.Length)
			throw new KernPanic($"type.create: '{@class.Canonical}' ждёт {fields.Length} значений полей, передано {values.Length}");
		for (var index = 0; index < values.Length; index++)
			instance.SetField(index, values[index]);
		return KValue.Obj(instance);
	}

	/// <summary><c>get(obj, "field")</c>: значение поля или геттера свойства.</summary>
	[KernMember]
	public static KValue Get(Interpreter interpreter, KValue self, KValue target, string name)
	{
		KClassType @class = RequireClass(self);
		if (target.Ref is not KObject instance)
			throw new KernPanic("type.get: ожидался объект");
		foreach (TypeDef definition in Layout(@class))
		{
			foreach (FieldDef field in definition.Fields)
				if (field.Name == name && (field.Flags & MemberFlags.Static) == 0)
					return instance.GetField(field.Slot);
			foreach (PropertyDef property in definition.Properties)
				if (property.Name == name && property.Getter >= 0)
					return interpreter.RunValue(
						KValue.Obj(interpreter.Module.FunctionPool[property.Getter]),
						[KValue.Obj(instance)]);
		}

		throw new KernPanic($"type.get: у '{@class.Canonical}' нет поля или свойства '{name}'");
	}

	private static KType Require(KValue self) =>
		self.Ref as KType ?? throw new KernPanic($"type: ожидалось значение типа, получено {self.TypeName}");

	private static KClassType RequireClass(KValue self) =>
		Require(self) as KClassType ?? throw new KernPanic($"type: '{self.Ref}' не является объявленным типом");

	private static IReadOnlyList<AttributeUse> Attributes(KClassType @class) =>
		@class.Definition.Attributes;

	/// <summary>Тип и его предки (поля выводятся в порядке слоёв, как в раскладке).</summary>
	private static IEnumerable<TypeDef> Layout(KClassType @class)
	{
		yield return @class.Definition;
	}
}
