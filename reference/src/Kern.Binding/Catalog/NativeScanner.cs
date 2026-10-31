using System.Reflection;
using System.Runtime.CompilerServices;
using Kern.Binding.Attributes;
using Kern.Bytecode;

namespace Kern.Binding.Catalog;

/// <summary>
/// Строит <see cref="NativeTypeSymbol"/> / <see cref="NativeMember"/> из C#-типов по правилам раздела 6.
/// Два режима: Kern (по атрибутам) и dotnet (способ C: любой публичный тип, без атрибутов).
/// Не потокобезопасен (NullabilityInfoContext) — вызывающий синхронизирует.
/// </summary>
internal sealed class NativeScanner(TypeMapper mapper)
{
	private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
	private const BindingFlags AllPublic = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
	private readonly NullabilityInfoContext _nullability = new();

	// ---------- Kern-режим ----------

	/// <summary>Нужно ли «чужому» self-типу (BigInteger, decimal, Guid…) регистрировать Kern-имя.</summary>
	public void RegisterSelfTypes(string module, string name, IEnumerable<Type> clrTypes)
	{
		foreach (var t in clrTypes)
		{
			if (!(t.IsAbstract && t.IsSealed)) continue;
			foreach (var m in t.GetMethods(Declared))
			{
				var ps = m.GetParameters();
				if (!m.IsStatic || ps.Length == 0 || ps[0].Name != "self") continue;
				var self = ps[0].ParameterType;
				if (mapper.NeedsRegistration(self))
					mapper.RegisterSelf(self, new TypeSig.Named($"{module}.{name}", []));
				return;
			}
		}
	}

	/// <summary>Строит символ типа по группе C#-классов с одним [KernType]-именем.</summary>
	public NativeTypeSymbol BuildKernSymbol(string module, string name, List<Type> clrTypes)
	{
		var first = clrTypes[0];
		var kt = first.GetCustomAttribute<KernTypeAttribute>()!;
		bool anyStaticClass = clrTypes.Any(t => t.IsAbstract && t.IsSealed);
		bool hasSelf = clrTypes.Any(HasSelfMembers);

		NativeTypeCategory category;
		if (anyStaticClass) category = hasSelf ? NativeTypeCategory.Extension : NativeTypeCategory.StaticHolder;
		else if (first.IsEnum) category = NativeTypeCategory.Enum;
		else if (first.IsInterface || kt.Kind == KernTypeKind.Interface) category = NativeTypeCategory.Interface;
		else category = kt.Kind switch
		{
			KernTypeKind.Unit => NativeTypeCategory.Unit,
			KernTypeKind.ImmutableUnit => NativeTypeCategory.ImmutableUnit,
			KernTypeKind.Enum => NativeTypeCategory.Enum,
			_ => NativeTypeCategory.Class,
		};

		TypeSig sig;
		IReadOnlyList<NativeGenericParam> typeGenerics = [];
		if (category == NativeTypeCategory.Extension)
			(sig, typeGenerics) = ExtensionSig(module, name, clrTypes);
		else if (first.IsGenericTypeDefinition)
		{
			typeGenerics = GenericParamsOf(first.GetGenericArguments(), 0, false);
			sig = new TypeSig.Named($"{module}.{name}", typeGenerics.Select(g => (TypeSig)new TypeSig.Param(0, g.Index, g.Name)).ToArray());
		}
		else sig = new TypeSig.Named($"{module}.{name}", []);

		var symbol = new NativeTypeSymbol
		{
			Name = name, Module = module, Category = category, ClrType = first, Sig = sig,
			GenericParams = typeGenerics, IsOpen = kt.Open,
			Parent = KernParentOf(first, category),
		};
		symbol.ClrTypes.AddRange(clrTypes);

		if (category == NativeTypeCategory.Enum)
		{
			foreach (var n in Enum.GetNames(first)) symbol.EnumNames.Add(SnakeCase.Of(n));
			foreach (var v in Enum.GetValuesAsUnderlyingType(first)) symbol.EnumValues.Add(Convert.ToInt64(v));
		}
		foreach (var t in clrTypes) ScanMembers(symbol, t, dotnet: false, members: symbol.Members);
		return symbol;
	}

