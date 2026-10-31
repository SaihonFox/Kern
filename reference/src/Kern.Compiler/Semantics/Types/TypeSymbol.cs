using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Compiler.Semantics.Symbols;

namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Семантический тип. Иммутабелен, сравнивается структурно (объявления — по ссылке на символ).
/// Не путать с синтаксическим <c>Kern.Compiler.Syntax.TypeRef</c>.
/// </summary>
public abstract class TypeSymbol : IEquatable<TypeSymbol>
{
	/// <summary>Подпись для пула типов модуля.</summary>
	public abstract TypeSig ToTypeSig();

	/// <summary>Читаемое имя для сообщений об ошибках.</summary>
	public abstract string ToDisplayString();

	/// <summary>Подставляет generic-параметры (T → аргумент).</summary>
	public virtual TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map) => this;

	/// <summary>Содержит ли тип несвязанные generic-параметры.</summary>
	public virtual bool ContainsTypeParam => false;

	public virtual bool IsError => false;

	public abstract bool Equals(TypeSymbol? other);
	public abstract override int GetHashCode();
	public sealed override bool Equals(object? obj) => obj is TypeSymbol t && Equals(t);
	public override string ToString() => ToDisplayString();

	public static bool operator ==(TypeSymbol? a, TypeSymbol? b) => a is null ? b is null : a.Equals(b);
	public static bool operator !=(TypeSymbol? a, TypeSymbol? b) => !(a == b);

	protected static bool SeqEq(IReadOnlyList<TypeSymbol> a, IReadOnlyList<TypeSymbol> b)
	{
		if (ReferenceEquals(a, b)) return true;
		if (a.Count != b.Count) return false;
		for (int i = 0; i < a.Count; i++)
			if (!a[i].Equals(b[i])) return false;
		return true;
	}

	protected static int SeqHash(IReadOnlyList<TypeSymbol> a)
	{
		var h = new HashCode();
		foreach (var t in a) h.Add(t);
		return h.ToHashCode();
	}

	/// <summary>Подстановка в список; возвращает исходный список, если ничего не изменилось.</summary>
	protected static IReadOnlyList<TypeSymbol> SubstAll(IReadOnlyList<TypeSymbol> list, IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		if (list.Count == 0) return list;
		TypeSymbol[]? res = null;
		for (int i = 0; i < list.Count; i++)
		{
			var s = list[i].Substitute(map);
			if (!ReferenceEquals(s, list[i]))
			{
				res ??= [.. list];
				res[i] = s;
			}
		}
		return res ?? list;
	}

	protected static string ArgsDisplay(IReadOnlyList<TypeSymbol> args) =>
		args.Count == 0 ? "" : "<" + string.Join(", ", args.Select(a => a.ToDisplayString())) + ">";

	protected static List<TypeSig> Sigs(IReadOnlyList<TypeSymbol> args) => [.. args.Select(a => a.ToTypeSig())];
}

// ---------------------------------------------------------------- примитивы и служебные

/// <summary>Встроенный примитив: числа, bool, char, str, object, void, type, inum/fnum/num.</summary>
public sealed class PrimitiveType : TypeSymbol
{
	private PrimitiveType(PrimKind kind, string name) { Kind = kind; Name = name; }

	public PrimKind Kind { get; }
	public string Name { get; }

