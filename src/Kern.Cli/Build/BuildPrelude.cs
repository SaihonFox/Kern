namespace Kern.Cli.Build;

/// <summary>
/// Исходник встроенного модуля <c>kern.build</c> (docs/BUILDER.md §3-4). Склеивается с
/// <c>build.kern</c> проекта в один проект компиляции: типы <c>workspace</c>/<c>target</c>/
/// <c>profile</c> — обычные Kern-классы, поэтому <c>w.name = "demo"</c> и <c>t.deps = [...]</c>
/// проверяются тем же компилятором, а не отдельным парсером конфигурации.
/// </summary>
internal static class BuildPrelude
{
	/// <summary>Путь модуля в <c>SourceBundle</c>: по нему считается имя модуля <c>build_prelude</c>.</summary>
	public const string Path = "build_prelude.kern";

	/// <summary>
	/// Импорт встраивается в конец пользовательского <c>build.kern</c>: строки исходника не
	/// сдвигаются, поэтому диагностики указывают на настоящие строки файла.
	/// </summary>
	public const string Import = "\nuse build_prelude.*\n";

	/// <summary>Имя функции, создающей пустой <c>workspace</c> (вызывается CLI).</summary>
	public const string NewWorkspace = "__new_workspace";

	/// <summary>Имя пользовательской функции сборки.</summary>
	public const string BuildFunction = "build";

	public const string Source = """
		// kern.build — встроенные типы builder'а (docs/BUILDER.md §3, §4).
		// Модуль компилируется вместе с build.kern; CLI читает поля workspace после fun build.

		type compiler_settings() {
			i32 optimize = 1
			bool warnings_as_errors = false
			bool macros = true
			bool debug_info = true
		}

		type build_task(str name, list<str> after, fun() -> void action)

		type profile(str name) {
			i32 optimize = 1
			bool warnings_as_errors = false
			dict<str, str> env = <str: str>[]
		}

		type target(str name) {
			str kind = "app"
			list<str> src = ["src"]
			str entry = "main"
			list<str> deps = <str>[]
			list<str> kbc_paths = <str>[]
			list<str> libs = <str>[]
			list<str> lib_paths = <str>[]
			list<str> args = <str>[]
			dict<str, str> env = <str: str>[]
			compiler_settings compiler = compiler_settings()
			list<build_task> tasks = <build_task>[]
		}

		type workspace() {
			str name = ""
			str version = "0.0.0"
			str root = ""
			compiler_settings compiler = compiler_settings()
			list<str> includes = <str>[]
			list<str> lib_paths = <str>[]
			list<profile> profiles = <profile>[]
			list<target> targets = <target>[]
			list<build_task> tasks = <build_task>[]
		}

		fun workspace.include(str path) {
			$includes.add(path)
		}

		fun workspace.lib_path(str path) {
			$lib_paths.add(path)
		}

		fun workspace.profile(str name, fun(profile) -> void configure) {
			profile p(name)
			configure(p)
			$profiles.add(p)
		}

		fun workspace.target(str name, fun(target) -> void configure) -> target {
			target t(name)
			configure(t)
			$targets.add(t)
			t
		}

		fun workspace.task(str name, list<str> after, fun() -> void action) {
			$tasks.add(build_task(name, after, action))
		}

		fun workspace.task(str name, fun() -> void action) {
			$tasks.add(build_task(name, <str>[], action))
		}

		fun workspace.env(str name) -> str? __build_env(name)

		fun target.kbc(str path) {
			$kbc_paths.add(path)
		}

		fun target.lib(str module) {
			$libs.add(module)
		}

		fun target.lib_path(str path) {
			$lib_paths.add(path)
		}

		fun target.task(str name, fun() -> void action) {
			$tasks.add(build_task(name, <str>[], action))
		}

		fun __new_workspace(str root) -> workspace {
			workspace w()
			w.root = root
			w
		}
		""";
}
