using Kern.Lsp.Analysis;
using Kern.Lsp.Protocol;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>LSP видит внешние библиотеки из libs/_out и типы вида или orm/sqlite/table.</summary>
public sealed class LspLibraryTests
{
	[Fact]
	public void External_db_library_is_visible_to_analysis()
	{
		string? root = RepositoryRoot();
		if (root is null)
			return;
		string output = Path.Combine(root, "libs", "_out", "Kern.Lib.Db", "Kern.Lib.Db.dll");
		if (!File.Exists(output))
			return;

		var analysis = new AnalysisService();
		analysis.SetRoot(root);
		var documents = new DocumentStore();
		documents.SetRoot(new Uri(root).AbsoluteUri);
		string uri = new Uri(Path.Combine(root, "examples", "15_databases", "src", "main.kern")).AbsoluteUri;
		documents.Open(uri, """
			use kern.db[orm, sqlite]
			use kern.db.sqlite[sqlite]

			fun probe() {
				sqlite db(":memory:")
			}
			""");
		IReadOnlyList<Kern.Compiler.Compilation.ProjectDiagnostic> diagnostics = analysis.Analyze(documents);
		Assert.DoesNotContain(diagnostics, diagnostic =>
			diagnostic.Diagnostic.Code is "KRN1001" or "KRN1101");
	}

	private static string? RepositoryRoot()
	{
		for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
			if (File.Exists(Path.Combine(current.FullName, "Kern.slnx")))
				return current.FullName;
		return null;
	}
}
