using Kern.Binding.Catalog;
using Kern.Compiler.Compilation;
using Kern.Lsp.Protocol;

namespace Kern.Lsp.Analysis;

/// <summary>
/// Компилирует открытые документы как один проект и отдаёт диагностику.
/// Каталог нативов — только <c>Kern.Runtime</c>; внешние <c>Kern.Lib.*</c>
/// подключаются позже (LSP MVP).
/// </summary>
public sealed class AnalysisService
{
	private readonly NativeCatalog _catalog;

	public AnalysisService()
		: this(NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly))
	{
	}

	public AnalysisService(NativeCatalog catalog) => _catalog = catalog;

	public ProjectCompileResult? Last { get; private set; }

	public IReadOnlyList<ProjectDiagnostic> Analyze(DocumentStore documents)
	{
		ArgumentNullException.ThrowIfNull(documents);
		IReadOnlyList<ProjectSourceFile> files = documents.ToProject();
		if (files.Count == 0)
		{
			Last = null;
			return [];
		}

		try
		{
			Last = ProjectCompiler.Compile(files, "lsp", _catalog);
			return Last.Diagnostics;
		}
		catch (Exception exception)
		{
			// LSP не должен падать из-за одного битого документа: отдаём ошибку как строку.
			Last = null;
			return
			[
				new ProjectDiagnostic(
					files[0].RelativePath,
					new Kern.Core.Diagnostics.Diagnostic("KRN0000", "ошибка анализа: " + exception.Message, default))
			];
		}
	}
}
