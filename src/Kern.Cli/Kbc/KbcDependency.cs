using Kern.Bytecode;
using Kern.Cli.Build;

namespace Kern.Cli.Kbc;

/// <summary>
/// Загруженная <c>.kbc</c>-библиотека (spec round 25): модуль для линковки и
/// интерфейсные исходники из атрибутов <c>kern.stub</c>.
/// </summary>
internal sealed class KbcDependency
{
	private KbcDependency(string path, KbcModule module, IReadOnlyList<StubSource> stubs)
	{
		Path = path;
		Module = module;
		Stubs = stubs;
	}

	public string Path { get; }

	public KbcModule Module { get; }

	public IReadOnlyList<StubSource> Stubs { get; }

	public static IReadOnlyList<KbcDependency> Load(string root, IReadOnlyList<string> paths)
	{
		if (paths.Count == 0)
			return [];

		var dependencies = new List<KbcDependency>(paths.Count);
		foreach (string path in paths)
		{
			string full = System.IO.Path.IsPathRooted(path)
				? path
				: System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
			if (!File.Exists(full))
				throw new BuildException($".kbc-зависимость не найдена: {path}");

			KbcModule module;
			try
			{
				module = KbcReader.Read(File.ReadAllBytes(full));
				KbcModuleValidator.Validate(module);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new BuildException($"не удалось прочитать .kbc {path}: {exception.Message}");
			}
			catch (KbcFormatException exception)
			{
				throw new BuildException($".kbc {path}: {exception.Message}");
			}
			catch (InvalidOperationException exception)
			{
				throw new BuildException($".kbc {path}: {exception.Message}");
			}

			var stubs = new List<StubSource>();
			foreach (AttributeUse attribute in module.Attributes)
			{
				if (attribute.Name != "kern.stub" || attribute.Args.Count < 3)
					continue;
				stubs.Add(new StubSource(attribute.Args[0].AsStr, attribute.Args[2].AsStr));
			}

			if (stubs.Count == 0)
				throw new BuildException($".kbc {path}: нет интерфейсных исходников (соберите через `kernc dll`)");

			dependencies.Add(new KbcDependency(path, module, stubs));
		}

		return dependencies;
	}
}

/// <summary>Интерфейсный исходник внутри <c>.kbc</c>: путь модуля и текст.</summary>
internal sealed record StubSource(string Path, string Text);
