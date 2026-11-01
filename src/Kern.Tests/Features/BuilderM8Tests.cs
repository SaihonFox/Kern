using System.Text;
using Kern.Cli;
using Kern.Cli.Build;
using Kern.Tests.Features.Support;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>M8: профили, окружение, include, t.args, exe и поиск библиотек.</summary>
[Collection("timing")]
public sealed class BuilderM8Tests : IDisposable
{
	private readonly List<string> _roots = [];

	[Fact]
	public void Profile_affects_optimization_and_environment()
	{
		string root = Create(
			("build.kern", """
				use kern.build[workspace, target]

				fun build(workspace w) {
					w.profile("fast", fun (profile p) {
						p.optimize = 0
						p.env["MODE"] = "fast"
					})
					w.compiler.optimize = 1
					w.target("app", fun (target t) {
						t.args = ["one", "two"]
					})
				}
				"""),
			("app/src/main.kern", "fun main { }"));

		BuildSession session = BuildSession.Open(root);
		session.ProfileName = "fast";
		TargetModel app = Assert.IsType<TargetModel>(session.Workspace.FindTarget("app"));

		Assert.Equal(0, session.OptionsFor(app).Optimize);
		Assert.Equal("fast", session.EnvironmentFor(app)["MODE"]);
		Assert.Equal(new[] { "one", "two" }, app.Args);
	}

	[Fact]
	public void Unknown_profile_is_reported()
	{
		string root = Create(
			("build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.target("app", fun (target t) { })
				}
				"""),
			("app/src/main.kern", "fun main { }"));

		BuildSession session = BuildSession.Open(root);
		session.ProfileName = "ghost";
		TargetModel app = Assert.IsType<TargetModel>(session.Workspace.FindTarget("app"));
		var exception = Assert.Throws<BuildException>(() => session.OptionsFor(app));
		Assert.Contains("ghost", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Include_builds_child_projects()
	{
		string root = Create(
			("build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.include("child")
					w.target("app", fun (target t) { })
				}
				"""),
			("app/src/main.kern", "fun main { }"),
			("child/src/util.kern", "pub fun value() -> i32 7"));

		(int code, string stdout, string stderr) = Run("build", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.True(File.Exists(Path.Combine(root, "child", "out", "child.kbc")));
		Assert.Contains("child.kbc", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Missing_external_library_is_reported()
	{
		string root = Create(
			("build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.target("app", fun (target t) {
						t.lib("kern.nosuch")
					})
				}
				"""),
			("app/src/main.kern", "fun main { }"));

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(1, code);
		Assert.Contains("kern.nosuch", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public void Exe_writes_host_and_program_kbc()
	{
		string root = Create(("src/main.kern", "fun main { println(\"exe\") }"));

		(int code, string stdout, string stderr) = Run("exe", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		string name = Path.GetFileName(root);
		string host = Path.Combine(root, "dist", OperatingSystem.IsWindows() ? name + ".exe" : name);
		Assert.True(File.Exists(host));
		Assert.True(File.Exists(Path.Combine(root, "dist", "program.kbc")));
		Assert.Contains(host, stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_honours_profile_environment_and_target_args()
	{
		string root = Create(
			("build.kern", """
				use kern.build[workspace, target]

				fun build(workspace w) {
					w.profile("fast", fun (profile p) {
						p.env["MODE"] = "fast"
					})
					w.target("app", fun (target t) {
						t.args = ["a", "b"]
					})
				}
				"""),
			("app/src/main.kern", """
				fun main(list<str> args) {
					println(f"${env.get_or("MODE", "none")} ${args.len}")
				}
				"""));

		var writer = new StringWriter { NewLine = "\n" };
		TextWriter original = Console.Out;
		Console.SetOut(writer);
		int code;
		string stderr;
		try
		{
			var error = new StringWriter();
			code = KernCli.Run(["run", "-p", "fast", root], new StringWriter(), error);
			stderr = error.ToString();
		}
		finally
		{
			Console.SetOut(original);
		}

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.Equal("fast 2\n", writer.ToString());
	}

	[Fact]
	public void Warnings_as_errors_fails_the_build()
	{
		string root = Create(
			("build.kern", """
				use kern.build[workspace, target]
				fun build(workspace w) {
					w.compiler.warnings_as_errors = true
					w.target("app", fun (target t) { })
				}
				"""),
			("app/src/main.kern", """
				#[deprecated]
				fun old -> i32 1

				fun main {
					println(old())
				}
				"""));

		(int code, _, string stderr) = Run("build", root);

		Assert.Equal(1, code);
		Assert.Contains("warnings_as_errors", stderr, StringComparison.Ordinal);
	}

	private string Create(params (string Path, string Text)[] files)
	{
		string root = Path.Combine(Path.GetTempPath(), "kern-builder-m8", Guid.NewGuid().ToString("N"));
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
