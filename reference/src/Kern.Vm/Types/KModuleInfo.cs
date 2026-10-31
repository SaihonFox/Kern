using Kern.Bytecode;
using Kern.Bytecode.Layout;
using Kern.Vm.Values;

namespace Kern.Vm.Types;

/// <summary>Рантайм-таблицы одного класса (TypeDef): раскладка полей, vtable, поиск членов по имени.</summary>
public sealed class KClassInfo(TypeDef def, int index)
{
	public TypeDef Def { get; } = def;
	public int Index { get; } = index;
	public int[] Parents { get; set; } = [];
	public int[] Ancestors { get; set; } = [];
	public HashSet<int> AncestorSet { get; set; } = [];
	public int FieldCount { get; set; }
	/// <summary>Нестатические поля: предки (дальние первыми), затем свои.</summary>
	public FieldDef[] AllFields { get; set; } = [];
	public Dictionary<string, FieldDef> FieldsByName { get; } = [];
	public Dictionary<string, PropertyDef> PropsByName { get; } = [];
	/// <summary>Корень виртуального метода → реализация (индекс функции).</summary>
	public Dictionary<int, int> Vtable { get; } = [];
	/// <summary>Имя метода → реализации (индексы функций), самые производные первыми.</summary>
	public Dictionary<string, List<int>> MethodsByName { get; } = [];
	public bool IsValueType => Def.Kind is TypeKind.Unit or TypeKind.ImmutableUnit;
	public Dictionary<int, KObject>? EnumValuesCache { get; set; }
}

/// <summary>Загруженный модуль: байткод + рантайм-таблицы. Создаётся ModuleLoader.</summary>
public sealed class KModuleInfo(KbcModule module)
{
	public KbcModule Module { get; } = module;
	public ModuleIndex Index { get; set; } = null!;
	public KClassInfo[] Classes { get; set; } = [];
	public KValue[] Statics { get; set; } = [];
	/// <summary>Символ оператора → функции-операторы (члены и расширения).</summary>
	public Dictionary<string, List<int>> Operators { get; } = [];
	/// <summary>Предвычисленные константы (TypeSig-константы разрешаются лениво и сюда не попадают).</summary>
	public KValue[] ConstValues { get; set; } = [];
	public TypeFactory Types { get; set; } = null!;
	public int[] Roots => Index.Roots;
	public bool IsSubtype(int type, int ancestor) => type == ancestor || Classes[type].AncestorSet.Contains(ancestor);
}
