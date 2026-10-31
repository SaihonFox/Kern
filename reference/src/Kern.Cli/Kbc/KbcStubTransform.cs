using Kern.Compiler.Semantics.Project;
using Kern.Core;

namespace Kern.Cli.Kbc;

/// <summary>
/// Добавляет к исходникам проекта интерфейсные файлы подключённых .kbc. Выполняется после раскрытия макросов:
/// в .kbc лежит уже раскрытый текст.
/// </summary>
public sealed class KbcStubTransform(ISourceTransform? inner, IReadOnlyList<KbcLib> libs) : ISourceTransform
{
	public IReadOnlyList<SourceText> Transform(string projectName, IReadOnlyList<SourceText> files, DiagnosticBag diagnostics)
	{
		IReadOnlyList<SourceText> result = inner?.Transform(projectName, files, diagnostics) ?? files;
		if (libs.Count == 0) return result;
		var all = new List<SourceText>(result);
		foreach (var lib in libs) all.AddRange(KbcStubs.Read(lib.Module));
		return all;
	}
}
