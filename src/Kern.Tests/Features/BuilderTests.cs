using System.Text;
using Kern.Bytecode;
using Kern.Cli;
using Kern.Cli.Build;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M8: builder <c>build.kern</c> — цели, источники с зависимостями, сборка app-цели,
/// команды <c>tasks</c>/<c>do</c>, порядок <c>after</c> и русские ошибки (docs/BUILDER.md §3-4).
/// </summary>
public sealed class BuilderTests : IDisposable
{
	private const string WorkspaceScript = """
		use kern.build[workspace, target]

		fun build(workspace w) {
			w.name = "demo"
			w.version = "1.2.3"
			w.compiler.optimize = 0

			w.target("core", fun (target t) {
				t.kind = "lib"
				t.src = ["core/src"]
			})

			w.target("app", fun (target t) {
				t.src = ["app/src"]
				t.deps = ["core"]
				t.entry = "main"
				t.task("hello", fun println("привет из app"))
			})

			w.task("first", fun println("первый"))
			w.task("second", ["first"], fun println("второй"))
		}
		""";

	private const string UtilSource = """
		pub fun greet(str name) -> str
			f"Привет, ${name}!"
		""";

	private const string MainSource = """
		use util[greet]

		fun main() {
			println(greet("Kern"))
		}
		""";

	private readonly List<string> _roots = [];

	public void Dispose()
	{
		foreach (string root in _roots)
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
	}

	[Fact]
	public void Build_session_reads_workspace_targets_and_tasks()
	{
		BuildSession session = BuildSession.Open(WorkspaceProject());

		Assert.True(session.HasBuildFile);
		Assert.NotNull(session.ScriptPath);
		Assert.NotNull(session.Interpreter);
		Assert.Equal("demo", session.Workspace.Name);
		Assert.Equal("1.2.3", session.Workspace.Version);
		Assert.Equal(0, session.Workspace.Compiler.Optimize);

		TargetModel core = Assert.IsType<TargetModel>(session.Workspace.FindTarget("core"));
		Assert.Equal("lib", core.Kind);
		Assert.Equal(new[] { "core/src" }, core.Src);

		TargetModel app = Assert.IsType<TargetModel>(session.Workspace.FindTarget("app"));
		Assert.Equal("app", app.Kind);
		Assert.Equal("main", app.Entry);
		Assert.Equal(new[] { "core" }, app.Deps);
		Assert.NotNull(app.FindTask("hello"));

		TaskModel second = Assert.Single(session.Workspace.Tasks, task => task.Name == "second");
		Assert.Equal(new[] { "first" }, second.After);
	}