	/// <summary>Наследование нативных типов берётся из C#: `class KernButton : KernWidget` ⇒ `button` наследует `widget`.</summary>
	private TypeSig? KernParentOf(Type t, NativeTypeCategory category)
	{
		if (category is not (NativeTypeCategory.Class or NativeTypeCategory.Unit or NativeTypeCategory.ImmutableUnit)) return null;
		var bt = t.BaseType;
		if (bt is null || !bt.IsPublic) return null;
		var def = bt.IsGenericType ? bt.GetGenericTypeDefinition() : bt;
		if (def.GetCustomAttribute<KernTypeAttribute>(false) is null) return null;
		return mapper.Map(bt);
	}

	/// <summary>Свободные функции модуля: static-класс с [KernModule] без [KernType].</summary>
	public void AddFunctions(NativeModule module, Type holder)
	{
		var tmp = new List<NativeMember>();
		ScanMembers(null, holder, dotnet: false, members: tmp);
		module.Functions.AddRange(tmp);
	}

	private static bool HasSelfMembers(Type t)
	{
		if (!(t.IsAbstract && t.IsSealed)) return false;
		foreach (var m in t.GetMethods(Declared))
		{
			var ps = m.GetParameters();
			if (m.IsStatic && ps.Length > 0 && ps[0].Name == "self" && HasKernMarker(m)) return true;
		}
		return false;
	}

	private static bool HasKernMarker(MemberInfo m) =>
		m.IsDefined(typeof(KernMemberAttribute)) || m.IsDefined(typeof(KernPropertyAttribute)) || m.IsDefined(typeof(KernOperatorAttribute))
		|| m.IsDefined(typeof(KernStaticAttribute)) || m.IsDefined(typeof(KernCtorAttribute));

	/// <summary>Подпись расширяемого типа берётся из первого члена с self; методовые generic'и поднимаются на уровень типа.</summary>
	private (TypeSig, IReadOnlyList<NativeGenericParam>) ExtensionSig(string module, string name, List<Type> clrTypes)
	{
		foreach (var t in clrTypes)
		{
			if (!(t.IsAbstract && t.IsSealed)) continue;
			foreach (var m in t.GetMethods(Declared))
			{
				var ps = m.GetParameters();
				if (!m.IsStatic || ps.Length == 0 || ps[0].Name != "self" || !HasKernMarker(m)) continue;
				var selfSig = mapper.Map(ps[0].ParameterType, _nullability.Create(ps[0]), ps[0]);
				var gps = m.IsGenericMethodDefinition ? GenericParamsOf(m.GetGenericArguments(), 1, false) : [];
				// голый T с числовым ограничением → сам универсальный числовой тип (num/inum/fnum)
				if (selfSig is TypeSig.Param sp && gps.FirstOrDefault(g => g.Index == sp.Index) is { Constraints.Count: 1 } g1
					&& g1.Constraints[0] is TypeSig.Prim { Kind: PrimKind.Num or PrimKind.INum or PrimKind.FNum } numeric)
					return (numeric, []);
				var used = new List<TypeSig.Param>();
				CollectParams(selfSig, used);
				var lifted = used.DistinctBy(p => p.Index).OrderBy(p => p.Index).Select((p, i) => new NativeGenericParam
				{
					Name = p.Name, Index = i, Scope = 0,
					Constraints = gps.FirstOrDefault(g => g.Index == p.Index)?.Constraints ?? [],
				}).ToList();
				return (Lift(selfSig, used.DistinctBy(p => p.Index).OrderBy(p => p.Index).Select(p => p.Index).ToList()), lifted);
			}
		}
		return (new TypeSig.Named($"{module}.{name}", []), []);
	}

	private static void CollectParams(TypeSig sig, List<TypeSig.Param> into)
	{
		switch (sig)
		{
			case TypeSig.Param { Scope: 1 } p: into.Add(p); break;
			case TypeSig.Named n: foreach (var a in n.Args) CollectParams(a, into); break;
			case TypeSig.Func f: foreach (var a in f.Params) CollectParams(a, into); CollectParams(f.Return, into); break;
			case TypeSig.Tuple t: foreach (var a in t.Items) CollectParams(a, into); break;
			case TypeSig.Nullable nn: CollectParams(nn.Inner, into); break;
			case TypeSig.Union u: foreach (var a in u.Members) CollectParams(a, into); break;
		}
	}

