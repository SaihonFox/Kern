using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Свойства и вызовы <c>method_info</c> (spec round 18).</summary>
public static partial class MethodInfoApi
{
	[KernProperty]
	[KernName("params")]
	[KernReturn("list<param_info>")]
	public static KValue Parameters(Interpreter interpreter, KNativeBox self)
	{
		KernMethodInfo info = Require(self);
		var values = new List<KValue>();
		if (info.Runtime is { } runtime)
		{
			for (var index = 0; index < runtime.Parameters.Count; index++)
				values.Add(KValue.Obj(new KNativeBox(
					new KernParamInfo(index, $"arg{index}", KTypeSignature.From(runtime.Parameters[index])))));
			return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
		}

		FunctionDef method = info.Method!;
		bool instance = info.Owner is not null && !IsStatic(method);
		int skip = instance ? 1 : 0;
		var names = ParameterNames(method);
		for (var index = skip; index < method.ParamTypes.Length; index++)
		{
			string name = index - skip < names.Length ? names[index - skip] : $"arg{index - skip}";
			values.Add(KValue.Obj(new KNativeBox(new KernParamInfo(index - skip, name, method.ParamTypes[index]))));
		}

		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	[KernProperty]
	[KernName("return_type")]
	[KernReturn("type")]
	public static KValue ReturnType(Interpreter interpreter, KNativeBox self)
	{
		KernMethodInfo info = Require(self);
		if (info.Runtime is { } runtime)
			return KValue.Obj(runtime.ReturnType);
		try
		{
			return KValue.Obj(interpreter.Types.FromSig(info.Method!.ReturnType));
		}
		catch (InvalidOperationException)
		{
			return KValue.Obj(interpreter.Types.Prim(PrimKind.Object));
		}
	}

	[KernProperty]
	[KernName("is_static")]
	public static bool IsStaticProperty(KNativeBox self) =>
		Require(self).Runtime?.IsStatic ?? IsStatic(Require(self).Method!);

	[KernProperty]
	[KernReturn("list<attribute_info>")]
	public static KValue Attributes(Interpreter interpreter, KNativeBox self)
	{
		var values = (Require(self).Method?.Attributes ?? [])
			.Select(attribute => KValue.Obj(new KNativeBox(new KernAttributeInfo(attribute))))
			.ToArray();
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	/// <summary><c>call_static([args])</c>: вызвать статическую функцию без получателя.</summary>
	[KernName("call_static")]
	[KernMember]
	public static KValue CallStatic(Interpreter interpreter, KNativeBox self, KValue arguments)
	{
		KernMethodInfo info = Require(self);
		KValue[] values = ListValues(arguments);
		return info.Runtime is { } runtime
			? interpreter.RunValue(runtime.Function, values)
			: interpreter.RunValue(KValue.Obj(info.Method!), values);
	}

	private static bool IsStatic(FunctionDef method) => (method.Flags & MemberFlags.Static) != 0;

	private static string[] ParameterNames(FunctionDef method)
	{
		foreach (AttributeUse attribute in method.Attributes)
		{
			if (attribute.Name != "kern.param_names" || attribute.Args.Count == 0)
				continue;
			return attribute.Args[0].AsStr.Split(',', StringSplitOptions.TrimEntries);
		}

		return [];
	}
}

/// <summary>Члены <c>property_info</c>.</summary>
[KernType("property_info")]
public static class PropertyInfoApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Runtime?.Name ?? Require(self).Property!.Name;

	[KernProperty]
	[KernReturn("type")]
	public static KValue Type(Interpreter interpreter, KNativeBox self)
	{
		KernPropertyInfo info = Require(self);
		if (info.Runtime is { } runtime)
			return KValue.Obj(runtime.Type);
		try
		{
			return KValue.Obj(interpreter.Types.FromSig(info.Property!.Type));
		}
		catch (InvalidOperationException)
		{
			return KValue.Obj(interpreter.Types.Prim(PrimKind.Object));
		}
	}

	[KernProperty]
	[KernReturn("list<attribute_info>")]
	public static KValue Attributes(Interpreter interpreter, KNativeBox self)
	{
		var values = (Require(self).Property?.Attributes ?? [])
			.Select(attribute => KValue.Obj(new KNativeBox(new KernAttributeInfo(attribute))))
			.ToArray();
		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), values));
	}

	[KernMember]
	public static KValue Get(Interpreter interpreter, KNativeBox self, KValue target)
	{
		KernPropertyInfo info = Require(self);
		if (info.Runtime is { } runtime)
			return runtime.Getter is { } getter
				? interpreter.RunValue(getter, [target])
				: throw new KernPanic($"property_info.get: у свойства '{runtime.Name}' нет геттера");
		return info.Property!.Getter >= 0
			? interpreter.RunValue(
				KValue.Obj(interpreter.Module.FunctionPool[info.Property.Getter]),
				[target])
			: ReadBackingField(info, target);
	}

	[KernMember]
	public static void Set(Interpreter interpreter, KNativeBox self, KValue target, KValue value)
	{
		KernPropertyInfo info = Require(self);
		if (info.Runtime is { } runtime)
		{
			if (runtime.Setter is { } setter)
			{
				_ = interpreter.RunValue(setter, [target, value]);
				return;
			}

			throw new KernPanic($"property_info.set: свойство '{runtime.Name}' только для чтения");
		}

		if (info.Property!.Setter >= 0)
		{
			_ = interpreter.RunValue(
				KValue.Obj(interpreter.Module.FunctionPool[info.Property.Setter]),
				[target, value]);
			return;
		}

		if (target.Ref is KObject instance && info.Property.BackingField >= 0)
		{
			instance.SetField(info.Property.BackingField, value);
			return;
		}

		throw new KernPanic($"property_info.set: свойство '{info.Property.Name}' только для чтения");
	}

	private static KValue ReadBackingField(KernPropertyInfo info, KValue target) =>
		target.Ref is KObject instance && info.Property!.BackingField >= 0
			? instance.GetField(info.Property.BackingField)
			: throw new KernPanic($"property_info.get: у свойства '{info.Property!.Name}' нет геттера");

	private static KernPropertyInfo Require(KNativeBox self) => self.Value switch
	{
		KernPropertyInfo info => info,
		_ => throw new KernPanic($"property_info: ожидалось описание свойства, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены <c>param_info</c>.</summary>
[KernType("param_info")]
public static class ParamInfoApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernProperty]
	public static int Index(KNativeBox self) => Require(self).Index;

	[KernProperty]
	[KernReturn("type")]
	public static KValue Type(Interpreter interpreter, KNativeBox self)
	{
		try
		{
			return KValue.Obj(interpreter.Types.FromSig(Require(self).Type));
		}
		catch (InvalidOperationException)
		{
			return KValue.Obj(interpreter.Types.Prim(PrimKind.Object));
		}
	}

	private static KernParamInfo Require(KNativeBox self) => self.Value switch
	{
		KernParamInfo info => info,
		_ => throw new KernPanic($"param_info: ожидалось описание параметра, получено {self.Value.GetType().Name}"),
	};
}
