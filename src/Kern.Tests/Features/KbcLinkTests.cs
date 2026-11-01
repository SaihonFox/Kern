using System.Text;
using Kern.Binding.Resolution;
using Kern.Cli;
using Kern.Cli.Build;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>M10: `kernc dll` + `t.kbc(path)` — интерфейсные исходники и линковка.</summary>
[Collection("timing")]
public sealed class KbcLinkTests : IDisposable
{
	private readonly List<string> _roots = [];

	[Fact]
	public void Dll_stubs_compile_and_link_the_consumer()
	{
		string root = CreateLibAndApp();
		string library = Path.Combine(root, "mathx");
		string app = Path.Combine(root, "app");

		(int dllCode, string dllOut, string dllErr) = Run("dll", library);
		Assert.Equal(0, dllCode);
		Assert.Equal(string.Empty, dllErr);
		Assert.True(File.Exists(Path.Combine(library, "out", "mathx.kbc")));
		Assert.Contains("mathx.kbc", dllOut, StringComparison.Ordinal);

		(int checkCode, _, string checkErr) = Run("check", app);
		Assert.Equal(0, checkCode);
		Assert.Equal(string.Empty, checkErr);

		Assert.Equal("len = 5\ndot = 11\n", RunApp(app));
	}

	[Fact]
	public void Library_static_initialization_runs_before_the_program()
	{
		string root = Create(
			("lib/src/lib/state.kern", """
				type State {
					#i32 seed = 40

					fun get() -> i32 seed + 2
				}
				"""),
			("app/build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.target("app", fun (target t) {
						t.kbc("../lib/out/lib.kbc")
					})
				}
				"""),
			("app/src/main.kern", """
				use lib.state[State]

				fun main {
					println(f"${State().get()}")
				}
				"""));
		Assert.Equal(0, Run("dll", Path.Combine(root, "lib")).Code);
		Assert.Equal("42\n", RunApp(Path.Combine(root, "app")));
	}

	[Fact]
	public void Linker_reports_missing_kbc_dependency()
	{
		string root = Create(
			("app/build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.target("app", fun (target t) {
						t.kbc("../gone/out/gone.kbc")
					})
				}
				"""),
			("app/src/main.kern", "fun main { }"));

		(int code, _, string stderr) = Run("check", Path.Combine(root, "app"));

		Assert.Equal(1, code);
		Assert.Contains("не найдена", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Repeated_build_uses_the_hash_cache_and_kbc_dependency()
	{
		string root = CreateLibAndApp();
		string library = Path.Combine(root, "mathx");
		string app = Path.Combine(root, "app");
		Assert.Equal(0, Run("dll", library).Code);

		(int first, string firstOut, _) = Run("build", app);
		Assert.Equal(0, first);
		Assert.True(File.Exists(Path.Combine(app, "out", "app.kbc")));

		(int second, string secondOut, _) = Run("build", app);
		Assert.Equal(0, second);
		Assert.True(File.Exists(Path.Combine(app, "out", "app.kbc")));
		Assert.Equal(firstOut, secondOut);
	}

	[Fact]
	public void Dll_stubs_include_macro_generated_members()
	{
		string root = Create(
			("lib/src/macros.kern", """
				use kern.macro[macro_ctx]

				#[macro]
				fun to_text(macro_ctx m) {
					var t = m.decl
					var body = ""
					var sep = ""
					loop var f in t.fields {
						body = body + sep + f.name + "=${" + f.name + "}"
						sep = ", "
					}
					m.member("fun to_text() -> str f\"" + t.name + "(" + body + ")\"")
				}
				"""),
			("lib/src/model.kern", """
				#[derive(to_text)]
				unit User(str name)
				"""),
			("app/build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.target("app", fun (target t) {
						t.kbc("../lib/out/lib.kbc")
					})
				}
				"""),
			("app/src/main.kern", """
				use model[User]

				fun main {
					println(User("аня").to_text())
				}
				"""));

		Assert.Equal(0, Run("dll", Path.Combine(root, "lib")).Code);
		Assert.Equal("User(name=аня)\n", RunApp(Path.Combine(root, "app")));
	}

	private string CreateLibAndApp()
	{
		string root = Create(
			("mathx/src/mathx/vec.kern", """
				unit Vec2(f64 x, f64 y) {
					f64 len => ($x ** 2 + $y ** 2) ** 0.5

					op +(Vec2 other) -> Vec2 {
						Vec2($x + other.x, $y + other.y)
					}
				}

				fun dot(Vec2 a, Vec2 b) -> f64 {
					a.x * b.x + a.y * b.y
				}
				"""),
			("app/build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.target("app", fun (target t) {
						t.kbc("../mathx/out/mathx.kbc")
					})
				}
				"""),
			("app/src/main.kern", """
				use mathx.vec[Vec2, dot]

				fun main {
					var a = Vec2(3.0, 4.0)
					var b = Vec2(1.0, 2.0)
					println(f"len = ${a.len}")
					println(f"dot = ${dot(a, b)}")
				}
				"""));
		return root;
	}

	private static string RunApp(string appRoot)
	{
		BuildSession session = BuildSession.Open(appRoot);
		BuildTargetResult built = BuildTargetCompiler.CompileApp(session);
		Assert.True(built.Compile.Success, string.Join('\n', built.Compile.ProjectMessages));
		var writer = new StringWriter { NewLine = "\n" };
		TextWriter original = Console.Out;
		Console.SetOut(writer);
		try
		{
			var interpreter = new Interpreter(built.Compile.Program!.Module, new NativeResolver(built.Compile.Program.Catalog));
			_ = interpreter.RunEntry();
		}
		finally
		{
			Console.SetOut(original);
		}

		return writer.ToString();
	}

	private string Create(params (string Path, string Text)[] files)
	{
		string root = Path.Combine(Path.GetTempPath(), "kern-kbc-tests", Guid.NewGuid().ToString("N"));
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
