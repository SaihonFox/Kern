namespace Kern.Bytecode;

[Flags]
public enum MemberFlags : uint
{
	None = 0,
	Pub = 1 << 0, Priv = 1 << 1, Prot = 1 << 2, Int = 1 << 3,
	Static = 1 << 4, Open = 1 << 5, Abstract = 1 << 6, Override = 1 << 7,
	Async = 1 << 8, Getter = 1 << 9, Setter = 1 << 10, Init = 1 << 11,
	Operator = 1 << 12, Extension = 1 << 13, Ctor = 1 << 14, Native = 1 << 15,
	Required = 1 << 16, Test = 1 << 17, Generated = 1 << 18, Lambda = 1 << 19,
}

public enum TypeKind : byte { Class, AbstractClass, Unit, ImmutableUnit, Union, Enum, Attribute, Interface }

public sealed class ConstantArg
{
	/// <summary>Одно из: null (nil), bool, long, ulong, double, string, char или TypeSig.</summary>
	public object? Value { get; set; }
	public string? Name { get; set; }
}

public sealed class AttributeUse
{
	public required string TypeName { get; set; }
	public List<ConstantArg> Args { get; set; } = [];
}

public sealed class GenericParamDef
{
	public required string Name { get; set; }
	/// <summary>Ограничения (<c>T[Constraint1, Constraint2]</c>).</summary>
	public List<TypeSig> Constraints { get; set; } = [];
	/// <summary>Ограничение конструктора ctor(args).</summary>
	public List<TypeSig>? CtorArgs { get; set; }
}

public sealed class ParamDef
{
	public required string Name { get; set; }
	public required TypeSig Type { get; set; }
	public bool HasDefault { get; set; }
	/// <summary>Константа по умолчанию либо индекс функции-инициализатора (-1 если константа).</summary>
	public ConstantArg? DefaultConstant { get; set; }
	public int DefaultFunction { get; set; } = -1;
	public bool IsVariadic { get; set; }
}

public sealed class FieldDef
{
	public required string Name { get; set; }
	public required TypeSig Type { get; set; }
	public MemberFlags Flags { get; set; }
	public List<AttributeUse> Attributes { get; set; } = [];
	public int Slot { get; set; }
}

public sealed class PropertyDef
{
	public required string Name { get; set; }
	public required TypeSig Type { get; set; }
	public MemberFlags Flags { get; set; }
	public int Getter { get; set; } = -1;
	public int Setter { get; set; } = -1;
	public int Init { get; set; } = -1;
	public int BackingField { get; set; } = -1;
	public List<AttributeUse> Attributes { get; set; } = [];
}

public sealed class FunctionDef
{
	public required string Name { get; set; }
	/// <summary>Индекс владельца в KbcModule.Types, -1 для верхнеуровневых функций.</summary>
	public int Owner { get; set; } = -1;
	public MemberFlags Flags { get; set; }
	public List<GenericParamDef> GenericParams { get; set; } = [];
	public List<ParamDef> Params { get; set; } = [];
	public TypeSig Return { get; set; } = TypeSig.Void;
	/// <summary>Для операторов — символ (+, -, ==, ...).</summary>
	public string? OpSymbol { get; set; }
	/// <summary>Для расширений — расширяемый тип.</summary>
	public TypeSig? ExtensionOf { get; set; }
	public int LocalCount { get; set; }
	public int MaxStack { get; set; }
	public int[] Code { get; set; } = [];
	/// <summary>Пары (смещение кода, строка исходника) для стек-трейсов.</summary>
	public List<(int Offset, int Line)> LineTable { get; set; } = [];
	public string? SourceFile { get; set; }
	public List<AttributeUse> Attributes { get; set; } = [];
	/// <summary>Индекс функции, которую переопределяет эта (слот vtable), либо -1.</summary>
	public int Overrides { get; set; } = -1;
	/// <summary>Нативная реализация (метод C# по уникальному ключу NativeCatalog), если Flags.Native.</summary>
	public string? NativeKey { get; set; }
	/// <summary>Для замыканий: число захваченных ячеек.</summary>
	public int CaptureCount { get; set; }
}

public sealed class TypeDef
{
	public required string Name { get; set; }
	public required string FullName { get; set; }
	public TypeKind Kind { get; set; }
	public MemberFlags Flags { get; set; }
	public List<GenericParamDef> GenericParams { get; set; } = [];
	public List<TypeSig> Parents { get; set; } = [];
	/// <summary>Вызовы родительских конструкторов (индексы функций-инициализаторов в порядке Parents).</summary>
	public List<int> ParentInit { get; set; } = [];
	public List<FieldDef> Fields { get; set; } = [];
	public List<PropertyDef> Properties { get; set; } = [];
	public List<int> Methods { get; set; } = [];
	public List<int> Ctors { get; set; } = [];
	public List<AttributeUse> Attributes { get; set; } = [];
	/// <summary>Для Union/Enum: члены.</summary>
	public List<TypeSig> UnionMembers { get; set; } = [];
	public List<string> EnumNames { get; set; } = [];
	public List<long> EnumValues { get; set; } = [];
	/// <summary>Явный выбор родителя при конфликте: (имя члена, индекс родителя) — для $[A].member.</summary>
	public List<(string Member, int Parent)> ParentChoices { get; set; } = [];
	public string? NativeType { get; set; }
}

public sealed class KbcModule
{
	public const string Magic = "KBC2";
	public const int Version = 1;

	public required string Name { get; set; }
	public List<string> References { get; set; } = [];
	public List<string> Strings { get; set; } = [];
	public List<TypeSig> TypeSigs { get; set; } = [];
	/// <summary>Списки подписей (аргументы типа в Call*/NewObj): элементы — индексы в TypeSigs.</summary>
	public List<int[]> TypeSigLists { get; set; } = [];
	public List<object?> Constants { get; set; } = [];
	public List<TypeDef> Types { get; set; } = [];
	public List<FunctionDef> Functions { get; set; } = [];
	public List<FieldDef> Globals { get; set; } = [];
	/// <summary>Функции-инициализаторы статических полей, выполняются при загрузке по порядку.</summary>
	public List<int> StaticInit { get; set; } = [];
	public int EntryPoint { get; set; } = -1;
	public bool EntryTakesArgs { get; set; }
	public bool EntryIsAsync { get; set; }
	public List<int> Tests { get; set; } = [];
	public List<AttributeUse> Attributes { get; set; } = [];

	public int Intern(string s)
	{
		int i = Strings.IndexOf(s);
		if (i >= 0) return i;
		Strings.Add(s);
		return Strings.Count - 1;
	}

	public int Intern(TypeSig sig)
	{
		int i = TypeSigs.IndexOf(sig);
		if (i >= 0) return i;
		TypeSigs.Add(sig);
		return TypeSigs.Count - 1;
	}

	public int InternList(IEnumerable<TypeSig> sigs)
	{
		int[] ids = sigs.Select(Intern).ToArray();
		int i = TypeSigLists.FindIndex(l => l.AsSpan().SequenceEqual(ids));
		if (i >= 0) return i;
		TypeSigLists.Add(ids);
		return TypeSigLists.Count - 1;
	}

	public int AddConstant(object? value)
	{
		int i = Constants.FindIndex(c => Equals(c, value) && c?.GetType() == value?.GetType());
		if (i >= 0) return i;
		Constants.Add(value);
		return Constants.Count - 1;
	}
}
