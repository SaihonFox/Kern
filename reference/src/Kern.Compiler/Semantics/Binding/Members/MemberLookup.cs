using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding.Members;

/// <summary>Результат поиска члена по имени: поле/свойство (ближайшее по иерархии) и/или группа функций.</summary>
public sealed class MemberSet
{
	public FieldSymbol? Field { get; set; }
	public PropertySymbol? Property { get; set; }
	public List<FunctionSymbol> Functions { get; } = [];
	public TypeDeclSymbol? NestedType { get; set; }
	/// <summary>Подстановка generic-параметров типов-владельцев (объединённая по иерархии) в аргументы получателя.</summary>
	public Dictionary<TypeParamSymbol, TypeSymbol> OwnerMap { get; } = [];
	/// <summary>Подстановка для найденного поля/свойства.</summary>
	public IReadOnlyDictionary<TypeParamSymbol, TypeSymbol>? MemberMap { get; set; }

	public bool IsEmpty => Field is null && Property is null && Functions.Count == 0 && NestedType is null;

	public TypeSymbol? FieldType => Field?.Type is { } t ? (MemberMap is null ? t : t.Substitute(MemberMap)) : null;
	public TypeSymbol? PropertyType => Property is null ? null : MemberMap is null ? Property.Type : Property.Type.Substitute(MemberMap);
}

/// <summary>
/// Поиск членов типа: объявленные члены с наследованием, нативные члены, нативные и пользовательские расширения.
/// Никакого слияния перегрузок по имени: все кандидаты с одним именем отдаются разрешателю перегрузок.
/// </summary>
public sealed class MemberLookup(ProjectModel project, ExtensionIndex extensions)
{
	private INativeSymbolSource Source => project.Binder.Source;

	// ------------------------------------------------------------ экземплярные члены

	public MemberSet FindInstance(TypeSymbol receiver, string name, FileModel file, bool includeExtensions = true)
	{
		var set = new MemberSet();
		CollectOwn(receiver, name, set, @static: false, new HashSet<TypeSymbol>());
		if (includeExtensions) AddExtensions(receiver, name, set, file);
		return set;
	}

	private void CollectOwn(TypeSymbol type, string name, MemberSet set, bool @static, HashSet<TypeSymbol> seen)
	{
		if (!seen.Add(type)) return;
		switch (type)
		{
			case DeclaredType d:
				CollectDeclared(d, name, set, @static);
				foreach (var b in Conversions.BaseTypes(d))
					if (b is DeclaredType bd) CollectDeclared(bd, name, set, @static);
				break;
			case NativeType n:
				CollectNative(n, name, set, @static);
				foreach (var nb in Conversions.BaseTypes(n))   // члены базовых нативных типов
					if (nb is NativeType nbt) CollectNative(nbt, name, set, @static);
				break;
			case TypeParamType tp:
				foreach (var bound in tp.Param.Bounds) CollectOwn(bound, name, set, @static, seen);
				break;
		}
	}

	private static void CollectDeclared(DeclaredType d, string name, MemberSet set, bool @static)
	{
		var decl = d.Decl;
		var map = d.TypeMap();
		foreach (var kv in map) set.OwnerMap.TryAdd(kv.Key, kv.Value);

		foreach (var f in decl.Fields)
			if (f.Name == name && f.IsStatic == @static && f.BackingFor is null && set.Field is null && set.Property is null)
			{
				set.Field = f;
				set.MemberMap = map;
			}
		if (@static)
			foreach (var f in decl.EnumEntries)
				if (f.Name == name && set.Field is null)
				{
					set.Field = f;
					set.MemberMap = map;
				}
		foreach (var p in decl.Properties)
			if (p.Name == name && p.IsStatic == @static && set.Property is null && set.Field is null)
			{
				set.Property = p;
				set.MemberMap = map;
			}
		foreach (var fn in decl.Functions)
		{
			if (fn.Name != name || fn.IsStatic != @static || fn.IsAccessor) continue;
			if (set.Functions.Any(e => e.HasSameSignature(fn))) continue; // переопределение скрывает базовую
			set.Functions.Add(fn);
		}
		if (@static && set.NestedType is null && decl.MemberScope?.LookupLocal(name) is TypeDeclSymbol nested) set.NestedType = nested;
	}

	private void CollectNative(NativeType n, string name, MemberSet set, bool @static)
	{
		var ps = project.Binder.TypeParamsOf(n.Symbol);
		for (int i = 0; i < ps.Count && i < n.Args.Count; i++) set.OwnerMap.TryAdd(ps[i], n.Args[i]);
		foreach (var m in Source.GetMembers(n.Symbol))
		{
			if (m.Kind == NativeMemberKind.Ctor) continue;
			bool isOp = m.Kind == NativeMemberKind.Operator && "op" + m.OpSymbol == name;
			if (m.Name != name && !isOp) continue;
			if (m.IsStatic != @static) continue;
			switch (project.Binder.Bind(m))
			{
				case PropertySymbol p when set.Property is null && set.Field is null:
					set.Property = p;
					set.MemberMap = set.OwnerMap;
					break;
				case FunctionSymbol f:
					if (!set.Functions.Any(e => e.HasSameSignature(f))) set.Functions.Add(f);   // переопределение скрывает базовую
					break;
			}
		}
	}

