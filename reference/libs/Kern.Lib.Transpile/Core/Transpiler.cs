using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Lib.Transpile.Mapping;
using Kern.Lib.Transpile.Targets.CSharp;
using Kern.Lib.Transpile.Targets.Kotlin;

namespace Kern.Lib.Transpile.Core;

/// <summary>Результат транспиляции: файлы (путь → текст) и предупреждения.</summary>
public sealed record TranspileResult(Dictionary<string, string> Files, IReadOnlyList<string> Warnings);

/// <summary>Платформенно-независимая часть API: выбор цели, подстановки, запуск.</summary>
public sealed class Transpiler(string target)
{
	public string Target { get; } = Normalize(target);
	private readonly List<IMappings> _user = [];

	public static string Normalize(string target) => target.ToLowerInvariant() switch
	{
		"c#" or "cs" or "csharp" => "csharp",
		"kt" or "kotlin" => "kotlin",
		var t => throw new ArgumentException($"неизвестная цель '{t}' (доступно: csharp, kotlin)"),
	};

	public void AddScript(string path) => _user.Add(ScriptMappings.Load(path));

	private Lowerer CreateLowerer()
	{
		var maps = new MappingChain([.. _user, new BuiltinMappings()]);
		return Target == "kotlin" ? new KotlinLowerer(maps) : new CSharpLowerer(maps);
	}

	public TranspileResult Run(BoundProgram program)
	{
		var lowerer = CreateLowerer();
		var files = lowerer.Run(program);
		// расширение файлов: .cs / .kt (имя модуля уже получено диалектом)
		return new TranspileResult(files, lowerer.Warnings.Distinct().ToList());
	}

	public TranspileResult RunPath(string path) => Run(ProgramLoader.LoadPath(path));
	public TranspileResult RunText(string name, string source) => Run(ProgramLoader.LoadText(name, source));
}
