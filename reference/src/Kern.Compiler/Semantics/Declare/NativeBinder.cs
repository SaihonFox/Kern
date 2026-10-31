using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Declare;

/// <summary>Переводит нативные символы каталога (TypeSig, NativeMember) в семантическую модель.</summary>
public sealed class NativeBinder(INativeSymbolSource natives)
{
	private readonly Dictionary<NativeTypeSymbol, List<TypeParamSymbol>> _typeParams = [];
	private readonly Dictionary<NativeMember, Symbol> _bound = [];

	public INativeSymbolSource Source { get; } = natives;

	/// <summary>Регистрирует корень коллекций (collection) для подтипирования встроенных list/array/str/dict/range.</summary>
	private readonly bool _rootRegistered = RegisterCollectionRoot(natives);

	private static bool RegisterCollectionRoot(INativeSymbolSource natives)
	{
		if (natives.FindType("kern.collections", "collection") is { } root) NativeType.CollectionRoot = root;
		return true;
	}

	/// <summary>Generic-параметры нативного типа как символы (кэшируются).</summary>
	public IReadOnlyList<TypeParamSymbol> TypeParamsOf(NativeTypeSymbol type)
	{
		if (_typeParams.TryGetValue(type, out var list)) return list;
		var owner = new NativeTypeBinding(type.Name, type);
		list = [];
		_typeParams[type] = list;
		foreach (var g in type.GenericParams) list.Add(new TypeParamSymbol(g.Name, g.Index, owner));
		for (int i = 0; i < list.Count; i++)
			foreach (var c in type.GenericParams[i].Constraints) list[i].Bounds.Add(FromSig(c, list, []));
		return list;
	}

	/// <summary>TypeSig → TypeSymbol. <paramref name="classParams"/> — Scope 0, <paramref name="methodParams"/> — Scope 1.</summary>
	public TypeSymbol FromSig(TypeSig sig, IReadOnlyList<TypeParamSymbol> classParams, IReadOnlyList<TypeParamSymbol> methodParams)
	{
		switch (sig)
		{
			case TypeSig.Prim p:
				return p.Kind switch
				{
					PrimKind.Nil => NilType.Instance,
					_ => PrimitiveType.FromKind(p.Kind),
				};
			case TypeSig.Param tp:
			{
				var list = tp.Scope == 0 ? classParams : methodParams;
				return tp.Index >= 0 && tp.Index < list.Count ? list[tp.Index].AsType : ErrorType.Instance;
			}
			case TypeSig.Nullable n:
				return NullableType.Of(FromSig(n.Inner, classParams, methodParams));
			case TypeSig.Func f:
				return new FuncType([.. f.Params.Select(x => FromSig(x, classParams, methodParams))], FromSig(f.Return, classParams, methodParams));
			case TypeSig.Tuple t:
				return new TupleType([.. t.Items.Select(x => FromSig(x, classParams, methodParams))], t.Names);
			case TypeSig.Union:
				return PrimitiveType.Object;
			case TypeSig.Named named:
			{
				var args = named.Args.Select(x => FromSig(x, classParams, methodParams)).ToList();
				if (BuiltinGenericType.Create(named.FullName, args) is { } b) return b;
				int dot = named.FullName.LastIndexOf('.');
				var nt = dot < 0 ? null : Source.FindType(named.FullName[..dot], named.FullName[(dot + 1)..]);
				return nt is null ? ErrorType.Instance : new NativeType(nt, args);
			}
			default:
				return ErrorType.Instance;
		}
	}

	/// <summary>Тип нативного символа с аргументами; расширения встроенных типов (str, list…) дают встроенный тип.</summary>
	public TypeSymbol TypeOf(NativeTypeSymbol type, IReadOnlyList<TypeSymbol>? args = null)
	{
		args ??= [];
		if (type.IsExtension)
		{
			var self = FromSig(type.Sig, TypeParamsOf(type), []);
			if (args.Count > 0 && self is not ErrorType)
			{
				var map = new Dictionary<TypeParamSymbol, TypeSymbol>();
				var ps = TypeParamsOf(type);
				for (int i = 0; i < ps.Count && i < args.Count; i++) map[ps[i]] = args[i];
				return self.Substitute(map);
			}
			return self;
		}
		return new NativeType(type, args);
	}