	// ------------------------------------------------------------ статические члены

	public MemberSet FindStatic(TypeSymbol type, string name)
	{
		var set = new MemberSet();
		CollectOwn(type, name, set, @static: true, new HashSet<TypeSymbol>());
		string? builtin = BuiltinName(type);
		if (builtin is not null)
		{
			foreach (var f in extensions.Statics(builtin, name)) set.Functions.Add(f);
			if (set.Property is null && set.Field is null && extensions.StaticProperty(builtin, name) is { } sp) set.Property = sp;
		}
		return set;
	}

	// ------------------------------------------------------------ конструкторы

	public IReadOnlyList<FunctionSymbol> FindConstructors(TypeSymbol type, out IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> ownerMap)
	{
		var map = new Dictionary<TypeParamSymbol, TypeSymbol>();
		ownerMap = map;
		switch (type)
		{
			case DeclaredType d:
				foreach (var kv in d.TypeMap()) map[kv.Key] = kv.Value;
				return d.Decl.Constructors;
			case NativeType n:
			{
				var ps = project.Binder.TypeParamsOf(n.Symbol);
				for (int i = 0; i < ps.Count && i < n.Args.Count; i++) map[ps[i]] = n.Args[i];
				var list = new List<FunctionSymbol>();
				foreach (var m in Source.GetMembers(n.Symbol))
					if (m.Kind == NativeMemberKind.Ctor && project.Binder.Bind(m) is FunctionSymbol f) list.Add(f);
				return list;
			}
			default:
			{
				string? builtin = BuiltinName(type);
				if (builtin is null) return [];
				var list = extensions.Constructors(builtin);
				if (list.Count > 0 && list[0] is NativeMemberSymbol { Member.Owner: { } owner } && type is BuiltinGenericType b)
				{
					var ps = project.Binder.TypeParamsOf(owner);
					for (int i = 0; i < ps.Count && i < b.Args.Count; i++) map[ps[i]] = b.Args[i];
				}
				return list;
			}
		}
	}

	// ------------------------------------------------------------ расширения

	private void AddExtensions(TypeSymbol receiver, string name, MemberSet set, FileModel file)
	{
		// нативные расширения встроенных типов
		foreach (var f in extensions.Functions(name))
			if (f.ExtensionOf is { } eo && Unify(eo, receiver, [])) set.Functions.Add(f);
		if (set.Property is null && set.Field is null)
		{
			// второй проход — «мягкий»: получатель с generic-параметрами (`list<T>` внутри `type Stack<T>`) не должен проваливаться на ограничениях
			for (int pass = 0; pass < (receiver.ContainsTypeParam ? 2 : 1) && set.Property is null; pass++)
				foreach (var p in extensions.Properties(name))
				{
					var map = new Dictionary<TypeParamSymbol, TypeSymbol>();
					if (p.ExtensionOf is { } eo && Unify(eo, receiver, map, loose: pass == 1))
					{
						set.Property = p;
						set.MemberMap = map;   // подстановка параметров расширения (`T` у `list<T>.first`)
						break;
					}
				}
			// третий проход — по «голове» типа: `list<…>`/`dict<…,…>` совпадают с образцом по имени и числу аргументов, даже если точная унификация не удалась
			if (set.Property is null && receiver is BuiltinGenericType rb)
				foreach (var p in extensions.Properties(name))
					if (p.ExtensionOf is BuiltinGenericType pb && pb.Name == rb.Name && pb.Args.Count == rb.Args.Count)
					{
						var map = new Dictionary<TypeParamSymbol, TypeSymbol>();
						Unify(pb, rb, map, loose: true);
						set.Property = p;
						set.MemberMap = map;
						break;
					}
			if (set.Property is null && Environment.GetEnvironmentVariable("KERN_TRACE_MEMBERS") is { Length: > 0 })
			{
				var lines = new List<string> { $"[trace] свойство '{name}' у {receiver.GetType().Name} '{receiver.ToDisplayString()}': кандидатов {extensions.Properties(name).Count}" };
				foreach (var p in extensions.Properties(name))
					lines.Add($"[trace]   {p.Native?.Owner?.Name}.{p.Name}: ExtensionOf = {p.ExtensionOf?.GetType().Name} '{p.ExtensionOf?.ToDisplayString()}'");
				// stderr тест-хост xunit не показывает — пишем ещё и в файл
				try { File.AppendAllLines(Path.Combine(Path.GetTempPath(), "kern-trace.txt"), lines); } catch (IOException) { }
				foreach (var l in lines) Console.Error.WriteLine(l);
			}
		}

		// пользовательские расширения: из области файла и из целиком импортированных модулей, плюс собственный модуль
		var seen = new HashSet<FunctionSymbol>(set.Functions);
		void AddFrom(IEnumerable<FunctionSymbol> fs)
		{
			foreach (var f in fs)
				if (f.Name == name && f.ExtensionOf is { } eo && !f.IsNative && Unify(eo, receiver, []) && seen.Add(f)) set.Functions.Add(f);
		}
		if (file.Scope.LookupFunctions(name) is { } imported) AddFrom(imported.Functions);
		AddFrom(file.Module.Functions);
		foreach (var m in file.ImportedModules) AddFrom(m.Functions.Where(f => f.Access != Kern.Compiler.Syntax.Access.Priv));

		if (set.Property is null && set.Field is null)
		{
			var props = file.Module.ExtensionProperties.Concat(file.ImportedExtensionProperties)
				.Concat(file.ImportedModules.SelectMany(m => m.ExtensionProperties.Where(p => p.Access != Kern.Compiler.Syntax.Access.Priv)));
			foreach (var p in props)
			{
				var map = new Dictionary<TypeParamSymbol, TypeSymbol>();
				if (p.Name == name && p.ExtensionOf is { } eo && Unify(eo, receiver, map))
				{
					set.Property = p;
					set.MemberMap = map;
					break;
				}
			}
		}
	}

