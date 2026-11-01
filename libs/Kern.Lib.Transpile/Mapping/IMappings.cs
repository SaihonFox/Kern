namespace Kern.Lib.Transpile.Mapping;

/// <summary>
/// Подстановки для нативных типов и членов (то, что нельзя вывести из дерева Kern-кода).
/// Любой метод возвращает <c>null</c> — «правила нет, используй следующий источник»
/// (скрипт → встроенные правила → имя по соглашению языка).
/// </summary>
public interface IMappings
{
	/// <summary>Имя целевого типа для нативного типа Kern; аргументы уже переведены в целевой синтаксис.</summary>
	string? MapType(string target, string kernType, IReadOnlyList<string> args);

	/// <summary>
	/// Вызов нативной функции или метода. <paramref name="owner"/> — имя нативного типа
	/// (пустая строка для свободных функций), <paramref name="receiver"/> — выражение
	/// получателя (пустая строка для статических), <paramref name="args"/> — готовые аргументы.
	/// </summary>
	string? MapCall(string target, string owner, string name, string receiver, IReadOnlyList<string> args);

	/// <summary>Чтение нативного свойства.</summary>
	string? MapMember(string target, string owner, string name, string receiver);
}

/// <summary>Цепочка источников: первый непустой ответ побеждает.</summary>
public sealed class MappingChain(params IMappings[] sources) : IMappings
{
	public string? MapType(string target, string kernType, IReadOnlyList<string> args) =>
		sources.Select(source => source.MapType(target, kernType, args)).FirstOrDefault(result => result is not null);

	public string? MapCall(string target, string owner, string name, string receiver, IReadOnlyList<string> args) =>
		sources.Select(source => source.MapCall(target, owner, name, receiver, args)).FirstOrDefault(result => result is not null);

	public string? MapMember(string target, string owner, string name, string receiver) =>
		sources.Select(source => source.MapMember(target, owner, name, receiver)).FirstOrDefault(result => result is not null);
}
