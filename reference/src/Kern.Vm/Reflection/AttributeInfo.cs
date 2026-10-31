using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

/// <summary>Применение атрибута к типу/члену/модулю: имя типа атрибута и константные аргументы.</summary>
public sealed class AttributeInfo
{
	private readonly Interpreter _vm;
	private readonly AttributeUse _use;

	private AttributeInfo(Interpreter vm, AttributeUse use) { _vm = vm; _use = use; }

	public string TypeName => _use.TypeName;

	/// <summary>Позиционные аргументы (без имени).</summary>
	public IReadOnlyList<KValue> Positional =>
		[.. _use.Args.Where(a => a.Name is null).Select(a => Reflector.ConstantToValue(_vm, a))];

	/// <summary>Именованные аргументы (<c>#[Route(path: "/x")]</c>).</summary>
	public IReadOnlyDictionary<string, KValue> Named =>
		_use.Args.Where(a => a.Name is not null).ToDictionary(a => a.Name!, a => Reflector.ConstantToValue(_vm, a));

	public bool Is(string name) => Reflector.NameMatches(_use.TypeName, name);

	/// <summary>Тип атрибута в модуле (null, если атрибут внешний/нативный).</summary>
	public TypeInfo? AttributeType
	{
		get
		{
			int i = Reflector.FindTypeIndex(_vm, _use.TypeName);
			return i < 0 ? null : Reflector.ClassOf(_vm, i);
		}
	}

	/// <summary>Создать экземпляр атрибута: позиционные аргументы → конструктор, именованные → свойства/поля.</summary>
	public KValue Instantiate()
	{
		var type = AttributeType ?? throw new KernPanic($"тип атрибута '{_use.TypeName}' не найден в модуле");
		var obj = type.CreateInstance([.. Positional]);
		if (obj.Ref is KObject ko)
			foreach (var (name, value) in Named)
				type.SetMember(ko, name, value);
		return obj;
	}

	internal static IReadOnlyList<AttributeInfo> Wrap(Interpreter vm, IEnumerable<AttributeUse> uses) =>
		[.. uses.Select(u => new AttributeInfo(vm, u))];

	public override string ToString() => "#[" + _use.TypeName + "]";
}
