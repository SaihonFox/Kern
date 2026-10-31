using Kern.Vm.Types;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Vm.Native;

/// <summary>Вид нативного члена в ключе привязки.</summary>
public enum NativeMemberKind : byte
{
	Method = 0,
	PropertyGet = 1,
	PropertySet = 2,
	Constructor = 3,
}

/// <summary>
/// Стабильный ключ нативного члена. Один и тот же ключ строят
/// <c>Kern.Binding.NativeCatalog</c> (при сканировании сборки), компилятор (в
/// <see cref="Kern.Bytecode.FunctionDef.NativeKey"/>) и <c>NativeResolver</c> (при вызове),
/// поэтому формат фиксирован: <c>owner|name|kind|static|param1,param2</c>.
/// </summary>
public static class NativeKey
{
	public static string Create(
		string owner,
		string name,
		NativeMemberKind kind,
		bool isStatic,
		IReadOnlyList<string> parameterTypes)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(parameterTypes);
		char kindCode = kind switch
		{
			NativeMemberKind.Method => 'm',
			NativeMemberKind.PropertyGet => 'g',
			NativeMemberKind.PropertySet => 's',
			_ => 'c',
		};
		return $"{owner}|{name}|{kindCode}|{(isStatic ? '1' : '0')}|{string.Join(',', parameterTypes)}";
	}
}

/// <summary>Вызов нативного члена: аргументы уже приведены к VM-значениям.</summary>
public delegate KValue NativeInvoke(Interpreter interpreter, KValue[] arguments, KType[] typeArguments);

/// <summary>Разрешённая нативная привязка: делегат и число параметров Kern-сигнатуры.</summary>
public sealed class NativeBinding
{
	public required NativeInvoke Invoke { get; init; }

	/// <summary>Сколько аргументов принимает Kern-член (включая <c>self</c> у статических расширений).</summary>
	public int ParameterCount { get; init; }

	public override string ToString() => $"native({ParameterCount})";
}

/// <summary>
/// Поиск нативного члена по <see cref="NativeKey"/> среди загруженных сборок.
/// VM не знает про атрибуты — реализация живёт в <c>Kern.Binding</c>.
/// </summary>
public interface INativeResolver
{
	bool TryResolve(string key, out NativeBinding binding);
}
