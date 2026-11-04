using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Полная поверхность значения <c>type</c> (spec round 18).</summary>
public static partial class TypeMembers
{
	[KernProperty]
	[KernName("full_name")]
	public static string FullName(KValue self) => Require(self) switch
	{
		KClassType @class => @class.Definition.FullName,
		_ => Require(self).Canonical,
	};

	[KernProperty]
	[KernReturn("list<type>")]
	public static KValue Args(Interpreter interpreter, KValue self)
	{
		IReadOnlyList<KType> arguments = Require(self) switch
		{
			KClassType @class => @class.TypeArgs,
			KBuiltinGenericType builtin => builtin.TypeArgs,
			_ => [],
		};
		return Types(interpreter, arguments);
	}

	[KernProperty]
	[KernReturn("list<type>")]
	public static KValue Parents(Interpreter interpreter, KValue self)
	{
		if (Require(self) is not KClassType @class)
			return Types(interpreter, []);
		var values = new List<KType>();
		foreach (TypeSig parent in @class.Definition.Parents)
		{
			try
			{
				values.Add(interpreter.Types.FromSig(parent, @class));
			}
			catch (InvalidOperationException)
			{
				// Неизвестный родитель из повреждённого пула пропускаем.
			}
		}

		return Types(interpreter, values);
	}