	public static readonly PrimitiveType Void = new(PrimKind.Void, "void");
	public static readonly PrimitiveType Bool = new(PrimKind.Bool, "bool");
	public static readonly PrimitiveType Char = new(PrimKind.Char, "char");
	public static readonly PrimitiveType Str = new(PrimKind.Str, "str");
	public static readonly PrimitiveType Object = new(PrimKind.Object, "object");
	public static readonly PrimitiveType I8 = new(PrimKind.I8, "i8");
	public static readonly PrimitiveType I16 = new(PrimKind.I16, "i16");
	public static readonly PrimitiveType I32 = new(PrimKind.I32, "i32");
	public static readonly PrimitiveType I64 = new(PrimKind.I64, "i64");
	public static readonly PrimitiveType U8 = new(PrimKind.U8, "u8");
	public static readonly PrimitiveType U16 = new(PrimKind.U16, "u16");
	public static readonly PrimitiveType U32 = new(PrimKind.U32, "u32");
	public static readonly PrimitiveType U64 = new(PrimKind.U64, "u64");
	public static readonly PrimitiveType F16 = new(PrimKind.F16, "f16");
	public static readonly PrimitiveType F32 = new(PrimKind.F32, "f32");
	public static readonly PrimitiveType F64 = new(PrimKind.F64, "f64");
	public static readonly PrimitiveType INum = new(PrimKind.INum, "inum");
	public static readonly PrimitiveType FNum = new(PrimKind.FNum, "fnum");
	public static readonly PrimitiveType Num = new(PrimKind.Num, "num");
	public static readonly PrimitiveType Type = new(PrimKind.Type, "type");

	private static readonly Dictionary<string, PrimitiveType> ByName = new[]
	{
		Void, Bool, Char, Str, Object, I8, I16, I32, I64, U8, U16, U32, U64, F16, F32, F64, INum, FNum, Num, Type,
	}.ToDictionary(p => p.Name);

	public static PrimitiveType? FromName(string name) => ByName.GetValueOrDefault(name);

	public static PrimitiveType FromKind(PrimKind kind) => ByName.Values.First(p => p.Kind == kind);

	public bool IsSignedInt => Kind is PrimKind.I8 or PrimKind.I16 or PrimKind.I32 or PrimKind.I64;
	public bool IsUnsignedInt => Kind is PrimKind.U8 or PrimKind.U16 or PrimKind.U32 or PrimKind.U64;
	public bool IsInteger => IsSignedInt || IsUnsignedInt;
	public bool IsFloat => Kind is PrimKind.F16 or PrimKind.F32 or PrimKind.F64;
	/// <summary>Конкретное числовое значение (без универсальных inum/fnum/num).</summary>
	public bool IsNumeric => IsInteger || IsFloat;
	public bool IsUniversalNumeric => Kind is PrimKind.INum or PrimKind.FNum or PrimKind.Num;

	/// <summary>Разрядность числового типа в битах (0 — не число).</summary>
	public int Bits => Kind switch
	{
		PrimKind.I8 or PrimKind.U8 => 8,
		PrimKind.I16 or PrimKind.U16 or PrimKind.F16 => 16,
		PrimKind.I32 or PrimKind.U32 or PrimKind.F32 => 32,
		PrimKind.I64 or PrimKind.U64 or PrimKind.F64 => 64,
		_ => 0,
	};

	public override TypeSig ToTypeSig() => new TypeSig.Prim(Kind);
	public override string ToDisplayString() => Name;
	public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
	public override int GetHashCode() => (int)Kind;
}

/// <summary>Тип литерала `nil` (совместим только с T?).</summary>
public sealed class NilType : TypeSymbol
{
	public static readonly NilType Instance = new();
	private NilType() { }
	public override TypeSig ToTypeSig() => new TypeSig.Prim(PrimKind.Nil);
	public override string ToDisplayString() => "nil";
	public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
	public override int GetHashCode() => 1;
}

/// <summary>Тип значения `none` (начальное состояние переменной; совместим с любым типом).</summary>
public sealed class NoneType : TypeSymbol
{
	public static readonly NoneType Instance = new();
	private NoneType() { }
	// В пуле отдельной подписи нет: none в байткоде — специальное значение, тип стирается до object.
	public override TypeSig ToTypeSig() => TypeSig.Object;
	public override string ToDisplayString() => "none";
	public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
	public override int GetHashCode() => 2;
}