	private static TypeSig Lift(TypeSig sig, List<int> order) => sig switch
	{
		TypeSig.Param { Scope: 1 } p => new TypeSig.Param(0, order.IndexOf(p.Index), p.Name),
		TypeSig.Named n => new TypeSig.Named(n.FullName, n.Args.Select(a => Lift(a, order)).ToArray()),
		TypeSig.Func f => new TypeSig.Func(f.Params.Select(a => Lift(a, order)).ToArray(), Lift(f.Return, order)),
		TypeSig.Tuple t => new TypeSig.Tuple(t.Items.Select(a => Lift(a, order)).ToArray(), t.Names),
		TypeSig.Nullable nn => new TypeSig.Nullable(Lift(nn.Inner, order)),
		TypeSig.Union u => new TypeSig.Union(u.Members.Select(a => Lift(a, order)).ToArray()),
		_ => sig,
	};

	// ---------- Общий обход членов ----------

	private void ScanMembers(NativeTypeSymbol? owner, Type t, bool dotnet, List<NativeMember> members)
	{
		var flags = dotnet ? AllPublic : Declared;
		bool isEnum = t.IsEnum;
		if (isEnum && !dotnet) return;

		// конструкторы
		if (!(t.IsAbstract && t.IsSealed) && !t.IsInterface && !(dotnet && t.IsAbstract) && !isEnum)
			foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
			{
				if (!dotnet && !c.IsDefined(typeof(KernCtorAttribute))) continue;
				if (c.IsDefined(typeof(KernIgnoreAttribute)) || !Usable(c)) continue;
				members.Add(BuildMethod(c, "ctor", NativeMemberKind.Ctor, owner, null, dotnet));
			}

		// методы
		foreach (var m in t.GetMethods(flags))
		{
			if (m.IsDefined(typeof(KernIgnoreAttribute))) continue;
			if (dotnet && !IsDotnetCandidate(m, t)) continue;
			if (!dotnet && m.IsSpecialName && !HasKernMarker(m)) continue;
			if (m.IsGenericMethodDefinition && dotnet && m.GetGenericArguments().Any(g => g.GetGenericParameterConstraints().Any(c => c.IsGenericType && c.ContainsGenericParameters && false))) continue;
			if (!Usable(m)) continue;

			if (m.GetCustomAttribute<KernOperatorAttribute>() is { } op)
			{
				members.Add(BuildMethod(m, op.Symbol, NativeMemberKind.Operator, owner, op.Symbol, dotnet));
			}
			else if (m.GetCustomAttribute<KernPropertyAttribute>() is { } kp)
			{
				// `[KernStatic][KernProperty] static T Name()` — статическое свойство; имя берётся из любого из атрибутов
				if (kp.Name is null && m.GetCustomAttribute<KernStaticAttribute>() is { Name: { } sname })
					kp = new KernPropertyAttribute(sname) { Setter = kp.Setter };
				var prop = BuildMethodProperty(m, kp, owner, dotnet);
				if (prop is not null) members.Add(prop);
			}
			else if (m.GetCustomAttribute<KernCtorAttribute>() is not null)
			{
				members.Add(BuildMethod(m, "ctor", NativeMemberKind.Ctor, owner, null, dotnet));
			}
			else if (m.GetCustomAttribute<KernStaticAttribute>() is { } ks)
			{
				members.Add(BuildMethod(m, ks.Name ?? NameOf(m), NativeMemberKind.Static, owner, null, dotnet));
			}
			else if (m.GetCustomAttribute<KernMemberAttribute>() is { } km)
			{
				members.Add(BuildMethod(m, km.Name ?? NameOf(m), NativeMemberKind.Method, owner, null, dotnet));
			}
			else if (dotnet)
			{
				if (m.IsSpecialName && DotnetOperators.TryGetValue(m.Name, out var sym))
					members.Add(BuildMethod(m, sym, NativeMemberKind.Operator, owner, sym, dotnet));
				else if (!m.IsSpecialName)
					members.Add(BuildMethod(m, DotnetName(m), m.IsStatic && !IsExtensionMethod(m) ? NativeMemberKind.Static : NativeMemberKind.Method, owner, null, dotnet));
			}
		}

		// свойства C#
		foreach (var p in t.GetProperties(flags))
		{
			if (p.IsDefined(typeof(KernIgnoreAttribute))) continue;
			var kp = p.GetCustomAttribute<KernPropertyAttribute>();
			var ks = p.GetCustomAttribute<KernStaticAttribute>();
			if (!dotnet && kp is null && ks is null) continue;
			if (p.GetIndexParameters().Length > 0)
			{
				if (dotnet && BuildIndexer(p, owner) is { } idx) members.Add(idx);
				continue;
			}
			if (dotnet && (p.PropertyType.IsByRefLike || p.PropertyType.IsPointer || p.PropertyType.IsByRef)) continue;
			var name = kp?.Name ?? ks?.Name ?? p.GetCustomAttribute<KernNameAttribute>()?.Name ?? SnakeCase.Of(p.Name);
			if (BuildProperty(p, name, owner) is { } member) members.Add(member);
		}

		// поля / константы
		foreach (var f in t.GetFields(flags))
		{
			if (f.IsDefined(typeof(KernIgnoreAttribute)) || f.IsSpecialName) continue;
			var ks = f.GetCustomAttribute<KernStaticAttribute>();
			var kp = f.GetCustomAttribute<KernPropertyAttribute>();
			if (!dotnet && ks is null && kp is null) continue;
			if (isEnum && dotnet) continue; // значения enum уже в EnumNames
			if (f.FieldType.IsByRefLike || f.FieldType.IsPointer) continue;
			var name = ks?.Name ?? kp?.Name ?? f.GetCustomAttribute<KernNameAttribute>()?.Name ?? SnakeCase.Of(f.Name);
			var type = mapper.Map(f.FieldType, _nullability.Create(f), f);
			bool writable = !f.IsInitOnly && !f.IsLiteral;
			members.Add(new NativeMember
			{
				Key = NativeKey.Of(f), Name = name, Kind = NativeMemberKind.Field, Owner = owner, Clr = f,
				IsStatic = f.IsStatic, IsInstance = !f.IsStatic, SelfType = f.IsStatic ? null : owner?.Sig,
				Return = type, PropertyType = type,
				GetterKey = NativeKey.Of(f), SetterKey = writable ? NativeKey.Of(f, setter: true) : null,
			});
		}
	}

