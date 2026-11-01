using Kern.Compiler.Semantics.Bound;
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

	public static string Normalize(string target) => target?.Trim().ToLowerInvariant() switch
	{
		"c#" or "cs" or "csharp" => "csharp",
		"kt" or "kotlin" => "kotlin",
		null or "" => throw new ArgumentException("пустая цель транспиляции (доступно: csharp, kotlin)"),
		var unknown => throw new ArgumentException($"неизвестная цель '{unknown}' (доступно: csharp, kotlin)"),
	};

	/// <summary>Подключает Kern-скрипт подстановок; учитывается при следующих запусках.</summary>
	public void AddScript(string path) => _user.Add(ScriptMappings.Load(path));

	private Lowerer CreateLowerer()
	{
		var mappings = new MappingChain([.. _user, new BuiltinMappings()]);
		return Target == "kotlin" ? new KotlinLowerer(mappings) : new CSharpLowerer(mappings);
	}

	public TranspileResult Run(BoundModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		Lowerer lowerer = CreateLowerer();
		Dictionary<string, string> files = lowerer.Run(module);
		return new TranspileResult(files, [.. lowerer.Warnings.Distinct(StringComparer.Ordinal)]);
	}

	public TranspileResult RunPath(string path) => Run(ProgramLoader.LoadPath(path));

	public TranspileResult RunText(string name, string source) => Run(ProgramLoader.LoadText(name, source));
}