	// ------------------------------------------------------------ вспомогательное

	public static string? BuiltinName(TypeSymbol t) => t switch
	{
		PrimitiveType p => p.Name,
		BuiltinGenericType b => b.Name,
		_ => null,
	};

	/// <summary>Приблизительное сопоставление типа-образца расширения (`list&lt;T&gt;`, `num`, `T?`) с типом получателя.</summary>
	public static bool Unify(TypeSymbol pattern, TypeSymbol actual, Dictionary<TypeParamSymbol, TypeSymbol> map, bool loose = false)
	{
		if (actual.IsError || pattern.IsError) return true;
		if (pattern is TypeParamType tp)
		{
			if (map.TryGetValue(tp.Param, out var bound)) return loose || bound.Equals(actual) || Conversions.IsImplicit(actual, bound);
			if (!loose)
				foreach (var b in tp.Param.Bounds)
					if (!b.ContainsTypeParam && !Conversions.IsImplicit(actual, b)) return false;
			map[tp.Param] = actual;
			return true;
		}
		if (!pattern.ContainsTypeParam) return (loose && actual is TypeParamType) || Conversions.IsImplicit(actual, pattern);
		switch (pattern)
		{
			case NullableType pn:
				return actual is NullableType an ? Unify(pn.Inner, an.Inner, map, loose) : Unify(pn.Inner, actual, map, loose);
			case BuiltinGenericType pb:
				if (actual is not BuiltinGenericType ab || ab.Name != pb.Name || ab.Args.Count != pb.Args.Count) return false;
				for (int i = 0; i < pb.Args.Count; i++) if (!Unify(pb.Args[i], ab.Args[i], map, loose)) return false;
				return true;
			case FuncType pf:
				if (actual is not FuncType af || af.Params.Count != pf.Params.Count) return false;
				for (int i = 0; i < pf.Params.Count; i++) if (!Unify(pf.Params[i], af.Params[i], map, loose)) return false;
				return Unify(pf.Return, af.Return, map, loose);
			case TupleType pt:
				if (actual is not TupleType at || at.Items.Count != pt.Items.Count) return false;
				for (int i = 0; i < pt.Items.Count; i++) if (!Unify(pt.Items[i], at.Items[i], map, loose)) return false;
				return true;
			case DeclaredType pd:
			{
				if (actual is not DeclaredType) return false;
				foreach (var cand in new[] { actual }.Concat(Conversions.BaseTypes(actual)))
					if (cand is DeclaredType cd && ReferenceEquals(cd.Decl, pd.Decl) && cd.Args.Count == pd.Args.Count)
					{
						for (int i = 0; i < pd.Args.Count; i++) if (!Unify(pd.Args[i], cd.Args[i], map, loose)) return false;
						return true;
					}
				return false;
			}
			case NativeType pn2:
			{
				if (actual is not NativeType) return false;
				foreach (var cand in new[] { actual }.Concat(Conversions.BaseTypes(actual)))
					if (cand is NativeType an2 && an2.Symbol.FullName == pn2.Symbol.FullName && pn2.Args.Count == an2.Args.Count)
						return pn2.Args.Zip(an2.Args).All(x => Unify(x.First, x.Second, map, loose));
				return false;
			}
		}
		return false;
	}
}
