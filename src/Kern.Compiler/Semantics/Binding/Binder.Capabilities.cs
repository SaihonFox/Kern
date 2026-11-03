using Kern.Compiler.Semantics.Declarations;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Compile-time проверка capabilities (spec §7/§16): функция с <c>#[needs(...)]</c> может
/// вызывать только нативные/пользовательские функции, чьи capabilities покрыты объявлением.
/// </summary>
public sealed partial class Binder
{
	private HashSet<string> _declaredCapabilities = new(StringComparer.Ordinal);
	private readonly HashSet<string> _reportedCapabilities = new(StringComparer.Ordinal);

	private void BeginCapabilityScope(FunctionSymbol function)
	{
		_declaredCapabilities = new HashSet<string>(function.DeclaredCapabilities, StringComparer.Ordinal);
		_reportedCapabilities.Clear();
	}

	private void RecordCapabilities(FunctionSymbol function, TextSpan span)
	{
		// Compile-time проверка строгая только для функций, которые объявили #[needs]:
		// старый код и обычные приложения без объявления продолжают компилироваться,
		// а runtime-политика --allow/--deny всё равно проверяет нативные вызовы.
		if (_declaredCapabilities.Count == 0)
			return;
		foreach (string capability in function.NativeCapabilities.Concat(function.DeclaredCapabilities))
		{
			if (_declaredCapabilities.Contains(capability))
				continue;
			if (!_reportedCapabilities.Add(capability))
				continue;
			Report(
				"KRN2401",
				$"функция использует capability '{capability}', но не объявила её в #[needs({capability})]",
				span);
		}
	}
}
