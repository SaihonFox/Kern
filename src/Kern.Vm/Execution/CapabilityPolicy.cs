using Kern.Bytecode;

namespace Kern.Vm.Execution;

/// <summary>
/// Runtime-политика capabilities (spec §7/§16): <c>--allow</c> задаёт белый список,
/// <c>--deny</c> запрещает имена (deny важнее allow). <c>null</c> в <see cref="Interpreter.Capabilities"/>
/// означает «разрешено всё» и сохраняет поведение обычного запуска.
/// </summary>
public sealed class CapabilityPolicy
{
	private readonly HashSet<string>? _allowed;
	private readonly HashSet<string> _denied;

	public CapabilityPolicy(IEnumerable<string>? allowed, IEnumerable<string>? denied)
	{
		_allowed = allowed is null ? null : new HashSet<string>(allowed, StringComparer.Ordinal);
		_denied = denied is null ? [] : new HashSet<string>(denied, StringComparer.Ordinal);
	}

	public static CapabilityPolicy AllowAll { get; } = new(null, null);

	public bool IsAllowed(string capability) =>
		(_allowed is null || _allowed.Contains(capability)) && !_denied.Contains(capability);

	/// <summary>Проверить все capability-атрибуты нативной функции; бросить панику при отказе.</summary>
	public void Check(FunctionDef function)
	{
		foreach (AttributeUse attribute in function.Attributes)
		{
			if (!string.Equals(attribute.Name, "kern.capability", StringComparison.Ordinal) ||
				attribute.Args.Count == 0)
				continue;
			string capability = attribute.Args[0].AsStr;
			if (!IsAllowed(capability))
				throw new KernPanic(
					$"нет разрешения на '{capability}' (функция '{function.Name}'); используйте --allow {capability}");
		}
	}
}
