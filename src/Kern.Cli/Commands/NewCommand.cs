using System.Text;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc new app|exe|console|lib|library|classlib|service &lt;папка&gt;</c>: создать проект
/// по шаблону. Виды проекта как в C#: запускаемое приложение (<c>app</c>, он же
/// <c>exe</c>/<c>console</c>) и библиотека (<c>lib</c>, он же <c>library</c>/<c>classlib</c>).
/// Шаблоны — обычные исходники и <c>build.kern</c>, без скрытой магии.
/// </summary>
internal static class NewCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (args.Length != 2)
		{
			stderr.WriteLine("ошибка: использование: kernc new app|lib|service <папка>");
			return 2;
		}

		string? template = args[0] switch
		{
			"app" or "exe" or "console" => "app",
			"lib" or "library" or "classlib" => "lib",
			"service" => "service",
			_ => null,
		};
		if (template is null)
		{
			stderr.WriteLine(
				$"ошибка: неизвестный вид проекта: {args[0]} (ожидалось app|exe|console|lib|library|classlib|service)");
			return 2;
		}

		string path = Path.GetFullPath(args[1]);
		if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
		{
			stderr.WriteLine($"ошибка: папка уже существует и не пуста: {path}");
			return 1;
		}

		try
		{
			Directory.CreateDirectory(path);
			string name = ProjectName(Path.GetFileName(path));
			string sourceDirectory = Path.Combine(path, "src");
			Directory.CreateDirectory(sourceDirectory);

			File.WriteAllText(Path.Combine(path, "build.kern"), BuildScript(template, name), new UTF8Encoding(false));
			string source = template switch
			{
				"lib" => LibrarySource(name),
				"service" => ServiceSource(),
				_ => AppSource(),
			};
			string sourceName = template == "lib" ? "lib.kern" : "main.kern";
			File.WriteAllText(Path.Combine(sourceDirectory, sourceName), source, new UTF8Encoding(false));

			stdout.WriteLine($"создан проект {template}: {path}");
			stdout.WriteLine($"  build.kern");
			stdout.WriteLine($"  src/{sourceName}");
			stdout.WriteLine(template == "service"
				? "  запуск: kernc run . (нужны libs/Kern.Lib.Backend и libs/Kern.Lib.Di)"
				: "  запуск: kernc run .");
			return 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"ошибка: не удалось создать проект: {exception.Message}");
			return 1;
		}
	}

	private static string ProjectName(string directory)
	{
		var name = new StringBuilder();
		foreach (char symbol in directory)
			name.Append(char.IsLetterOrDigit(symbol) ? char.ToLowerInvariant(symbol) : '_');
		string text = name.ToString().Trim('_');
		if (text.Length == 0)
			return "project";
		return char.IsDigit(text[0]) ? "_" + text : text;
	}

	private static string BuildScript(string template, string name)
	{
		string kind = template == "lib" ? "t.kind = \"lib\"" : "t.kind = \"app\"\n\t\t\t\t\tt.entry = \"main\"";
		string libs = template == "service" ? "\n\t\tt.lib(\"kern.backend\")\n\t\tt.lib(\"kern.di\")" : "";
		return $$"""
			use kern.build[workspace, target]

			fun build(workspace w) {
				w.name = "{{name}}"
				w.target("{{name}}", fun (target t) {
					{{kind}}{{libs}}
				})
			}
			""";
	}

	private static string AppSource() => """
		fun main {
			println("Привет, мир!")
		}
		""";

	private static string LibrarySource(string name) => $$"""
		// Библиотека {{name}}: функции видны потребителям через use.
		fun add(i32 a, i32 b) -> i32 a + b
		""";

	private static string ServiceSource() => """
		use kern.backend[app, ctx, res]
		use kern.backend.server.*

		async fun main {
			app a(8080)
			a.get("/health", fun (ctx c) -> res res.text("ok"))
			println("http://localhost:8080/")
			await a.serve()
		}
		""";
}
