using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Examples;

public sealed class StageOneSyntaxTests
{
	[Fact]
	public void Stage_one_sources_have_no_syntax_diagnostics()
	{
		var root = FindRepositoryRoot();
		var files = Directory.EnumerateFiles(Path.Combine(root, "examples"), "*.kern", SearchOption.AllDirectories)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		
		Assert.NotEmpty(files);
		var failures = new List<string>();
		foreach (var file in files)
		{
			var source = File.ReadAllText(file);
			foreach (var diagnostic in Parser.Parse(source).Diagnostics.Where(diagnostic =>
				diagnostic.Code.StartsWith("LEX", StringComparison.Ordinal) || diagnostic.Code.StartsWith("PAR", StringComparison.Ordinal)))
			{
				var line = source.AsSpan(0, Math.Min(diagnostic.Span.Start, source.Length)).Count('\n') + 1;
				var lineStart = source.LastIndexOf('\n', Math.Min(diagnostic.Span.Start, source.Length) is 0 ? 0 : Math.Min(diagnostic.Span.Start, source.Length) - 1);
				var lineEnd = source.IndexOf('\n', diagnostic.Span.Start);
				if (lineEnd < 0) lineEnd = source.Length;
				var context = source[(lineStart + 1)..lineEnd].Trim();
				failures.Add($"{Path.GetRelativePath(root, file)}:{line}: {diagnostic.Code}: {diagnostic.Message} | {context}");
			}
		}

		Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
	}

	private static string FindRepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
			if (Directory.Exists(Path.Combine(directory.FullName, "examples")) && File.Exists(Path.Combine(directory.FullName, "Kern.slnx")))
				return directory.FullName;
		throw new DirectoryNotFoundException("Не найден корень репозитория Kern");
	}
}