	private static string NameOf(MemberInfo m) => m.GetCustomAttribute<KernNameAttribute>()?.Name ?? SnakeCase.Of(m.Name);

	private static bool IsExtensionMethod(MethodInfo m) => m.IsDefined(typeof(ExtensionAttribute), false);

	private static string DotnetName(MethodInfo m) => m.Name switch
	{
		"ToString" => "to_str",
		"Equals" => "equals",
		"GetHashCode" => "hash_code",
		"CompareTo" => "compare_to",
		_ => SnakeCase.Of(m.Name),
	};

	/// <summary>Откидывает методы, которые нельзя замаршалить (ref/out/указатели/Span).</summary>
	private static bool Usable(MethodBase m)
	{
		foreach (var p in m.GetParameters())
		{
			var pt = p.ParameterType;
			if (pt.IsPointer || pt.IsByRefLike) return false;
			if (pt.IsByRef && !TypeMapper.IsHidden(p)) return false;
		}
		if (m is MethodInfo mi && (mi.ReturnType.IsPointer || mi.ReturnType.IsByRefLike || mi.ReturnType.IsByRef)) return false;
		return true;
	}

	private static bool IsDotnetCandidate(MethodInfo m, Type owner)
	{
		if (m.IsSpecialName && !m.Name.StartsWith("op_", StringComparison.Ordinal)) return false;
		if (m.DeclaringType == typeof(object))
			return m.Name is "ToString" or "Equals" or "GetHashCode";
		if (m.Name is "GetType" or "Finalize" or "MemberwiseClone") return false;
		if (m.DeclaringType == typeof(ValueType) || m.DeclaringType == typeof(Enum)) return m.Name is "ToString" or "Equals" or "GetHashCode" or "CompareTo";
		if (m.IsStatic && owner.IsInterface) return false;
		return true;
	}