/// <summary>Тип-заглушка после ошибки: совместим со всем, чтобы не плодить каскад диагностик.</summary>
public sealed class ErrorType : TypeSymbol
{
	public static readonly ErrorType Instance = new();
	private ErrorType() { }
	public override bool IsError => true;
	public override TypeSig ToTypeSig() => TypeSig.Object;
	public override string ToDisplayString() => "<ошибка>";
	public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
	public override int GetHashCode() => 3;
}

/// <summary>Использование generic-параметра `T` внутри типа/функции.</summary>
public sealed class TypeParamType(TypeParamSymbol param) : TypeSymbol
{
	public TypeParamSymbol Param { get; } = param;
	public override bool ContainsTypeParam => true;
	public override TypeSig ToTypeSig() => new TypeSig.Param(Param.Scope, Param.Index, Param.Name);
	public override string ToDisplayString() => Param.Name;
	public override TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map) =>
		map.TryGetValue(Param, out var t) ? t : this;
	public override bool Equals(TypeSymbol? other) => other is TypeParamType p && ReferenceEquals(p.Param, Param);
	public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Param);
}

// ---------------------------------------------------------------- составные

public sealed class FuncType(IReadOnlyList<TypeSymbol> parameters, TypeSymbol returnType) : TypeSymbol
{
	public IReadOnlyList<TypeSymbol> Params { get; } = parameters;
	public TypeSymbol Return { get; } = returnType;
	public override bool ContainsTypeParam => Return.ContainsTypeParam || Params.Any(p => p.ContainsTypeParam);
	public override TypeSig ToTypeSig() => new TypeSig.Func(Sigs(Params), Return.ToTypeSig());
	public override string ToDisplayString() =>
		$"fun({string.Join(", ", Params.Select(p => p.ToDisplayString()))})" + (Return == PrimitiveType.Void ? "" : " -> " + Return.ToDisplayString());
	public override TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		var ps = SubstAll(Params, map);
		var r = Return.Substitute(map);
		return ReferenceEquals(ps, Params) && ReferenceEquals(r, Return) ? this : new FuncType(ps, r);
	}
	public override bool Equals(TypeSymbol? other) => other is FuncType f && Return.Equals(f.Return) && SeqEq(Params, f.Params);
	public override int GetHashCode() => HashCode.Combine(SeqHash(Params), Return);
}

/// <summary>Кортеж `(i32, str)` / `(i32 x, i32 y)`. Имена не участвуют в равенстве.</summary>
public sealed class TupleType(IReadOnlyList<TypeSymbol> items, IReadOnlyList<string?>? names = null) : TypeSymbol
{
	public IReadOnlyList<TypeSymbol> Items { get; } = items;
	public IReadOnlyList<string?>? Names { get; } = names is not null && names.Any(n => n is not null) ? names : null;
	public override bool ContainsTypeParam => Items.Any(p => p.ContainsTypeParam);
	public int IndexOfName(string name) => Names is null ? -1 : Names.ToList().IndexOf(name);
	public override TypeSig ToTypeSig() => new TypeSig.Tuple(Sigs(Items), Names);
	public override string ToDisplayString() =>
		"(" + string.Join(", ", Items.Select((t, i) => Names?[i] is { } n ? $"{t.ToDisplayString()} {n}" : t.ToDisplayString())) + ")";
	public override TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		var xs = SubstAll(Items, map);
		return ReferenceEquals(xs, Items) ? this : new TupleType(xs, Names);
	}
	public override bool Equals(TypeSymbol? other) => other is TupleType t && SeqEq(Items, t.Items);
	public override int GetHashCode() => SeqHash(Items);
}

/// <summary>`T?`. Не бывает вложенным: используйте <see cref="Of"/>.</summary>
public sealed class NullableType(TypeSymbol inner) : TypeSymbol
{
	public TypeSymbol Inner { get; } = inner;

	/// <summary>Фабрика: `T??` → `T?`, `nil?` → `nil`, ошибка остаётся ошибкой.</summary>
	public static TypeSymbol Of(TypeSymbol inner) => inner switch
	{
		NullableType or ErrorType or NilType => inner,
		_ => new NullableType(inner),
	};

