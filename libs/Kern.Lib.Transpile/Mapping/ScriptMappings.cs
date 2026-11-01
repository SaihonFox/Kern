using Kern.Bytecode;
using Kern.Lib.Transpile.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Transpile.Mapping;

/// <summary>
/// Пользовательские подстановки, написанные на Kern. Скрипт — обычный .kern-файл; если в нём
/// есть функции <c>map_type(str, str, list&lt;str&gt;) -&gt; str?</c>,
/// <c>map_call(str, str, str, str, list&lt;str&gt;) -&gt; str?</c> и
/// <c>map_member(str, str, str, str) -&gt; str?</c> — они вызываются для каждого нативного
/// типа/члена. Результат <c>nil</c> означает «правила нет».
/// </summary>
public sealed class ScriptMappings : IMappings
{
	private readonly Interpreter _vm;
	private readonly string _path;
	private readonly int _type;
	private readonly int _call;
	private readonly int _member;

	private ScriptMappings(Interpreter vm, string path)
	{
		_vm = vm;
		_path = path;
		_type = Find("map_type", 3);
		_call = Find("map_call", 5);
		_member = Find("map_member", 4);
	}

	public static ScriptMappings Load(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		string full = Path.GetFullPath(path);
		Interpreter vm = ProgramLoader.LoadScript(full);
		return new ScriptMappings(vm, full);
	}

	private int Find(string name, int parameterCount) =>
		_vm.Module.TryFindFunction(name, parameterCount, out int index) ? index : -1;

	public string? MapType(string target, string kernType, IReadOnlyList<string> args) =>
		Invoke(_type, "map_type", KValue.Str(target), KValue.Str(kernType), List(args));

	public string? MapCall(string target, string owner, string name, string receiver, IReadOnlyList<string> args) =>
		Invoke(
			_call,
			"map_call",
			KValue.Str(target),
			KValue.Str(owner),
			KValue.Str(name),
			KValue.Str(receiver),
			List(args));

	public string? MapMember(string target, string owner, string name, string receiver) =>
		Invoke(_member, "map_member", KValue.Str(target), KValue.Str(owner), KValue.Str(name), KValue.Str(receiver));

	private KValue List(IReadOnlyList<string> items) =>
		KValue.Obj(new KList(_vm.Types.Prim(PrimKind.Str), items.Select(KValue.Str)));

	private string? Invoke(int function, string name, params KValue[] args)
	{
		if (function < 0)
			return null;

		KValue result;
		try
		{
			result = _vm.Run(function, args);
		}
		catch (KernPanic panic)
		{
			throw new InvalidOperationException($"ошибка в скрипте подстановок {_path}, функция {name}: {panic.Message}");
		}

		return result.IsNilOrNone ? null : result.AsStr;
	}
}