	// ---------- Построение методов ----------

	private (bool SelfParam, TypeSig? SelfType) SelfOf(MethodBase m, NativeTypeSymbol? owner)
	{
		var ps = m.GetParameters();
		if (m.IsStatic && ps.Length > 0 && ps[0].Name == "self" && !TypeMapper.IsHidden(ps[0]))
			return (true, mapper.Map(ps[0].ParameterType, _nullability.Create(ps[0]), ps[0]));
		if (m.IsStatic && ps.Length > 0 && m is MethodInfo mi && IsExtensionMethod(mi))
			return (true, mapper.Map(ps[0].ParameterType, _nullability.Create(ps[0]), ps[0]));
		if (!m.IsStatic && m is not ConstructorInfo) return (false, owner?.Sig);
		return (false, null);
	}

	private NativeMember BuildMethod(MethodBase m, string name, NativeMemberKind kind, NativeTypeSymbol? owner, string? op, bool dotnet)
	{
		var (selfParam, selfType) = SelfOf(m, owner);
		var ps = m.GetParameters();
		var list = new List<NativeParam>();
		for (int i = selfParam ? 1 : 0; i < ps.Length; i++)
		{
			var p = ps[i];
			if (TypeMapper.IsHidden(p)) continue;
			list.Add(new NativeParam
			{
				Name = p.GetCustomAttribute<KernNameAttribute>()?.Name ?? SnakeCase.Of(p.Name ?? $"arg{i}"),
				Type = mapper.Map(p.ParameterType, _nullability.Create(p), p),
				HasDefault = p.HasDefaultValue,
				DefaultValue = NormalizeDefault(p),
				IsVariadic = p.IsDefined(typeof(ParamArrayAttribute), false),
				Clr = p,
			});
		}

		TypeSig ret;
		bool isAsync = false;
		if (m is MethodInfo mi)
		{
			ret = mapper.MapReturn(mi, _nullability);
			isAsync = TypeMapper.IsAsync(mi.ReturnType);
		}
		else ret = owner?.Sig ?? TypeSig.Object;

		bool isInstance = selfParam || (!m.IsStatic && m is not ConstructorInfo);
		return new NativeMember
		{
			Key = NativeKey.Of(m), Name = name, Kind = kind, Owner = owner, Clr = m,
			IsStatic = m.IsStatic && !selfParam && kind != NativeMemberKind.Ctor,
			IsInstance = isInstance, SelfType = selfType,
			GenericParams = m.IsGenericMethodDefinition ? GenericParamsOf(m.GetGenericArguments(), 1, dotnet) : [],
			Params = list, Return = ret, IsAsync = isAsync, OpSymbol = op,
		};
	}

	private NativeMember? BuildMethodProperty(MethodInfo getter, KernPropertyAttribute attr, NativeTypeSymbol? owner, bool dotnet)
	{
		var (selfParam, selfType) = SelfOf(getter, owner);
		string name = attr.Name ?? NameOf(getter);
		// имя «GetX» → свойство x
		if (attr.Name is null && !getter.IsDefined(typeof(KernNameAttribute)) && getter.Name.StartsWith("Get", StringComparison.Ordinal) && getter.Name.Length > 3 && char.IsUpper(getter.Name[3]))
			name = SnakeCase.Of(getter.Name[3..]);
		var ret = mapper.MapReturn(getter, _nullability);
		MethodInfo? setter = null;
		if (attr.Setter is { } sn)
		{
			int want = getter.GetParameters().Length + 1;
			setter = getter.DeclaringType!.GetMethods(Declared).FirstOrDefault(x => x.Name == sn && x.GetParameters().Length == want && x.IsStatic == getter.IsStatic);
		}
		return new NativeMember
		{
			Key = NativeKey.Of(getter), Name = name, Kind = NativeMemberKind.Property, Owner = owner, Clr = getter,
			IsStatic = getter.IsStatic && !selfParam, IsInstance = selfParam || !getter.IsStatic, SelfType = selfType,
			GenericParams = getter.IsGenericMethodDefinition ? GenericParamsOf(getter.GetGenericArguments(), 1, dotnet) : [],
			Return = ret, PropertyType = ret, GetterKey = NativeKey.Of(getter),
			SetterKey = setter is null ? null : NativeKey.Of(setter),
		};
	}