	public override bool ContainsTypeParam => Inner.ContainsTypeParam;
	public override TypeSig ToTypeSig() => new TypeSig.Nullable(Inner.ToTypeSig());
	public override string ToDisplayString() => Inner is FuncType ? $"({Inner.ToDisplayString()})?" : Inner.ToDisplayString() + "?";
	public override TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		var i = Inner.Substitute(map);
		return ReferenceEquals(i, Inner) ? this : Of(i);
	}
	public override bool Equals(TypeSymbol? other) => other is NullableType n && Inner.Equals(n.Inner);
	public override int GetHashCode() => HashCode.Combine(Inner, 7);
}

// ---------------------------------------------------------------- встроенные generic-контейнеры

/// <summary>Встроенный generic-тип с именем из <see cref="TypeMapper"/> (list/array/dict/result/task).</summary>
public abstract class BuiltinGenericType(string name, IReadOnlyList<TypeSymbol> args) : TypeSymbol
{
	public string Name { get; } = name;
	public IReadOnlyList<TypeSymbol> Args { get; } = args;
	protected abstract BuiltinGenericType With(IReadOnlyList<TypeSymbol> args);
	public override bool ContainsTypeParam => Args.Any(a => a.ContainsTypeParam);
	public override TypeSig ToTypeSig() => new TypeSig.Named(Name, Sigs(Args));
	public override string ToDisplayString() => Name + ArgsDisplay(Args);
	public override TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		var a = SubstAll(Args, map);
		return ReferenceEquals(a, Args) ? this : With(a);
	}
	public override bool Equals(TypeSymbol? other) => other is BuiltinGenericType b && b.GetType() == GetType() && SeqEq(Args, b.Args);
	public override int GetHashCode() => HashCode.Combine(Name, SeqHash(Args));

	/// <summary>Создаёт встроенный generic по имени; null — имя не встроенное или неверная арность.</summary>
	public static BuiltinGenericType? Create(string name, IReadOnlyList<TypeSymbol> args) => (name, args.Count) switch
	{
		(TypeMapper.ListName, 1) => new ListType(args[0]),
		(TypeMapper.ArrayName, 1) => new ArrayType(args[0]),
		(TypeMapper.DictName, 2) => new DictType(args[0], args[1]),
		(TypeMapper.ResultName, 2) => new ResultType(args[0], args[1]),
		(TypeMapper.TaskName, 1) => new TaskType(args[0]),
		_ => null,
	};

	/// <summary>Арность встроенного generic-имени; -1 — имя не встроенное.</summary>
	public static int ArityOf(string name) => name switch
	{
		TypeMapper.ListName or TypeMapper.ArrayName or TypeMapper.TaskName => 1,
		TypeMapper.DictName or TypeMapper.ResultName => 2,
		_ => -1,
	};
}

public sealed class ListType(TypeSymbol element) : BuiltinGenericType(TypeMapper.ListName, [element])
{
	public TypeSymbol Element => Args[0];
	protected override BuiltinGenericType With(IReadOnlyList<TypeSymbol> a) => new ListType(a[0]);
}

public sealed class ArrayType(TypeSymbol element) : BuiltinGenericType(TypeMapper.ArrayName, [element])
{
	public TypeSymbol Element => Args[0];
	protected override BuiltinGenericType With(IReadOnlyList<TypeSymbol> a) => new ArrayType(a[0]);
}

public sealed class DictType(TypeSymbol key, TypeSymbol value) : BuiltinGenericType(TypeMapper.DictName, [key, value])
{
	public TypeSymbol Key => Args[0];
	public TypeSymbol Value => Args[1];
	protected override BuiltinGenericType With(IReadOnlyList<TypeSymbol> a) => new DictType(a[0], a[1]);
}