	/// <summary>Нативный член → функция/свойство. Результат кэшируется.</summary>
	public Symbol Bind(NativeMember m)
	{
		if (_bound.TryGetValue(m, out var s)) return s;
		s = m.Kind == NativeMemberKind.Property || m.Kind == NativeMemberKind.Field ? BindProperty(m) : BindFunction(m);
		_bound[m] = s;
		return s;
	}

	private FunctionSymbol BindFunction(NativeMember m)
	{
		var kind = m.Kind switch
		{
			NativeMemberKind.Ctor => FunctionKind.Constructor,
			NativeMemberKind.Operator => FunctionKind.Operator,
			_ when m.Owner is null => FunctionKind.Function,
			_ => FunctionKind.Method,
		};
		var f = new NativeMemberSymbol(m.Kind == NativeMemberKind.Operator ? "op" + m.OpSymbol : m.Name, kind, m)
		{
			IsStatic = m.IsStatic,
			IsAsync = m.IsAsync,
			OpSymbol = m.OpSymbol,
			Access = Kern.Compiler.Syntax.Access.Pub,
		};
		IReadOnlyList<TypeParamSymbol> classParams = m.Owner is null ? [] : TypeParamsOf(m.Owner);
		for (int i = 0; i < m.GenericParams.Count; i++)
			f.TypeParams.Add(new TypeParamSymbol(m.GenericParams[i].Name, m.GenericParams[i].Index, f));
		var methodParams = f.TypeParams;
		for (int i = 0; i < m.GenericParams.Count; i++)
			foreach (var c in m.GenericParams[i].Constraints) methodParams[i].Bounds.Add(FromSig(c, classParams, methodParams));

		int idx = 0;
		foreach (var p in m.Params)
		{
			var t = FromSig(p.Type, classParams, methodParams);
			if (p.IsVariadic && t is ListType lt) t = lt.Element;
			f.Parameters.Add(new ParameterSymbol(p.Name, t, idx++) { IsVariadic = p.IsVariadic, HasNativeDefault = p.HasDefault });
		}
		f.ReturnType = FromSig(m.Return, classParams, methodParams);
		if (m.SelfType is { } self && !m.IsStatic && m.Owner is { IsExtension: true })
			f.ExtensionOf = FromSig(self, classParams, methodParams);
		return f;
	}

	private PropertySymbol BindProperty(NativeMember m)
	{
		IReadOnlyList<TypeParamSymbol> classParams = m.Owner is null ? [] : TypeParamsOf(m.Owner);
		// свойство-расширение `[KernProperty] static T First<T>(KList<T> self)`: generic-параметры метода
		var methodParams = new List<TypeParamSymbol>();
		if (m.GenericParams.Count > 0)
		{
			var owner = new NativeMemberSymbol(m.Name, FunctionKind.Method, m);
			for (int i = 0; i < m.GenericParams.Count; i++)
				methodParams.Add(new TypeParamSymbol(m.GenericParams[i].Name, m.GenericParams[i].Index, owner));
			owner.TypeParams.AddRange(methodParams);
		}
		var type = m.PropertyType is { } pt ? FromSig(pt, classParams, methodParams) : ErrorType.Instance;
		var p = new PropertySymbol(m.Name, type) { Native = m, IsStatic = m.IsStatic, Access = Kern.Compiler.Syntax.Access.Pub };
		if (m.SelfType is { } self && !m.IsStatic && m.Owner is { IsExtension: true })
			p.ExtensionOf = FromSig(self, classParams, methodParams);
		return p;
	}

	/// <summary>Группа перегрузок из нативных функций с одним именем.</summary>
	public OverloadSet BindGroup(string name, IEnumerable<NativeMember> members)
	{
		var set = new OverloadSet(name);
		foreach (var m in members)
			if (Bind(m) is FunctionSymbol f) set.AddUnchecked(f);
		return set;
	}
}