	private NativeMember? BuildProperty(PropertyInfo p, string name, NativeTypeSymbol? owner)
	{
		var getter = p.GetMethod is { IsPublic: true } g ? g : null;
		var setter = p.SetMethod is { IsPublic: true } s ? s : null;
		if (getter is null && setter is null) return null;
		bool isStatic = (getter ?? setter)!.IsStatic;
		bool isInit = setter is not null && setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
		var type = mapper.Map(p.PropertyType, _nullability.Create(p), p);
		return new NativeMember
		{
			Key = NativeKey.Of((getter ?? setter)!), Name = name, Kind = NativeMemberKind.Property, Owner = owner, Clr = p,
			IsStatic = isStatic, IsInstance = !isStatic, SelfType = isStatic ? null : owner?.Sig,
			Return = type, PropertyType = type,
			GetterKey = getter is null ? null : NativeKey.Of(getter),
			SetterKey = setter is null ? null : NativeKey.Of(setter), IsInit = isInit,
		};
	}

	/// <summary>Индексатор .NET → оператор "[]" (get — GetterKey, set — SetterKey).</summary>
	private NativeMember? BuildIndexer(PropertyInfo p, NativeTypeSymbol? owner)
	{
		var getter = p.GetMethod is { IsPublic: true } g ? g : null;
		var setter = p.SetMethod is { IsPublic: true } s ? s : null;
		if ((getter ?? setter) is not { } any || any.IsStatic) return null;
		if (p.GetIndexParameters().Any(ip => ip.ParameterType.IsByRef || ip.ParameterType.IsPointer || ip.ParameterType.IsByRefLike)) return null;
		var type = mapper.Map(p.PropertyType, _nullability.Create(p), p);
		var list = p.GetIndexParameters().Select(ip => new NativeParam
		{
			Name = SnakeCase.Of(ip.Name ?? "index"), Type = mapper.Map(ip.ParameterType, _nullability.Create(ip), ip), Clr = ip,
		}).ToList();
		return new NativeMember
		{
			Key = NativeKey.Of(any), Name = "[]", Kind = NativeMemberKind.Operator, Owner = owner, Clr = p, OpSymbol = "[]",
			IsInstance = true, SelfType = owner?.Sig, Params = list, Return = type, PropertyType = type,
			GetterKey = getter is null ? null : NativeKey.Of(getter), SetterKey = setter is null ? null : NativeKey.Of(setter),
		};
	}

	// ---------- Generic-параметры, значения по умолчанию ----------

	private IReadOnlyList<NativeGenericParam> GenericParamsOf(Type[] args, int scope, bool dotnet)
	{
		var res = new List<NativeGenericParam>(args.Length);
		foreach (var g in args)
		{
			var cons = new List<TypeSig>();
			foreach (var c in g.GetGenericParameterConstraints())
			{
				if (c == typeof(IKernNum)) cons.Add(new TypeSig.Prim(PrimKind.Num));
				else if (c == typeof(IKernInum)) cons.Add(new TypeSig.Prim(PrimKind.INum));
				else if (c == typeof(IKernFnum)) cons.Add(new TypeSig.Prim(PrimKind.FNum));
				else if (c == typeof(IKernHashable)) cons.Add(new TypeSig.Named("kern.IHashable", []));
				else if (c == typeof(IKernComparable)) cons.Add(new TypeSig.Named("kern.IComparable", []));
				else if (dotnet && c != typeof(ValueType) && c != typeof(object)) cons.Add(mapper.Map(c));
			}
			foreach (var a in g.GetCustomAttributes<KernConstraintAttribute>())
				cons.Add(a.Kind switch
				{
					KernConstraintKind.Num => new TypeSig.Prim(PrimKind.Num),
					KernConstraintKind.Inum => new TypeSig.Prim(PrimKind.INum),
					KernConstraintKind.Fnum => new TypeSig.Prim(PrimKind.FNum),
					KernConstraintKind.Hashable => new TypeSig.Named("kern.IHashable", []),
					_ => new TypeSig.Named("kern.IComparable", []),
				});
			res.Add(new NativeGenericParam
			{
				Name = SnakeCase.Of(g.Name) is { Length: > 0 } n && g.Name.Length == 1 ? g.Name : g.Name,
				Index = g.GenericParameterPosition, Scope = scope, Constraints = cons,
			});
		}
		return res;
	}