public sealed class ResultType(TypeSymbol ok, TypeSymbol err) : BuiltinGenericType(TypeMapper.ResultName, [ok, err])
{
	public TypeSymbol Ok => Args[0];
	public TypeSymbol Err => Args[1];
	protected override BuiltinGenericType With(IReadOnlyList<TypeSymbol> a) => new ResultType(a[0], a[1]);
}

public sealed class TaskType(TypeSymbol result) : BuiltinGenericType(TypeMapper.TaskName, [result])
{
	public TypeSymbol Result => Args[0];
	protected override BuiltinGenericType With(IReadOnlyList<TypeSymbol> a) => new TaskType(a[0]);
}

// ---------------------------------------------------------------- нативные типы

/// <summary>Тип из каталога нативных типов (Kern-модуль или dotnet).</summary>
public sealed class NativeType(NativeTypeSymbol symbol, IReadOnlyList<TypeSymbol> args) : TypeSymbol
{
	public NativeTypeSymbol Symbol { get; } = symbol;
	public IReadOnlyList<TypeSymbol> Args { get; } = args;
	public override bool ContainsTypeParam => Args.Any(a => a.ContainsTypeParam);
	public override TypeSig ToTypeSig() => new TypeSig.Named(Symbol.FullName, Sigs(Args));
	public override string ToDisplayString() => Symbol.Name + ArgsDisplay(Args);
	public override TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		var a = SubstAll(Args, map);
		return ReferenceEquals(a, Args) ? this : new NativeType(Symbol, a);
	}
	public override bool Equals(TypeSymbol? other) => other is NativeType n && n.Symbol.FullName == Symbol.FullName && SeqEq(Args, n.Args);
	public override int GetHashCode() => HashCode.Combine(Symbol.FullName, SeqHash(Args));

	/// <summary>
	/// Символ корня коллекций `collection` (kern.collections). Выставляется NativeBinder; нужен, чтобы встроенные
	/// list/array/dict/str/range считались потомками `collection&lt;T&gt;`. Сравнение типов идёт по FullName, поэтому
	/// символ из любого каталога равноценен.
	/// </summary>
	public static NativeTypeSymbol? CollectionRoot { get; set; }

	/// <summary>`collection&lt;элемент&gt;` для встроенных коллекций: list, array, str (char), dict ((K, V)), range (i64); иначе null.</summary>
	public static NativeType? CollectionParentOf(TypeSymbol t)
	{
		if (CollectionRoot is not { } root) return null;
		TypeSymbol? elem = t switch
		{
			ListType l => l.Element,
			ArrayType a => a.Element,
			DictType d => new TupleType([d.Key, d.Value], null),
			PrimitiveType { Kind: PrimKind.Str } => PrimitiveType.Char,
			NativeType { Symbol: { Name: "range", Module: "kern.collections" } } => PrimitiveType.I64,
			_ => null,
		};
		return elem is null ? null : new NativeType(root, [elem]);
	}

	/// <summary>Базовый нативный тип (из C#-наследования) с подставленными аргументами; null — базы нет.</summary>
	public NativeType? BaseType
	{
		get
		{
			if (Symbol.ParentSymbol is not { } ps) return null;
			var sigArgs = (Symbol.Parent as TypeSig.Named)?.Args ?? [];
			var args = new List<TypeSymbol>(sigArgs.Count);
			foreach (var a in sigArgs)
				args.Add(a switch
				{
					TypeSig.Param { Scope: 0 } p when p.Index < Args.Count => Args[p.Index],
					TypeSig.Prim pr => PrimitiveType.FromKind(pr.Kind),
					_ => PrimitiveType.Object,
				});
			return new NativeType(ps, args);
		}
	}
}

// ---------------------------------------------------------------- объявленные пользователем типы

/// <summary>Ссылка на объявление типа с generic-аргументами.</summary>
public abstract class DeclaredType(TypeDeclSymbol decl, IReadOnlyList<TypeSymbol> args) : TypeSymbol
{
	public TypeDeclSymbol Decl { get; } = decl;
	public IReadOnlyList<TypeSymbol> Args { get; } = args;

