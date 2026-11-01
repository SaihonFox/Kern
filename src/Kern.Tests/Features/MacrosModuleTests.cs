using System.Text;
using Kern.Cli;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M9, макросы (spec round 22): отдельный мини-проект, атрибутные макросы
/// (<c>#[name]</c>/<c>#[derive(name)]</c>), <c>name!(...)</c> и диагностика KRN1502/1503.
/// </summary>
[Collection("timing")]
public sealed class MacrosModuleTests : IDisposable
{
	private readonly List<string> _roots = [];

	[Fact]
	public void Attribute_derive_and_expression_macros_generate_code()
	{
		string root = Create(
			("src/macros.kern", """
				use kern.macro[macro_ctx]

				#[macro]
				fun to_text(macro_ctx m) {
					var body = ""
					var sep = ""
					loop var f in m.decl.fields {
						body = body + sep + f.name + "=${" + f.name + "}"
						sep = ", "
					}
					m.member("fun to_text() -> str f\"" + m.decl.name + "(" + body + ")\"")
				}

				#[macro]
				fun field_count(macro_ctx m) {
					m.top(f"fun ${m.decl.name}_field_count() -> i32 ${m.decl.fields.len}")
				}

				#[macro("expr")]
				fun sq(macro_ctx m) -> str {
					var a = m.args[0]
					f"(${a}) * (${a})"
				}
				"""),
			("src/main.kern", """
				#[derive(to_text)]
				type point(f64 x, f64 y)

				#[field_count]
				#[to_text]
				unit user(i64 id, str name)

				fun main {
					println(point(1.0, 2.0).to_text())
					println(user(1, "аня").to_text())
					println(user_field_count())
					println(sq!(3 + 1))
				}
				"""));

		(int code, _, string stderr) = Run("check", root);
		Assert.True(code == 0, stderr);
		Assert.Equal(string.Empty, stderr);

		Assert.Equal(
			"point(x=1, y=2)\nuser(id=1, name=аня)\n2\n16\n",
			CaptureRun(root));
	}

	[Fact]
	public void Macro_error_is_reported_as_KRN1502()
	{
		string root = Create(
			("src/macros.kern", """
				use kern.macro[macro_ctx]

				#[macro]
				fun require_id(macro_ctx m) {
					if m.decl.field("id") == nil {
						m.error(f"в типе ${m.decl.name} нет поля id")
					}
				}
				"""),
			("src/main.kern", """
				#[require_id]
				unit broken(str name)

				fun main {}
				"""));

		(int code, _, string stderr) = Run("check", root);

		Assert.Equal(1, code);
		Assert.Contains("KRN1502", stderr, StringComparison.Ordinal);
		Assert.Contains("нет поля id", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Nested_expression_macros_expand_in_passes()
	{
		string root = Create(
			("src/macros.kern", """
				use kern.macro[macro_ctx]

				#[macro("expr")]
				fun sq(macro_ctx m) -> str {
					var a = m.args[0]
					f"(${a}) * (${a})"
				}
				"""),
			("src/main.kern", """
				fun main {
					println(sq!(sq!(2)))
				}
				"""));

		Assert.Equal("16\n", CaptureRun(root));
	}

	[Fact]
	public void Macro_warning_does_not_fail_compilation()
	{
		string root = Create(
			("src/macros.kern", """
				use kern.macro[macro_ctx]
				#[macro]
				fun note(macro_ctx m) {
					m.warn("заметка")
				}
				"""),
			("src/main.kern", """
				#[note]
				type User(str name)
				fun main {}
				"""));

		(int code, _, string stderr) = Run("check", root);

		Assert.Equal(0, code);
		Assert.Contains("KRN1503", stderr, StringComparison.Ordinal);
		Assert.Contains("заметка", stderr, StringComparison.Ordinal);
	}

	private string Create(params (string Path, string Text)[] files)
	{
		string root = Path.Combine(Path.GetTempPath(), "kern-macro-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		_roots.Add(root);
		foreach (var (path, text) in files)
		{
			string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllText(full, text, new UTF8Encoding(false));
		}

		return root;
	}

	private static string CaptureRun(string root)
	{
		var writer = new StringWriter { NewLine = "\n" };
		TextWriter original = Console.Out;
		Console.SetOut(writer);
		try
		{
			(int code, _, string stderr) = Run("run", root);
			Assert.Equal(0, code);
			Assert.Equal(string.Empty, stderr);
		}
		finally
		{
			Console.SetOut(original);
		}

		return writer.ToString();
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
		{
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
}
