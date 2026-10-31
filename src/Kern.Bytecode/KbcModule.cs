namespace Kern.Bytecode;

/// <summary>
/// Модуль байткода: код функций и все пулы, на которые ссылаются операнды.
/// Индексы в пулах — стабильные ключи внутри модуля; <c>Intern*</c> не создают дубликатов.
/// </summary>
public sealed class KbcModule
{
	public const string Magic = "KBC2";
	public const ushort Version = 2;

	public required string Name { get; set; }

	/// <summary>Имена подключённых модулей (для линковки и ошибок).</summary>
	public List<string> References { get; set; } = [];

	public List<string> StringPool { get; set; } = [];
	public List<KConst> ConstPool { get; set; } = [];
	public List<TypeSig> TypeSigPool { get; set; } = [];
	/// <summary>Списки индексов в <see cref="TypeSigPool"/> (аргументы дженериков у Call/NewObj).</summary>
	public List<int[]> TypeSigListPool { get; set; } = [];
	public List<TypeDef> TypePool { get; set; } = [];
	public List<FieldDef> FieldPool { get; set; } = [];
	public List<PropertyDef> PropertyPool { get; set; } = [];
	public List<FunctionDef> FunctionPool { get; set; } = [];
	public List<AttributeUse> Attributes { get; set; } = [];

	/// <summary>Индекс функции main в пуле; -1 — модуль без точки входа (библиотека).</summary>
	public int EntryPoint { get; set; } = -1;

	/// <summary>
	/// Индекс функции <c>&lt;static-init&gt;</c>; -1 — статических инициализаторов нет;
	/// выполняется один раз до точки входа (spec §12).
	/// </summary>
	public int StaticInit { get; set; } = -1;

	public int InternString(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		int index = StringPool.IndexOf(value);
		if (index >= 0) return index;
		StringPool.Add(value);
		return StringPool.Count - 1;
	}

	public int InternConst(KConst value)
	{
		int index = ConstPool.IndexOf(value);
		if (index >= 0) return index;
		ConstPool.Add(value);
		return ConstPool.Count - 1;
	}

	public int InternTypeSig(TypeSig value)
	{
		ArgumentNullException.ThrowIfNull(value);
		int index = TypeSigPool.IndexOf(value);
		if (index >= 0) return index;
		TypeSigPool.Add(value);
		return TypeSigPool.Count - 1;
	}

	public int InternTypeSigList(IEnumerable<TypeSig> sigs)
	{
		int[] ids = [.. sigs.Select(InternTypeSig)];
		for (int i = 0; i < TypeSigListPool.Count; i++)
		{
			int[] existing = TypeSigListPool[i];
			if (existing.AsSpan().SequenceEqual(ids)) return i;
		}
		TypeSigListPool.Add(ids);
		return TypeSigListPool.Count - 1;
	}

	public int AddFunction(FunctionDef function)
	{
		ArgumentNullException.ThrowIfNull(function);
		FunctionPool.Add(function);
		return FunctionPool.Count - 1;
	}

	public int AddType(TypeDef type)
	{
		ArgumentNullException.ThrowIfNull(type);
		TypePool.Add(type);
		return TypePool.Count - 1;
	}

	/// <summary>Поиск функции по имени и числу параметров (перегрузки различаются арностью).</summary>
	public bool TryFindFunction(string name, int paramCount, out int index)
	{
		for (int i = 0; i < FunctionPool.Count; i++)
		{
			FunctionDef fn = FunctionPool[i];
			if (fn.Name == name && fn.ParamCount == paramCount)
			{
				index = i;
				return true;
			}
		}
		index = -1;
		return false;
	}

	public TypeDef? FindType(string fullName)
	{
		foreach (TypeDef type in TypePool)
			if (type.FullName == fullName) return type;
		return null;
	}
}