	[Fact]
	public void Build_command_compiles_app_target_with_dependency_sources()
	{
		string root = WorkspaceProject();

		(int code, string stdout, string stderr) = Run("build", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.True(File.Exists(Path.Combine(root, "out", "app.kbc")));
		// lib-цель, как библиотека в C#, тоже даёт готовый .kbc (без точки входа).
		Assert.True(File.Exists(Path.Combine(root, "out", "core.kbc")));
		Assert.Contains("app.kbc", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_command_writes_a_library_kbc_without_entry_and_with_stubs()
	{
		string root = WorkspaceProject();

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		KbcModule library = KbcReader.Read(File.ReadAllBytes(Path.Combine(root, "out", "core.kbc")));
		Assert.Equal(-1, library.EntryPoint);
		Assert.Contains(library.Attributes, attribute => attribute.Name == "kern.stub");
	}

	[Fact]
	public void Build_command_keeps_convention_for_project_without_build_kern()
	{
		string root = Create(("src/main.kern", "fun main() { println(\"ok\") }"));

		(int code, string stdout, string stderr) = Run("build", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		string expected = Path.Combine(root, "out", Path.GetFileName(root) + ".kbc");
		Assert.True(File.Exists(expected));
		Assert.Contains(expected, stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_command_reports_unknown_dependency()
	{
		string root = Create(
			("build.kern", AppScript("""t.deps = ["ghost"]""")),
			("app/src/main.kern", MainSource));

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(1, code);
		Assert.Contains("цель 'app': зависимость 'ghost' не найдена", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_command_reports_target_cycle()
	{
		string script = """
			use kern.build[workspace, target]

			fun build(workspace w) {
				w.target("a", fun (target t) {
					t.src = ["a/src"]
					t.deps = ["b"]
				})
				w.target("b", fun (target t) {
					t.src = ["b/src"]
					t.deps = ["a"]
				})
			}
			""";
		string root = Create(
			("build.kern", script),
			("a/src/main.kern", "fun main() { }"),
			("b/src/util.kern", "pub fun helper() -> i32 { 1 }"));

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(1, code);
		Assert.Contains("цикл зависимостей целей: a -> b -> a", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_command_reports_missing_main_in_app_target()
	{
		string root = Create(
			("build.kern", AppScript("t.entry = \"main\"")),
			("app/src/main.kern", "fun helper() -> i32 { 1 }"));

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(1, code);
		Assert.Contains("цель 'app': в точке входа нет fun main", stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(root, "out", "app.kbc")));
	}

	[Fact]
	public void Build_command_reports_script_diagnostics()
	{
		string script = """
			use kern.build[workspace]

			fun build(workspace w) {
				w.name = 42
			}
			""";
		string root = Create(("build.kern", script));

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(1, code);
		Assert.Contains("build.kern:", stderr, StringComparison.Ordinal);
		Assert.Contains("ошибка", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_command_prints_target_diagnostics_by_project_path()
	{
		string root = Create(
			("build.kern", AppScript("t.entry = \"main\"")),
			("app/src/main.kern", """
				fun main() {
					i32 value = "текст"
				}
				"""));

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(1, code);
		Assert.Contains("app/src/main.kern:", stderr, StringComparison.Ordinal);
		Assert.Contains("ошибка", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Tasks_command_lists_targets_tasks_and_dependencies()
	{
		string root = WorkspaceProject();

		(int code, string stdout, string stderr) = Run("tasks", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.Contains("проект: demo", stdout, StringComparison.Ordinal);
		Assert.Contains("цель core (lib)", stdout, StringComparison.Ordinal);
		Assert.Contains("цель app (app, deps: core, entry: main)", stdout, StringComparison.Ordinal);
		Assert.Contains("задача app:hello", stdout, StringComparison.Ordinal);
		Assert.Contains("задача second (после: first)", stdout, StringComparison.Ordinal);
		Assert.Contains("встроенные задачи:", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Tasks_command_marks_convention_project()
	{
		string root = Create(("src/main.kern", "fun main() { }"));

		(int code, string stdout, string stderr) = Run("tasks", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.Contains("(по соглашению)", stdout, StringComparison.Ordinal);
		Assert.Contains($"цель {Path.GetFileName(root)} (app, entry: main)", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Do_command_runs_after_tasks_in_topological_order()
	{
		string root = WorkspaceProject();

		(int code, string stdout, string stderr) = Run("do", "second", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		string[] lines = stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(["задача first", "задача second"], lines);
	}

	[Fact]
	public void Do_command_reports_task_cycle()
	{
		string script = """
			use kern.build[workspace]

			fun build(workspace w) {
				w.task("a", ["b"], fun println("a"))
				w.task("b", ["a"], fun println("b"))
			}
			""";
		string root = Create(("build.kern", script));

		(int code, _, string stderr) = Run("do", "a", root);

		Assert.Equal(1, code);
		Assert.Contains("цикл в задачах: a -> b -> a", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Do_command_runs_builtin_compile()
	{
		string root = WorkspaceProject();

		(int code, string stdout, string stderr) = Run("do", ":compile", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.True(File.Exists(Path.Combine(root, "out", "app.kbc")));
		Assert.Contains("задача :compile", stdout, StringComparison.Ordinal);
		Assert.Contains("app.kbc", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Do_command_compiles_one_target()
	{
		string root = WorkspaceProject();

		(int code, string stdout, string stderr) = Run("do", "core:compile", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.True(File.Exists(Path.Combine(root, "out", "core.kbc")));
		Assert.Contains("задача core:compile", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Do_command_runs_builtin_compile_for_convention_project()
	{
		string root = Create(("src/main.kern", "fun main() { }"));

		(int code, _, string stderr) = Run("do", ":compile", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.True(File.Exists(Path.Combine(root, "out", Path.GetFileName(root) + ".kbc")));
	}

	[Fact]
	public void Do_command_keeps_task_closure_alive()
	{
		string script = """
			use kern.build[workspace]

			fun build(workspace w) {
				str message = "замыкание живо"
				w.task("fail", fun panic(message))
			}
			""";
		string root = Create(("build.kern", script));

		(int code, _, string stderr) = Run("do", "fail", root);

		Assert.Equal(1, code);
		Assert.Contains("ошибка выполнения задачи 'fail': замыкание живо", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Do_command_reports_unknown_task()
	{
		string root = WorkspaceProject();

		(int code, _, string stderr) = Run("do", "ghost", root);

		Assert.Equal(1, code);
		Assert.Contains("задача 'ghost' не найдена", stderr, StringComparison.Ordinal);
		Assert.Contains("first, second", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Do_command_runs_builtin_exe()
	{
		string root = WorkspaceProject();

		(int code, string stdout, string stderr) = Run("do", "app:exe", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.True(File.Exists(Path.Combine(root, "dist", OperatingSystem.IsWindows() ? "app.exe" : "app")));
		Assert.True(File.Exists(Path.Combine(root, "dist", "program.kbc")));
		Assert.Contains("app", stdout, StringComparison.Ordinal);
	}

	private string WorkspaceProject() => Create(
		("build.kern", WorkspaceScript),
		("core/src/util.kern", UtilSource),
		("app/src/main.kern", MainSource));

	private static string AppScript(string setting) => $$"""
		use kern.build[workspace, target]

		fun build(workspace w) {
			w.target("app", fun (target t) {
				t.src = ["app/src"]
				{{setting}}
			})
		}
		""";

	private string Create(params (string Path, string Text)[] files)
	{
		string root = Path.Combine(Path.GetTempPath(), "kern-builder-tests", Guid.NewGuid().ToString("N"));
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
}
