using Kern.Bytecode;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>Результат генерации: модуль байткода и ошибки codegen (KRN22xx).</summary>
public sealed record CodegenResult(KbcModule Module, IReadOnlyList<Diagnostic> Diagnostics)
{
	public bool Success => Diagnostics.Count == 0;
}
