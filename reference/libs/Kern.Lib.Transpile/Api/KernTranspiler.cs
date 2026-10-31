using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Transpile.Core;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Lib.Transpile.Api;

/// <summary>
/// Транспилятор Kern → C# / Kotlin (ядро языка: функции, типы, unit, match, циклы, generics, result).
/// <code>
/// use kern.transpile
/// val t = transpiler("csharp")
/// t.map_script("mappings.kern")
/// val code = t.text("fun main() { println(1) }")
/// </code>
/// Подстановки для нативных типов и членов задаются Kern-скриптом с функциями <c>map_type</c>, <c>map_call</c>, <c>map_member</c>.
/// </summary>
[KernType("transpiler")]
public sealed class KernTranspiler
{
	private readonly Transpiler _inner;

	/// <summary>Цель: "csharp" или "kotlin".</summary>
	[KernCtor] public KernTranspiler(string target) => _inner = new Transpiler(target);

	[KernProperty] public string Target => _inner.Target;

	/// <summary>Подключает Kern-скрипт подстановок; возвращает сам транспилятор (для цепочки вызовов).</summary>
	[KernMember]
	public KernTranspiler MapScript(string path)
	{
		_inner.AddScript(path);
		return this;
	}

	/// <summary>Исходный текст одного модуля → код главного файла (runtime-хелперы не включаются).</summary>
	[KernMember]
	public KResult<string, string> Text(string source) => Guard(() =>
	{
		var r = _inner.RunText("main", source);
		return KValue.Str(r.Files.Where(f => !f.Key.StartsWith("KernRuntime")).Select(f => f.Value).FirstOrDefault() ?? "");
	});

	/// <summary>Файл или папка проекта → код всех модулей, объединённый в одну строку (разделитель — строка с именем файла).</summary>
	[KernMember]
	public KResult<string, string> File(string path) => Guard(() =>
	{
		var r = _inner.RunPath(path);
		var sb = new System.Text.StringBuilder();
		foreach (var (name, text) in r.Files.Where(f => !f.Key.StartsWith("KernRuntime")))
			sb.Append("// ==== ").Append(name).Append(" ====\n").Append(text).Append('\n');
		return KValue.Str(sb.ToString());
	});

	/// <summary>Проект → файлы в каталог <paramref name="outDir"/> (вместе с KernRuntime); возвращает список записанных путей.</summary>
	[KernMember]
	public KResult<KList<string>, string> Project(string path, string outDir)
	{
		try
		{
			var r = _inner.RunPath(path);
			Directory.CreateDirectory(outDir);
			var written = new List<string>();
			foreach (var (name, text) in r.Files)
			{
				var target = Path.Combine(outDir, name);
				System.IO.File.WriteAllText(target, text.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
				written.Add(target);
			}
			return KResult<KList<string>, string>.Ok(KValue.Obj(Kv.Strs(written).Inner));
		}
		catch (Exception e)
		{
			return KResult<KList<string>, string>.Err(KValue.Str(e.Message));
		}
	}

	private static KResult<string, string> Guard(Func<KValue> body)
	{
		try { return KResult<string, string>.Ok(body()); }
		catch (Exception e) { return KResult<string, string>.Err(KValue.Str(e.Message)); }
	}
}
