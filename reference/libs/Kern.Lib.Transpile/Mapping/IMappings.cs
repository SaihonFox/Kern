namespace Kern.Lib.Transpile.Mapping;

/// <summary>
/// Подстановки для нативных типов и членов (то, что нельзя вывести из дерева Kern-кода).
/// Любой метод возвращает null — «нет правила, используй следующий источник» (скрипт → встроенные → имя по умолчанию).
/// </summary>
public interface IMappings
{
	/// <summary>Имя целевого типа для нативного типа Kern. <paramref name="args"/> — уже готовые аргументы типа.</summary>
	string? MapType(string target, string kernType, IReadOnlyList<string> args);

	/// <summary>
	/// Вызов нативной функции или метода. <paramref name="owner"/> — имя нативного типа ("" для свободных функций),
	/// <paramref name="receiver"/> — выражение получателя ("" для статических), <paramref name="args"/> — готовые аргументы.
	/// </summary>
	string? MapCall(string target, string owner, string name, string receiver, IReadOnlyList<string> args);

	/// <summary>Чтение нативного свойства.</summary>
	string? MapMember(string target, string owner, string name, string receiver);
}

/// <summary>Цепочка источников: первый непустой ответ побеждает.</summary>
public sealed class MappingChain(params IMappings[] sources) : IMappings
{
	public string? MapType(string t, string k, IReadOnlyList<string> a) => sources.Select(s => s.MapType(t, k, a)).FirstOrDefault(r => r is not null);
	public string? MapCall(string t, string o, string n, string r, IReadOnlyList<string> a) => sources.Select(s => s.MapCall(t, o, n, r, a)).FirstOrDefault(x => x is not null);
	public string? MapMember(string t, string o, string n, string r) => sources.Select(s => s.MapMember(t, o, n, r)).FirstOrDefault(x => x is not null);
}