	protected abstract DeclaredType With(IReadOnlyList<TypeSymbol> args);

	public override bool ContainsTypeParam => Args.Any(a => a.ContainsTypeParam);
	public override TypeSig ToTypeSig() => new TypeSig.Named(Decl.FullName, Sigs(Args));
	public override string ToDisplayString() => Decl.Name + ArgsDisplay(Args);

	/// <summary>Отображение параметров объявления на аргументы этого вхождения.</summary>
	public IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> TypeMap()
	{
		var d = new Dictionary<TypeParamSymbol, TypeSymbol>();
		for (int i = 0; i < Decl.TypeParams.Count && i < Args.Count; i++) d[Decl.TypeParams[i]] = Args[i];
		return d;
	}

	public override TypeSymbol Substitute(IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map)
	{
		var a = SubstAll(Args, map);
		return ReferenceEquals(a, Args) ? this : With(a);
	}

	public override bool Equals(TypeSymbol? other) =>
		other is DeclaredType d && ReferenceEquals(d.Decl, Decl) && SeqEq(Args, d.Args);
	public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Decl), SeqHash(Args));

	/// <summary>Создаёт тип нужного класса по виду объявления.</summary>
	public static DeclaredType Create(TypeDeclSymbol decl, IReadOnlyList<TypeSymbol> args) => decl.DeclKind switch
	{
		TypeDeclKind.Unit or TypeDeclKind.ImmutableUnit => new UnitType(decl, args),
		TypeDeclKind.Enum => new EnumType(decl, args),
		TypeDeclKind.Union => new UnionType(decl, args),
		TypeDeclKind.Attribute => new AttributeType(decl, args),
		_ => new ClassType(decl, args),
	};
}

/// <summary>`type` / `@type`.</summary>
public sealed class ClassType(TypeDeclSymbol decl, IReadOnlyList<TypeSymbol> args) : DeclaredType(decl, args)
{
	protected override DeclaredType With(IReadOnlyList<TypeSymbol> a) => new ClassType(Decl, a);
}

/// <summary>`unit` / `unit!` (value-тип).</summary>
public sealed class UnitType(TypeDeclSymbol decl, IReadOnlyList<TypeSymbol> args) : DeclaredType(decl, args)
{
	public bool IsImmutable => Decl.DeclKind == TypeDeclKind.ImmutableUnit;
	protected override DeclaredType With(IReadOnlyList<TypeSymbol> a) => new UnitType(Decl, a);
}

public sealed class EnumType(TypeDeclSymbol decl, IReadOnlyList<TypeSymbol> args) : DeclaredType(decl, args)
{
	protected override DeclaredType With(IReadOnlyList<TypeSymbol> a) => new EnumType(Decl, a);
}

/// <summary>Union-алиас `type Shape = [Circle, Rect]`. Номинален по объявлению; члены — <see cref="Members"/>.</summary>
public sealed class UnionType(TypeDeclSymbol decl, IReadOnlyList<TypeSymbol> args) : DeclaredType(decl, args)
{
	/// <summary>Члены объединения с подставленными generic-аргументами.</summary>
	public IReadOnlyList<TypeSymbol> Members
	{
		get
		{
			if (Args.Count == 0) return Decl.UnionMembers;
			var map = TypeMap();
			return [.. Decl.UnionMembers.Select(m => m.Substitute(map))];
		}
	}
	public override TypeSig ToTypeSig() => new TypeSig.Union(Sigs(Members));
	protected override DeclaredType With(IReadOnlyList<TypeSymbol> a) => new UnionType(Decl, a);
}

/// <summary>Пользовательский `attribute`-тип.</summary>
public sealed class AttributeType(TypeDeclSymbol decl, IReadOnlyList<TypeSymbol> args) : DeclaredType(decl, args)
{
	protected override DeclaredType With(IReadOnlyList<TypeSymbol> a) => new AttributeType(Decl, a);
}
