using Kern.Bytecode;

namespace Kern.Vm.Execution;

/// <summary>
/// Runtime-политика capabilities (spec §7/§16): по умолчанию разрешено всё, а
/// <c>--deny</c> запрещает перечисленные имена. Политика действует только на нативные
/// вызовы, которые объявили capability. <c>null</c> в <see cref="Interpreter.Capabilities"/>
/// означает «разрешено всё» и сохраняет поведение обычного запуска.
/// </summary>
public sealed class CapabilityPolicy
{
	private readonly HashSet<string> _denied;

	public CapabilityPolicy(IEnumerable<string>? denied)
	{
		_denied = denied is null ? [] : new HashSet<string>(denied, StringComparer.Ordinal);
	}

	public static CapabilityPolicy AllowAll { get; } = new(null);

	/// <summary>Запрещена ли capability политикой запуска; <c>all</c> запрещает всё.</summary>
	public bool IsDenied(string capability) =>
		_denied.Contains("all") || _denied.Contains(capability);

	/// <summary>Проверить все capability-атрибуты нативной функции; бросить панику при отказе.</summary>
	public void Check(FunctionDef function)
	{
		foreach (AttributeUse attribute in function.Attributes)
		{
			if (!string.Equals(attribute.Name, "kern.capability", StringComparison.Ordinal) ||
				attribute.Args.Count == 0)
				continue;
			string capability = attribute.Args[0].AsStr;
			if (IsDenied(capability))
				throw new KernPanic(
					$"capability '{capability}' запрещена политикой запуска (--deny); функция '{function.Name}'");
		}
	}
}
