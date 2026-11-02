using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Lsp.Analysis;

/// <summary>
/// Семантический снимок проекта для completion/signature help: функции и типы из
/// <c>ProjectCompiler</c>, включая нативные модули и импортированные файлы. Строится после
/// анализа открытых/дисковых файлов; не заменяет binder, а даёт LSP имена и сигнатуры.
/// </summary>
public sealed class LspIndex
{
	private readonly ModuleSymbol _module;

	public LspIndex(ModuleSymbol module, IReadOnlyList<string> modules)
	{
		ArgumentNullException.ThrowIfNull(module);
		_module = module;
		Modules = modules;
	}

	/// <summary>Имена модулей проекта и нативных модулей (для <c>use …</c>).</summary>
	public IReadOnlyList<string> Modules { get; }

	public IReadOnlyDictionary<string, TypeSymbol> Types => _module.Types;

	public IEnumerable<FunctionSymbol> Functions =>
		_module.Functions.Values.SelectMany(group => group);

	public IEnumerable<FieldSymbol> Globals => _module.Globals.Values;

	public IEnumerable<FunctionSymbol> FindFunctions(string name) =>
		_module.GetFunctions(name);

	/// <summary>Функции/методы/нативные члены с этим именем (для signature help).</summary>
	public IEnumerable<FunctionSymbol> FindCallables(string name)
	{
		var seen = new HashSet<FunctionSymbol>(ReferenceEqualityComparer.Instance);
		foreach (FunctionSymbol function in _module.GetFunctions(name))
			if (seen.Add(function))
				yield return function;
		foreach (TypeSymbol type in _module.Types.Values)
			foreach (FunctionSymbol function in type.Members.GetFunctions(name))
				if (seen.Add(function))
					yield return function;
		foreach (FunctionSymbol function in _module.NativeFunctions)
			if (string.Equals(function.Name, name, StringComparison.Ordinal) && seen.Add(function))
				yield return function;
	}

	public TypeSymbol? FindType(string name) => _module.FindType(name);

	/// <summary>Члены по имени типа; для встроенных (<c>str</c>, <c>list</c>) — нативные.</summary>
	public IEnumerable<MemberItem> MembersForName(string typeName)
	{
		TypeSymbol? type = FindType(typeName);
		if (type is not null)
			return Members(type);
		return _module.NativeFunctions
			.Where(function => function.NativeSelfType is not null &&
				string.Equals(BaseName(function.NativeSelfType.ToString()), typeName, StringComparison.Ordinal))
			.Select(function => new MemberItem(function.Name, "method", function.Signature))
			.Concat(UniversalMembers(typeName));
	}

	/// <summary>
	/// У любого значения есть <c>to_str</c>/<c>to_debug</c> (spec §3), даже если у
	/// примитива нет отдельного нативного класса.
	/// </summary>
	private static IEnumerable<MemberItem> UniversalMembers(string typeName)
	{
		if (!IsPrimitive(typeName) && typeName is not ("unit" or "object"))
			yield break;
		yield return new MemberItem("to_str", "method", "to_str() -> str");
		yield return new MemberItem("to_debug", "method", "to_debug() -> str");
	}

	private static bool IsPrimitive(string typeName) => typeName is
		"bool" or "char" or "str" or "void" or "object" or "never" or
		"i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64" or
		"f16" or "f32" or "f64" or "inum" or "fnum" or "num";

	/// <summary>Члены типа: методы, поля, свойства, конструкторы и нативные члены.</summary>
	public IEnumerable<MemberItem> Members(TypeSymbol type)
	{
		ArgumentNullException.ThrowIfNull(type);
		foreach (var group in type.Members.Functions.Values)
			foreach (FunctionSymbol function in group)
				yield return new MemberItem(function.Name, "method", function.Signature);
		foreach (FieldSymbol field in type.Members.Fields)
			yield return new MemberItem(field.Name, "field", $"{field.Type} {field.Name}");
		foreach (PropertySymbol property in type.Members.Properties)
			yield return new MemberItem(property.Name, "property", $"{property.Type} {property.Name}");
		foreach (FunctionSymbol constructor in type.Members.Constructors)
			yield return new MemberItem(constructor.Name, "constructor", constructor.Signature);

		string typeName = type.Name;
		foreach (FunctionSymbol native in _module.NativeFunctions)
		{
			if (native.NativeSelfType is null ||
				!string.Equals(BaseName(native.NativeSelfType.ToString()), typeName, StringComparison.Ordinal))
				continue;
			yield return new MemberItem(native.Name, "method", native.Signature);
		}
	}

	private static string BaseName(string typeText)
	{
		int angle = typeText.IndexOf('<', StringComparison.Ordinal);
		return angle < 0 ? typeText : typeText[..angle];
	}
}

/// <summary>Один член для автодополнения.</summary>
public readonly record struct MemberItem(string Name, string Kind, string Detail);
