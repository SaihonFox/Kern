using System.Diagnostics.CodeAnalysis;

namespace Kern.Bytecode;

/// <summary>Флаги члена (функции/поля/свойства/типа). Часть флагов используется компилятором и рефлексией.</summary>
[Flags]
public enum MemberFlags : uint
{
	None = 0,
	Pub = 1 << 0,
	Priv = 1 << 1,
	Static = 1 << 2,
	Readonly = 1 << 3,
	Async = 1 << 4,
	Getter = 1 << 5,
	Setter = 1 << 6,
	Init = 1 << 7,
	Operator = 1 << 8,
	Extension = 1 << 9,
	Ctor = 1 << 10,
	Native = 1 << 11,
	Test = 1 << 12,
	Generated = 1 << 13,
	Lambda = 1 << 14,
	Override = 1 << 15,
	Abstract = 1 << 16,
	Open = 1 << 17,
}

/// <summary>Использование атрибута: пара «имя типа атрибута + аргументы-константы».</summary>
public sealed class AttributeUse
{
	public required string Name { get; set; }
	public List<KConst> Args { get; set; } = [];

	public AttributeUse()
	{
	}

	[SetsRequiredMembers]
	public AttributeUse(string name, params KConst[] args)
	{
		Name = name;
		Args = [.. args];
	}

	public override string ToString() =>
		Args.Count == 0 ? Name : $"{Name}({string.Join(", ", Args.Select(a => a.Describe()))})";
}

/// <summary>Поле типа или глобальная переменная модуля.</summary>
public sealed class FieldDef
{
	public required string Name { get; set; }
	public TypeSig Type { get; set; } = TypeSig.Object;
	public MemberFlags Flags { get; set; }
	/// <summary>Слот в экземпляре (для глобалов — индекс в таблице статики).</summary>
	public int Slot { get; set; }
	public List<AttributeUse> Attributes { get; set; } = [];
}

/// <summary>Свойство: геттер/сеттер/init — отдельные функции, -1 если аксессора нет.</summary>
public sealed class PropertyDef
{
	public required string Name { get; set; }
	public TypeSig Type { get; set; } = TypeSig.Object;
	public MemberFlags Flags { get; set; }
	public int Getter { get; set; } = -1;
	public int Setter { get; set; } = -1;
	public int Init { get; set; } = -1;
	public int BackingField { get; set; } = -1;
	public List<AttributeUse> Attributes { get; set; } = [];
}

/// <summary>Запись таблицы строк исходника: смещение кода -> строка.</summary>
public readonly record struct LineEntry(int Offset, int Line);

/// <summary>Функция (в т.ч. лямбда, геттер, конструктор) — единица кода в пуле функций.</summary>
public sealed class FunctionDef
{
	public required string Name { get; set; }
	/// <summary>Индекс владельца в <see cref="KbcModule.TypePool"/>, -1 для верхнеуровневой функции.</summary>
	public int OwnerType { get; set; } = -1;
	public MemberFlags Flags { get; set; }
	public TypeSig[] ParamTypes { get; set; } = [];
	public TypeSig ReturnType { get; set; } = TypeSig.Void;
	/// <summary>Число параметров; всегда равно <c>ParamTypes.Length</c>.</summary>
	public int ParamCount => ParamTypes.Length;
	/// <summary>Всего локальных слотов, включая параметры (LocalCount >= ParamCount).</summary>
	public int LocalCount { get; set; }
	/// <summary>Максимальная глубина стека операндов (для оценки размера стека VM).</summary>
	public int MaxStack { get; set; }
	public int[] Code { get; set; } = [];
	public List<LineEntry> LineTable { get; set; } = [];
	public string? SourceFile { get; set; }
	public List<AttributeUse> Attributes { get; set; } = [];

	/// <summary>
	/// Стабильный ключ нативного члена для <c>Kern.Binding.NativeResolver</c>; задаётся только
	/// у функций с флагом <see cref="MemberFlags.Native"/>. В .kbc хранится строкой.
	/// </summary>
	public string? NativeKey { get; set; }

	/// <summary>Индекс списка reified-аргументов в <see cref="KbcModule.TypeSigListPool"/>; -1 — нет.</summary>
	public int NativeTypeArgs { get; set; } = -1;

	public bool IsNative => (Flags & MemberFlags.Native) != 0;

	/// <summary>Строка исходника для смещения кода (последняя запись с Offset &lt;= offset).</summary>
	public int? LineAt(int offset)
	{
		int? line = null;
		foreach (LineEntry entry in LineTable)
		{
			if (entry.Offset > offset) break;
			line = entry.Line;
		}
		return line;
	}
}

/// <summary>Вид объявленного типа.</summary>
public enum TypeDefKind : byte
{
	Class = 0,
	Unit,
	ImmutableUnit,
	Union,
}

/// <summary>Объявление типа: поля, свойства, методы, конструкторы, родители, атрибуты.</summary>
public sealed class TypeDef
{
	public required string Name { get; set; }
	/// <summary>Полное имя — стабильный ключ (модуль + имя), используется в подписях.</summary>
	public required string FullName { get; set; }
	public TypeDefKind Kind { get; set; }
	public MemberFlags Flags { get; set; }
	public List<string> GenericParams { get; set; } = [];
	public List<TypeSig> Parents { get; set; } = [];
	public List<FieldDef> Fields { get; set; } = [];
	public List<PropertyDef> Properties { get; set; } = [];
	/// <summary>Индексы методов в <see cref="KbcModule.FunctionPool"/>.</summary>
	public List<int> Methods { get; set; } = [];
	/// <summary>Индексы конструкторов в <see cref="KbcModule.FunctionPool"/>.</summary>
	public List<int> Ctors { get; set; } = [];
	public List<AttributeUse> Attributes { get; set; } = [];
	/// <summary>Члены union-типа (для <see cref="TypeDefKind.Union"/>).</summary>
	public List<TypeSig> UnionMembers { get; set; } = [];

	public override string ToString() => FullName;
}