	[KernProperty]
	[KernReturn("list<property_info>")]
	public static KValue Properties(Interpreter interpreter, KValue self)
	{
		KClassType @class = RequireClass(self);
		var values = new List<KValue>();
		foreach (TypeDef definition in Layout(interpreter, @class))
			foreach (PropertyDef property in definition.Properties)
				values.Add(KValue.Obj(new KNativeBox(new KernPropertyInfo(@class, property))));
		if (RuntimeTypeRegistry.Find(@class.Definition) is { } runtime)
			foreach (RuntimeProperty property in runtime.Properties)
				values.Add(KValue.Obj(new KNativeBox(new KernPropertyInfo(@class, null, property))));
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	[KernProperty]
	[KernReturn("list<method_info>")]
	public static KValue Methods(Interpreter interpreter, KValue self)
	{
		KClassType @class = RequireClass(self);
		var values = new List<KValue>();
		foreach (TypeDef definition in Layout(interpreter, @class))
			foreach (int index in definition.Methods)
				values.Add(KValue.Obj(new KNativeBox(new KernMethodInfo(@class, interpreter.Module.FunctionPool[index]))));
		if (RuntimeTypeRegistry.Find(@class.Definition) is { } runtime)
			foreach (RuntimeMethod method in runtime.Methods)
				values.Add(KValue.Obj(new KNativeBox(new KernMethodInfo(@class, null, method))));
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	[KernProperty]
	[KernReturn("list<method_info>")]
	public static KValue Constructors(Interpreter interpreter, KValue self)
	{
		KClassType @class = RequireClass(self);
		var values = new List<KValue>();
		foreach (TypeDef definition in Layout(interpreter, @class))
			foreach (int index in definition.Ctors)
				values.Add(KValue.Obj(new KNativeBox(new KernMethodInfo(@class, interpreter.Module.FunctionPool[index]))));
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	[KernProperty]
	[KernReturn("list<attribute_info>")]
	public static KValue AttributesOf(Interpreter interpreter, KValue self)
	{
		var values = Attributes(RequireClass(self))
			.Select(attribute => KValue.Obj(new KNativeBox(new KernAttributeInfo(attribute))))
			.ToArray();
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	[KernProperty]
	[KernName("is_unit")]
	public static bool IsUnitType(KValue self) =>
		Require(self) is KClassType { Definition.Kind: TypeDefKind.Unit or TypeDefKind.ImmutableUnit };

	[KernProperty]
	[KernName("is_enum")]
	public static bool IsEnumType(KValue self) =>
		Require(self) is KClassType { Definition.Kind: TypeDefKind.Union };

	[KernProperty]
	[KernName("is_abstract")]
	public static bool IsAbstractType(KValue self) =>
		Require(self) is KClassType { Definition.Flags: var flags } && (flags & MemberFlags.Abstract) != 0;

	[KernProperty]
	[KernName("is_interface")]
	public static bool IsInterfaceType(KValue self) =>
		Require(self) is KClassType { Definition.Flags: var flags } && (flags & MemberFlags.Abstract) != 0;

	[KernProperty]
	[KernName("is_declared")]
	public static bool IsDeclaredType(Interpreter interpreter, KValue self) =>
		Require(self) is KClassType @class && interpreter.Module.TypePool.Contains(@class.Definition);

	/// <summary><c>enum_names()</c>: имена вариантов union-типа.</summary>
	[KernName("enum_names")]
	[KernMember]
	[KernReturn("list<str>")]
	public static KValue EnumNames(Interpreter interpreter, KValue self)
	{
		if (Require(self) is not KClassType { Definition.Kind: TypeDefKind.Union } @class)
			return KValue.Obj(new KList(null, []));
		var names = new List<KValue>();
		foreach (TypeSig member in @class.Definition.UnionMembers)
		{
			try
			{
				if (interpreter.Types.FromSig(member, @class) is KClassType variant)
					names.Add(KValue.Str(variant.Definition.Name));
			}
			catch (InvalidOperationException)
			{
			}
		}

		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Str), names));
	}

	[KernName("get_field")]
	[KernMember]
	[KernReturn("field_info?")]
	public static KValue GetField(Interpreter interpreter, KValue self, string name)
	{
		KClassType @class = RequireClass(self);
		foreach (TypeDef definition in Layout(interpreter, @class))
			foreach (FieldDef field in definition.Fields)
				if (field.Name == name && (field.Flags & MemberFlags.Static) == 0)
					return KValue.Obj(new KNativeBox(new KernFieldInfo(@class, field)));
		return KValue.Nil;
	}

	[KernName("get_property")]
	[KernMember]
	[KernReturn("property_info?")]
	public static KValue GetProperty(Interpreter interpreter, KValue self, string name)
	{
		KClassType @class = RequireClass(self);
		if (RuntimeTypeRegistry.Find(@class.Definition) is { } runtime)
			foreach (RuntimeProperty property in runtime.Properties)
				if (property.Name == name)
					return KValue.Obj(new KNativeBox(new KernPropertyInfo(@class, null, property)));
		foreach (TypeDef definition in Layout(interpreter, @class))
			foreach (PropertyDef property in definition.Properties)
				if (property.Name == name)
					return KValue.Obj(new KNativeBox(new KernPropertyInfo(@class, property)));
		return KValue.Nil;
	}

	[KernName("get_methods")]
	[KernMember]
	[KernReturn("list<method_info>")]
	public static KValue GetMethods(Interpreter interpreter, KValue self, string name)
	{
		KClassType @class = RequireClass(self);
		var values = new List<KValue>();
		foreach (TypeDef definition in Layout(interpreter, @class))
			foreach (int index in definition.Methods)
			{
				FunctionDef method = interpreter.Module.FunctionPool[index];
				if (method.Name == name)
					values.Add(KValue.Obj(new KNativeBox(new KernMethodInfo(@class, method))));
			}

		if (RuntimeTypeRegistry.Find(@class.Definition) is { } runtime)
			foreach (RuntimeMethod method in runtime.Methods)
				if (method.Name == name)
					values.Add(KValue.Obj(new KNativeBox(new KernMethodInfo(@class, null, method))));

		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	/// <summary><c>set(obj, name, value)</c>: записать поле или свойство.</summary>
	[KernMember]
	public static void Set(Interpreter interpreter, KValue self, KValue target, string name, KValue value)
	{
		KClassType @class = RequireClass(self);
		if (target.Ref is not KObject instance)
			throw new KernPanic("type.set: ожидался объект");
		if (RuntimeTypeRegistry.Find(@class.Definition) is { } runtime)
			foreach (RuntimeProperty property in runtime.Properties)
				if (property.Name == name)
				{
					PropertyInfoApi.Set(
						interpreter,
						new KNativeBox(new KernPropertyInfo(@class, null, property)),
						target,
						value);
					return;
				}
		foreach (TypeDef definition in Layout(interpreter, @class))
		{
			foreach (FieldDef field in definition.Fields)
				if (field.Name == name && (field.Flags & MemberFlags.Static) == 0)
				{
					instance.SetField(field.Slot, value);
					return;
				}

			foreach (PropertyDef property in definition.Properties)
				if (property.Name == name)
				{
					var info = new KernPropertyInfo(@class, property);
					PropertyInfoApi.Set(interpreter, new KNativeBox(info), target, value);
					return;
				}
		}

		throw new KernPanic($"type.set: у '{@class.Canonical}' нет поля или свойства '{name}'");
	}

	[KernName("is_instance")]
	[KernMember]
	public static bool IsInstance(Interpreter interpreter, KValue self, KValue value)
	{
		if (Require(self) is not KClassType @class)
			return false;
		return value.Ref switch
		{
			KObject instance => IsSubtype(interpreter, instance.Type, @class),
			KList list => IsBuiltin(@class, BuiltinGenericKind.List),
			KArray array => IsBuiltin(@class, BuiltinGenericKind.Array),
			KDict dict => IsBuiltin(@class, BuiltinGenericKind.Dict),
			_ => false,
		};
	}

	[KernName("is_assignable_from")]
	[KernMember]
	public static bool IsAssignableFrom(Interpreter interpreter, KValue self, KValue other)
	{
		if (Require(self) is not KClassType @class || other.Ref is not KType from)
			return false;
		return from is KClassType source
			? IsSubtype(interpreter, source, @class)
			: from.Canonical == @class.Canonical;
	}

	private static bool IsSubtype(Interpreter interpreter, KClassType source, KClassType target)
	{
		if (source == target)
			return true;
		if (source.Definition.FullName == target.Definition.FullName)
			return true;
		foreach (TypeSig parent in source.Definition.Parents)
		{
			try
			{
				if (interpreter.Types.FromSig(parent, source) is KClassType parentType &&
					IsSubtype(interpreter, parentType, target))
					return true;
			}
			catch (InvalidOperationException)
			{
			}
		}

		return false;
	}

	private static bool IsBuiltin(KClassType @class, BuiltinGenericKind kind) =>
		@class.Definition.Kind == TypeDefKind.Class && @class.Definition.Name == kind.ToString().ToLowerInvariant();

	private static KValue Types(Interpreter interpreter, IReadOnlyList<KType> types)
	{
		var values = types.Select(type => KValue.Obj(type)).ToArray();
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}
}
