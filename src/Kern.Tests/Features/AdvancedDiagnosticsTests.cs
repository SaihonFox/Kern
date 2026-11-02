using System.Text;
using Kern.Cli;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Продвинутая диагностика (spec §7/§11): связанные места («объявлено здесь»),
/// <c>#[allow(CODE)]</c> на объявлении и флаг <c>--deny-warnings</c>.
/// </summary>
public sealed class AdvancedDiagnosticsTests : IDisposable
{
	private readonly List<string> _roots = [];

	[Fact]
	public void Duplicate_function_reports_the_first_declaration()
	{
		var (code, _, error) = Run("check", File("fun f() -> i32 1\nfun f() -> i32 2\n"));

		Assert.Equal(1, code);
		Assert.Contains("KRN1400", error, StringComparison.Ordinal);
		Assert.Contains("примечание: первое объявление здесь", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Allow_suppresses_a_warning_inside_the_declaration()
	{
		const string source = """
			#[must_use] fun f() -> i32 1

			#[allow(KRN2305)]
			fun g() {
				f()
			}

			fun main() { g() }
			""";

		var (code, _, error) = Run("check", File(source));

		Assert.Equal(0, code);
		Assert.DoesNotContain("KRN2305", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Deny_warnings_turns_a_warning_into_a_failure()
	{
		const string source = """
			#[must_use] fun f() -> i32 1
			fun main() { f() }
			""";
		string path = File(source);

		var (allowed, _, allowedError) = Run("check", path);
		Assert.Equal(0, allowed);
		Assert.Contains("KRN2305", allowedError, StringComparison.Ordinal);

		var (denied, _, deniedError) = Run("check", "--deny-warnings", path);
		Assert.Equal(1, denied);
		Assert.Contains("warnings_as_errors", deniedError, StringComparison.Ordinal);
	}

	private string File(string source)
	{
		string root = Path.Combine(Path.GetTempPath(), "kern-diag", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		_roots.Add(root);
		string path = Path.Combine(root, "main.kern");
		System.IO.File.WriteAllText(path, source, new UTF8Encoding(false));
		return path;
	}

	private static (int Code, string Stdout, string Stderr) Run(params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		int code = KernCli.Run(args, stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	public void Dispose()
	{
		foreach (string root in _roots)
			try
			{
				if (Directory.Exists(root))
					Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
			}
	}
}
