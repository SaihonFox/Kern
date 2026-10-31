using Kern.Compiler.Semantics.Declarations;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Modules;

/// <summary>Разрешённый импорт одного модуля: цель, явные имена и признак `.*`.</summary>
public sealed record ResolvedImport(
	ModuleSymbol Module,
	IReadOnlyList<string> Items,
	bool IsWildcard,
	TextSpan Span)
{
	/// <summary>Импортируется весь модуль (без списка имён или через `.*`).</summary>
	public bool ImportsEverything => IsWildcard || Items.Count == 0;
}