	private static object? NormalizeDefault(ParameterInfo p)
	{
		if (!p.HasDefaultValue) return null;
		var v = p.DefaultValue;
		if (v is null || v is DBNull || v is Missing) return null;
		if (v is Enum e) return Convert.ToInt64(e);
		return v switch
		{
			sbyte or short or int or byte or ushort or uint => Convert.ToInt64(v),
			long l => l,
			ulong u => u,
			float f => (double)f,
			Half h => (double)h,
			double d => d,
			bool or char or string => v,
			_ => null,
		};
	}

	// ---------- Dotnet-режим ----------

	/// <summary>Символ для произвольного публичного .NET-типа (способ C).</summary>
	public NativeTypeSymbol BuildDotnetSymbol(Type t)
	{
		var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
		string ns = def.Namespace ?? "";
		string name = def.Name;
		int tick = name.IndexOf('`');
		if (tick >= 0) name = name[..tick];
		string module = "dotnet." + ns;

		var category = def.IsEnum ? NativeTypeCategory.Enum
			: def.IsAbstract && def.IsSealed ? NativeTypeCategory.StaticHolder
			: def.IsInterface ? NativeTypeCategory.Interface
			: def.IsValueType ? NativeTypeCategory.Unit
			: NativeTypeCategory.Class;

		var generics = def.IsGenericTypeDefinition ? GenericParamsOf(def.GetGenericArguments(), 0, true) : [];
		var sig = new TypeSig.Named(TypeMapper.DotnetName(def), generics.Select(g => (TypeSig)new TypeSig.Param(0, g.Index, g.Name)).ToArray());
		var symbol = new NativeTypeSymbol
		{
			Name = name, Module = module, Category = category, ClrType = def, Sig = sig, GenericParams = generics,
			IsOpen = !def.IsSealed, Parent = def.BaseType is { } bt && bt != typeof(object) && bt != typeof(ValueType) && bt != typeof(Enum) && bt.IsPublic ? mapper.Map(bt) : null,
		};
		symbol.ClrTypes.Add(def);
		if (def.IsEnum)
		{
			foreach (var n in Enum.GetNames(def)) symbol.EnumNames.Add(SnakeCase.Of(n));
			foreach (var v in Enum.GetValuesAsUnderlyingType(def)) symbol.EnumValues.Add(Convert.ToInt64(v));
		}
		ScanMembers(symbol, def, dotnet: true, members: symbol.Members);
		return symbol;
	}

	private static readonly Dictionary<string, string> DotnetOperators = new()
	{
		["op_Addition"] = "+", ["op_Subtraction"] = "-", ["op_Multiply"] = "*", ["op_Division"] = "/", ["op_Modulus"] = "%",
		["op_Equality"] = "==", ["op_Inequality"] = "!=", ["op_LessThan"] = "<", ["op_GreaterThan"] = ">",
		["op_LessThanOrEqual"] = "<=", ["op_GreaterThanOrEqual"] = ">=", ["op_UnaryNegation"] = "-", ["op_UnaryPlus"] = "+",
		["op_LogicalNot"] = "!", ["op_BitwiseAnd"] = "&", ["op_BitwiseOr"] = "|", ["op_LeftShift"] = "<<", ["op_RightShift"] = ">>",
		["op_OnesComplement"] = "~",
	};
}
